# Server TLS Certificate from Windows Certificate Store

> Note: this repo has no `_specs/template.md`, so this spec follows a standard
> feature-spec structure (matching `update-ad-phone-numbers.md`). No code
> examples are included, per the spec workflow.

## Summary

Have **Kestrel terminate HTTPS in-process** by loading the API's server
certificate from the **Windows certificate store** on the AD FS host
(`LocalMachine\My`). The certificate is issued and renewed by **Let's Encrypt
via `win-acme`** (or equivalent), which installs each renewed certificate into
the same store. Because Let's Encrypt certs renew every ~60 days and each
renewal produces a **new thumbprint**, the app must:

- select the cert by **subject (CN/SAN)**, not by a fixed thumbprint;
- **live-reload** the cert when a renewed copy appears in the store, with
  **no service restart**.

The app never reads PFX/PEM files from disk and never participates in the
ACME protocol itself.

## Background / context

`CLAUDE.md` currently states: *"TLS is the deployer's responsibility (IIS/proxy
in front of Kestrel); the filter does not enforce HTTPS."* This feature changes
that posture for deployments where there is no IIS/reverse-proxy fronting the
service: Kestrel binds HTTPS directly using a certificate that `win-acme` has
installed into the local-machine personal store.

Operational model: `win-acme` runs as a scheduled task on the host, obtains
and renews the LE certificate using the **DNS-01 challenge** (the AD FS host
needs no inbound HTTP/port-80 reachability for ACME — the challenge is
satisfied by `win-acme` writing a TXT record via the configured DNS provider
API), and writes the renewed cert into `LocalMachine\My`. Each renewal yields
a fresh thumbprint, but the **subject (CN/SAN) stays the same**, so the app
keys off subject and treats whichever matching cert has the latest
`NotBefore` as authoritative. A periodic refresh inside the process detects
renewals and swaps the live binding without a restart.

## Goals

- Bind Kestrel's HTTPS endpoint using a certificate loaded from
  `LocalMachine\My`, selected by **configured subject** (CN/SAN).
- **Live-reload** the certificate when `win-acme` installs a renewed copy in
  the store — **no service restart** required, no in-flight connection drop.
- **Fail fast at startup** on a missing, expired, not-yet-valid, or
  private-key-inaccessible certificate, with a clear log message — never
  silently fall back to HTTP.
- Keep certificate issuance and renewal entirely an **ops/`win-acme`
  concern**: the app reads from the store, never participates in ACME.
- Preserve the existing `X-API-Key` auth on top of TLS. Adding TLS is
  defence-in-depth, not a replacement.

## Non-goals

- **No client-certificate authentication (mTLS).** If callers should be
  required to present a client cert, that is a separate spec.
- **No in-process ACME implementation.** The app does not speak ACME, does
  not perform HTTP-01 / DNS-01 / TLS-ALPN-01 challenges, and does not write
  to the certificate store. `win-acme` (or equivalent) owns that.
- No reading PFX/PEM files from disk; no embedding private keys in
  configuration or environment variables.
- No change to API endpoints, request/response shapes, or the API-key filter.
- No support for stores other than `LocalMachine` (e.g. `CurrentUser`) unless
  trivially exposed via config (see open questions).

## Functional requirements

1. **HTTPS binding**
   - Kestrel binds an HTTPS endpoint on a configured port (default to a
     well-known port to be confirmed in planning).
   - The bound certificate is sourced from the Windows certificate store, not
     from a file path.

2. **Certificate selection**
   - Primary key: **subject** (CN or SAN match), configured at e.g.
     `Kestrel:Certificate:Subject` (env: `Kestrel__Certificate__Subject`).
   - Store location: `LocalMachine`. Store name: `My` (Personal). Both should
     be overridable in config but default to those values.
   - When more than one certificate in the store matches the subject (normal
     during a `win-acme` renewal overlap window), the app picks the one with
     the **latest `NotBefore`** among those that are currently valid and have
     a readable private key. Older copies are ignored.
   - A configured thumbprint is **not required** but may be supported as an
     optional pin for emergencies (see open questions).

3. **Startup validation (fail-fast)**
   - On startup the app opens the configured store **read-only**, resolves
     the certificate by subject as above, and verifies:
     - certificate is currently valid (`NotBefore` ≤ now ≤ `NotAfter`);
     - the certificate has an accessible private key (the service account can
       read it);
     - (optionally) the certificate is intended for server authentication
       (EKU `1.3.6.1.5.5.7.3.1`) — see open questions.
   - Any failure → process exits non-zero with a clear log line naming the
     configured subject, store, location, and the specific failure (no match /
     expired / not-yet-valid / no private key / wrong EKU). Never silently
     fall back to HTTP.

4. **Live reload on renewal**
   - Kestrel is configured with a **per-connection certificate selector**
     (`HttpsConnectionAdapterOptions.ServerCertificateSelector` or
     equivalent) that returns the **currently-cached** certificate.
   - A background refresh polls the certificate store on a fixed interval
     (default to be confirmed in planning — order of minutes, e.g. every
     5–15 minutes) and re-runs the selection logic from §2. If the resolved
     certificate's thumbprint differs from the cached one, the cache is
     swapped atomically.
   - The swap takes effect on the **next new connection**. In-flight TLS
     sessions are not torn down.
   - Each successful refresh logs at Debug; each actual swap logs at
     Information with old and new thumbprints, subject, and `NotAfter` of
     the new cert. A failed refresh logs at Warning and keeps the previous
     cert in use — the app does not break on a transient store-read error.

5. **HTTP behavior**
   - **Production: no plain-HTTP listener is bound.** The app listens on
     `5199` HTTPS only. Rationale: this is an internal service-to-service
     API (not browser-facing), DNS-01 ACME does not need port 80, and not
     binding HTTP is the most defensible default — there is nothing to
     accidentally consume over plaintext.
   - In **Development**, the existing HTTP behaviour is preserved so the dev
     loop on Linux still works (Linux has no Windows cert store).

6. **Protocol floor**
   - Kestrel restricted to **TLS 1.2 minimum** (TLS 1.3 preferred when the OS
     supports it). Older protocols disabled.

7. **Auth interaction**
   - The `X-API-Key` filter is unchanged and continues to run on every
     request — TLS is added below the filter, not in place of it.

## Configuration

- New section, conceptually `Kestrel:Certificate`:
  - `Subject` (required) — CN or SAN to match.
  - `StoreName` (optional, default `My`).
  - `StoreLocation` (optional, default `LocalMachine`).
  - `RefreshInterval` (optional, default to be confirmed in planning).
- The built-in `Kestrel:Endpoints:*:Certificate` schema natively supports
  `Subject` + `Store` + `Location` but resolves the cert **once at bind
  time**, so it does not satisfy the live-reload requirement on its own. The
  spec keeps the option open to use that schema for the initial bind and
  layer live-reload on top via `ServerCertificateSelector`, or to drive the
  whole thing from a small custom section (open question).
- Subject and store fields are **not secret** and may live in tracked
  `appsettings.<env>.json`. Per `CLAUDE.md`, production-specific values still
  come from environment variables / non-tracked overrides.

### Example: `appsettings.Production.json`

```json
{
  "Kestrel": {
    "Certificate": {
      "Subject": "api.example.no",
      "StoreName": "My",
      "StoreLocation": "LocalMachine",
      "RefreshInterval": "00:05:00"
    }
  },
  "ApiKey": {
    "Keys": []
  }
}
```

Notes on each field:

- `Subject` — CN or SAN to match in the store; **required**. Matches the
  hostname Let's Encrypt issues the cert for and that callers use to reach
  the API.
- `StoreName` — defaults to `My`; omit unless you need a different store.
- `StoreLocation` — defaults to `LocalMachine`; omit unless you need
  `CurrentUser` (not recommended for a service).
- `RefreshInterval` — `TimeSpan` (`hh:mm:ss`); defaults to `00:05:00`.

### Example: environment-variable overrides

For production, the keys above are typically supplied via environment
variables (double-underscore separator, matching the existing
`ApiKey__Keys__0` convention):

```
Kestrel__Certificate__Subject=api.example.no
Kestrel__Certificate__StoreName=My
Kestrel__Certificate__StoreLocation=LocalMachine
Kestrel__Certificate__RefreshInterval=00:05:00
```

### Development

`appsettings.Development.json` does **not** set `Kestrel:Certificate`. On
Linux the certificate-store feature is inert and the dev loop keeps its
existing plain-HTTP behaviour on `5199`.

## Security

- The cert's **private key ACL** must grant read access to the service
  account that runs `SmartLockoutApi.exe`. This is set in `certlm.msc` →
  *Manage Private Keys*. The app cannot grant itself this access and will
  fail fast if it cannot read the key.
- Thumbprints are not secrets; they may appear in logs.
- HSTS should be enabled in non-Development environments.
- The store is opened **read-only**; the app never installs, modifies, or
  deletes certificates.
- This is a Windows-only feature. On non-Windows hosts the feature is
  inert — matching the existing AD FS / AD module Windows-only constraint.

## Runtime / environment requirements

- The certificate is installed in `LocalMachine\My` on the target Windows
  host with its private key.
- The service account has read access to that private key.
- `win-acme` is installed on the host and configured with a **DNS provider
  plugin** + API credentials for the zone that hosts the cert's subject —
  this is what the DNS-01 challenge requires. The app itself has no
  dependency on those credentials.
- The host needs no inbound port-80 reachability for ACME (DNS-01).
- (If chain validation is enforced by clients) intermediate CAs are present
  in `LocalMachine\CA` and the root in `LocalMachine\Root`. The app does
  not manage these.

## Error handling

- All startup-time certificate errors surface as fail-fast exceptions with
  log lines that name **thumbprint, store, location, and the specific
  failure cause**. They do **not** become 500s on the first request.
- Runtime TLS handshake failures are logged by Kestrel as warnings; no
  custom handling is required beyond standard ASP.NET Core behaviour.

## Acceptance criteria

- [ ] With a valid cert installed in `LocalMachine\My` whose subject matches
      configuration, the API serves HTTPS using that certificate.
- [ ] An unknown subject (no matching cert) produces a startup failure naming
      the subject and store, and the process does not begin serving requests.
- [ ] An expired (or not-yet-valid) matching cert is **ignored** in favour of
      another valid match; if no valid match exists, startup fails naming the
      validity dates of the rejected cert.
- [ ] A cert whose private key the service account cannot read is ignored;
      if it is the only match, startup fails naming the ACL problem.
- [ ] When two matching certs are present (renewal overlap), the one with
      the **latest `NotBefore`** is served.
- [ ] **Live reload:** installing a newer matching cert into the store while
      the service is running causes new TLS connections to use the new cert
      within one refresh interval, **without a restart** and without
      dropping in-flight connections. A swap log line at Information is
      emitted with old/new thumbprints.
- [ ] A failed refresh (transient store-read error) keeps the previous cert
      in use and logs a Warning; the service does not stop serving.
- [ ] The existing `X-API-Key` filter still runs on every HTTPS request.
- [ ] Connections negotiating TLS < 1.2 are refused.
- [ ] Development behaviour on Linux is unchanged (the feature is inert when
      no Windows cert store is available).

## Risks & open questions

1. ~~**ACME challenge type:**~~ **Resolved** — **DNS-01**. `win-acme` will
   publish the `_acme-challenge` TXT record via the configured DNS provider
   API; no inbound HTTP/port-80 is needed on the host.
2. ~~**Refresh interval:**~~ **Resolved** — **5 minutes** (configurable
   via `Kestrel:Certificate:RefreshInterval`). Best-practice default for
   this class of solution: short enough that a renewal is picked up
   essentially immediately, long enough that the store-read cost is
   negligible. LE renewals happen on the order of every ~60 days, so the
   exact cadence is not load-sensitive.
3. ~~**HTTP behaviour during ACME:**~~ **Resolved** — moot under DNS-01;
   `win-acme` needs no listener on port 80. The plain-HTTP decision in §5
   is now purely an app-level concern.
4. ~~**Cert pinning override:**~~ **Resolved** — **no thumbprint pin**.
   YAGNI: emergency rollback is handled at the store level (operator
   removes the bad cert or installs the previous one; the selector then
   picks the latest valid `NotBefore`). Keeping the app subject-only avoids
   a second selection path that has to be kept in sync.
5. ~~**Renewal overlap behaviour:**~~ **Resolved** — **app is read-only**.
   It never deletes or archives certs. Cert-store hygiene is `win-acme` /
   ops responsibility. The selector simply ignores older / invalid /
   no-private-key matches and serves the best valid candidate.
6. ~~**Deployment topology:**~~ **Resolved** — **no TLS offloading**.
   Kestrel is the TLS terminator end-to-end; no reverse proxy / load balancer
   sits in front decrypting traffic. Consequences: `ForwardedHeaders`
   middleware is **not** required, and the caller IP recorded in
   `AUDIT update-ad-phone` / `AUDIT reset-lockout` lines remains the real
   remote address from `HttpContext.Connection.RemoteIpAddress`.
7. ~~**Configuration shape:**~~ **Resolved** — **custom
   `Kestrel:Certificate` section** (`Subject`, `StoreName`, `StoreLocation`,
   `RefreshInterval`) driving a `ServerCertificateSelector` + an
   `IHostedService` background refresher. The built-in
   `Kestrel:Endpoints:*:Certificate` schema is rejected because it resolves
   once at bind time and offers no live-reload path; layering both would
   create two configuration shapes to keep in sync. One config section,
   one selector, one refresher is the cleaner conventional pattern.
8. ~~**EKU enforcement:**~~ **Resolved** — **strict**. Startup rejects a
   cert lacking the `Server Authentication` EKU (`1.3.6.1.5.5.7.3.1`).
   Let's Encrypt always sets this EKU, so a missing EKU indicates the
   wrong cert was selected and failing fast is correct.
9. ~~**Renewal monitoring:**~~ **Resolved** — **no in-app expiry warning**.
   Monitoring `NotAfter` and alerting on impending expiry is ops monitoring's
   responsibility, not the app's.
10. ~~**Port number(s):**~~ **Resolved** — **HTTPS on `5199`**. The existing
    port is repurposed for TLS (no separate plain-HTTP listener implied; see
    open §5 for the plain-HTTP behaviour decision).
11. ~~**Mutual TLS follow-up:**~~ **Resolved (out of scope)** — not part of
    this feature. If/when it is added later, it will reuse the same
    cert-store loading code path.
