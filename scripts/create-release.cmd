@echo off
setlocal enabledelayedexpansion

echo.
echo ==========================================
echo   SteamViewer Release Tagger
echo ==========================================
echo.

set "PUBLIC_REPO=D:\_Development\SteamViewer"
set "GH_REPO=Paelsmoessan/SteamViewer"

if not exist "%PUBLIC_REPO%\.git" (
    echo ERROR: Public repo not found at %PUBLIC_REPO%
    exit /b 1
)

:: Get latest release tag from GitHub
set "LATEST="
for /f "tokens=*" %%t in ('gh release list --repo %GH_REPO% --limit 1 --json tagName -q ".[0].tagName" 2^>nul') do set "LATEST=%%t"

if not defined LATEST (
    echo   No previous releases found. Starting at v0.1.0-alpha
    set "NEXT_PATCH=0"
    goto :show_tag
)

echo   Latest release: %LATEST%

:: Parse patch number from tag like v0.1.2-alpha or v0.1.0-alpha.1
:: Strip "v" prefix
set "VER=%LATEST:~1%"
:: Strip "-alpha" and anything after
for /f "tokens=1 delims=-" %%v in ("%VER%") do set "VER_CLEAN=%%v"
:: Get patch number (third segment): 0.1.2 -> 2
for /f "tokens=3 delims=." %%p in ("%VER_CLEAN%") do set "CURRENT_PATCH=%%p"

if not defined CURRENT_PATCH (
    echo ERROR: Could not parse patch number from %LATEST%
    exit /b 1
)

set /a NEXT_PATCH=CURRENT_PATCH+1

:show_tag
set "TAG=v0.1.%NEXT_PATCH%-alpha"

echo   Next release:   %TAG%
echo.

:: Allow override if passed as argument
if "%~1" neq "" (
    set "TAG=%~1"
    echo   Override tag:   %TAG%
    echo.
)

cd /d "%PUBLIC_REPO%"

:: Check if tag already exists
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
echo   https://github.com/%GH_REPO%/actions
echo ==========================================
echo.
