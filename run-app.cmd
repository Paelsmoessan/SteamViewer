@echo off
cd /d "%~dp0"
echo Building app...
dotnet build src\SteamViewer.App -f net8.0-windows10.0.19041.0 --verbosity quiet
if errorlevel 1 (
    echo Build failed!
    pause
    exit /b 1
)
echo Starting app...
cd /d "%~dp0src\SteamViewer.App\bin\Debug\net8.0-windows10.0.19041.0\win10-x64"
start "" SteamViewer.App.exe
