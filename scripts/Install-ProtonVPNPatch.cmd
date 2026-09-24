@echo off
setlocal

set "SCRIPT=%~dp0Install-ProtonVPNPatch.ps1"
set "PAYLOAD=%~dp0payload.zip"
set "POWERSHELL=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"

if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" (
    set "POWERSHELL=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
)

if not exist "%SCRIPT%" (
    echo Installer script not found: "%SCRIPT%"
    pause
    exit /b 1
)

if not exist "%POWERSHELL%" (
    echo Windows PowerShell not found: "%POWERSHELL%"
    pause
    exit /b 1
)

if exist "%PAYLOAD%" (
    "%POWERSHELL%" -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" -PatchPath "%PAYLOAD%"
) else (
    "%POWERSHELL%" -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"
)

set "EXITCODE=%ERRORLEVEL%"
echo.
if "%EXITCODE%"=="0" (
    echo Proton VPN patch installation completed successfully.
) else (
    echo Proton VPN patch installation failed with exit code %EXITCODE%.
)
echo.
pause

exit /b %EXITCODE%
