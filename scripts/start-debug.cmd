@echo off
setlocal

echo ========================================
echo  SteamViewer.NET Debug Launcher
echo ========================================
echo.

pushd "%~dp0.."
set LOGFILE=%CD%\logs\build.log
if not exist logs mkdir logs
call :log "=== %~nx0 started ==="

REM Kill any existing app instances
echo [1/2] Stopping existing app instances...
taskkill /IM SteamViewer.App.exe /F >nul 2>&1
ping -n 2 127.0.0.1 >nul

REM Build app only (server runs on Railway)
echo [2/2] Building app...
call :log "Building app..."
dotnet build src\SteamViewer.App -f net8.0-windows10.0.19041.0 --verbosity quiet
if errorlevel 1 (
    call :log "BUILD FAILED"
    echo ERROR: App build failed
    popd
    pause
    exit /b 1
)
call :log "Build succeeded"
echo       Build complete.

REM Start App
echo.
echo Starting app...
set APPDIR=%CD%\src\SteamViewer.App\bin\Debug\net8.0-windows10.0.19041.0\win10-x64

start "SteamViewer" cmd /k "cd /d %APPDIR% && SteamViewer.App.exe"
call :log "App launched from %APPDIR%"

echo.
echo ========================================
echo  App started!
echo ========================================
echo.
echo  Server:  Railway (remote)
echo  Host:    ID and password shown in the app window
echo.
echo  Press any key to stop app...
pause >nul

REM Cleanup
echo Stopping app...
taskkill /IM SteamViewer.App.exe /F >nul 2>&1
call :log "App stopped"

popd
echo Done.
endlocal
goto :eof

:log
echo [%date% %time%] %~1 >> "%LOGFILE%"
echo [%date% %time%] %~1
goto :eof
