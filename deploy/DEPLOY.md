# Production deployment — step by step

Target audience: an operator deploying SmartLockoutApi onto an AD FS server
for the first time. The README has the overview; this is the concrete
sequence, with the exact commands and verification at each step.

## Assumptions

- The service will run on the **AD FS server itself** (the `ADFS` PS module is
  only importable there).
- TLS is terminated by Kestrel in-process on port **5199**, using a Let's
  Encrypt cert installed by `win-acme` via the **DNS-01** challenge.
- A reverse proxy / TLS offloader is **not** in front of the service.
- You have Domain Admin (or equivalent on the affected OU) for the AD
  delegation step. You have Local Admin on the AD FS server.

## Values to collect before you start

Write these down — every later step references them.

| Placeholder            | Example                                                    | Notes |
|------------------------|------------------------------------------------------------|-------|
| `<hostname>`           | `api.example.no`                                           | Public FQDN of the API. Must have an A/AAAA record reaching the AD FS server, and the DNS zone must support TXT updates via API (for DNS-01). |
| `<install-path>`       | `C:\Program Files\SmartLockoutApi`                          | Where the published artefacts and `deploy/` folder land on the AD FS server. |
| `<service-account>`    | `EXAMPLE\svc-smartlockout`                                  | AD service account that the Windows service runs as. Created in Step 3. |
| `<user-ou>`            | `OU=Users,DC=example,DC=com`                                | OU containing users whose `mobile`/`telephoneNumber` may be updated. |
| `<dns-provider>`       | Cloudflare, Azure DNS, Route 53, etc.                       | win-acme has plugins for the common ones. |

---

## Step 1 — Pre-flight checks (on the AD FS server)

From an elevated PowerShell:

```powershell
# AD FS smart lockout enabled?
(Get-AdfsProperties).EnableExtranetLockout            # should be: True

# Both PS modules importable?
Import-Module ADFS;            Get-Command Get-AdfsAccountActivity | Format-Table Name,Source
Import-Module ActiveDirectory; Get-Command Get-ADUser              | Format-Table Name,Source

# DNS resolves to this host?
Resolve-DnsName <hostname>     # A record points to this server's external IP / FQDN

# Port 5199 free?
Get-NetTCPConnection -LocalPort 5199 -ErrorAction SilentlyContinue   # should return nothing
```

If any of these fail, fix them before continuing. In particular,
`EnableExtranetLockout=False` makes `Get-AdfsAccountActivity` return records
with `FamiliarLockout`/`UnknownLockout` always `False` — the API still
"works" but reports nonsense.

---

## Step 2 — Build the deployment package (on any host with the .NET 8 SDK)

From the repo root:

```
dotnet publish -c Release -p:PublishSingleFile=true -o publish
```

Verify the publish output:

```
ls publish/
# expect: SmartLockoutApi.exe, runtimes/, appsettings.json, appsettings.Development.json, web.config
```

Bundle the publish output **plus** the `deploy/` folder for transfer:

```powershell
# Windows PowerShell
New-Item -ItemType Directory -Force staging | Out-Null
Copy-Item publish\*  staging\ -Recurse
Copy-Item deploy     staging\ -Recurse
Compress-Archive -Path staging\* -DestinationPath SmartLockoutApi.zip -Force
Remove-Item staging -Recurse -Force
```

```bash
# Linux / macOS
mkdir -p staging && cp -r publish/* staging/ && cp -r deploy staging/
(cd staging && zip -r ../SmartLockoutApi.zip .)
rm -rf staging
```

The resulting `SmartLockoutApi.zip` is the deployment artefact. Transfer
it to the AD FS server (RDP clipboard, SMB share, signed artifact store —
your preference).

---

## Step 3 — Create the AD service account (on a DC or any RSAT host)

Skip if it already exists.

```powershell
# Generate a long random password and store it somewhere safe (password manager).
$bytes = New-Object byte[] 24
[Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
$initialPwd = [Convert]::ToBase64String($bytes) + '!Aa9'
Write-Host "Initial password (capture now): $initialPwd"

New-ADUser `
    -Name 'svc-smartlockout' `
    -SamAccountName 'svc-smartlockout' `
    -UserPrincipalName 'svc-smartlockout@example.com' `
    -DisplayName 'SmartLockoutApi service account' `
    -Description 'Runs the SmartLockoutApi Windows service on the AD FS host' `
    -Path 'OU=ServiceAccounts,DC=example,DC=com' `
    -AccountPassword (ConvertTo-SecureString $initialPwd -AsPlainText -Force) `
    -Enabled $true `
    -PasswordNeverExpires $true `
    -CannotChangePassword $true
```

`PasswordNeverExpires` + `CannotChangePassword` are common for service
accounts, but follow your org's policy. If your policy mandates rotation,
plan the rotation procedure (new password → update service credential →
restart service).

The account does **not** need any group memberships yet — Step 6 grants
them locally on the AD FS server.

---

## Step 4 — Install win-acme and issue the LE certificate (on the AD FS server)

### 4a. Install win-acme

Download the **zip** release from <https://www.win-acme.com/> (current
stable, x64). Extract to `C:\Tools\win-acme\`. Confirm:

```powershell
& 'C:\Tools\win-acme\wacs.exe' --version
```

### 4b. Issue the cert via DNS-01

Run `wacs.exe` interactively, elevated. Follow the prompts:

| Prompt                                        | Choose |
|-----------------------------------------------|--------|
| Main menu                                     | `M` — Create renewal (full options) |
| Input method                                  | Manual input |
| Comma-separated host names                    | `<hostname>` |
| Validation                                    | DNS validation — pick the plugin matching `<dns-provider>` |
| (Plugin will prompt for the DNS API credentials — supply them) | — |
| CSR / private key                             | RSA, 4096-bit (default is fine) |
| Store(s) for the certificate                  | Windows Certificate Store |
| Store name                                    | `My` (Personal), location `LocalMachine` |
| Installation step                             | **No (additional) installation steps** — we do not install into IIS |
| Friendly name (optional)                      | `SmartLockoutApi` |

`win-acme` will:

1. Publish a `_acme-challenge.<hostname>` TXT record via the DNS API.
2. Wait for DNS propagation, then call Let's Encrypt.
3. Receive the cert and install it into `LocalMachine\My`.
4. Create a scheduled task that runs daily and renews when needed.

Verify the cert is present:

```powershell
Get-ChildItem Cert:\LocalMachine\My |
    Where-Object { $_.Subject -match 'CN=<hostname>' } |
    Format-Table Subject, Thumbprint, NotAfter
```

Verify the private key is accessible (`HasPrivateKey` should be `True` and
the key file should exist under `%ProgramData%\Microsoft\Crypto\Keys\`):

```powershell
$c = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -match 'CN=<hostname>' } | Select-Object -First 1
$c.HasPrivateKey
[System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($c)
```

The renewal task: `Get-ScheduledTask -TaskName 'win-acme renew*'` should
return a task scheduled daily at a random morning time.

---

## Step 5 — Lay down the binaries (on the AD FS server)

Extract the package to the install path:

```powershell
$dst = '<install-path>'
New-Item -ItemType Directory -Force $dst | Out-Null
Expand-Archive -Path C:\Path\To\SmartLockoutApi.zip -DestinationPath $dst -Force
```

After extraction `<install-path>` contains:

```
<install-path>\
├── SmartLockoutApi.exe
├── runtimes\               ← do not delete
├── appsettings.json
├── appsettings.Development.json
├── web.config              (optional; safe to delete if not using IIS)
└── deploy\
    └── Setup-ServiceAccount.ps1
```

---

## Step 6 — Permission the service account (on the AD FS server)

From an elevated PowerShell:

```powershell
cd '<install-path>'

.\deploy\Setup-ServiceAccount.ps1 `
    -ServiceAccount '<service-account>' `
    -CertSubject    '<hostname>' `
    -UserOU         '<user-ou>' `
    -BinaryPath     '<install-path>\SmartLockoutApi.exe' `
    -InstallService
```

The script is idempotent. Each step prints `Already granted/added/exists.
Skipping.` when re-run. The six things it does (five permissions plus
registering the Windows Event Log source) and why each matters are
listed in the README §4.5 and in the script header.

If the **dsacls step fails** because `dsacls.exe` isn't on the AD FS
server, run the same script on a DC (or any RSAT host) with the same
parameters — earlier steps will skip on re-run.

`-InstallService` will prompt for the service account password. Capture
the same value you set in Step 3.

After it finishes, verify the service is registered (it is created
**stopped**):

```powershell
Get-Service SmartLockoutApi | Format-List Name,Status,StartType,UserName
```

`UserName` should be `<service-account>`. `Status` is `Stopped`.

---

## Step 7 — Configure the production environment (on the AD FS server)

Configuration comes from two layers:

- **Non-secret** values (Logging levels, `AllowedHosts`, the `Kestrel:Certificate`
  section) can live in an `appsettings.Production.json` next to the exe.
  Copy `appsettings.Production.json.example` from the repo (it ships in the
  `deploy/` folder of the zip) to `<install-path>\appsettings.Production.json`
  and edit the values.

  ```powershell
  Copy-Item '<install-path>\deploy\appsettings.Production.json.example' `
            '<install-path>\appsettings.Production.json'
  # Then open in notepad / VS Code and set Subject + AllowedHosts to your hostname.
  ```

  This file is **not** in source control — `appsettings.Production.json` is
  gitignored so real production values can't leak into the repo. The
  `.example` template ships alongside it.

- **Secrets** (the API key) come only from machine-scope environment
  variables. Never put `ApiKey:Keys` in a tracked file.

Set the API key as a machine-scope environment variable. If you did **not**
use the `appsettings.Production.json` file above, also set the cert subject
as an env var (env vars and the JSON file are equivalent — pick one per
value to avoid two sources of truth):

```powershell
# Generate an API key
$bytes = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
$apiKey = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
Write-Host "API key (capture now): $apiKey"

[Environment]::SetEnvironmentVariable('ApiKey__Keys__0', $apiKey, 'Machine')

# Only needed if you did NOT set Kestrel:Certificate:Subject in
# appsettings.Production.json above:
[Environment]::SetEnvironmentVariable('Kestrel__Certificate__Subject', '<hostname>', 'Machine')

# IMPORTANT: ASPNETCORE_URLS must NOT be set.
[Environment]::GetEnvironmentVariable('ASPNETCORE_URLS', 'Machine')   # expect: $null
[Environment]::SetEnvironmentVariable('ASPNETCORE_URLS', $null, 'Machine')   # clears it, just in case
```

Optional overrides only if you need non-defaults:

```powershell
# [Environment]::SetEnvironmentVariable('Kestrel__Certificate__RefreshInterval', '00:05:00', 'Machine')
# [Environment]::SetEnvironmentVariable('DOTNET_BUNDLE_EXTRACT_BASE_DIR',         'C:\ProgramData\SmartLockoutApi\bundle', 'Machine')
```

Capture the API key into your password manager — it's the **only** time
the value is recoverable.

---

## Step 8 — Start and smoke-test

```powershell
Start-Service SmartLockoutApi
Start-Sleep -Seconds 3
Get-Service SmartLockoutApi
```

If the service **stops immediately**, the cert or module-import probe
failed at startup. Run the exe interactively to see the error:

```powershell
cd '<install-path>'
.\SmartLockoutApi.exe
# Ctrl+C to stop. The startup error names the cause precisely
# (missing module, no matching cert, EKU missing, no private key, etc.).
```

Once running, smoke-test from the AD FS server. `-k` because the LE cert
is for `<hostname>`, not the loopback:

```powershell
# 0. /health — unauthenticated liveness probe, 200 "Healthy"
curl -ik https://127.0.0.1:5199/health

# 1. 401 — no key
curl -ik https://127.0.0.1:5199/api/adfs/smart-lockout/not-a-upn

# 2. 400 — auth passes, UPN invalid
curl -ik -H "X-API-Key: $apiKey" https://127.0.0.1:5199/api/adfs/smart-lockout/not-a-upn

# 3. 200 / 404 — real path
curl -ik -H "X-API-Key: $apiKey" https://127.0.0.1:5199/api/adfs/smart-lockout/<real-upn>

# 4. Phone read — 200 / 404
curl -ik -H "X-API-Key: $apiKey" https://127.0.0.1:5199/api/ad/user/<real-upn>/phone

# 5. Phone update (rejected — bad number)
curl -ik -H "X-API-Key: $apiKey" -H "Content-Type: application/json" `
     -X PATCH -d '{"mobile":"+4612345678"}' `
     https://127.0.0.1:5199/api/ad/user/<real-upn>/phone   # → 400
```

Then test from a remote host using the real hostname (no `-k`, the cert
chain should validate cleanly):

```bash
curl -i -H "X-API-Key: $apiKey" https://<hostname>:5199/api/adfs/smart-lockout/<real-upn>
```

A successful chain validation here confirms TLS is correctly terminated by
Kestrel and the LE chain is trusted.

---

## Step 9 — Verify the live cert reload (optional but recommended)

The whole point of the cert-store integration is zero-restart renewal.
Force a renewal once, while the service is running, to prove the wiring:

```powershell
& 'C:\Tools\win-acme\wacs.exe' --renew --force
```

Within `RefreshInterval` (default 5 minutes) the service log (Event Viewer
→ Application → source `SmartLockoutApi`) should emit:

```
TLS certificate swapped: old <oldThumbprint> -> new <newThumbprint>, subject '<hostname>', NotAfter <date>
```

```powershell
Get-WinEvent -FilterHashtable @{
    LogName = 'Application'; ProviderName = 'SmartLockoutApi'
} -MaxEvents 20 |
    Where-Object { $_.Message -match 'TLS certificate swapped' } |
    Select-Object TimeCreated, Message
```

Confirm new connections use the new cert:

```powershell
echo Q | openssl s_client -connect 127.0.0.1:5199 -servername <hostname> 2>$null |
    Select-String 'subject=', 'issuer=', 'notAfter='
```

(If `openssl` isn't on the host, install it via Chocolatey, or skip — the
log line is the primary signal.)

---

## Step 10 — Hand-over checklist

Before declaring the deployment done:

- [ ] `Get-Service SmartLockoutApi` reports **Running** and **Automatic**.
- [ ] `<hostname>` resolves from the consumer side, TLS validates without `-k`.
- [ ] All five curl smoke tests in Step 8 returned the expected status codes.
- [ ] API key value is captured in the password manager (the only place it survives).
- [ ] Service account password is captured (separate entry from the API key).
- [ ] `win-acme` renewal task exists and is enabled.
- [ ] `_specs/server-tls-cert-from-windows-store.md` and README §4 are linked from your team's runbook so future operators can find this guide.

---

## Appendix A — Troubleshooting

### "Service starts then stops within seconds"

Run the exe interactively (Step 8) to see the startup error. Common causes:

| Startup message contains                             | Cause                                                                                              | Fix |
|------------------------------------------------------|----------------------------------------------------------------------------------------------------|-----|
| `Failed to import the ADFS PowerShell module`         | Not running on an AD FS server, or the ADFS role was removed.                                      | Re-host on an AD FS server. |
| `Failed to import the ActiveDirectory PowerShell module` | RSAT AD DS tools missing.                                                                          | `Install-WindowsFeature RSAT-AD-PowerShell`. |
| `No usable TLS certificate found in LocalMachine/My`  | Cert subject mismatch / cert not installed yet / expired / missing private key / missing EKU.      | Re-run win-acme; verify `Kestrel__Certificate__Subject`; ACL the private key (Step 6). |
| `private key not accessible`                          | Service account lacks read on the LE private-key file.                                             | Re-run `Setup-ServiceAccount.ps1` (Step 6). |

### 500 from an endpoint that previously worked

Search the Event Log (Event Viewer → Application → source
`SmartLockoutApi`, or via `Get-WinEvent`) for `AUDIT` / `Failed` /
`Access is denied`:

| Error from cmdlet                       | Meaning                                                          | Fix |
|-----------------------------------------|------------------------------------------------------------------|-----|
| `Get-AdfsAccountActivity ... Access is denied` | Service account not in Administrators on AD FS server.    | Re-run `Setup-ServiceAccount.ps1`. |
| `Set-ADUser ... insufficient access rights`    | AD delegation (Step 6.5) missed or scoped to wrong OU.    | Re-delegate with the correct `-UserOU`. |
| `Get-ADUser ... directory service is unavailable` | Domain controller unreachable.                         | Network / DC health issue. |

### `TLS certificate swapped` doesn't fire after a renewal

- Is the service actually running?  `Get-Service SmartLockoutApi`.
- Did the renewal actually install a new cert?  Check `Get-ChildItem
  Cert:\LocalMachine\My` thumbprints before and after `wacs.exe --renew
  --force`. If only one thumbprint, the renewal didn't complete — check
  win-acme's log.
- Has `RefreshInterval` elapsed? Default is 5 minutes.

### How to rotate the API key (zero-downtime)

```powershell
$bytes = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
$new = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')

[Environment]::SetEnvironmentVariable('ApiKey__Keys__1', $new, 'Machine')
Restart-Service SmartLockoutApi

# … point consumers at $new, verify …

[Environment]::SetEnvironmentVariable('ApiKey__Keys__0', $null, 'Machine')
Restart-Service SmartLockoutApi
```

---

## Appendix B — Backup / disaster recovery

The deployment's recoverable state lives in three places:

1. **`<install-path>`** — re-creatable from `dotnet publish` against the
   same commit. Capture the commit SHA in your change record.
2. **Windows certificate store (`LocalMachine\My`)** — re-issuable by
   `win-acme` (cert is not a secret). The DNS API credentials configured
   into `win-acme` *are* a secret; back those up.
3. **Environment variables `ApiKey__Keys__0/1`** — store the values in a
   password manager. Losing them requires consumer-side reconfiguration to
   the new key, not a service rebuild.

The service account password is also in scope; capture it in the same
password manager.

No persistent data lives in the service itself — there is no database or
local state to restore.
