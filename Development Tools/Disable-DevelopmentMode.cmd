@echo off
reg delete "HKCU\Environment" /v PARALLEL_SYSTEMS_DEVELOPMENT_MODE /f >nul 2>&1

echo Development mode disabled
echo Restart Revit to apply the change.
pause
