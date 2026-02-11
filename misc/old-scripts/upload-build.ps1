$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
Set-Location $repoRoot

$logFile = Join-Path $repoRoot "logs\build.log"
if (!(Test-Path (Split-Path $logFile))) { New-Item -ItemType Directory -Path (Split-Path $logFile) -Force | Out-Null }
Start-Transcript -Path $logFile -Append

Write-Host "Building..."
dotnet build src/SteamViewer.App -f net8.0-windows10.0.19041.0 -c Debug
if ($LASTEXITCODE -ne 0) { Stop-Transcript; exit 1 }

Write-Host "Zipping app files only..."
$buildPath = "$repoRoot\src\SteamViewer.App\bin\Debug\net8.0-windows10.0.19041.0\win10-x64"
$zipPath = Join-Path $repoRoot "app-update.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath }

# Get only SteamViewer files + config + wwwroot (skip runtime)
$files = @(
    (Get-ChildItem "$buildPath\SteamViewer.*.dll"),
    (Get-ChildItem "$buildPath\SteamViewer.*.exe"),
    (Get-ChildItem "$buildPath\System.Drawing.Common.dll"),
    (Get-ChildItem "$buildPath\*.json"),
    (Get-Item "$buildPath\wwwroot")
) | ForEach-Object { $_ }

# Include Claude-to-Claude communication file as CLAUDE.md for remote
$claudeFile = Join-Path $repoRoot ".claude\ClaudeToClaude-communication.md"
$claudeDest = Join-Path $buildPath "CLAUDE.md"
if (Test-Path $claudeFile) {
    Copy-Item $claudeFile $claudeDest -Force
    $files += @(Get-Item $claudeDest)
}

# Include gist ID file for two-way Claude communication
$gistFile = Join-Path $repoRoot ".claude\gist-id.txt"
$gistDest = Join-Path $buildPath "gist-id.txt"
if (Test-Path $gistFile) {
    Copy-Item $gistFile $gistDest -Force
    $files += @(Get-Item $gistDest)
}

Compress-Archive -Path $files.FullName -DestinationPath $zipPath -Force

Write-Host "Uploading to GitHub..."
$gh = "$env:ProgramFiles\GitHub CLI\gh.exe"
$ErrorActionPreference = "SilentlyContinue"
& $gh release delete dev-builds -y 2>&1 | Out-Null
$ErrorActionPreference = "Stop"
& $gh release create dev-builds app-update.zip --title "App Update" --notes "Latest code"

Remove-Item $zipPath
Write-Host "Done! (~3MB uploaded)"

Stop-Transcript
