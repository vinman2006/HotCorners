@echo off
title Smart Hot Corners Installer
cd /d "%~dp0"
echo ========================================================
echo         Smart Hot Corners for Windows 11 / 10
echo ========================================================
echo Installing...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
if %errorlevel% neq 0 (
    echo.
    echo Installation failed! Press any key to see error details...
    pause
    exit /b %errorlevel%
)
echo.
echo Press any key to finish...
pause >nul
exit /b 0

