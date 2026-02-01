@echo off
setlocal

:: Destination folder - network share on test machine
set DEST=\\MEDIASERVER\SteamViewer

:: Source folder
set SRC=%~dp0src\SteamViewer.App\bin\Debug\net8.0-windows10.0.19041.0\win10-x64

echo Deploying SteamViewer to %DEST%...

:: Create destination if it doesn't exist
if not exist "%DEST%" mkdir "%DEST%"

:: Robocopy with mirror mode (fast, only copies changed files)
:: /MIR = mirror (delete files not in source)
:: /NFL /NDL = no file/dir listing (quieter)
:: /NJH /NJS = no job header/summary
:: /NC /NS = no class/size info
:: /MT:8 = 8 threads for speed
robocopy "%SRC%" "%DEST%" /MIR /NFL /NDL /NJH /NJS /NC /NS /MT:8 /XF nul

if %ERRORLEVEL% LEQ 7 (
    echo Deploy complete!
    echo Run from: %DEST%\SteamViewer.App.exe
) else (
    echo Deploy failed with error %ERRORLEVEL%
)

endlocal
