@echo off
REM TruckingAdvisor setup from source.
REM Runs Install_TruckingAdvisor.ps1, which builds the full app with the .NET 8 SDK when available,
REM or the lightweight simulator window with Windows' built-in compiler otherwise.
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Sta -File "%~dp0Install_TruckingAdvisor.ps1"
endlocal
