@echo off
title VisionForge - Install .NET 8 Desktop Runtime
chcp 65001 >nul 2>&1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install-runtime.ps1" %*
set "RC=%ERRORLEVEL%"
echo.
echo (exit code: %RC%)
echo.
pause
exit /b %RC%
