@echo off
cd /d "%~dp0"
echo Downloading full runtime (~170MB)...
set GH="%ProgramFiles%\GitHub CLI\gh.exe"
%GH% release download dev-full -p "full-build.zip" -R Paelsmoessan/SteamViewer.NET --clobber
if not exist full-build.zip (
    echo ERROR: Download failed. Run 'gh auth login' first.
    exit /b 1
)
echo Extracting...
powershell -Command "Expand-Archive -Path 'full-build.zip' -DestinationPath '.' -Force"
del full-build.zip
echo Runtime installed! Now use download-run.cmd for updates.
