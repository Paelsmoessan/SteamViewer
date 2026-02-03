$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "Building..."
dotnet build src/SteamViewer.App -f net8.0-windows10.0.19041.0 -c Debug
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "Zipping app files only..."
$buildPath = "src\SteamViewer.App\bin\Debug\net8.0-windows10.0.19041.0\win10-x64"
$zipPath = Join-Path $PSScriptRoot "app-update.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath }

# Get only SteamViewer files + config + wwwroot (skip runtime)
$files = @(
    (Get-ChildItem "$buildPath\SteamViewer.*.dll"),
    (Get-ChildItem "$buildPath\SteamViewer.*.exe"),
    (Get-ChildItem "$buildPath\*.json"),
    (Get-Item "$buildPath\wwwroot")
) | ForEach-Object { $_ }

Compress-Archive -Path $files.FullName -DestinationPath $zipPath -Force

Write-Host "Uploading to GitHub..."
$gh = "$env:ProgramFiles\GitHub CLI\gh.exe"
$ErrorActionPreference = "SilentlyContinue"
& $gh release delete dev-builds -y 2>&1 | Out-Null
$ErrorActionPreference = "Stop"
& $gh release create dev-builds app-update.zip --title "App Update" --notes "Latest code"

Remove-Item $zipPath
Write-Host "Done! (~3MB uploaded)"
