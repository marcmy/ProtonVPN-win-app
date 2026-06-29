@echo off
setlocal

set "SCRIPT=%~dp0Install-ProtonVPNPatch.ps1"
set "PAYLOAD=%~dp0payload.zip"

if not exist "%SCRIPT%" (
    echo Installer script not found: "%SCRIPT%"
    pause
    exit /b 1
)

if exist "%PAYLOAD%" (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" -PatchPath "%PAYLOAD%"
) else (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"
)

set "EXITCODE=%ERRORLEVEL%"
if not "%EXITCODE%"=="0" (
    echo.
    echo Proton VPN patch installation failed with exit code %EXITCODE%.
    pause
)

exit /b %EXITCODE%
