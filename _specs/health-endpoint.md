# Health Endpoint

> Note: this repo has no `_specs/template.md`, so this spec follows the same
> structure as the other specs in `_specs/`. No code examples are included,
> per the spec workflow.

## Summary

Add a tiny **`GET /health`** endpoint that returns `200 Healthy` when the
process is responding and `503 Unhealthy` if a registered health-check
indicates a problem. Intended for monitors, load balancers, and the SCM —
**not** for AD FS or AD diagnostics.

The endpoint is **unauthenticated** (no `X-API-Key` required), and access
logs for `/health` are **suppressed** to avoid flooding the Event Log under
high-frequency polling.

## Background / context

The service currently has no health-check endpoint. External monitors and
load balancers must rely on TCP-port connectivity to know if the service is
up, which conflates "Kestrel is listening" with "the app is in a usable
state." A standard ASP.NET Core health-check endpoint is a near-zero-cost
addition that makes the service play nicely with the surrounding ops tooling.

Per `CLAUDE.md`, new endpoints must be scrutinized — but `/health` is a
**read-only**, **state-free**, **PowerShell-free** endpoint that reveals no
sensitive information, so it does not widen the scope concerns recorded in
that document.

## Goals

- Provide a stable, well-known URL (`/health`) that returns 200 when the
  process is up and able to serve requests.
- Use the **built-in `Microsoft.AspNetCore.Diagnostics.HealthChecks`**
  middleware. No third-party packages, no custom abstractions.
- Keep `/health` **unauthenticated** so monitors / load balancers / the SCM
  do not need to know the API key.
- **Suppress access logs** for `/health` so high-frequency polling does not
  fill the Application event log.
- Return responses fast (<10 ms) and with a fixed, small body.

## Non-goals

- **No AD FS / AD reachability probing** in this endpoint. The current
  state-changing endpoints already fail fast at process startup if their
  PowerShell module cannot be imported; a separate `/ready` endpoint
  covering live dependency probes is a possible later spec, not part of
  this one.
- No TLS-certificate freshness check inside `/health`. The
  `WindowsStoreCertificateProvider` already fails fast at startup and the
  background refresher logs `TLS certificate swapped` on renewals.
- No detailed health JSON, no per-check breakdown, no Prometheus
  exposition format. Plain text + status code only.
- No metrics endpoint.
- No `/livez` or `/readyz` alongside; keep the surface area at exactly one
  endpoint until the need for split liveness/readiness is real.

## Functional requirements

1. **Endpoint shape**
   - `GET /health`
   - No request body, no query parameters honoured.
   - Plain-text response. Built-in middleware default: body is the literal
     `Healthy` or `Unhealthy`; this is acceptable.

2. **Responses**
   - **200 OK** with body `Healthy` when the process is up and any
     registered checks pass.
   - **503 Service Unavailable** with body `Unhealthy` if a registered
     check fails.
   - No JSON. No additional headers beyond what Kestrel adds.

3. **Initial check set**
   - **A single "process is up" check** that always reports healthy as long
     as it can execute. This satisfies the request "healthy if up,
     unhealthy if down" — when the process is down it cannot respond at
     all, which a monitor will observe as a connection failure (effectively
     "unhealthy") without any 503 needed.
   - The check infrastructure is registered such that adding deeper checks
     later is a one-liner without an API contract change.

4. **Auth bypass**
   - `/health` is **not** gated by `ApiKeyEndpointFilter`. It returns 200
     without an `X-API-Key` header.
   - It is still served over the same Kestrel HTTPS listener on 5199 — TLS
     applies (no plain-HTTP alternative).

5. **Log suppression**
   - The standard request/response log line for `/health` must be silenced
     at Information; otherwise polling every 5 seconds creates ~17 280
     access-log entries per day in the Application event log.
   - Audit log markers (`AUDIT reset-lockout`, `AUDIT update-ad-phone`)
     and warnings (cert refresh failures) must continue to log normally —
     suppression is scoped to the `/health` request, not to all logging.

6. **OpenAPI / Swagger**
   - The endpoint appears in Swagger (Development only, per existing
     convention) so the contract is discoverable, but is not gated by the
     API-key security definition.

## Auth & security

- The endpoint exposes only a fixed status string. No request data is
  reflected; no internal state is leaked.
- Unauthenticated by design — see *Functional requirements §4*.
- TLS still applies (the listener is HTTPS-only on 5199), so callers
  connect via `https://<hostname>:5199/health`.
- A successful 200 does not imply that AD FS / AD calls would succeed — it
  only implies "process is up." Operators consuming this endpoint should
  set their alerting expectations accordingly.

## Operational behaviour

- The endpoint must continue to respond 200 even when:
  - the AD FS server is temporarily unreachable;
  - the cert refresh background task has logged a Warning;
  - the `ApiKey:Keys` configuration is empty (the current 401-everywhere
    fail-closed mode still applies to the real endpoints, but `/health`
    stays open).
- The endpoint must return 503 only when a future deeper check is
  explicitly registered to fail under a defined condition. Today there is
  no such check.

## Acceptance criteria

- [ ] `curl -ik https://127.0.0.1:5199/health` on the production host
      returns `HTTP/1.1 200 OK` with body `Healthy` and no `X-API-Key`.
- [ ] The same call **without** TLS verification but with a wrong / missing
      API key also returns 200 — `/health` is unauthenticated.
- [ ] Polling `/health` once per second for 60 s does **not** produce 60
      access-log entries in the Application event log.
- [ ] All other endpoints (`/api/adfs/...`, `/api/ad/user/...`) still
      require `X-API-Key` and return 401 without one.
- [ ] Swagger UI (Development) lists `/health` and does not show a lock
      icon next to it.
- [ ] On Linux dev (`dotnet run`), `curl http://localhost:5140/health`
      returns `200 Healthy`.

## Risks & open questions

1. ~~**Log suppression scope:**~~ **Resolved** — implemented as a
   **provider-scoped** filter: `Microsoft.AspNetCore.Hosting.Diagnostics` is
   demoted to `Warning` **only in the EventLog provider**. Console output
   keeps the framework's `Request starting / Request finished` lines, which
   is useful when running interactively. The acceptance criterion is about
   the Application event log specifically, so this satisfies it with zero
   custom code.

   A true path-scoped filter would need a custom `ILoggerProvider` wrapper
   (~30 lines) because the `Microsoft.Extensions.Logging` filter API is
   category+level, not path. The provider-scoped variant is what production
   .NET 8 services without Serilog most commonly do. Warning- and
   Error-level framework logs continue to flow on every provider, so real
   failures during `/health` processing remain visible everywhere.
2. ~~**No-op check or no checks at all?**~~ **Resolved** — register a
   single named **`self`** check that always returns Healthy. ASP.NET Core
   would respond 200 to an empty check set, but an explicit named check is
   the documented MS-samples pattern: it makes the intent obvious in code
   and gives future deeper checks a co-located place to live.
3. ~~**Path collision:**~~ **Resolved** — `/health` does not collide with
   any current route (everything else is under `/api/adfs/...` or
   `/api/ad/...`). Confirmed safe.
4. ~~**Future `/ready`:**~~ **Resolved (out of scope)** — split liveness
   (`/health`) and readiness (`/ready` or `/health/ready`) is the
   recommended pattern when readiness probing is actually needed. Not part
   of this feature; if/when added later it will be a separate endpoint
   with its own auth-posture decision.
