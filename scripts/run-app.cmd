@echo off
pushd "%~dp0.."
set LOGFILE=%CD%\logs\build.log
if not exist logs mkdir logs
call :log "=== %~nx0 started ==="

echo Building and running SteamViewer...
call :log "Building app..."
dotnet run --project src/SteamViewer.App -f net8.0-windows10.0.19041.0
if errorlevel 1 (
    call :log "BUILD/RUN FAILED"
) else (
    call :log "Run completed"
)

popd
goto :eof

:log
echo [%date% %time%] %~1 >> "%LOGFILE%"
echo [%date% %time%] %~1
goto :eof
