##############################################################################
#  Deploy-AHNi-RSEService.ps1
#  AHNi RADETSync Engine (AHNi-RSE) - Enterprise Deployment Script
#
#  WHAT THIS SCRIPT DOES
#  ---------------------
#  0. LEGACY MIGRATION: detects and retires any previous AutoReport / AutoRADET
#     service installations before installing the new AHNi-RSE service.
#     Known legacy names handled:
#       AutoReport, AutoReport_v1.3, AutoReport_v1.2, AutoReport_v2.0,
#       AutoReport_v2.1, AutoReport_v2.2, AutoReport_v2.3, AutoReport_v2.4,
#       AutoRADET
#  1. Installs or updates the Windows Service
#  2. Sets recovery: restart after 1 min on every failure (1st, 2nd, subsequent)
#  3. Sets startup type to Automatic (Delayed) for boot resilience
#  4. Adds Windows Defender / antivirus exclusions for the service directory and log path
#  5. Opens inbound + outbound firewall rules for all ports the service needs
#  6. Configures the service to restart after 1-minute on failure - PERMANENTLY
#
#  USAGE
#  -----
#  Run as Administrator in PowerShell:
#    .\Deploy-AHNi-RSEService.ps1
#
#  Or with a custom install path:
#    .\Deploy-AHNi-RSEService.ps1 -InstallPath "D:\Services\AHNi-RSE"
#
##############################################################################

param(
    [string]$InstallPath    = "C:\Services\AHNi-RSE",
    [string]$ServiceName    = "AHNi-RSE",
    [string]$DisplayName    = "AHNi RADETSync Engine (AHNi-RSE) - Facility Data Uploader",
    [string]$Description    = "Runs scheduled RADET/HTS reports and sends heartbeats to BayCentral.",
    [string]$ExeName        = "AHNiRSE.exe",
    [string]$LogPath        = "C:\ProgramData\AHNi-RSE\Logs",
    [string]$StagingPath    = "C:\Reports\Staging",
    [string]$OutputPath     = "C:\Reports\Output",
    [int]   $RestartDelayMs = 60000   # 60 seconds - how long to wait before restart on failure
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# -- 0. Elevation check ---------------------------------------------------------
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
{
    Write-Error "This script must be run as Administrator. Right-click PowerShell -> 'Run as administrator'."
    exit 1
}

Write-Host ""
Write-Host "=======================================================" -ForegroundColor Cyan
Write-Host "  AHNi RADETSync Engine (AHNi-RSE) Enterprise Deployment" -ForegroundColor Cyan
Write-Host "=======================================================" -ForegroundColor Cyan
Write-Host ""

# -- 0. Legacy migration: stop and remove any previous AutoReport / AutoRADET service ──────────
#
#  Facilities that ran earlier versions have a Windows Service registered under
#  one of these names.  We stop it, delete the service registration, and migrate
#  the appsettings.json so no configuration is lost.
#
$LegacyServiceNames = @(
    'AutoReport',
    'AutoReport_v1.2',
    'AutoReport_v1.3',
    'AutoReport_v2.0',
    'AutoReport_v2.1',
    'AutoReport_v2.2',
    'AutoReport_v2.3',
    'AutoReport_v2.4',
    'AutoRADET'
)

# Paths where legacy installs commonly live — we will try to migrate appsettings.json from these
$LegacyInstallPaths = @(
    'C:\Services\AutoReport',
    'C:\Services\AutoReport_v1.2',
    'C:\Services\AutoReport_v1.3',
    'C:\Services\AutoReport_v2.0',
    'C:\Services\AutoReport_v2.1',
    'C:\Services\AutoReport_v2.2',
    'C:\Services\AutoReport_v2.3',
    'C:\Services\AutoReport_v2.4',
    'C:\Services\AutoRADET'
)

$legacyFound = $false

foreach ($legacyName in $LegacyServiceNames)
{
    $legacySvc = Get-Service -Name $legacyName -ErrorAction SilentlyContinue
    if (-not $legacySvc) { continue }

    $legacyFound = $true
    Write-Host ""
    Write-Host "[MIGRATE] Found legacy service: '$legacyName' (Status=$($legacySvc.Status))" -ForegroundColor Yellow

    # Stop it
    if ($legacySvc.Status -ne 'Stopped')
    {
        Write-Host "[MIGRATE]   Stopping '$legacyName'..." -ForegroundColor Yellow
        try
        {
            Stop-Service -Name $legacyName -Force -ErrorAction Stop
            Start-Sleep -Seconds 4
            Write-Host "[MIGRATE]   Stopped." -ForegroundColor Green
        }
        catch
        {
            Write-Warning "[MIGRATE]   Could not stop '$legacyName': $_"
        }
    }

    # Remove the service registration
    Write-Host "[MIGRATE]   Removing service registration '$legacyName'..." -ForegroundColor Yellow
    $deleteOutput = & sc.exe delete $legacyName 2>&1
    if ($LASTEXITCODE -eq 0)
    {
        Write-Host "[MIGRATE]   Service '$legacyName' removed successfully." -ForegroundColor Green
    }
    else
    {
        Write-Warning "[MIGRATE]   sc.exe delete returned: $deleteOutput (exit $LASTEXITCODE). May already be gone."
    }

    # Wait for SCM to release the entry
    Start-Sleep -Seconds 2
}

# Migrate appsettings.json from the first legacy install path that has one,
# but only if the NEW install path doesn't already have an appsettings.json.
$newSettings = Join-Path $InstallPath 'appsettings.json'
if (-not (Test-Path $newSettings))
{
    foreach ($legacyPath in $LegacyInstallPaths)
    {
        $legacySettings = Join-Path $legacyPath 'appsettings.json'
        if (Test-Path $legacySettings)
        {
            Write-Host "[MIGRATE] Copying appsettings.json from legacy path: $legacyPath" -ForegroundColor Yellow
            New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
            Copy-Item -Path $legacySettings -Destination $newSettings -Force
            Write-Host "[MIGRATE] appsettings.json preserved at: $newSettings" -ForegroundColor Green
            Write-Host "[MIGRATE] NOTE: Review the copied appsettings.json — connection strings and keys should remain valid." -ForegroundColor Cyan
            break
        }
    }
}

if ($legacyFound)
{
    Write-Host ""
    Write-Host "[MIGRATE] Legacy service migration complete." -ForegroundColor Green
    Write-Host "[MIGRATE] Old install folders (if any) were NOT deleted — remove them manually if no longer needed:" -ForegroundColor Cyan
    foreach ($p in $LegacyInstallPaths) { if (Test-Path $p) { Write-Host "           $p" -ForegroundColor Gray } }
    Write-Host ""
}
else
{
    Write-Host "[MIGRATE] No legacy AutoReport / AutoRADET services found on this machine." -ForegroundColor Gray
}

# -- 1. Create directories ------------------------------------------------------
foreach ($dir in @($InstallPath, $LogPath, $StagingPath, $OutputPath))
{
    if (-not (Test-Path $dir))
    {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        Write-Host "[DIR] Created: $dir" -ForegroundColor Green
    }
    else
    {
        Write-Host "[DIR] Exists:  $dir" -ForegroundColor Gray
    }
}

# -- 2. Stop existing service (if running) -------------------------------------
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc)
{
    Write-Host "[SVC] Stopping existing service '$ServiceName'..." -ForegroundColor Yellow
    if ($svc.Status -ne "Stopped")
    {
        Stop-Service -Name $ServiceName -Force
        Start-Sleep -Seconds 3
    }
    Write-Host "[SVC] Stopped." -ForegroundColor Green
}

# -- 3. Copy files --------------------------------------------------------------
$sourceDir = $PSScriptRoot   # directory containing this script = publish output
Write-Host "[COPY] Copying files from '$sourceDir' to '$InstallPath'..." -ForegroundColor Yellow
Copy-Item -Path "$sourceDir\*" -Destination $InstallPath -Recurse -Force
Write-Host "[COPY] Done." -ForegroundColor Green

# -- 4. Install / update Windows Service ---------------------------------------
$exePath = Join-Path $InstallPath $ExeName

if (-not (Test-Path $exePath))
{
    Write-Error "[SVC] Executable not found: $exePath. Ensure publish output is in '$sourceDir'."
    exit 1
}

if ($svc)
{
    Write-Host "[SVC] Updating existing service binPath..." -ForegroundColor Yellow
    & sc.exe config $ServiceName binPath= "`"$exePath`"" | Out-Null
}
else
{
    Write-Host "[SVC] Installing new service '$ServiceName'..." -ForegroundColor Yellow
    & sc.exe create $ServiceName `
        binPath= "`"$exePath`"" `
        DisplayName= "$DisplayName" `
        start= delayed-auto | Out-Null
}

# Set description
& sc.exe description $ServiceName "$Description" | Out-Null

Write-Host "[SVC] Service registered: $DisplayName" -ForegroundColor Green

# -- 5. Recovery settings - restart every 60 s on ALL failures -----------------
# Format: actions=restart/<delay_ms>/restart/<delay_ms>/restart/<delay_ms>
# reset=86400 -> reset failure counter after 24 hours of clean running
Write-Host "[SVC] Configuring recovery - restart after ${RestartDelayMs}ms on failure..." -ForegroundColor Yellow

$recoveryActions = "restart/$RestartDelayMs/restart/$RestartDelayMs/restart/$RestartDelayMs"
& sc.exe failure $ServiceName reset= 86400 actions= $recoveryActions | Out-Null

# Also set failure flag so recovery fires even on non-zero exit codes
& sc.exe failureflag $ServiceName 1 | Out-Null

Write-Host "[SVC] Recovery: restart after 60 s on 1st, 2nd, and all subsequent failures." -ForegroundColor Green

# -- 6. Startup type - Automatic (Delayed) -------------------------------------
& sc.exe config $ServiceName start= delayed-auto | Out-Null
Write-Host "[SVC] Startup type: Automatic (Delayed)" -ForegroundColor Green

# -- 7. Windows Defender / antivirus exclusions --------------------------------
Write-Host "[AV] Adding antivirus exclusions..." -ForegroundColor Yellow

$exclusionPaths = @(
    $InstallPath,
    $LogPath,
    $StagingPath,
    $OutputPath,
    $exePath
)

foreach ($path in $exclusionPaths)
{
    try
    {
        Add-MpPreference -ExclusionPath $path -ErrorAction Stop
        Write-Host "[AV]   Excluded path: $path" -ForegroundColor Green
    }
    catch
    {
        Write-Warning "[AV]   Could not add exclusion for '$path': $_"
    }
}

# Exclude the process executable from real-time scanning
try
{
    Add-MpPreference -ExclusionProcess $ExeName -ErrorAction Stop
    Write-Host "[AV]   Excluded process: $ExeName" -ForegroundColor Green
}
catch
{
    Write-Warning "[AV]   Could not add process exclusion: $_"
}

# -- 8. Firewall rules ----------------------------------------------------------
Write-Host "[FW] Configuring firewall rules..." -ForegroundColor Yellow

function Set-FwRule
{
    param([string]$Name, [string]$Direction, [string]$Program)
    $existing = Get-NetFirewallRule -DisplayName $Name -ErrorAction SilentlyContinue
    if ($existing)
    {
        Write-Host "[FW]   Updating: $Name" -ForegroundColor Gray
        Set-NetFirewallRule -DisplayName $Name -Program $Program
    }
    else
    {
        Write-Host "[FW]   Creating: $Name" -ForegroundColor Green
        New-NetFirewallRule `
            -DisplayName  $Name `
            -Direction    $Direction `
            -Program      $Program `
            -Action       Allow `
            -Profile      Any `
            -Enabled      True | Out-Null
    }
}

Set-FwRule -Name "AHNi-RSE Outbound" -Direction Outbound -Program $exePath
Set-FwRule -Name "AHNi-RSE Inbound"  -Direction Inbound  -Program $exePath

# Allow outbound HTTPS (443) for Azure Blob + Table Storage
$azureRule = Get-NetFirewallRule -DisplayName "AHNi-RSE Azure HTTPS" -ErrorAction SilentlyContinue
if (-not $azureRule)
{
    New-NetFirewallRule `
        -DisplayName  "AHNi-RSE Azure HTTPS" `
        -Direction    Outbound `
        -Protocol     TCP `
        -RemotePort   443 `
        -Action       Allow `
        -Profile      Any `
        -Enabled      True | Out-Null
    Write-Host "[FW]   Created: AHNi-RSE Azure HTTPS (TCP 443 outbound)" -ForegroundColor Green
}

# Allow outbound PostgreSQL (5432) for local DB
$pgRule = Get-NetFirewallRule -DisplayName "AHNi-RSE PostgreSQL" -ErrorAction SilentlyContinue
if (-not $pgRule)
{
    New-NetFirewallRule `
        -DisplayName  "AHNi-RSE PostgreSQL" `
        -Direction    Outbound `
        -Protocol     TCP `
        -RemotePort   5432 `
        -Action       Allow `
        -Profile      Any `
        -Enabled      True | Out-Null
    Write-Host "[FW]   Created: AHNi-RSE PostgreSQL (TCP 5432 outbound)" -ForegroundColor Green
}

# -- 9. Start service -----------------------------------------------------------
Write-Host "[SVC] Starting service '$ServiceName'..." -ForegroundColor Yellow
Start-Service -Name $ServiceName
Start-Sleep -Seconds 3

$svc = Get-Service -Name $ServiceName
if ($svc.Status -eq "Running")
{
    Write-Host "[SVC] Service is RUNNING." -ForegroundColor Green
}
else
{
    Write-Warning "[SVC] Service status: $($svc.Status). Check Event Viewer or logs at: $LogPath"
}

# -- 10. Summary ----------------------------------------------------------------
Write-Host ""
Write-Host "=======================================================" -ForegroundColor Cyan
Write-Host "  Deployment Complete" -ForegroundColor Cyan
Write-Host "=======================================================" -ForegroundColor Cyan
Write-Host "  Service name : $ServiceName"
Write-Host "  Install path : $InstallPath"
Write-Host "  Log path     : $LogPath"
Write-Host "  Status       : $($svc.Status)"
Write-Host "  Recovery     : restart after 60s on ALL failures"
Write-Host "  Startup      : Automatic (Delayed)"
Write-Host ""
Write-Host "  TO CHECK LOGS:" -ForegroundColor Yellow
Write-Host "    Get-Content '$LogPath\ahni-rse-$(Get-Date -f yyyy-MM-dd).log' -Wait -Tail 50"
Write-Host ""
Write-Host "  TO MANUALLY STOP/START:" -ForegroundColor Yellow
Write-Host "    Stop-Service  $ServiceName"
Write-Host "    Start-Service $ServiceName"
Write-Host ""

