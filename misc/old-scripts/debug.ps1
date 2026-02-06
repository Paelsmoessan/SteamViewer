#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Debug script for SteamViewer.NET - runs server and app instances for testing.
.DESCRIPTION
    This script:
    1. Kills any existing server/app processes
    2. Builds the server and app projects
    3. Starts the signaling server
    4. Runs app instances (configurable: terminal, exe, or both)
.PARAMETER Mode
    "terminal" - Run app via dotnet run (verbose output)
    "exe" - Run built executable
    "both" - Run one terminal instance and one exe instance
    "dual-terminal" - Run two terminal instances (default)
.PARAMETER Verbose
    Enable verbose logging for all processes
.EXAMPLE
    .\debug.ps1 -Mode dual-terminal
    .\debug.ps1 -Mode both -Verbose
#>

param(
    [ValidateSet("terminal", "exe", "both", "dual-terminal")]
    [string]$Mode = "dual-terminal",

    [switch]$SkipBuild,
    [switch]$ServerOnly
)

$ErrorActionPreference = "Stop"
$ProjectRoot = $PSScriptRoot
$ServerProject = Join-Path $ProjectRoot "src\SteamViewer.Server"
$AppProject = Join-Path $ProjectRoot "src\SteamViewer.App"
$ServerExe = Join-Path $ProjectRoot "src\SteamViewer.Server\bin\Debug\net8.0\SteamViewer.Server.exe"
$AppExe = Join-Path $ProjectRoot "src\SteamViewer.App\bin\Debug\net8.0-windows10.0.19041.0\win10-x64\SteamViewer.App.exe"

# Colors for output
function Write-Header($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Write-Step($msg) { Write-Host ">>> $msg" -ForegroundColor Yellow }
function Write-Success($msg) { Write-Host "[OK] $msg" -ForegroundColor Green }
function Write-Fail($msg) { Write-Host "[FAIL] $msg" -ForegroundColor Red }

# Kill existing processes
function Stop-ExistingProcesses {
    Write-Header "Stopping existing processes"

    $processes = @("SteamViewer.Server", "SteamViewer.App")
    foreach ($proc in $processes) {
        $running = Get-Process -Name $proc -ErrorAction SilentlyContinue
        if ($running) {
            Write-Step "Killing $proc..."
            $running | Stop-Process -Force
            Start-Sleep -Milliseconds 500
        }
    }

    # Also kill any dotnet processes running our projects
    $dotnetProcs = Get-Process -Name "dotnet" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*SteamViewer*" }
    if ($dotnetProcs) {
        Write-Step "Killing dotnet processes..."
        $dotnetProcs | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }

    Write-Success "Processes stopped"
}

# Build projects
function Build-Projects {
    Write-Header "Building projects"

    Write-Step "Building Server..."
    $serverResult = & dotnet build $ServerProject --verbosity minimal 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "Server build failed"
        $serverResult | Write-Host
        exit 1
    }
    Write-Success "Server built"

    Write-Step "Building App (Windows only)..."
    # Build only Windows framework to avoid macOS workload issue
    $appResult = & dotnet build $AppProject -f net8.0-windows10.0.19041.0 --verbosity minimal 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "App build failed"
        $appResult | Write-Host
        exit 1
    }
    Write-Success "App built"
}

# Start server
function Start-Server {
    Write-Header "Starting signaling server"

    $serverArgs = @{
        FilePath = "dotnet"
        ArgumentList = "run --project `"$ServerProject`" --no-build"
        WorkingDirectory = $ProjectRoot
        PassThru = $true
    }

    # Start in new window for visibility
    $serverArgs.NoNewWindow = $false

    $script:ServerProcess = Start-Process @serverArgs
    Write-Step "Server PID: $($script:ServerProcess.Id)"

    # Wait for server to start
    Write-Step "Waiting for server to start..."
    Start-Sleep -Seconds 2

    # Check if server is running
    if ($script:ServerProcess.HasExited) {
        Write-Fail "Server failed to start"
        exit 1
    }

    Write-Success "Server running on ws://localhost:8080"
}

# Run app instance via terminal (dotnet run)
function Start-AppTerminal($instanceName) {
    Write-Step "Starting $instanceName (terminal mode)..."

    $env:STEAMVIEWER_INSTANCE = $instanceName
    $env:STEAMVIEWER_VERBOSE = "true"

    $args = @{
        FilePath = "dotnet"
        ArgumentList = "run --project `"$AppProject`" -f net8.0-windows10.0.19041.0 --no-build -- --verbose"
        WorkingDirectory = $ProjectRoot
        PassThru = $true
        NoNewWindow = $false
    }

    return Start-Process @args
}

# Run app instance via exe
function Start-AppExe($instanceName) {
    Write-Step "Starting $instanceName (exe mode)..."

    if (-not (Test-Path $AppExe)) {
        # Try to find the exe
        $exeSearch = Get-ChildItem -Path (Join-Path $AppProject "bin") -Recurse -Filter "SteamViewer.App.exe" |
            Select-Object -First 1
        if ($exeSearch) {
            $script:AppExe = $exeSearch.FullName
        } else {
            Write-Fail "App exe not found. Build may have failed."
            return $null
        }
    }

    $env:STEAMVIEWER_INSTANCE = $instanceName
    $env:STEAMVIEWER_VERBOSE = "true"

    return Start-Process -FilePath $AppExe -PassThru -ArgumentList "--verbose"
}

# Main execution
Write-Host @"

  ____  _                     __     ___
 / ___|| |_ ___  __ _ _ __ ___\ \   / (_) _____      _____ _ __
 \___ \| __/ _ \/ _` | '_ ` _ \\ \ / /| |/ _ \ \ /\ / / _ \ '__|
  ___) | ||  __/ (_| | | | | | |\ V / | |  __/\ V  V /  __/ |
 |____/ \__\___|\__,_|_| |_| |_| \_/  |_|\___| \_/\_/ \___|_|

                     DEBUG LAUNCHER
"@ -ForegroundColor Magenta

Write-Host "Mode: $Mode" -ForegroundColor White
Write-Host ""

try {
    Stop-ExistingProcesses

    if (-not $SkipBuild) {
        Build-Projects
    }

    Start-Server

    if (-not $ServerOnly) {
        Write-Header "Starting app instances"

        $processes = @()

        switch ($Mode) {
            "terminal" {
                $processes += Start-AppTerminal "Instance-1"
            }
            "exe" {
                $processes += Start-AppExe "Instance-1"
            }
            "both" {
                $processes += Start-AppTerminal "Host"
                Start-Sleep -Seconds 2  # Stagger starts
                $processes += Start-AppExe "Viewer"
            }
            "dual-terminal" {
                $processes += Start-AppTerminal "Host"
                Start-Sleep -Seconds 2  # Stagger starts
                $processes += Start-AppTerminal "Viewer"
            }
        }

        $validProcesses = $processes | Where-Object { $_ -ne $null }
        if ($validProcesses.Count -gt 0) {
            Write-Success "Started $($validProcesses.Count) app instance(s)"
            $validProcesses | ForEach-Object { Write-Step "  PID: $($_.Id)" }
        }
    }

    Write-Header "Debug session started"
    Write-Host ""
    Write-Host "Press Ctrl+C to stop all processes..." -ForegroundColor Gray
    Write-Host ""

    # Wait for user interrupt
    while ($true) {
        Start-Sleep -Seconds 1

        # Check if server is still running
        if ($script:ServerProcess.HasExited) {
            Write-Fail "Server has stopped unexpectedly"
            break
        }
    }
}
catch {
    Write-Fail "Error: $_"
}
finally {
    Write-Header "Cleaning up"
    Stop-ExistingProcesses
    Write-Success "Debug session ended"
}
