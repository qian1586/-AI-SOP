@echo off
title VisionForge - Deploy to D:\VisionForge
chcp 65001 >nul 2>&1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\deploy-to-folder.ps1" %*
set "RC=%ERRORLEVEL%"
echo.
echo (exit code: %RC%)
echo.
pause
exit /b %RC%
