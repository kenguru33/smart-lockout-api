<#
.SYNOPSIS
    Configures Windows / AD permissions for the SmartLockoutApi service account.

.DESCRIPTION
    Run on the host where SmartLockoutApi.exe will run as a Windows service.
    For this project that is the AD FS server itself, because the ADFS
    PowerShell module is only importable there.

    Steps:
      1. "Log on as a service" right for the account (local privilege).
      2. Local Administrators membership on the AD FS server. This is the
         only documented way to satisfy Get-AdfsAccountActivity /
         Reset-AdfsAccountLockout, which require AD FS administrator rights.
      3. Read access to the TLS cert's private key in LocalMachine\My.
      4. Inbound Windows Firewall rule for the HTTPS port.
      5. Delegate write on 'mobile' and 'telephoneNumber' on the target OU,
         so Set-ADUser can update those attributes WITHOUT making the
         account Account Operators or Domain Admin.
      6. Register the Windows Event Log source 'SmartLockoutApi' so the
         service (running as a non-admin account) can write Information /
         Warning / Error events into the Application log without needing
         to self-register at runtime.

    Optional:
      - Install the Windows service (-InstallService -BinaryPath ...).

    Re-running is safe: each step detects whether it has already been done.

    The service account itself must already exist in AD before running this.

.PARAMETER ServiceAccount
    AD service account in DOMAIN\sAMAccountName form.

.PARAMETER CertSubject
    Subject (CN or SAN DnsName) of the LE certificate the API serves on the
    HTTPS port. Used to locate the cert in LocalMachine\My and ACL its
    private key file.

.PARAMETER UserOU
    Distinguished name of the OU whose users may have their mobile /
    telephoneNumber attributes updated via /api/ad/user/{upn}/phone.
    Example: 'OU=Users,DC=example,DC=com'.

.PARAMETER Port
    HTTPS port (default 5199, matching the app).

.PARAMETER BinaryPath
    Full path to SmartLockoutApi.exe. Required when -InstallService is set.

.PARAMETER InstallService
    Also create the Windows service entry after permissions are configured.
    Prompts for the service account password.

.EXAMPLE
    .\Setup-ServiceAccount.ps1 `
        -ServiceAccount 'EXAMPLE\svc-smartlockout' `
        -CertSubject 'api.example.no' `
        -UserOU 'OU=Users,DC=example,DC=com' `
        -BinaryPath 'C:\Apps\SmartLockoutApi\SmartLockoutApi.exe' `
        -InstallService

.NOTES
    Requires elevation. The AD delegation step (5) additionally requires the
    invoking user to have the right to delegate on $UserOU (Domain Admin or
    equivalent on that OU). If you cannot run the delegation step from this
    host, comment it out and run it separately from a DC / RSAT host.
#>

#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ServiceAccount,
    [Parameter(Mandatory)][string]$CertSubject,
    [Parameter(Mandatory)][string]$UserOU,
    [int]$Port = 5199,
    [string]$BinaryPath,
    [switch]$InstallService
)

$ErrorActionPreference = 'Stop'

function Get-AccountSid {
    param([string]$Account)
    try {
        return (New-Object System.Security.Principal.NTAccount($Account)) `
            .Translate([System.Security.Principal.SecurityIdentifier]).Value
    } catch {
        throw "Could not resolve SID for '$Account'. Check that the account exists in AD."
    }
}

# ---------------------------------------------------------------------------
# 1. 'Log on as a service' right (SeServiceLogonRight)
# ---------------------------------------------------------------------------
function Grant-LogonAsService {
    param([string]$Account)
    Write-Host "[1/6] Granting 'Log on as a service' to $Account..." -ForegroundColor Cyan

    $sid = Get-AccountSid -Account $Account
    $tempBase = [System.IO.Path]::GetTempPath()
    $cfgPath  = Join-Path $tempBase "secedit-$([guid]::NewGuid()).inf"
    $dbPath   = Join-Path $tempBase "secedit-$([guid]::NewGuid()).sdb"

    try {
        & secedit /export /cfg $cfgPath /areas USER_RIGHTS | Out-Null
        $content = Get-Content $cfgPath -Raw

        if ($content -match '(?m)^SeServiceLogonRight\s*=([^\r\n]*)') {
            $current = $Matches[1].Trim()
            if ($current -match "\*$sid\b") {
                Write-Host "  Already granted. Skipping." -ForegroundColor Yellow
                return
            }
            $newValue = if ($current) { "$current,*$sid" } else { "*$sid" }
            $content = $content -replace '(?m)^SeServiceLogonRight\s*=[^\r\n]*', `
                "SeServiceLogonRight = $newValue"
        } else {
            $content = $content -replace '(?m)^\[Privilege Rights\]', `
                "[Privilege Rights]`r`nSeServiceLogonRight = *$sid"
        }

        Set-Content -Path $cfgPath -Value $content -Encoding Unicode
        & secedit /configure /db $dbPath /cfg $cfgPath /areas USER_RIGHTS | Out-Null
        Write-Host "  Granted." -ForegroundColor Green
    } finally {
        Remove-Item $cfgPath, $dbPath -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------
# 2. AD FS administrator rights (= local Administrators on the AD FS server)
# ---------------------------------------------------------------------------
function Add-AdfsAdminMembership {
    param([string]$Account)
    Write-Host "[2/6] Adding $Account to local Administrators (required for AD FS cmdlets)..." -ForegroundColor Cyan

    $alreadyMember = $false
    try {
        if (Get-LocalGroupMember -Group 'Administrators' -Member $Account -ErrorAction Stop) {
            $alreadyMember = $true
        }
    } catch {
        # Get-LocalGroupMember throws when the principal is not a member; treat as not-member.
    }

    if ($alreadyMember) {
        Write-Host "  Already a member. Skipping." -ForegroundColor Yellow
        return
    }

    Add-LocalGroupMember -Group 'Administrators' -Member $Account
    Write-Host "  Added." -ForegroundColor Green
    Write-Host "  NOTE: Local Administrators is currently the only documented way to satisfy" -ForegroundColor Yellow
    Write-Host "        Get-AdfsAccountActivity / Reset-AdfsAccountLockout. If your AD FS" -ForegroundColor Yellow
    Write-Host "        environment defines a tighter AD FS admin group, prefer that instead." -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# 3. Read access to the TLS cert's private key in LocalMachine\My
# ---------------------------------------------------------------------------
function Grant-CertPrivateKeyRead {
    param([string]$Account, [string]$Subject)
    Write-Host "[3/6] Granting $Account read access to the private key of '$Subject'..." -ForegroundColor Cyan

    $now = Get-Date
    $cert = Get-ChildItem Cert:\LocalMachine\My |
        Where-Object { $_.NotBefore -le $now -and $_.NotAfter -ge $now } |
        Where-Object {
            (($_.DnsNameList | ForEach-Object { $_.Unicode }) -contains $Subject) -or
            ($_.Subject -match "CN=$([regex]::Escape($Subject))(,|$)")
        } |
        Sort-Object NotBefore -Descending |
        Select-Object -First 1

    if (-not $cert) {
        throw "No valid certificate found in LocalMachine\My matching subject '$Subject'. Install it (e.g. via win-acme) and re-run."
    }
    Write-Host "  Cert thumbprint: $($cert.Thumbprint), NotAfter: $($cert.NotAfter)"

    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
    if (-not $rsa) {
        throw "Certificate $($cert.Thumbprint) has no readable RSA private key (wrong key type, or no private key installed)."
    }

    $isCng = $rsa.GetType().Name -eq 'RSACng'
    $keyName = if ($isCng) {
        $rsa.Key.UniqueName
    } elseif ($rsa.GetType().Name -eq 'RSACryptoServiceProvider') {
        $rsa.CspKeyContainerInfo.UniqueKeyContainerName
    } else {
        throw "Unrecognised RSA implementation $($rsa.GetType().Name); cannot locate key file."
    }

    $keyPath = if ($isCng) {
        Join-Path "$env:ProgramData\Microsoft\Crypto\Keys" $keyName
    } else {
        Join-Path "$env:ProgramData\Microsoft\Crypto\RSA\MachineKeys" $keyName
    }
    if (-not (Test-Path $keyPath)) {
        throw "Private key file not found at $keyPath."
    }

    $acl = Get-Acl -Path $keyPath
    $existing = $acl.Access | Where-Object {
        $_.IdentityReference.Value -eq $Account -and
        ($_.FileSystemRights.value__ -band [System.Security.AccessControl.FileSystemRights]::Read.value__)
    }
    if ($existing) {
        Write-Host "  Already granted. Skipping." -ForegroundColor Yellow
        return
    }

    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $Account, 'Read', 'None', 'None', 'Allow')
    $acl.AddAccessRule($rule)
    Set-Acl -Path $keyPath -AclObject $acl
    Write-Host "  Granted." -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# 4. Inbound firewall rule for the HTTPS port
# ---------------------------------------------------------------------------
function New-HttpsFirewallRule {
    param([int]$Port)
    Write-Host "[4/6] Opening inbound TCP $Port in Windows Firewall..." -ForegroundColor Cyan
    $name = "SmartLockoutApi HTTPS $Port"

    if (Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue) {
        Write-Host "  Rule already exists. Skipping." -ForegroundColor Yellow
        return
    }
    New-NetFirewallRule -DisplayName $name -Direction Inbound -Action Allow `
        -Protocol TCP -LocalPort $Port -Profile Domain | Out-Null
    Write-Host "  Created." -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# 5. AD delegation: RPWP on 'mobile' and 'telephoneNumber' for user objects
#    inside $UserOU. This is what makes Set-ADUser -MobilePhone / -OfficePhone
#    work for the service account WITHOUT granting it broader AD rights.
# ---------------------------------------------------------------------------
function Grant-AdAttributeDelegation {
    param([string]$Account, [string]$Ou)
    Write-Host "[5/6] Delegating write on 'mobile','telephoneNumber' inside $Ou..." -ForegroundColor Cyan

    $dsacls = Get-Command dsacls.exe -ErrorAction SilentlyContinue
    if (-not $dsacls) {
        Write-Host "  dsacls.exe not found on this host." -ForegroundColor Yellow
        Write-Host "  Install AD DS tools (RSAT) or run this step on a DC. Equivalent via ADUC:" -ForegroundColor Yellow
        Write-Host "    Right-click the OU -> Delegate Control -> add $Account ->" -ForegroundColor Yellow
        Write-Host "    Create a custom task -> Only the following: User objects ->" -ForegroundColor Yellow
        Write-Host "    Property-specific -> tick Read+Write for 'mobile' and 'telephoneNumber'." -ForegroundColor Yellow
        return
    }

    & dsacls.exe $Ou /I:S /G "${Account}:RPWP;mobile;user"          | Out-Null
    & dsacls.exe $Ou /I:S /G "${Account}:RPWP;telephoneNumber;user" | Out-Null
    Write-Host "  Delegated." -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# 6. Windows Event Log source. The service runs as a non-admin account and
#    cannot self-register; do it here while we have admin rights. The .NET
#    EventLog provider uses this source when writing into the Application log.
# ---------------------------------------------------------------------------
function Register-EventLogSource {
    param([string]$Source = 'SmartLockoutApi', [string]$LogName = 'Application')
    Write-Host "[6/6] Registering Event Log source '$Source' in '$LogName'..." -ForegroundColor Cyan

    if ([System.Diagnostics.EventLog]::SourceExists($Source)) {
        $existingLog = [System.Diagnostics.EventLog]::LogNameFromSourceName($Source, '.')
        if ($existingLog -eq $LogName) {
            Write-Host "  Source already registered in '$LogName'. Skipping." -ForegroundColor Yellow
            return
        }
        throw "Source '$Source' is already registered in event log '$existingLog', not '$LogName'. Resolve manually with Remove-EventLog -Source $Source."
    }

    New-EventLog -LogName $LogName -Source $Source
    Write-Host "  Registered." -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# Optional: install the Windows service entry
# ---------------------------------------------------------------------------
function Install-SmartLockoutService {
    param([string]$Account, [string]$Binary)
    Write-Host "[Service] Installing SmartLockoutApi as a Windows service..." -ForegroundColor Cyan

    if (-not (Test-Path $Binary)) {
        throw "Binary not found at $Binary."
    }
    if (Get-Service SmartLockoutApi -ErrorAction SilentlyContinue) {
        Write-Host "  Service already exists. Skipping creation." -ForegroundColor Yellow
        return
    }

    $cred = Get-Credential -UserName $Account -Message "Password for $Account"
    New-Service -Name 'SmartLockoutApi' `
        -BinaryPathName "`"$Binary`"" `
        -DisplayName 'Smart Lockout API' `
        -Description 'Internal API exposing AD FS smart lockout state and AD phone-number updates.' `
        -StartupType Automatic `
        -Credential $cred | Out-Null
    Write-Host "  Installed. Start with: Start-Service SmartLockoutApi" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# Run
# ---------------------------------------------------------------------------
Write-Host "Configuring SmartLockoutApi service account: $ServiceAccount`n" -ForegroundColor White

Grant-LogonAsService     -Account $ServiceAccount
Add-AdfsAdminMembership  -Account $ServiceAccount
Grant-CertPrivateKeyRead -Account $ServiceAccount -Subject $CertSubject
New-HttpsFirewallRule    -Port    $Port
Grant-AdAttributeDelegation -Account $ServiceAccount -Ou $UserOU
Register-EventLogSource

if ($InstallService) {
    if (-not $BinaryPath) { throw "-InstallService requires -BinaryPath." }
    Install-SmartLockoutService -Account $ServiceAccount -Binary $BinaryPath
}

Write-Host "`nDone. Remember to set the service environment:" -ForegroundColor White
Write-Host "  ApiKey__Keys__0                       (the shared API key)" -ForegroundColor White
Write-Host "  Kestrel__Certificate__Subject=$CertSubject" -ForegroundColor White
Write-Host "  (do NOT set ASPNETCORE_URLS)" -ForegroundColor White
