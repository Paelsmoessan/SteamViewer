$ws = New-Object -ComObject WScript.Shell

# Server shortcut
$s = $ws.CreateShortcut("$PSScriptRoot\SteamViewer Server (Debug).lnk")
$s.TargetPath = "$PSScriptRoot\src\SteamViewer.Server\bin\Debug\net8.0\SteamViewer.Server.exe"
$s.WorkingDirectory = "$PSScriptRoot\src\SteamViewer.Server\bin\Debug\net8.0"
$s.Save()

# App shortcut
$s = $ws.CreateShortcut("$PSScriptRoot\SteamViewer App (Debug).lnk")
$s.TargetPath = "$PSScriptRoot\src\SteamViewer.App\bin\Debug\net8.0-windows10.0.19041.0\win10-x64\SteamViewer.App.exe"
$s.WorkingDirectory = "$PSScriptRoot\src\SteamViewer.App\bin\Debug\net8.0-windows10.0.19041.0\win10-x64"
$s.Save()

Write-Host "Shortcuts created!"
