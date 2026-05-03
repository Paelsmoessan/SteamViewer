# SteamViewer Launcher - Downloads and runs the server
# Compiled to EXE via ps2exe for ~50KB distribution
$ErrorActionPreference = "Stop"

$appDir = "$env:APPDATA\SteamViewer"
$serverExe = "$appDir\SteamViewer.Server.exe"
$versionFile = "$appDir\version.txt"
$repoOwner = "Jeyloh"  # TODO: Update to your GitHub username
$repoName = "SteamViewer.NET"

function Write-Status {
    param([string]$Message)
    Write-Host "[SteamViewer] $Message" -ForegroundColor Cyan
}

function Write-Error-Message {
    param([string]$Message)
    Write-Host "[ERROR] $Message" -ForegroundColor Red
}

# Get latest release info from GitHub API
function Get-LatestRelease {
    $api = "https://api.github.com/repos/$repoOwner/$repoName/releases/latest"
    try {
        $release = Invoke-RestMethod -Uri $api -Headers @{
            "User-Agent" = "SteamViewer-Launcher"
            "Accept" = "application/vnd.github.v3+json"
        }
        return $release
    }
    catch {
        Write-Error-Message "Failed to fetch release info: $_"
        return $null
    }
}

# Download file with progress
function Download-File {
    param(
        [string]$Url,
        [string]$OutFile
    )

    Write-Status "Downloading from: $Url"

    try {
        # Use WebClient for progress support
        $webClient = New-Object System.Net.WebClient
        $webClient.Headers.Add("User-Agent", "SteamViewer-Launcher")

        # Progress event
        $downloadComplete = $false
        Register-ObjectEvent -InputObject $webClient -EventName DownloadProgressChanged -Action {
            $percent = $Event.SourceEventArgs.ProgressPercentage
            $received = [math]::Round($Event.SourceEventArgs.BytesReceived / 1MB, 2)
            $total = [math]::Round($Event.SourceEventArgs.TotalBytesToReceive / 1MB, 2)
            Write-Progress -Activity "Downloading SteamViewer Server" -Status "${received}MB / ${total}MB" -PercentComplete $percent
        } | Out-Null

        Register-ObjectEvent -InputObject $webClient -EventName DownloadFileCompleted -Action {
            $script:downloadComplete = $true
        } | Out-Null

        $webClient.DownloadFileAsync([Uri]$Url, $OutFile)

        # Wait for download
        while (-not $downloadComplete -and $webClient.IsBusy) {
            Start-Sleep -Milliseconds 100
        }

        Write-Progress -Activity "Downloading SteamViewer Server" -Completed
        $webClient.Dispose()
        return $true
    }
    catch {
        Write-Error-Message "Download failed: $_"
        return $false
    }
}

# Main execution
Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "       SteamViewer Launcher" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""

# Check if server exists
$needsDownload = -not (Test-Path $serverExe)
$release = $null

if (-not $needsDownload) {
    # Server exists, check for updates
    Write-Status "Checking for updates..."
    $release = Get-LatestRelease

    if ($release -and (Test-Path $versionFile)) {
        $currentVersion = Get-Content $versionFile -Raw
        $currentVersion = $currentVersion.Trim()
        $latestVersion = $release.tag_name

        if ($currentVersion -ne $latestVersion) {
            Write-Status "Update available: $currentVersion -> $latestVersion"
            $needsDownload = $true
        }
        else {
            Write-Status "Already up to date ($currentVersion)"
        }
    }
    elseif ($release) {
        # No version file, assume needs update
        Write-Status "Version file missing, downloading latest..."
        $needsDownload = $true
    }
}
else {
    Write-Status "Server not found, downloading..."
}

if ($needsDownload) {
    # Get release info if we don't have it
    if (-not $release) {
        $release = Get-LatestRelease
    }

    if (-not $release) {
        Write-Error-Message "Could not fetch release information. Check your internet connection."
        Write-Host "Press any key to exit..."
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
        exit 1
    }

    # Find the Windows server asset
    $assetName = "SteamViewer.Server-win-x64.exe"
    $asset = $release.assets | Where-Object { $_.name -eq $assetName }

    if (-not $asset) {
        Write-Error-Message "Could not find $assetName in release $($release.tag_name)"
        Write-Host "Available assets:"
        $release.assets | ForEach-Object { Write-Host "  - $($_.name)" }
        Write-Host "Press any key to exit..."
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
        exit 1
    }

    # Create app directory
    if (-not (Test-Path $appDir)) {
        New-Item -ItemType Directory -Force -Path $appDir | Out-Null
        Write-Status "Created directory: $appDir"
    }

    # Download server
    $downloadUrl = $asset.browser_download_url
    Write-Status "Downloading SteamViewer Server $($release.tag_name)..."

    $tempFile = "$appDir\SteamViewer.Server.exe.tmp"
    $success = Download-File -Url $downloadUrl -OutFile $tempFile

    if ($success -and (Test-Path $tempFile)) {
        # Verify file size
        $fileSize = (Get-Item $tempFile).Length
        if ($fileSize -gt 1MB) {
            # Move temp to final
            if (Test-Path $serverExe) {
                Remove-Item $serverExe -Force
            }
            Move-Item $tempFile $serverExe -Force

            # Save version
            $release.tag_name | Out-File $versionFile -NoNewline -Encoding UTF8

            Write-Status "Download complete! ($([math]::Round($fileSize / 1MB, 2)) MB)"
        }
        else {
            Write-Error-Message "Downloaded file is too small (${fileSize} bytes), may be corrupt"
            Remove-Item $tempFile -Force -ErrorAction SilentlyContinue
            exit 1
        }
    }
    else {
        Write-Error-Message "Download failed"
        Remove-Item $tempFile -Force -ErrorAction SilentlyContinue
        exit 1
    }
}

# Run the server
if (Test-Path $serverExe) {
    Write-Host ""
    Write-Status "Starting SteamViewer Server..."
    Write-Host ""

    # Run server in same window
    & $serverExe

    # If we get here, server exited
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        Write-Error-Message "Server exited with code $exitCode"
    }
}
else {
    Write-Error-Message "Server executable not found at: $serverExe"
    exit 1
}
