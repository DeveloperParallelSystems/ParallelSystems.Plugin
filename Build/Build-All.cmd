@echo off
setlocal
set "CONFIGURATION=%~1"
if "%CONFIGURATION%"=="" set "CONFIGURATION=Debug"
set "VERSION=%~2"
if "%VERSION%"=="" set "VERSION=All"
set "DEPLOY_SWITCH="
if /I "%~3"=="Deploy" set "DEPLOY_SWITCH=-Deploy"

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-All.ps1" -Configuration "%CONFIGURATION%" -RevitVersion "%VERSION%" %DEPLOY_SWITCH%
exit /b %errorlevel%
