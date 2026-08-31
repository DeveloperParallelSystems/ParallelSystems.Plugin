@echo off
setlocal
set "CONFIGURATION=%~1"
if "%CONFIGURATION%"=="" set "CONFIGURATION=Debug"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Deploy-Revit-Full.ps1" -RevitVersion "2026" -Configuration "%CONFIGURATION%"
exit /b %errorlevel%
