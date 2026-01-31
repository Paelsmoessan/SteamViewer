@echo off
REM Debug launcher for SteamViewer.NET
REM Usage: debug.cmd [mode]
REM   mode: terminal, exe, both, dual-terminal (default)

setlocal

set MODE=%1
if "%MODE%"=="" set MODE=dual-terminal

echo Starting SteamViewer.NET debug session (mode: %MODE%)
echo.

powershell -ExecutionPolicy Bypass -File "%~dp0debug.ps1" -Mode %MODE%

endlocal
