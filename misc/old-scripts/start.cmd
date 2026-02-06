@echo off
cd /d "%~dp0"
taskkill /IM SteamViewer.Server.exe /F >nul 2>&1
taskkill /IM SteamViewer.App.exe /F >nul 2>&1
start "" "src\SteamViewer.Server\bin\Debug\net8.0\SteamViewer.Server.exe"
timeout /t 2 /nobreak >nul
start "" "src\SteamViewer.App\bin\Debug\net8.0-windows10.0.19041.0\win10-x64\SteamViewer.App.exe"
echo Started (no build)
