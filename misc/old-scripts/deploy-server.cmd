@echo off
setlocal

echo ========================================
echo  SteamViewer Server - Build ^& Deploy
echo ========================================
echo.

cd /d "%~dp0"

REM Build server
echo Building server...
dotnet build src\SteamViewer.Server -c Release --verbosity quiet
if errorlevel 1 (
    echo ERROR: Server build failed
    pause
    exit /b 1
)
echo Build complete.

echo.
echo Committing and pushing to trigger Railway deploy...
git add src/SteamViewer.Server/
git status --short src/SteamViewer.Server/

echo.
set /p COMMIT_MSG="Commit message (or press Enter to skip): "
if "%COMMIT_MSG%"=="" (
    echo Skipping commit - just built locally.
) else (
    git commit -m "%COMMIT_MSG%"
    git push
    echo.
    echo Pushed to main - Railway will auto-deploy.
)

echo.
echo Done.
pause
endlocal
