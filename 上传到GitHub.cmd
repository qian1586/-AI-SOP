@echo off
rem Upload this project to GitHub (version number is bumped automatically).
rem NOTE: keep this file ASCII-only. cmd.exe reads .cmd in the console code page,
rem so non-ASCII text here would show as garbage. All Chinese messages live in
rem scripts\upload-to-github.ps1 (which is saved as UTF-8 with BOM).
title Upload to GitHub - VisionForge AI-SOP
echo.
echo   Uploading project to GitHub (version will be auto-incremented)...
echo.
echo   To add a note:
echo       scripts\upload-to-github.ps1 -Note "your note"
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\upload-to-github.ps1" %*
set "RC=%ERRORLEVEL%"
echo.
if "%RC%"=="0" (
    echo   [ DONE ] Upload finished. Press any key to close.
) else (
    echo   [ FAILED ] Follow the instructions above. Press any key to close.
)
pause >nul
exit /b %RC%
