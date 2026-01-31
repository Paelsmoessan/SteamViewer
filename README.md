# SteamViewer.NET

A cross-platform remote desktop application built with .NET 8 MAUI Blazor. Enables secure, peer-to-peer screen sharing and remote control similar to TeamViewer.

## Features

- **P2P Screen Sharing** - Direct WebRTC connection between host and viewer
- **Low Latency** - Browser-native video encoding with H264 hardware acceleration
- **Secure** - DTLS-SRTP encryption, BLAKE3 password hashing
- **File Transfer** - Chunked transfers over WebRTC data channel
- **Cross-Platform** - Windows and macOS support (macOS requires Mac to build)

## Quick Start

### Prerequisites
- .NET 8 SDK
- Windows 10/11 (for Windows build)

### Run in Debug Mode

```powershell
# Start signaling server
dotnet run --project src/SteamViewer.Server

# In another terminal, start the app (two instances)
dotnet run --project src/SteamViewer.App -f net8.0-windows10.0.19041.0
```

### Debug Credentials
In DEBUG builds, predetermined credentials are used for easy testing:
- **Host ID**: `123456789`
- **Password**: `TESTPASS`

## How It Works

1. **Host** starts and registers with the signaling server
2. **Viewer** connects using the Host's ID and password
3. Host approves the connection
4. WebRTC peer connection is established (SDP/ICE exchange)
5. Host shares screen via `getDisplayMedia()`
6. Video streams directly to Viewer over WebRTC

## Architecture

```
┌─────────────┐     WebSocket      ┌─────────────────┐
│    Host     │◄──────────────────►│ Signaling Server│
│  (MAUI App) │                    │   (ASP.NET)     │
└──────┬──────┘                    └────────┬────────┘
       │                                    │
       │ WebRTC (P2P)                       │ WebSocket
       │                                    │
       ▼                                    ▼
┌─────────────┐                    ┌─────────────────┐
│   Viewer    │◄──────────────────►│ Signaling Server│
│  (MAUI App) │                    └─────────────────┘
└─────────────┘
```

## Project Structure

```
SteamViewer.NET/
├── src/
│   ├── SteamViewer.Server/        # WebSocket signaling server
│   ├── SteamViewer.App/           # MAUI Blazor client
│   ├── SteamViewer.Client.Core/   # Core client logic
│   ├── SteamViewer.Common/        # Shared protocol/models
│   ├── SteamViewer.Platform.Windows/
│   └── SteamViewer.Platform.macOS/
├── tests/
├── SteamViewer.Windows.sln        # Windows-only solution
└── SteamViewer.NET.sln            # Full solution (requires macOS workload)
```

## Tech Stack

- **.NET 8** with C# 12
- **MAUI Blazor** - Cross-platform UI
- **WebRTC** - P2P video/data streaming
- **ASP.NET Core** - Signaling server
- **BLAKE3** - Password hashing

## Known Limitations

- **Single-monitor testing**: Full-screen capture creates infinite mirror effect. Use window capture instead.
- **macOS**: Requires macOS workload to build. Use `SteamViewer.Windows.sln` on Windows.

## License

MIT

## Contributing

Contributions welcome! Please read CLAUDE.md for architecture details and code style guidelines.
