<#
.SYNOPSIS
    Updates an already-installed SmartLockoutApi service from the git checkout.

.DESCRIPTION
    Run on the AD FS server, from an elevated PowerShell. Assumes the initial
    install (service registration, permissions, cert, firewall) is already
    done per DEPLOY.md — this script only handles subsequent code updates:

      1. git pull (fast-forward only) in the repo directory.
      2. dotnet build -c Release (TreatWarningsAsErrors catches problems
         before the service is touched).
      3. dotnet publish into <repo>\publish (cleaned first, so stale
         artefacts never ship).
      4. Stop the Windows service and wait for it to fully stop.
      5. Copy the publish output over the install directory.
      6. Start the service again and verify /health responds.

    If the build or publish fails, the service is never stopped. If the copy
    fails, the service is started again on the old binaries.

.PARAMETER RepoDir
    Git checkout to build from. Default: C:\api\smart-lockout-api

.PARAMETER InstallDir
    Directory the Windows service runs from. Default: C:\Program Files\smart-lockout-api

.PARAMETER ServiceName
    Windows service name. Default: SmartLockoutApi

.EXAMPLE
    .\Deploy.ps1
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$RepoDir     = 'C:\api\smart-lockout-api',
    [string]$InstallDir  = 'C:\Program Files\smart-lockout-api',
    [string]$ServiceName = 'SmartLockoutApi'
)

$ErrorActionPreference = 'Stop'
$PublishDir = Join-Path $RepoDir 'publish'

function Invoke-Step {
    param([string]$Name, [scriptblock]$Action)
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed (exit code $LASTEXITCODE)."
    }
}

# Fail early if the service doesn't exist — this script updates, it doesn't install.
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    throw "Service '$ServiceName' not found. Run the initial install per DEPLOY.md first."
}

Push-Location $RepoDir
try {
    Invoke-Step 'git pull'       { git pull --ff-only }
    Invoke-Step 'dotnet build'   { dotnet build -c Release }

    if (Test-Path $PublishDir) {
        Remove-Item $PublishDir -Recurse -Force
    }
    Invoke-Step 'dotnet publish' { dotnet publish -c Release -p:PublishSingleFile=true -o $PublishDir }
}
finally {
    Pop-Location
}

Write-Host "==> Stopping service $ServiceName" -ForegroundColor Cyan
Stop-Service -Name $ServiceName
(Get-Service -Name $ServiceName).WaitForStatus('Stopped', '00:00:30')

try {
    Write-Host "==> Copying $PublishDir\* -> $InstallDir" -ForegroundColor Cyan
    # The SCM reports Stopped slightly before the process releases the exe;
    # retry briefly if the file is still locked.
    $attempt = 0
    while ($true) {
        try {
            Copy-Item -Path (Join-Path $PublishDir '*') -Destination $InstallDir -Recurse -Force
            break
        }
        catch [System.IO.IOException] {
            if (++$attempt -ge 5) { throw }
            Write-Host "    Files still locked, retrying ($attempt/5)..." -ForegroundColor Yellow
            Start-Sleep -Seconds 2
        }
    }
}
finally {
    Write-Host "==> Starting service $ServiceName" -ForegroundColor Cyan
    Start-Service -Name $ServiceName
    (Get-Service -Name $ServiceName).WaitForStatus('Running', '00:00:30')
}

# Liveness check: -k because the cert subject is the public FQDN, not loopback.
Write-Host '==> Verifying /health' -ForegroundColor Cyan
$healthy = $false
foreach ($i in 1..10) {
    curl.exe -fsk https://127.0.0.1:5199/health > $null 2>&1
    if ($LASTEXITCODE -eq 0) { $healthy = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $healthy) {
    throw "Service is running but /health did not respond on https://127.0.0.1:5199 within 20s. Check the Application event log (source: $ServiceName)."
}

Write-Host 'Deploy complete.' -ForegroundColor Green
