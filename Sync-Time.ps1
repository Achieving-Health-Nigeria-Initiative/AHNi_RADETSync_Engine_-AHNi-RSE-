##############################################################################
#  Sync-Time.ps1
#  AHNi — Facility Clock Synchronisation
#
#  Syncs the machine clock to pool.ntp.org (and fallbacks).
#  Required on facilities whose clock drifts, causing Azure 403 auth failures.
#
#  Run as Administrator:
#    .\Sync-Time.ps1
##############################################################################

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# -- 0. Elevation check --------------------------------------------------------
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
{
    Write-Error "This script must be run as Administrator."
    exit 1
}

Write-Host ""
Write-Host "=======================================================" -ForegroundColor Cyan
Write-Host "  AHNi Facility Clock Synchronisation" -ForegroundColor Cyan
Write-Host "=======================================================" -ForegroundColor Cyan
Write-Host ""

# -- 1. Show current time and sync status before fix ---------------------------
Write-Host "[TIME] Current system time : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Yellow
Write-Host ""
Write-Host "[W32TM] Status BEFORE sync:" -ForegroundColor Cyan
try { w32tm /query /status 2>&1 | ForEach-Object { Write-Host "        $_" } }
catch { Write-Warning "w32tm status query failed: $_" }
Write-Host ""

# -- 2. Configure NTP servers --------------------------------------------------
$NtpServers = "pool.ntp.org,0.africa.pool.ntp.org,time.windows.com"

Write-Host "[NTP] Configuring NTP peers: $NtpServers" -ForegroundColor Yellow
w32tm /config /manualpeerlist:"$NtpServers" /syncfromflags:manual /reliable:YES /update | Out-Null
Write-Host "[NTP] NTP peers set." -ForegroundColor Green

# -- 3. Restart Windows Time service -------------------------------------------
Write-Host "[SVC] Restarting Windows Time service (w32tm)..." -ForegroundColor Yellow
Stop-Service  -Name w32tm -Force -ErrorAction SilentlyContinue
Start-Service -Name w32tm
Start-Sleep   -Seconds 2
Write-Host "[SVC] Windows Time service running." -ForegroundColor Green

# -- 4. Force immediate resync -------------------------------------------------
Write-Host "[SYNC] Forcing immediate time resync..." -ForegroundColor Yellow
$resync = w32tm /resync /force 2>&1
Write-Host "       $resync"

# Brief wait for the sync to settle
Start-Sleep -Seconds 3

# -- 5. Show updated time and status -------------------------------------------
Write-Host ""
Write-Host "[TIME] System time AFTER sync : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Green
Write-Host ""
Write-Host "[W32TM] Status AFTER sync:" -ForegroundColor Cyan
try { w32tm /query /status 2>&1 | ForEach-Object { Write-Host "        $_" } }
catch { Write-Warning "w32tm status query failed: $_" }

# -- 6. Set Windows Time service to start automatically -----------------------
Write-Host ""
Write-Host "[SVC] Setting Windows Time service to Automatic startup..." -ForegroundColor Yellow
Set-Service -Name w32tm -StartupType Automatic
Write-Host "[SVC] Done." -ForegroundColor Green

# -- 7. Summary ----------------------------------------------------------------
Write-Host ""
Write-Host "=======================================================" -ForegroundColor Green
Write-Host "  Clock Sync Complete" -ForegroundColor Green
Write-Host "=======================================================" -ForegroundColor Green
Write-Host "  Current time : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Write-Host "  NTP servers  : $NtpServers"
Write-Host "  w32tm startup: Automatic"
Write-Host ""
Write-Host "  If 'Stratum' in the status above is 3 or 4, the clock is synced." -ForegroundColor Cyan
Write-Host "  If it shows 'Leap Indicator: 3 (not synchronized)', wait 30s and re-run." -ForegroundColor Yellow
Write-Host ""
