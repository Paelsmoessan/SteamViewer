@echo off
cd /d "%~dp0"
echo Downloading latest app update (~3MB)...
set GH="%ProgramFiles%\GitHub CLI\gh.exe"
%GH% release download dev-builds -p "app-update.zip" -R Paelsmoessan/SteamViewer.NET --clobber
if not exist app-update.zip (
    echo ERROR: Download failed. Run 'gh auth login' first.
    exit /b 1
)
echo Extracting...
powershell -Command "Expand-Archive -Path 'app-update.zip' -DestinationPath '.' -Force"
del app-update.zip
echo Starting...
start SteamViewer.App.exe
