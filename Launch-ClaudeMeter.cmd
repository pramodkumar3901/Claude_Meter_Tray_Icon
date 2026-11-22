@echo off
rem One-click launcher: closes any running Claude Meter, rebuilds, and starts it.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Launch-ClaudeMeter.ps1"
if errorlevel 1 (
    echo.
    echo Launch failed. See the message above.
    pause
)
