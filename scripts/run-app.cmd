@echo off
pushd "%~dp0.."
echo Building and running SteamViewer...
dotnet run --project src/SteamViewer.App -f net8.0-windows10.0.19041.0
popd
