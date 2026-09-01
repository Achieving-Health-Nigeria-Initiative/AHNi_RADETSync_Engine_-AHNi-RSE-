@echo off
setlocal
echo.
echo  =====================================================
echo   AHNi RADETSync Engine (AHNi-RSE)  v2026-07-01
echo   Facility Data Uploader - Service Installer
echo  =====================================================
echo   Installing from: %~dp0
echo.
echo   This installer will:
echo     - Stop and remove any legacy AutoReport / AutoRADET service
echo     - Install (or update) the AHNi-RSE Windows Service
echo     - Configure automatic restart on failure
echo     - Add antivirus and firewall exclusions
echo.

set "DEPLOY_SCRIPT=%~dp0Deploy-AHNi-RSEService.ps1"
if not exist "%DEPLOY_SCRIPT%" (
  echo [ERROR] Deploy script not found: %DEPLOY_SCRIPT%
  echo [ERROR] Ensure Deploy-AHNi-RSEService.ps1 is in the same folder as this bat file.
  echo.
  pause
  endlocal
  exit /b 1
)

echo [CHECK] Validating deploy script syntax...
PowerShell -NoProfile -ExecutionPolicy Bypass -Command ^
  "try { [void][scriptblock]::Create((Get-Content -Raw '%DEPLOY_SCRIPT%')); Write-Host '[CHECK] OK'; exit 0 } catch { Write-Error $_; exit 1 }"
if errorlevel 1 (
  echo [ERROR] Deploy script parse failed. This usually means file encoding corruption.
  echo [ERROR] Replace Deploy-AHNi-RSEService.ps1 with a clean copy and retry.
  echo.
  pause
  endlocal
  exit /b 1
)

PowerShell -NoProfile -ExecutionPolicy Bypass -File "%DEPLOY_SCRIPT%"
echo.
pause
endlocal
