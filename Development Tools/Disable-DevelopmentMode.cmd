@echo off
setx PARALLEL_SYSTEMS_DEVELOPMENT_MODE "" >nul
reg delete "HKCU\Environment" /v PARALLEL_SYSTEMS_DEVELOPMENT_MODE /f >nul 2>&1

echo Development mode disabled
echo Restart Revit and the application used to launch it to apply the change.
pause
