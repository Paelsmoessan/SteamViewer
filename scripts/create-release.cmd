@echo off
setlocal enabledelayedexpansion

echo.
echo ==========================================
echo   SteamViewer Release Tagger
echo ==========================================
echo.

set "PUBLIC_REPO=D:\_Development\SteamViewer"

if not exist "%PUBLIC_REPO%\.git" (
    echo ERROR: Public repo not found at %PUBLIC_REPO%
    exit /b 1
)

:: Get version from the app DLL if available, otherwise prompt
set "VERSION="
if "%~1" neq "" (
    set "VERSION=%~1"
) else (
    set "APP_DLL=src\SteamViewer.App\bin\Release\net8.0-windows10.0.19041.0\win10-x64\SteamViewer.App.dll"
    if exist "%APP_DLL%" (
        for /f "tokens=*" %%v in ('powershell -Command "(Get-Item '%APP_DLL%').VersionInfo.ProductVersion -replace '\+.*$',''"') do set "AUTO_VER=%%v"
        echo   Detected version from build: !AUTO_VER!
    )
    echo.
    set /p VERSION="Tag version (e.g., 0.1.42): "
)

if "%VERSION%"=="" (
    echo ERROR: No version specified.
    exit /b 1
)

:: Ensure v prefix
set "TAG=v%VERSION%"
echo %VERSION% | findstr /b "v" >nul && set "TAG=%VERSION%"

echo.
echo   Tag: %TAG%
echo   Repo: %PUBLIC_REPO%
echo.

:: Check if tag already exists on public repo
cd /d "%PUBLIC_REPO%"
git tag -l "%TAG%" | findstr "%TAG%" >nul 2>&1
if %errorlevel%==0 (
    echo ERROR: Tag %TAG% already exists on public repo.
    exit /b 1
)

echo This will:
echo   1. Create tag %TAG% on the public repo
echo   2. Push to GitHub (triggers release build)
echo.
set /p CONFIRM="Proceed? (y/n): "
if /i not "%CONFIRM%"=="y" (
    echo Aborted.
    exit /b 0
)

git tag "%TAG%"
git push origin "%TAG%"

echo.
echo ==========================================
echo   Tag %TAG% pushed - release build triggered
echo   https://github.com/Paelsmoessan/SteamViewer/actions
echo ==========================================
echo.
