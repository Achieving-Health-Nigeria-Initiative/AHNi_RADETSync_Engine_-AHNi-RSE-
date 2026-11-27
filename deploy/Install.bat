@echo off
setlocal

rem -- Unattended: installs straight away and never waits for a key, so a facility's software installer can run it.
rem    Everything it prints is also written to install.log next to this file. Exit code 0 = installed;
rem    anything else = failed (see install.log).
rem    Paths are only used outside ( ) blocks and are escaped for PowerShell, so any folder works, including
rem    ones with spaces, brackets or apostrophes (e.g. C:\Program Files (x86)\...).

rem -- Run as Administrator: without admin rights, ask Windows (UAC prompt) to start this same file again as
rem    Administrator, then close this window. An installer that already runs as Administrator or SYSTEM skips this.
fltmc >nul 2>&1
if errorlevel 1 goto :elevate

cd /d "%~dp0"
set "HERE=%~dp0"
set "HERE_PS=%HERE:'=''%"
set "LOG=%HERE%install.log"
set "LOG_PS=%HERE_PS%install.log"
set "DEPLOY_SCRIPT=%HERE%Deploy-AHNi-RSEService.ps1"
set "DEPLOY_SCRIPT_PS=%HERE_PS%Deploy-AHNi-RSEService.ps1"

echo.
echo  =====================================================
set "RSE_VERSION=unknown"
for /f "usebackq delims=" %%v in (`PowerShell -NoProfile -Command "try { (Get-Item -LiteralPath '%HERE_PS%AHNiRSE.exe' -ErrorAction Stop).VersionInfo.ProductVersion.Split('+')[0] } catch { 'unknown' }"`) do set "RSE_VERSION=%%v"
set "RSE_TYPE=GEN-POP facility"
for /f "usebackq delims=" %%t in (`PowerShell -NoProfile -Command "try { if ((Get-Content -Raw -LiteralPath '%HERE_PS%appsettings.json') -match '\x22IsCommunityCentre\x22\s*:\s*true') { 'COMMUNITY CENTRE (care)' } else { 'GEN-POP facility' } } catch { 'UNKNOWN - appsettings.json not readable' }"`) do set "RSE_TYPE=%%t"
echo   AHNi RADETSync Engine (AHNi-RSE)  version %RSE_VERSION%
echo   Facility Data Uploader - Service Installer
echo  =====================================================
echo   Installing from: %HERE%
echo   Facility type:   %RSE_TYPE%  (Facility:IsCommunityCentre in appsettings.json)
echo   Log:             %LOG%
echo.
echo   This installer will:
echo     - Stop and remove any legacy AutoReport / AutoRADET service
echo     - Install (or update) the AHNi-RSE Windows Service
echo     - Configure automatic restart on failure
echo     - Add antivirus and firewall exclusions
echo.
> "%LOG%" echo AHNi-RSE install %DATE% %TIME% - version %RSE_VERSION% - %RSE_TYPE% - from %HERE%

if not exist "%DEPLOY_SCRIPT%" goto :no_script

echo [CHECK] Validating deploy script syntax...
PowerShell -NoProfile -ExecutionPolicy Bypass -Command "try { [void][scriptblock]::Create((Get-Content -Raw -LiteralPath '%DEPLOY_SCRIPT_PS%')); Write-Host '[CHECK] OK'; exit 0 } catch { Write-Error $_; exit 1 }"
if errorlevel 1 goto :bad_script

rem The deploy script's output goes to this window and install.log; its exit code is this file's exit code.
PowerShell -NoProfile -ExecutionPolicy Bypass -Command "function Out-Both($o) { $t = ($o | Out-String).TrimEnd(); Write-Host $t; Add-Content -LiteralPath '%LOG_PS%' -Value $t -Encoding Default }; try { & '%DEPLOY_SCRIPT_PS%' *>&1 | ForEach-Object { Out-Both $_ }; exit $LASTEXITCODE } catch { Out-Both $_; exit 1 }"
set "RESULT=%errorlevel%"
echo.
if not "%RESULT%"=="0" goto :failed
echo [DONE] AHNi-RSE installed. Log: %LOG%
>> "%LOG%" echo [DONE] AHNi-RSE installed.
endlocal & exit /b 0

:failed
echo [ERROR] Install failed with exit code %RESULT%. See %LOG%
>> "%LOG%" echo [ERROR] Install failed with exit code %RESULT%.
endlocal & exit /b %RESULT%

:no_script
echo [ERROR] Deploy script not found: %DEPLOY_SCRIPT%
echo [ERROR] Ensure Deploy-AHNi-RSEService.ps1 is in the same folder as this bat file.
>> "%LOG%" echo [ERROR] Deploy script not found: %DEPLOY_SCRIPT%
endlocal & exit /b 1

:bad_script
echo [ERROR] Deploy script parse failed. This usually means file encoding corruption.
echo [ERROR] Replace Deploy-AHNi-RSEService.ps1 with a clean copy and retry.
>> "%LOG%" echo [ERROR] Deploy script parse failed - replace Deploy-AHNi-RSEService.ps1 with a clean copy.
endlocal & exit /b 1

:elevate
echo Requesting Administrator rights - choose Yes in the Windows prompt...
set "SELF=%~f0"
set "SELF_PS=%SELF:'=''%"
set "HERE_PS=%~dp0"
set "HERE_PS=%HERE_PS:'=''%"
PowerShell -NoProfile -ExecutionPolicy Bypass -Command "try { Start-Process -FilePath '%SELF_PS%' -WorkingDirectory '%HERE_PS%' -Verb RunAs -ErrorAction Stop } catch { exit 1 }"
if errorlevel 1 goto :elevate_refused
endlocal & exit /b 0

:elevate_refused
echo.
echo [ERROR] Administrator rights were not granted.
echo         Right-click Install.bat and choose "Run as administrator".
endlocal & exit /b 1
