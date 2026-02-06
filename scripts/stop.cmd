@echo off
taskkill /IM SteamViewer.Server.exe /F >nul 2>&1
taskkill /IM SteamViewer.App.exe /F >nul 2>&1
echo Stopped
