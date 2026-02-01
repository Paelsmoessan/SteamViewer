@echo off
setlocal

:: Destination folder - network share on test machine
set DEST=\\MEDIASERVER\SteamViewer

echo [1/2] Building...
dotnet build src\SteamViewer.App -f net8.0-windows10.0.19041.0 -v q
if %ERRORLEVEL% NEQ 0 (
    echo Build FAILED
    exit /b 1
)

echo [2/2] Deploying to %DEST%...
set SRC=%~dp0src\SteamViewer.App\bin\Debug\net8.0-windows10.0.19041.0\win10-x64

if not exist "%DEST%" mkdir "%DEST%"

robocopy "%SRC%" "%DEST%" /MIR /NFL /NDL /NJH /NJS /NC /NS /MT:8 /XF nul

if %ERRORLEVEL% LEQ 7 (
    echo.
    echo === Done! Run: %DEST%\SteamViewer.App.exe ===
) else (
    echo Deploy failed with error %ERRORLEVEL%
)

endlocal
