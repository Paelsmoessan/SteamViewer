@echo off
setlocal enabledelayedexpansion

echo ========================================
echo  SteamViewer.NET Debug Launcher
echo ========================================
echo.

cd /d "%~dp0"

REM Kill any existing processes
echo [1/4] Stopping existing processes...
for /f "tokens=5" %%a in ('netstat -ano ^| findstr :8080 ^| findstr LISTENING 2^>nul') do (
    taskkill /PID %%a /F >nul 2>&1
)
taskkill /IM SteamViewer.Server.exe /F >nul 2>&1
taskkill /IM SteamViewer.App.exe /F >nul 2>&1
ping -n 2 127.0.0.1 >nul

REM Build
echo [2/4] Building projects...
dotnet build src\SteamViewer.Server --verbosity quiet
if errorlevel 1 (
    echo ERROR: Server build failed
    pause
    exit /b 1
)
dotnet build src\SteamViewer.App -f net8.0-windows10.0.19041.0 --verbosity quiet
if errorlevel 1 (
    echo ERROR: App build failed
    pause
    exit /b 1
)
echo       Build complete.

REM Start Server
echo [3/4] Starting signaling server...
start "SteamViewer Server" /min cmd /k "cd /d %~dp0src\SteamViewer.Server\bin\Debug\net8.0 && SteamViewer.Server.exe"
ping -n 3 127.0.0.1 >nul

REM Verify server
netstat -ano | findstr :8080 | findstr LISTENING >nul
if errorlevel 1 (
    echo ERROR: Server failed to start on port 8080
    pause
    exit /b 1
)
echo       Server running on port 8080

REM Start Apps
echo [4/4] Starting app instances...
set APPDIR=%~dp0src\SteamViewer.App\bin\Debug\net8.0-windows10.0.19041.0\win10-x64

echo       - Host (Support Client)
start "SteamViewer Host" cmd /k "cd /d %APPDIR% && SteamViewer.App.exe"
ping -n 3 127.0.0.1 >nul

echo       - Viewer (Support Technician)
start "SteamViewer Viewer" cmd /k "cd /d %APPDIR% && SteamViewer.App.exe"

echo.
echo ========================================
echo  Debug session started!
echo ========================================
echo.
echo  Server:  http://localhost:8080
echo  Host:    ID=123456789  Pass=TESTPASS
echo  Viewer:  Pre-filled with test credentials
echo.
echo  Close this window to stop the server.
echo  Press any key to stop all processes...
pause >nul

REM Cleanup
echo Stopping all processes...
taskkill /IM SteamViewer.Server.exe /F >nul 2>&1
taskkill /IM SteamViewer.App.exe /F >nul 2>&1

echo Done.
endlocal
