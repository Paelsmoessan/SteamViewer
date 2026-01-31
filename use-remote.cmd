@echo off
cd /d "%~dp0"
copy /Y "src\SteamViewer.App\appsettings.remote.json" "src\SteamViewer.App\appsettings.json" >nul
echo Switched to REMOTE mode (Railway)
echo TURN: enabled (metered.ca)
