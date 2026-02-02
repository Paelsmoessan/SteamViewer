@echo off
setlocal

echo ========================================
echo  SteamViewer.NET Debug Launcher
echo ========================================
echo.

cd /d "%~dp0"

REM Kill any existing app instances
echo [1/2] Stopping existing app instances...
taskkill /IM SteamViewer.App.exe /F >nul 2>&1
ping -n 2 127.0.0.1 >nul

REM Build app only (server runs on Railway)
echo [2/2] Building app...
dotnet build src\SteamViewer.App -f net8.0-windows10.0.19041.0 --verbosity quiet
if errorlevel 1 (
    echo ERROR: App build failed
    pause
    exit /b 1
)
echo       Build complete.

REM Start App
echo.
echo Starting app...
set APPDIR=%~dp0src\SteamViewer.App\bin\Debug\net8.0-windows10.0.19041.0\win10-x64

start "SteamViewer" cmd /k "cd /d %APPDIR% && SteamViewer.App.exe"

echo.
echo ========================================
echo  App started!
echo ========================================
echo.
echo  Server:  Railway (remote)
echo  Host:    ID=123456789  Pass=TESTPASS
echo.
echo  Press any key to stop app...
pause >nul

REM Cleanup
echo Stopping app...
taskkill /IM SteamViewer.App.exe /F >nul 2>&1

echo Done.
endlocal
