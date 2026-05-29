# SmartLockoutApi

Internal .NET 8 Web API that lets non-AD FS hosts call a small, scrutinized set
of AD FS and Active Directory PowerShell cmdlets:

- AD FS Extranet Smart Lockout — read and reset.
- AD user phone numbers — read and update (`mobile`, `telephoneNumber`).

The point of the project is to expose **only** those operations, behind an API
key and TLS, so helpdesk tooling does not need direct PowerShell access to the
AD FS / domain controllers.

## Endpoints

| Method | Path                                   | Effect |
|--------|----------------------------------------|--------|
| GET    | `/health`                              | Readiness probe — 200 `Healthy` when AD FS, AD, and the TLS cert all check out. **Unauthenticated.** |
| GET    | `/api/adfs/smart-lockout/{upn}`        | `Get-AdfsAccountActivity -Identity <upn>` |
| POST   | `/api/adfs/smart-lockout/{upn}/reset`  | `Reset-AdfsAccountLockout -Identity <upn>` (204 on success; AUDIT logged) |
| GET    | `/api/ad/user/{upn}/phone`             | `Get-ADUser` returning `mobile` + `telephoneNumber` |
| PATCH  | `/api/ad/user/{upn}/phone`             | `Set-ADUser` — partial update; AUDIT logged |

All endpoints **except `/health`** are gated by an `X-API-Key` header
(see §4.3). State-changing endpoints emit an `AUDIT` log line on every
attempt with caller IP, target UPN, and what changed.

Status codes (consistent across endpoints):

| Code | When                                                              |
|------|-------------------------------------------------------------------|
| 200  | Read endpoint, record returned                                    |
| 204  | State-changing endpoint succeeded (reset / patch)                 |
| 400  | UPN failed validation, or PATCH body had no actionable fields, or phone number is not a valid Norwegian number |
| 401  | Missing / wrong / no-keys-configured API key                      |
| 404  | No matching AD FS activity record or AD user                      |
| 500  | PowerShell call failed (module missing, AD unreachable, etc.)     |

Smart-lockout 200 response (camelCase JSON):

```json
{
  "userPrincipalName": "user@contoso.com",
  "isLockedOut": false,
  "familiarLockout": false,
  "unknownLockout": false,
  "badPwdCountFamiliar": 0,
  "badPwdCountUnknown": 0,
  "lastFailedAuthFamiliar": null,
  "lastFailedAuthUnknown": null,
  "familiarIps": []
}
```

Phone read 200 response:

```json
{ "userPrincipalName": "user@contoso.com", "mobile": "+4791234567", "telephoneNumber": "+4722000000" }
```

Phone PATCH body — `null`/omitted = leave unchanged, `""` = clear the
attribute in AD, non-empty = set (must be a Norwegian number, normalized to
canonical E.164 before storage):

```json
{ "mobile": "+47 912 34 567", "telephoneNumber": "" }
```

---

## Examples (curl)

Capture these once so the snippets stay readable. From the production AD FS
host these run against `https://<hostname>:5199`; for loopback testing on
that host, swap to `https://127.0.0.1:5199` and add `-k` (the LE cert is
issued for `<hostname>`, not the loopback).

```bash
# bash / zsh
HOST=api.example.no
KEY='<api-key>'
UPN=alice@example.com
```

```powershell
# PowerShell
$HOST = 'api.example.no'
$KEY  = '<api-key>'
$UPN  = 'alice@example.com'
```

### Readiness probe (unauthenticated)

```bash
curl -i https://$HOST:5199/health
```

```
HTTP/1.1 200 OK
Content-Type: text/plain

Healthy
```

Wired into monitors / load balancers / the SCM. Returns:

- `200 Healthy` when AD FS (`Get-AdfsProperties`), AD (`Get-ADRootDSE`),
  and the TLS cert all pass.
- `200 Degraded` when the TLS cert expires within the next 14 days (early
  warning; the service is still functional).
- `503 Unhealthy` when any check fails or times out (3 s per PowerShell
  probe).

Results are cached for 30 s, so a monitor polling once per second still
produces at most one real AD / AD FS probe every half-minute. The
endpoint is filtered out of the Event Log access trail.

### Read AD FS smart-lockout state

```bash
curl -s -H "X-API-Key: $KEY" https://$HOST:5199/api/adfs/smart-lockout/$UPN
```

```json
{
  "userPrincipalName": "alice@example.com",
  "isLockedOut": false,
  "familiarLockout": false,
  "unknownLockout": false,
  "badPwdCountFamiliar": 0,
  "badPwdCountUnknown": 0,
  "lastFailedAuthFamiliar": null,
  "lastFailedAuthUnknown": null,
  "familiarIps": []
}
```

### Reset a smart-lockout

```bash
curl -i -X POST -H "X-API-Key: $KEY" \
  https://$HOST:5199/api/adfs/smart-lockout/$UPN/reset
```

Expect `HTTP/1.1 204 No Content`. An `AUDIT reset-lockout` line is written
to the Event Log on every attempt.

### Read phone numbers

```bash
curl -s -H "X-API-Key: $KEY" https://$HOST:5199/api/ad/user/$UPN/phone
```

```json
{ "userPrincipalName": "alice@example.com", "mobile": "+4791234567", "telephoneNumber": "+4722000000" }
```

`null` for either field means the attribute is unset in AD.

### Set the mobile number, leave the office number unchanged

```bash
curl -i -X PATCH -H "X-API-Key: $KEY" -H "Content-Type: application/json" \
  -d '{"mobile":"+47 912 34 567"}' \
  https://$HOST:5199/api/ad/user/$UPN/phone
```

Expect 204. The submitted `+47 912 34 567` is normalized to `+4791234567`
in AD.

### Clear the office number

```bash
curl -i -X PATCH -H "X-API-Key: $KEY" -H "Content-Type: application/json" \
  -d '{"telephoneNumber":""}' \
  https://$HOST:5199/api/ad/user/$UPN/phone
```

### Set both fields at once

```bash
curl -i -X PATCH -H "X-API-Key: $KEY" -H "Content-Type: application/json" \
  -d '{"mobile":"+4791234567","telephoneNumber":"+4722000000"}' \
  https://$HOST:5199/api/ad/user/$UPN/phone
```

### Error paths (no AD round-trip needed)

```bash
# 401 — missing API key
curl -i https://$HOST:5199/api/adfs/smart-lockout/$UPN

# 400 — invalid UPN format
curl -i -H "X-API-Key: $KEY" https://$HOST:5199/api/adfs/smart-lockout/not-a-upn

# 400 — phone not a valid Norwegian number
curl -i -X PATCH -H "X-API-Key: $KEY" -H "Content-Type: application/json" \
  -d '{"mobile":"+4612345678"}' \
  https://$HOST:5199/api/ad/user/$UPN/phone

# 400 — PATCH body has no actionable fields
curl -i -X PATCH -H "X-API-Key: $KEY" -H "Content-Type: application/json" \
  -d '{}' \
  https://$HOST:5199/api/ad/user/$UPN/phone

# 404 — UPN does not exist in AD
curl -i -H "X-API-Key: $KEY" https://$HOST:5199/api/ad/user/nobody@example.com/phone
```

---

## 1. Dev setup

The app builds on Windows, Linux, or macOS. The AD FS- and AD-backed paths
(200/404) only work when running on a Windows host with the relevant
PowerShell modules importable:

- `ADFS` module (an AD FS server itself) — for the smart-lockout endpoints.
- `ActiveDirectory` module (AD RSAT or a DC) — for the phone endpoints.

On other hosts those endpoints return 500 because the module import fails.
The API-key / validation paths still return 401 / 400 correctly anywhere.

Prerequisites:

- **.NET 8 SDK** — install from <https://dot.net/download>. Verify:
  ```
  dotnet --version       # → 8.0.x (or newer SDK that supports net8.0)
  ```
- A code editor with C# support (VS Code + C# Dev Kit, Rider, or Visual
  Studio 2022 17.8+).

Clone and restore:

```
git clone <repo-url> smart-lockout-api
cd smart-lockout-api
dotnet restore
```

---

## 2. Build and run locally

```
dotnet build            # → bin/Debug/net8.0/SmartLockoutApi.dll
dotnet run              # starts Kestrel on the URLs in Properties/launchSettings.json
```

Defaults from `launchSettings.json` (Development only — production uses
HTTPS on 5199, see §4.4):
- HTTP:  `http://localhost:5140`
- HTTPS: `https://localhost:7228` (uses ASP.NET Core's dev cert)
- Environment: `Development`

Swagger UI is enabled in `Development` only:

```
http://localhost:5140/swagger
```

Smoke-test the auth and validation paths from any host (the AD-backed
200/404 paths only work on a real AD FS host — see §4):

```
# 401 — no API key
curl -i http://localhost:5140/api/adfs/smart-lockout/not-a-upn

# 400 — auth passes, UPN fails validation
curl -i -H "X-API-Key: dev-only-do-not-use-in-prod" \
  http://localhost:5140/api/adfs/smart-lockout/not-a-upn

# 400 — invalid phone number on PATCH
curl -i -H "X-API-Key: dev-only-do-not-use-in-prod" -H "Content-Type: application/json" \
  -X PATCH -d '{"mobile":"+4612345678"}' \
  http://localhost:5140/api/ad/user/alice@example.com/phone

# 500 on non-AD FS hosts (module import fails); 200/404 on a real AD FS host
curl -i -H "X-API-Key: dev-only-do-not-use-in-prod" \
  http://localhost:5140/api/adfs/smart-lockout/alice@example.com
```

The dev key `dev-only-do-not-use-in-prod` lives in
`appsettings.Development.json` and is intentionally non-secret. Production
keys must come from environment variables — see §4.3.

---

## 3. Publish a self-contained Windows executable

Cross-publish from any OS that has the .NET 8 SDK:

```
dotnet publish -c Release -p:PublishSingleFile=true -o publish
```

This produces `publish/` (~66 MB total):

| Path                                | Size   | Purpose                                                          |
|-------------------------------------|--------|------------------------------------------------------------------|
| `SmartLockoutApi.exe`               | ~63 MB | Self-contained, compressed, win-x64. .NET 8 runtime + app + PS SDK. PDB embedded. |
| `runtimes/`                         | ~350 KB | PowerShell built-in module manifests. PS engine loads them from disk next to the exe; cannot be bundled into the exe. **Do not delete.** |
| `appsettings.json` (+ Development)  | <1 KB  | Editable on the target host.                                     |
| `web.config`                        | 472 B  | Web SDK artifact for IIS. Harmless when running standalone; delete if you like. |

If you need a single deliverable, **zip the `publish/` folder**:

```
# Windows PowerShell
Compress-Archive -Path publish\* -DestinationPath SmartLockoutApi.zip

# Linux/macOS
(cd publish && zip -r ../SmartLockoutApi.zip .)
```

Notes on the publish profile (configured in `SmartLockoutApi.csproj`):

- `PublishTrimmed` is **off** — Microsoft.PowerShell.SDK uses heavy reflection
  and breaks under trimming.
- `IL3000`–`IL3003` warnings are suppressed only when publishing; they come
  from PS SDK calling `Assembly.Location` and are known/benign.
- Compression is on (`EnableCompressionInSingleFile=true`); first-run startup
  pays a small decompression cost.

The `deploy/` folder ships with the repo and contains `Setup-ServiceAccount.ps1`
(see §4.5). It is not part of `publish/`; copy it separately if you want it on
the target host.

---

## 4. Install on the AD FS server

> **Full step-by-step guide:** [`deploy/DEPLOY.md`](deploy/DEPLOY.md).
> The sections below summarise the moving parts; the guide is the canonical,
> command-by-command walk-through (DNS prerequisites, win-acme issuance,
> service-account setup, smoke tests, troubleshooting).

Because the service uses both the `ADFS` and `ActiveDirectory` PowerShell
modules, run it on the AD FS server itself. The deploy unit is the `publish/`
folder; TLS is terminated in-process on **port 5199** using a Let's Encrypt
certificate from the Windows certificate store (no reverse proxy required).

### 4.1. Prerequisites on the AD FS server

- Windows Server with the **AD FS** role.
- PowerShell modules `ADFS` and `ActiveDirectory` must be importable.
  Quick check from an admin PowerShell on the target:

  ```powershell
  Import-Module ADFS;            Get-Command Get-AdfsAccountActivity
  Import-Module ActiveDirectory; Get-Command Get-ADUser
  ```

- Extranet Smart Lockout enabled in the farm
  (`Set-AdfsProperties -EnableExtranetLockout $true`).
- `win-acme` (or equivalent) installed and configured with a **DNS provider
  plugin** + zone API credentials. Issuance and renewal use **DNS-01**, so
  the AD FS host does **not** need inbound port-80 reachability.
- A service account in AD that will run the API process (see §4.5 for the
  permissions it needs — the included PowerShell script grants them).

### 4.2. Copy the files

Pick a stable install path, for example `C:\Program Files\SmartLockoutApi\`,
and copy the entire **contents of `publish/`** there:

```
C:\Program Files\SmartLockoutApi\
├── SmartLockoutApi.exe
├── runtimes\           ← keep alongside the exe
├── appsettings.json
├── appsettings.Development.json
└── web.config          (optional; delete if not using IIS)
```

### 4.3. Configure the API key

Production keys come from environment variables, never from tracked config
files. `ApiKey:Keys` is read as a .NET configuration array, so the keys
live at `ApiKey__Keys__0`, `ApiKey__Keys__1`, etc. — `__1` is only used
during rotation.

Generate a key (any high-entropy string works):

```powershell
$bytes = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
[Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
```

Set it machine-wide so the service inherits it:

```powershell
[Environment]::SetEnvironmentVariable("ApiKey__Keys__0", "<paste-key-here>", "Machine")
```

If no key is configured the API fails closed (401 on every request, plus
a startup warning).

**Rotation** (zero-downtime): set `ApiKey__Keys__1` to a new key, restart,
switch the consumer over, then clear `ApiKey__Keys__0` and restart again.

### 4.4. Configure TLS (Kestrel + Windows cert store + Let's Encrypt)

The API terminates TLS itself on **port 5199** using a certificate from
`LocalMachine\My`, selected by **subject** (CN/SAN DnsName). When `win-acme`
installs a renewed cert (same subject, new thumbprint), the running process
swaps to the new cert within the refresh interval — **no restart, no
in-flight connection drop**. Search logs for `TLS certificate swapped`.

Required configuration (machine environment variables **or**
`appsettings.Production.json` — see `appsettings.Production.json.example`
for the file shape; secrets like `ApiKey:Keys` should stay env-var-only):

```powershell
[Environment]::SetEnvironmentVariable("Kestrel__Certificate__Subject", "api.example.no", "Machine")
# Defaults — set only if non-default:
# Kestrel__Certificate__StoreName       = "My"
# Kestrel__Certificate__StoreLocation   = "LocalMachine"
# Kestrel__Certificate__RefreshInterval = "00:05:00"
```

**Do NOT** set `ASPNETCORE_URLS` in production. The app binds HTTPS on 5199
explicitly via `ConfigureKestrel`; setting `ASPNETCORE_URLS` will add an
extra (likely plain-HTTP) listener.

The cert-store branch is gated on Windows + a non-empty `Subject`, so on a
Linux dev box it stays inert and `dotnet run` keeps its launchSettings HTTP
binding.

Startup will fail fast with a clear message if the cert is missing, expired,
not yet valid, missing the `Server Authentication` EKU, or its private key
is not readable by the service account. Transient store-read failures
during a later refresh log a Warning and keep the previous cert in use.

For the full TLS design, see `_specs/server-tls-cert-from-windows-store.md`.

Swagger UI is **off** in production by default. To turn it on temporarily for
debugging, set `Swagger__Enabled=true` (machine env var), restart the service,
then clear the env var and restart again when you're done. This replaces the
older trick of running with `ASPNETCORE_ENVIRONMENT=Development` in
production. Note: when Swagger is enabled, the **Authorize** button stashes
the entered API key in browser `localStorage` — clear the browser session
after debugging.

### 4.5. Service account: required permissions

The service account needs six things; the included
`deploy/Setup-ServiceAccount.ps1` configures them idempotently:

| Permission / setup                                             | Why |
|----------------------------------------------------------------|-----|
| `SeServiceLogonRight` ("Log on as a service")                  | SCM can start the process as the account. |
| Local Administrators on the AD FS server                       | Required by `Get-AdfsAccountActivity` / `Reset-AdfsAccountLockout`. |
| Read on the TLS cert's private-key file in `LocalMachine\My`   | Kestrel can read the LE key to serve HTTPS. |
| Inbound TCP 5199 (Windows Firewall, Domain profile)            | Callers can reach the listener. |
| `RPWP` on `mobile` + `telephoneNumber` for users in the target OU | `Set-ADUser -MobilePhone`/`-OfficePhone` works **without** Domain Admin / Account Operators. |
| Event Log source `SmartLockoutApi` registered in the Application log | The service can write to the Event Log without admin rights at runtime. |

Run the script from an elevated PowerShell on the AD FS server:

```powershell
.\deploy\Setup-ServiceAccount.ps1 `
    -ServiceAccount 'EXAMPLE\svc-smartlockout' `
    -CertSubject 'api.example.no' `
    -UserOU 'OU=Users,DC=example,DC=com' `
    -BinaryPath 'C:\Program Files\SmartLockoutApi\SmartLockoutApi.exe' `
    -InstallService
```

Re-running the script is safe; each step detects whether it has already been
done and skips. `-InstallService` is optional — omit it to install the
Windows service manually (§4.6).

### 4.6. Install as a Windows Service (manual alternative)

If you skipped `-InstallService` in §4.5:

```powershell
$exe = "C:\Program Files\SmartLockoutApi\SmartLockoutApi.exe"

New-Service -Name 'SmartLockoutApi' `
            -BinaryPathName "`"$exe`"" `
            -DisplayName 'AD FS Smart Lockout API' `
            -Description 'Internal API: AD FS smart lockout + AD phone-number management.' `
            -StartupType Automatic `
            -Credential (Get-Credential -UserName 'EXAMPLE\svc-smartlockout' -Message 'Service account password')

Start-Service SmartLockoutApi
Get-Service  SmartLockoutApi
```

Validate:

```powershell
curl -ik -H "X-API-Key: <your-key>" `
    https://127.0.0.1:5199/api/adfs/smart-lockout/realuser@contoso.com
```

`-k` because the LE cert is for the public subject, not the loopback. Expect
200 with the activity record, 404 if AD FS has never seen the UPN, 401 if the
key is wrong, or 500 with "Access is denied" if the service account lacks AD
FS admin rights.

First run extracts native libs to `%TEMP%\.net\SmartLockoutApi\<hash>\`. If
`%TEMP%` is locked down, point the extractor elsewhere via a machine env var:

```powershell
[Environment]::SetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR", `
  "C:\ProgramData\SmartLockoutApi\bundle", "Machine")
```

### 4.7. Logs

In production on Windows (non-Development environment) the app writes
logs to the **Windows Event Log** under source `SmartLockoutApi` in the
Application log, in addition to stdout. The event source is registered
once by the setup script (§4.5); the service account does not need to
self-register at runtime. View with Event Viewer or `Get-WinEvent`:

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'SmartLockoutApi' } -MaxEvents 50 |
    Select-Object TimeCreated, LevelDisplayName, Message
```

When running interactively (`dotnet run` on a dev box, or
`SmartLockoutApi.exe` from a console) the EventLog provider is **not**
loaded — Development is excluded from the provider gate, and stdout is
the only sink.

State-changing endpoints emit greppable audit lines:

- `AUDIT reset-lockout` — smart-lockout reset attempts.
- `AUDIT update-ad-phone` — phone-number updates (field names only, never values).
- `TLS certificate swapped` — live cert reload after `win-acme` renewal.

### 4.8. Uninstall

```powershell
Stop-Service SmartLockoutApi
sc.exe delete SmartLockoutApi
Remove-Item "C:\Program Files\SmartLockoutApi" -Recurse -Force
Remove-Item "C:\ProgramData\SmartLockoutApi" -Recurse -Force  # if DOTNET_BUNDLE_EXTRACT_BASE_DIR was set
```

The Windows Firewall rule, the AD delegation, the cert ACL, and the local
Administrators membership are **not** removed automatically — clean those
up with `Remove-NetFirewallRule`, `dsacls /R`, `certlm.msc`, and
`Remove-LocalGroupMember` as required.

---

## Security

- **API key auth.** Every request must carry `X-API-Key` matching an entry
  in `ApiKey:Keys`. Keys are compared with
  `CryptographicOperations.FixedTimeEquals`; missing/wrong/empty keys all
  return `401` with `WWW-Authenticate: ApiKey`. When no keys are configured
  the app fails closed (401 on every request, plus a startup warning).
  Keys never appear in logs.
- **TLS** is terminated in-process by Kestrel on port 5199 using a Let's
  Encrypt cert from the Windows certificate store. The cert is rotated by
  `win-acme` and picked up live by the API — see §4.4 and the spec at
  `_specs/server-tls-cert-from-windows-store.md`. TLS protocol floor is
  **1.2** (1.3 preferred when supported); older protocols are refused.
- UPN input is validated against a strict regex and length cap. PowerShell
  calls pass user input as typed parameters
  (`.AddParameter("Identity", upn)`) or via an LDAP filter with an LDAP-
  escaped value; nothing is interpolated into a script string. Validators
  are defense-in-depth.
- Phone numbers must be valid Norwegian numbers (8 national digits, optional
  `+47` / `0047` prefix), normalized to canonical E.164 (`+47XXXXXXXX`)
  before being written to AD.
- State-changing endpoints (`POST /reset`, `PATCH /phone`) are gated by the
  same shared API key as the read endpoints. The accepted compensating
  control is audit logging (`AUDIT reset-lockout`, `AUDIT update-ad-phone`).
  If/when a per-caller identity becomes necessary, the next step is Windows
  Auth (Negotiate/Kerberos) or Microsoft.Identity.Web for Entra ID — keep
  the surface area small until then.
- **Swagger UI is off in production by default** (`Swagger:Enabled = false`)
  and only turned on for transient debugging via `Swagger__Enabled=true` +
  restart. When enabled, the **Authorize** button stores the entered API
  key in browser `localStorage`; clear the browser session (or use a
  private window) after debugging so the key does not persist on the
  operator's machine.
