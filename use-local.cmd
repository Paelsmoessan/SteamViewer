@echo off
cd /d "%~dp0"
copy /Y "src\SteamViewer.App\appsettings.local.json" "src\SteamViewer.App\appsettings.json" >nul
echo Switched to LOCAL mode (localhost:8080)
echo TURN: disabled
