@echo off
title VisionForge - One Click Setup
chcp 65001 >nul 2>&1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\one-click-setup.ps1" %*
set "RC=%ERRORLEVEL%"
echo.
echo (exit code: %RC%)
echo.
pause
exit /b %RC%
