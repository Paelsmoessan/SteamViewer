@echo off
setlocal enabledelayedexpansion

echo.
echo ==========================================
echo   SteamViewer Release Builder
echo ==========================================
echo.

set "ROOT=%~dp0.."
set "APP_PROJ=%ROOT%\src\SteamViewer.App"
set "LAUNCHER_PROJ=%ROOT%\tools\app-launcher"
set "PAYLOAD_DIR=%LAUNCHER_PROJ%\payload"
set "OUTPUT_DIR=%ROOT%\release-output"
set "PUBLIC_REPO=Paelsmoessan/SteamViewer"

:: Step 1: Build the app (Release)
echo [1/6] Building SteamViewer.App (Release)...
dotnet build "%APP_PROJ%" -c Release -f net8.0-windows10.0.19041.0
if errorlevel 1 (
    echo [ERROR] App build failed.
    exit /b 1
)

set "APP_BIN=%APP_PROJ%\bin\Release\net8.0-windows10.0.19041.0\win10-x64"

:: Extract version from built assembly
for /f "tokens=*" %%v in ('powershell -Command "(Get-Item '%APP_BIN%\SteamViewer.App.dll').VersionInfo.ProductVersion -replace '\+.*$','' "') do set "VERSION=%%v"
echo   Version: %VERSION%

:: Step 2: Clean up build output
echo.
echo [2/6] Cleaning build output...
if exist "%APP_BIN%\*.pdb" del /q "%APP_BIN%\*.pdb"
if exist "%APP_BIN%\gist-id.txt" del /q "%APP_BIN%\gist-id.txt"
if exist "%APP_BIN%\kill-steamviewer.cmd" del /q "%APP_BIN%\kill-steamviewer.cmd"

:: Step 3: Zip the app
echo.
echo [3/6] Creating payload zip...
if not exist "%PAYLOAD_DIR%" mkdir "%PAYLOAD_DIR%"
if exist "%PAYLOAD_DIR%\SteamViewer.zip" del /q "%PAYLOAD_DIR%\SteamViewer.zip"
powershell -Command "Compress-Archive -Path '%APP_BIN%\*' -DestinationPath '%PAYLOAD_DIR%\SteamViewer.zip' -Force"
if errorlevel 1 (
    echo [ERROR] Zip creation failed.
    exit /b 1
)
for %%A in ("%PAYLOAD_DIR%\SteamViewer.zip") do echo   Payload: %%~zA bytes

:: Step 4: Publish the launcher
echo.
echo [4/6] Publishing launcher (self-contained, single-file, trimmed)...
if exist "%OUTPUT_DIR%" rmdir /s /q "%OUTPUT_DIR%"
dotnet publish "%LAUNCHER_PROJ%\SteamViewer.Launcher.csproj" -c Release -o "%OUTPUT_DIR%"
if errorlevel 1 (
    echo [ERROR] Launcher publish failed.
    exit /b 1
)

:: Step 5: Clean up
echo.
echo [5/6] Cleaning up...
if exist "%OUTPUT_DIR%\SteamViewer.pdb" del /q "%OUTPUT_DIR%\SteamViewer.pdb"
if exist "%PAYLOAD_DIR%\SteamViewer.zip" del /q "%PAYLOAD_DIR%\SteamViewer.zip"

for %%A in ("%OUTPUT_DIR%\SteamViewer.exe") do (
    echo   Output: %OUTPUT_DIR%\SteamViewer.exe
    echo   Size:   %%~zA bytes
)

:: Step 6: Create GitHub Release on public repo
echo.
echo [6/6] Publishing to GitHub Releases...
set "TAG=v%VERSION%-alpha"

:: Delete existing release with same tag (if re-running)
gh release delete "%TAG%" --repo %PUBLIC_REPO% --yes 2>nul

:: Create release and upload exe
gh release create "%TAG%" "%OUTPUT_DIR%\SteamViewer.exe" --repo %PUBLIC_REPO% --title "SteamViewer %TAG%" --notes "Early alpha release. Expect rough edges and breaking changes." --prerelease
if errorlevel 1 (
    echo [ERROR] GitHub release failed.
    exit /b 1
)

echo.
echo ==========================================
echo   Release complete! %TAG%
echo ==========================================
echo   https://github.com/%PUBLIC_REPO%/releases/tag/%TAG%
echo.
