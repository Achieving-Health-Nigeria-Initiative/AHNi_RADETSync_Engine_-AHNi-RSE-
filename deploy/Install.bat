@echo off
setlocal

rem -- Run as Administrator: when double-clicked without admin rights, ask Windows (UAC prompt) to
rem    start this same file again as Administrator, then close this window.
fltmc >nul 2>&1
if errorlevel 1 (
  echo Requesting Administrator rights - choose Yes in the Windows prompt...
  set "SELF=%~f0"
  set "HERE=%~dp0"
  call :elevate
  exit /b
)
cd /d "%~dp0"
echo.
echo  =====================================================
set "RSE_VERSION=unknown"
for /f "usebackq delims=" %%v in (`PowerShell -NoProfile -Command "try { (Get-Item -LiteralPath '%~dp0AHNiRSE.exe').VersionInfo.ProductVersion.Split('+')[0] } catch { 'unknown' }"`) do set "RSE_VERSION=%%v"
echo   AHNi RADETSync Engine (AHNi-RSE)  version %RSE_VERSION%
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
exit /b

:elevate
rem Single quotes in the path are doubled for PowerShell's single-quoted strings.
set "SELF=%SELF:'=''%"
set "HERE=%HERE:'=''%"
PowerShell -NoProfile -ExecutionPolicy Bypass -Command "try { Start-Process -FilePath '%SELF%' -WorkingDirectory '%HERE%' -Verb RunAs -ErrorAction Stop } catch { exit 1 }"
if errorlevel 1 (
  echo.
  echo [ERROR] Administrator rights were not granted.
  echo         Right-click Install.bat and choose "Run as administrator".
  echo.
  pause
)
exit /b
