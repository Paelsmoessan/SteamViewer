@echo off
cd /d "%~dp0"
echo Building server...
dotnet build src\SteamViewer.Server --verbosity quiet
if errorlevel 1 (
    echo Build failed!
    pause
    exit /b 1
)
echo Starting server...
cd /d "%~dp0src\SteamViewer.Server\bin\Debug\net8.0"
SteamViewer.Server.exe
