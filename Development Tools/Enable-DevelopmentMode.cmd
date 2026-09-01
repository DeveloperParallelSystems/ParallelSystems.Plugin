@echo off
setx PARALLEL_SYSTEMS_DEVELOPMENT_MODE "1" >nul
if errorlevel 1 (
    echo Unable to enable development mode.
    pause
    exit /b 1
)

echo Development mode enabled
echo Restart Revit to apply the change.
pause
