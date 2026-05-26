# SmartLockoutApi

Internal .NET 8 Web API that exposes AD FS Extranet Smart Lockout state for a
given user. Wraps the `Get-AdfsAccountActivity` PowerShell cmdlet behind one
HTTP endpoint so helpdesk tools can check lockout status without direct
PowerShell access to the AD FS server.

```
GET /api/adfs/smart-lockout/{upn}
```

| Code | When                                                      |
|------|-----------------------------------------------------------|
| 200  | Activity record returned                                  |
| 400  | UPN failed validation (format, length, illegal chars)     |
| 404  | AD FS has no activity record for the UPN                  |
| 500  | PowerShell call failed (module missing, AD FS error, etc) |

200 response (camelCase JSON):

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

`isLockedOut` is `familiarLockout || unknownLockout`.

---

## 1. Dev setup

Works on Windows, Linux, or macOS. AD FS PowerShell only exists on Windows
with the AD FS role or RSAT — so on Linux/macOS you can build, run, and
exercise the validation paths (400/500), but a real 200 requires a Windows
AD FS host.

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

Defaults from `launchSettings.json`:
- HTTP:  `http://localhost:5140`
- HTTPS: `https://localhost:7228` (uses ASP.NET Core's dev cert)
- Environment: `Development`

Swagger UI is enabled in `Development` only:

```
http://localhost:5140/swagger
```

Smoke-test the validation paths from any host (no AD FS needed):

```
# 400 — invalid UPN
curl -i http://localhost:5140/api/adfs/smart-lockout/not-a-upn

# 500 on non-AD FS host — the AD FS module fails to load
curl -i http://localhost:5140/api/adfs/smart-lockout/test.user@example.com
```

On a real AD FS server, the second call returns 200 (or 404 if the UPN has
no activity record yet).

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

---

## 4. Install on the AD FS server

> **Do not expose this API publicly.** No authentication is wired up yet —
> see the `TODO(auth)` in `Program.cs`. Bind it to a loopback or internal
> interface only.

### 4.1. Prerequisites on the AD FS server

- Windows Server with the **AD FS** role (or the AD FS RSAT feature on a
  management host).
- PowerShell module `ADFS` must be importable. Quick check from an admin
  PowerShell on the target:

  ```powershell
  Import-Module ADFS
  Get-Command Get-AdfsAccountActivity
  ```

  If both succeed, the host is suitable.

- Extranet Smart Lockout must already be enabled in your farm
  (`Set-AdfsProperties -EnableExtranetLockout $true`). Without it,
  `Get-AdfsAccountActivity` still returns a record, but the lockout fields
  will always read `False`.

- The account that runs the API process must have rights to call
  `Get-AdfsAccountActivity`. Members of the **AD FS Administrators** group
  (or local Administrators on a single-server farm) have this. Service
  accounts without those rights will get 500s with an access-denied PS error.

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

### 4.3. Quick run (interactive, for the first test)

Open an **elevated** PowerShell window on the AD FS server (or sign in with
an AD FS Admin account) and run:

```powershell
cd "C:\Program Files\SmartLockoutApi"
.\SmartLockoutApi.exe --urls http://localhost:5000
```

First run extracts native libraries to `%TEMP%\.net\SmartLockoutApi\<hash>\`.
If `%TEMP%` is locked down or AV blocks executables out of temp, point the
extractor at a writable directory before launching:

```powershell
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = "C:\ProgramData\SmartLockoutApi\bundle"
.\SmartLockoutApi.exe --urls http://localhost:5000
```

From another shell on the **same machine**, verify:

```powershell
curl http://localhost:5000/api/adfs/smart-lockout/realuser@contoso.com
```

Expect a 200 with the activity record, or 404 if AD FS has never seen that
UPN. A 500 with "module not found" means the host doesn't have the ADFS PS
module; "Access is denied" means the runtime account lacks AD FS admin
rights.

### 4.4. Install as a Windows Service (recommended for production)

The exe runs as a regular console process today. The `Program.cs` does not
yet call `UseWindowsService()`, so service stop/start signals are handled by
the default console host — sufficient for an internal lookup API, but not
ideal long-term (see the follow-up note below).

Register it as a service from an elevated PowerShell:

```powershell
$exe = "C:\Program Files\SmartLockoutApi\SmartLockoutApi.exe"

New-Service -Name "SmartLockoutApi" `
            -BinaryPathName "`"$exe`" --urls http://localhost:5000" `
            -DisplayName "AD FS Smart Lockout API" `
            -Description "Internal API wrapping Get-AdfsAccountActivity." `
            -StartupType Automatic

# Run under an AD FS Administrator service account, not LocalSystem:
sc.exe config SmartLockoutApi obj= "DOMAIN\svc-smartlockout" password= "********"

Start-Service SmartLockoutApi
Get-Service  SmartLockoutApi
```

Validate again with the same `curl` as in 4.3.

**Follow-up** (out of scope here, recommended before any real production use):
add the `Microsoft.Extensions.Hosting.WindowsServices` package and call
`builder.Host.UseWindowsService()` in `Program.cs`. That makes the process a
first-class Windows Service with proper stop-signal handling.

### 4.5. Logs

The app logs to stdout via the default ASP.NET Core console logger. When
running as a Windows Service the console output is discarded; for service
deployments wire up file logging (Serilog, NLog) or write to the Windows
Event Log before relying on it. Until then, run interactively when
diagnosing failures.

### 4.6. Uninstall

```powershell
Stop-Service SmartLockoutApi
sc.exe delete SmartLockoutApi
Remove-Item "C:\Program Files\SmartLockoutApi" -Recurse -Force
Remove-Item "C:\ProgramData\SmartLockoutApi" -Recurse -Force  # if you set DOTNET_BUNDLE_EXTRACT_BASE_DIR
```

---

## Security

- **No authentication.** The endpoint is anonymous until Windows Auth
  (Negotiate/Kerberos) or Entra ID (Microsoft.Identity.Web) is wired up. Bind
  to `localhost` and call it from co-located tooling only.
- UPN input is validated against a strict regex and length cap. The
  PowerShell call passes the UPN as a typed parameter
  (`.AddParameter("Identity", upn)`), never concatenated into a script — the
  SDK's parameter binding is the primary defense against command injection;
  the regex is defense-in-depth.
- The API is read-only. There is no endpoint to reset a lockout
  (`Reset-AdfsAccountLockout`) — that would need real auth first.
