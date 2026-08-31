@echo off
setlocal
set "VERSION=%~1"
if "%VERSION%"=="" set "VERSION=2025"
set "CONFIGURATION=%~2"
if "%CONFIGURATION%"=="" set "CONFIGURATION=Debug"

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Deploy-Revit-Full.ps1" -RevitVersion "%VERSION%" -Configuration "%CONFIGURATION%"
exit /b %errorlevel%
