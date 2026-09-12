@echo off
title VisionForge - Environment Check
chcp 65001 >nul 2>&1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\check-env.ps1" %*
echo.
pause
