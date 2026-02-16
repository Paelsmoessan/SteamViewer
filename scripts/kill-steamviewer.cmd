@echo off
echo Killing SteamViewer processes...
taskkill /im SteamViewer.App.exe /f >nul 2>&1
taskkill /im msedgewebview2.exe /f >nul 2>&1
echo Done.
