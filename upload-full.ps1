$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "Building..."
dotnet build src/SteamViewer.App -f net8.0-windows10.0.19041.0 -c Debug
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "Zipping full build..."
$buildPath = Resolve-Path "src\SteamViewer.App\bin\Debug\net8.0-windows10.0.19041.0\win10-x64"
$zipPath = Join-Path $PSScriptRoot "full-build.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($buildPath, $zipPath)

Write-Host "Uploading to GitHub (full)..."
$gh = "$env:ProgramFiles\GitHub CLI\gh.exe"
$ErrorActionPreference = "SilentlyContinue"
& $gh release delete dev-full -y 2>&1 | Out-Null
$ErrorActionPreference = "Stop"
& $gh release create dev-full full-build.zip --title "Full Build (Runtime)" --notes "One-time setup"

Remove-Item "full-build.zip"
Write-Host "Done! Remote should download this once to set up runtime."
