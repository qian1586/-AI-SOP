@echo off
title VisionForge - Rebuild with Fix
chcp 65001 >nul 2>&1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\rebuild-app.ps1" %*
set "RC=%ERRORLEVEL%"
echo.
echo (exit code: %RC%)
echo.
pause
exit /b %RC%
