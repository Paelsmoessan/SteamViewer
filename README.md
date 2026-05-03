# SteamViewer.NET

A portable remote desktop and support tool built with .NET 8 MAUI Blazor. No installation required - run the executable, share your ID and password, and connect. Supports full system-level elevation including UAC prompts, Secure Desktop, and lock screen access.

## Features

- **Portable** - Single executable, no installation, no account required
- **Full Elevation** - Control UAC prompts, Secure Desktop, and lock screen remotely
- **P2P Connection** - Direct UDP with NAT traversal and TURN relay fallback
- **Low Latency** - DXGI capture with hardware-accelerated H.264 encoding
- **Encrypted** - AES-256-GCM transport encryption, BLAKE3 password hashing
- **File Transfer** - Clipboard sync and chunked file transfers
- **Multi-Session** - Connect to multiple machines in tabbed sessions

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

1. **Host** starts and registers with the signaling server (WebSocket)
2. **Viewer** connects using the Host's ID and password
3. Host approves the connection
4. UDP hole-punch establishes a direct peer-to-peer link (with TURN relay fallback)
5. Host captures the screen via DXGI Desktop Duplication
6. Video is encoded with FFmpeg (libx264), encrypted with AES-256-GCM, and streamed over UDP
7. Viewer decodes and renders frames on a canvas via WebView2

## Architecture

```
┌─────────────┐     WebSocket      ┌─────────────────┐
│    Host     │◄──────────────────►│ Signaling Server│
│  (MAUI App) │                    │   (ASP.NET)     │
└──────┬──────┘                    └────────┬────────┘
       │                                    │
       │ UDP P2P (AES-256-GCM)              │ WebSocket
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
- **FFmpeg** (libx264) - Hardware-accelerated H.264 video encoding/decoding
- **DXGI Desktop Duplication** - Native screen capture
- **QOI** - Lossless image codec for Secure Desktop capture
- **ASP.NET Core** - WebSocket signaling server
- **AES-256-GCM** - Transport encryption
- **BLAKE3** - Password hashing
- **UDP P2P** - Direct peer-to-peer with NAT traversal and TURN relay fallback

## Known Limitations

- **Single-monitor testing**: Full-screen capture creates infinite mirror effect. Use window capture instead.
- **macOS**: Requires macOS workload to build. Use `SteamViewer.Windows.sln` on Windows.

## Acknowledgments

SteamViewer was built by studying and learning from many open-source projects. We're grateful to these communities:

### Remote Desktop & Input

| Project | License | What We Learned |
|---------|---------|-----------------|
| [Sunshine](https://github.com/LizardByte/Sunshine) | GPL-3.0 | Input injection, DXGI video pipeline, RTP/FEC |
| [RustDesk](https://github.com/rustdesk/rustdesk) | AGPL-3.0 | SendInput, virtual desktop handling, reboot/lock screen persistence |
| [FreeRDP](https://github.com/FreeRDP/FreeRDP) | Apache-2.0 | SendInput, split move+click, multi-monitor |
| [Barrier](https://github.com/debauchee/barrier) | GPL-2.0 | Input injection, UIPI bypass, DPI handling |
| [Synergy/Deskflow](https://github.com/symless/synergy) | GPL-2.0 | Desktop-switch retry, SendSAS |
| [TurboVNC](https://github.com/TurboVNC/turbovnc) | GPL-2.0 | Automatic Lossless Refresh, region classification |
| [SPICE](https://www.spice-space.org/) | LGPL-2.1 | Per-image auto codec selection, video stream detection |
| [Apache Guacamole](https://github.com/apache/guacamole-server) | Apache-2.0 | Clipboard text sync, drive redirection |

### WebRTC & Streaming

| Project | License | What We Learned |
|---------|---------|-----------------|
| [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery) | BSD-3 | .NET WebRTC, RTP, data channels, TURN client |
| [Selkies-GStreamer](https://github.com/selkies-project/selkies-gstreamer) | MPL-2.0 | WebRTC latency optimization, playout-delay |
| [Neko](https://github.com/m1k1o/neko) | Apache-2.0 | Separate A/V streams to bypass browser sync |
| [Pion WebRTC](https://github.com/pion/webrtc) | MIT | Data channel flow control |
| [PairDrop](https://github.com/schlagmichdoch/PairDrop) | GPL-3.0 | P2P file transfer over WebRTC data channels |
| [Moonlight](https://github.com/moonlight-stream) | GPL-3.0 | RTSP/RTP latency benchmarks |

### Video & Encoding

| Project | License | What We Learned |
|---------|---------|-----------------|
| [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) | LGPL-2.1 | FFmpeg P/Invoke bindings, DLL loading patterns |
| [Sdcb.FFmpeg](https://github.com/sdcb/Sdcb.FFmpeg) | MIT/GPL | FFmpeg NuGet runtime packaging |
| [LittleBigMouse](https://github.com/mgth/LittleBigMouse) | GPL-3.0 | Physical coordinates, EDID, DPI |

### Network & Error Correction

| Project | License | What We Learned |
|---------|---------|-----------------|
| [Haivision SRT](https://github.com/Haivision/srt) | MPL-2.0 | 2D XOR matrix FEC, hybrid FEC+ARQ |
| [UDPspeeder](https://github.com/wangyu-/UDPspeeder) | MIT | Reed-Solomon FEC over UDP, adaptive group sizes |
| [ReedSolomon.NET](https://github.com/egbakou/reedsolomon) | MIT | Erasure coding for .NET |
| [UDT](https://udt.sourceforge.io/) | BSD | UDP bulk data transfer, congestion control |

### Utilities

| Project | License | What We Learned |
|---------|---------|-----------------|
| [Vanara](https://github.com/dahall/Vanara) | MIT | Win32 P/Invoke wrappers |
| [VirtualFileDataObject](https://github.com/crackalak/VirtualFileDataObject) | MIT | Virtual file clipboard (IDataObject) |

We also studied proprietary protocols (Parsec BUD, Citrix HDX, Microsoft RDP/RemoteFX, NoMachine NX) for design insights.

### Built With

This project was developed with [Claude Code](https://claude.ai/code) by Anthropic.

## License

MIT

## Contributing

Contributions welcome! Please open an issue or pull request.
