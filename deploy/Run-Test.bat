@echo off
rem  AHNi-RSE TEST BUILD - runs AHNiRSE.exe in this window, NOT as a Windows service.
rem  The installed AHNi-RSE / AutoReport service keeps running and is not touched.
rem  This build uploads workbooks to TEST-RADET\<date>\ (not RADET), runs force backfills
rem  (workbook, raw, custom SQL) from the Backfill app, and never installs updates.
rem  Close this window (or press Ctrl+C) to stop it. Logs: the "logs" folder next to this file.
cd /d "%~dp0"
title AHNi-RSE TEST - keep this window open
echo.
echo  =====================================================
echo   AHNi-RSE TEST BUILD - running in this window
echo   Uploads to TEST-RADET. The installed service is not touched.
echo   Keep this window open; close it to stop.
echo  =====================================================
echo.
"%~dp0AHNiRSE.exe"
echo.
echo  AHNi-RSE stopped (exit code %errorlevel%). Logs: %~dp0logs
pause
