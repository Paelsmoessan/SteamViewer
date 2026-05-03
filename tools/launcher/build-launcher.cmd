@echo off
REM Build SteamViewer Launcher - Compiles PowerShell to EXE
REM Requires: Install-Module ps2exe -Scope CurrentUser

setlocal enabledelayedexpansion
cd /d "%~dp0"

echo ========================================
echo  Building SteamViewer Launcher
echo ========================================
echo.

REM Check if ps2exe is installed
powershell -Command "if (-not (Get-Module -ListAvailable -Name ps2exe)) { Write-Host 'ps2exe not found. Installing...' -ForegroundColor Yellow; Install-Module ps2exe -Scope CurrentUser -Force }"

REM Get version from git tag or use default
for /f "delims=" %%v in ('git describe --tags --abbrev^=0 2^>nul') do set VERSION=%%v
if "%VERSION%"=="" set VERSION=1.0.0

REM Remove 'v' prefix if present
set VERSION=%VERSION:v=%

echo Building version: %VERSION%
echo.

REM Build the launcher
powershell -Command "Invoke-ps2exe -InputFile 'Launcher.ps1' -OutputFile 'SteamViewer.Launcher.exe' -NoConsole -Title 'SteamViewer Launcher' -Description 'Downloads and runs SteamViewer Server' -Company 'SteamViewer' -Product 'SteamViewer Launcher' -Version '%VERSION%' -Copyright '(c) 2026 SteamViewer' -RequireAdmin:$false"

if exist "SteamViewer.Launcher.exe" (
    echo.
    echo ========================================
    echo  Build successful!
    echo ========================================
    echo.
    echo Output: %cd%\SteamViewer.Launcher.exe
    for %%A in (SteamViewer.Launcher.exe) do echo Size: %%~zA bytes
) else (
    echo.
    echo Build failed!
    exit /b 1
)
