@echo off
setlocal
cd /d "%~dp0"

echo ========================================
echo  SteamViewer.NET Debug Launcher
echo ========================================
echo.

REM Kill existing
echo [1/4] Stopping existing processes...
taskkill /IM SteamViewer.Server.exe /F >nul 2>&1
taskkill /IM SteamViewer.App.exe /F >nul 2>&1
timeout /t 1 /nobreak >nul

REM Build
echo [2/4] Building...
dotnet build src\SteamViewer.Server --verbosity quiet || (echo Server build failed & pause & exit /b 1)
dotnet build src\SteamViewer.App -f net8.0-windows10.0.19041.0 --verbosity quiet || (echo App build failed & pause & exit /b 1)

REM Start Server
echo [3/4] Starting server...
start "Server" /min cmd /c "cd /d "%~dp0src\SteamViewer.Server\bin\Debug\net8.0" && SteamViewer.Server.exe"
timeout /t 2 /nobreak >nul

REM Start App
echo [4/4] Starting app...
start "" "%~dp0src\SteamViewer.App\bin\Debug\net8.0-windows10.0.19041.0\win10-x64\SteamViewer.App.exe"

echo.
echo ========================================
echo  Ready! Server on localhost:8080
echo  Test credentials: 123456789 / TESTPASS
echo ========================================
echo.
echo Press any key to stop all...
pause >nul

taskkill /IM SteamViewer.Server.exe /F >nul 2>&1
taskkill /IM SteamViewer.App.exe /F >nul 2>&1
endlocal
