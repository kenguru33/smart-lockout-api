# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A small internal .NET 8 Web API that exposes AD FS Extranet Smart Lockout state
for a given UPN, plus a way to clear it. The endpoints are:

- `GET  /api/adfs/smart-lockout/{upn}`       → wraps `Get-AdfsAccountActivity -Identity <upn>`.
- `POST /api/adfs/smart-lockout/{upn}/reset` → wraps `Reset-AdfsAccountLockout -Identity <upn>`. Returns 204 on success. Every attempt is logged at Information with caller IP and target UPN (search logs for `AUDIT reset-lockout`).

It is **not** a general-purpose API. New endpoints should be scrutinized — the
project's reason for existing is to keep these AD FS PowerShell calls accessible
from non-AD FS hosts without granting broader access.

## Hard runtime requirement

The process must run on a Windows host where the `ADFS` PowerShell module is
importable (an AD FS server itself, or an admin host with AD FS RSAT). There
is no mock — every environment, including Development, calls real PowerShell.

## Commands

```
dotnet restore           # restore NuGet packages
dotnet build             # build, with TreatWarningsAsErrors=true
dotnet run               # run; Swagger at /swagger only in Development
```

Smoke-test (the 200 path requires running on an AD FS host). The deployed
binary on Windows serves HTTPS on 5199 (see *TLS*); curl with `-k` against
`127.0.0.1` because the cert is for the public subject, not the loopback:

```
curl -ik https://127.0.0.1:5199/api/adfs/smart-lockout/not-a-upn          # → 400
curl -ik https://127.0.0.1:5199/api/adfs/smart-lockout/user@example.com   # → 500 on non-AD FS host
```

On a Linux dev box the cert-store branch is inert; `dotnet run` binds plain
HTTP via launchSettings (`http://localhost:5140`).

No test project exists yet. If adding one, mock `IAdfsSmartLockoutService` for
endpoint tests and leave the PowerShell implementation as integration-tested
on a real AD FS host — `System.Management.Automation` is awkward to mock.

## Architecture

The endpoint in `Program.cs` is intentionally thin. All real work lives behind
`IAdfsSmartLockoutService`:

- `Program.cs` — DI wiring, Swagger (Dev only), and the single `MapGet`. It
  validates the UPN, calls the service, and maps `SmartLockoutResult` to HTTP
  status codes. HTTP-shape concerns stay here.
- `Services/IAdfsSmartLockoutService.cs` — interface.
- `Services/SmartLockoutResult.cs` — closed sum type with three cases:
  `Found(response)`, `NotFound(upn)`, `Error(message)`. The service never
  throws for expected outcomes; it returns one of these.
- `Services/PowerShellAdfsSmartLockoutService.cs` — the only implementation.
  Registered as a singleton so its cached `InitialSessionState` (with the
  `ADFS` module imported) is reused across requests; each request still gets
  a fresh `PowerShell` instance.
- `Validation/UpnValidator.cs` — strict regex + length cap. Source-generated
  regex via `[GeneratedRegex]`.
- `Dtos/SmartLockoutResponse.cs` — the 200 response shape.

### The trust boundary

The PowerShell invocation is the only place this app reaches outside its own
process. **UPN must be passed as a typed parameter** —
`.AddCommand("Get-AdfsAccountActivity").AddParameter("Identity", upn)` — and
never interpolated into a script string. The SDK's parameter binding is the
primary defense against PowerShell injection; `UpnValidator` is
defense-in-depth.

If you add another PS-backed endpoint, follow the same pattern: validate
input → `AddCommand` + `AddParameter` → branch on `HadErrors` and
`Streams.Error[0].CategoryInfo.Category` (`ObjectNotFound` → NotFound; any
other → Error).

## Publishing

```
dotnet publish -c Release -p:PublishSingleFile=true -o publish
```

The publish properties (`SelfContained`, `RuntimeIdentifier=win-x64`,
`EnableCompressionInSingleFile`, etc.) live in a `Condition="'$(PublishSingleFile)' == 'true'"`
PropertyGroup in `SmartLockoutApi.csproj`. They are dormant during `dotnet build` /
`dotnet run` so the dev loop still works on Linux.

The deploy unit is **a folder, not a single file**, despite the name:

- `SmartLockoutApi.exe` (~63 MB) — self-contained, compressed, win-x64.
- `runtimes/` (~350 KB) — PowerShell built-in module manifests. The PS engine
  resolves these from disk next to the exe; this is a Microsoft.PowerShell.SDK
  constraint, not something we can configure away. Do not delete this folder.
- `appsettings.json` (+ Development) and `web.config` — content files.

If a true single-artifact deliverable is required, ship `publish/` as a zip.

Constraints to preserve if you touch the publish config:

- **Never enable `PublishTrimmed`.** PS SDK uses reflection extensively;
  trimming will silently break cmdlet discovery and type loading at runtime.
- `IL3000`–`IL3003` are in `NoWarn` only under the publish condition. They
  come from PS SDK calling `Assembly.Location` and similar; benign without
  trimming.
- First run on the target extracts native libs to
  `%TEMP%\.net\SmartLockoutApi\<hash>\`. Override with
  `DOTNET_BUNDLE_EXTRACT_BASE_DIR` if `%TEMP%` is locked down on the AD FS server.

## Auth

API key in `X-API-Key`, validated by `Validation/ApiKeyEndpointFilter.cs`
against `ApiKey:Keys` from configuration. `FixedTimeEquals` for the
comparison; the filter is registered as a singleton so the byte-array list
is parsed once. Missing/wrong key → 401 with `WWW-Authenticate: ApiKey`.
No keys configured → fail closed (401 on every request, startup warning).

Production keys come from environment variables (`ApiKey__Keys__0`, `__1`
during rotation), never from tracked config files. `appsettings.Development.json`
holds a deliberately-public dev key (`dev-only-do-not-use-in-prod`).

Rotation: add the new key as `__1`, restart, switch the consumer over,
then clear `__0`, restart again. Keys never appear in logs.

## TLS

On Windows, Kestrel terminates HTTPS itself on **port 5199** using a server
cert loaded from `LocalMachine\My` by subject (CN/SAN). No reverse proxy /
TLS offloader is in front. Configuration lives at `Kestrel:Certificate`:

```
Kestrel__Certificate__Subject=api.example.no
Kestrel__Certificate__StoreName=My                  # default
Kestrel__Certificate__StoreLocation=LocalMachine    # default
Kestrel__Certificate__RefreshInterval=00:05:00      # default
```

`Subject` is the only required field; if it is empty (or the OS is not
Windows) the entire branch is skipped, so the Linux dev loop is unaffected.

Operator responsibilities, **not** the app's:
- `win-acme` (or equivalent) installed on the host, configured with a DNS
  provider plugin + zone API credentials, issuing the cert via **DNS-01**.
- Renewed cert lands in `LocalMachine\My`; `win-acme`'s scheduled task drives
  this every ~60 days.
- Service account has read access to the cert's private key
  (`certlm.msc` → Manage Private Keys).
- Do **not** set `ASPNETCORE_URLS` in production — `ConfigureKestrel` binds
  HTTPS on 5199 explicitly.

Selection rules enforced at startup and on every refresh: subject match
(SAN DnsName; CN fallback if no SAN), `NotBefore`..`NotAfter` window, has
`Server Authentication` EKU, has a process-readable private key. When two
matching valid certs exist (renewal overlap), the one with the **latest
`NotBefore`** wins. Any startup failure exits non-zero with a clear
message naming the subject and store.

Live reload: a `ServerCertificateSelector` returns the cached cert; the
`CertificateRefresher` `IHostedService` re-resolves every
`RefreshInterval` (default 5 min). When the resolved thumbprint changes,
the cache is swapped atomically and the next handshake uses the new
cert — no restart, no in-flight drop. Search logs for
`TLS certificate swapped`.

Transient store-read errors during refresh log a Warning and keep the
previous cert in use.

If the consumer story grows (multiple callers, per-caller identity needed)
the next step is Windows Auth (Negotiate/Kerberos) or Microsoft.Identity.Web
for Entra ID. The reset endpoint is currently gated by the same shared
`X-API-Key` as the read endpoint, so anyone with the key can clear any
lockout; audit logging (`AUDIT reset-lockout …`) is the compensating
control. If a per-caller identity is needed, do not add more state-changing
endpoints before doing the auth rework — keep the surface area small until
then.
