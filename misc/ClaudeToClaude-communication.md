# Claude-to-Claude Communication

This file is the shared communication channel between **Claude on the dev machine** and **Claude on the remote test machine**. Chris carries this file between machines. Both Claudes should read this file at the start of every session and append findings to the log at the bottom.

---

## Project Context

**SteamViewer.NET** - A cross-platform remote desktop app (like TeamViewer). Built with .NET 8, MAUI Blazor, WebRTC.

**Why two machines?** Testing cross-NAT WebRTC connections. The dev machine and the remote machine are on different networks (not on the same LAN).

## Architecture

```
Dev Machine (Chris)                 Railway (Cloud)                Remote Test Machine
      |                                  |                              |
      |--- WebSocket (signaling) ------->|<--- WebSocket (signaling) ---|
      |                                  |                              |
      |<============ WebRTC P2P (video + input) =======================>|
      |              (direct, encrypted, no server in the middle)       |
```

- **Railway** only handles signaling (who wants to connect to whom)
- **WebRTC** handles the actual screen sharing, encrypted end-to-end
- If direct P2P fails (strict NAT), TURN relay kicks in as fallback

## Machine Roles

| | Dev Machine | Remote Test Machine |
|---|---|---|
| **Purpose** | Development, building, uploading | Testing outside NAT |
| **Repo** | Full clone at `C:\_Development\SteamViewer.NET` | No repo - just the built app |
| **App location** | Built from source | `C:\SteamViewer\` |
| **Server** | No local server needed | No local server needed |
| **Signaling** | Connects to Railway | Connects to Railway |
| **Builds delivered via** | `upload-build.cmd` / `upload-full.cmd` | `download-run.cmd` / `download-setup.cmd` |

## Remote Machine Setup

### Prerequisites
1. **GitHub CLI**: `winget install GitHub.cli` (then restart terminal)
2. **Authenticate**: `gh auth login` (GitHub.com, HTTPS, Login with browser)
3. **Verify**: `gh auth status`

### First-Time Install
```cmd
mkdir C:\SteamViewer
cd C:\SteamViewer
```

Copy `download-setup.cmd` and `download-run.cmd` from `tools/remote-client/` in the repo (or create manually - see script contents below), then:

```cmd
download-setup.cmd
```

Downloads ~170MB (app + .NET runtime + all dependencies).

### Daily Update & Run
```cmd
cd C:\SteamViewer
download-run.cmd
```

Downloads latest ~3MB update and launches the app.

### Script Contents (if you need to create them manually)

**download-setup.cmd:**
```batch
@echo off
cd /d "%~dp0"
echo Downloading full runtime (~170MB)...
set GH="%ProgramFiles%\GitHub CLI\gh.exe"
%GH% release download dev-full -p "full-build.zip" -R Paelsmoessan/SteamViewer.NET --clobber
if not exist full-build.zip (
    echo ERROR: Download failed. Run 'gh auth login' first.
    exit /b 1
)
echo Extracting...
powershell -Command "Expand-Archive -Path 'full-build.zip' -DestinationPath '.' -Force"
del full-build.zip
echo Runtime installed! Now use download-run.cmd for updates.
```

**download-run.cmd:**
```batch
@echo off
cd /d "%~dp0"
echo Downloading latest app update (~3MB)...
set GH="%ProgramFiles%\GitHub CLI\gh.exe"
%GH% release download dev-builds -p "app-update.zip" -R Paelsmoessan/SteamViewer.NET --clobber
if not exist app-update.zip (
    echo ERROR: Download failed. Run 'gh auth login' first.
    exit /b 1
)
echo Extracting...
powershell -Command "Expand-Archive -Path 'app-update.zip' -DestinationPath '.' -Force"
del app-update.zip
echo Starting...
start SteamViewer.App.exe
```

## Dev Machine Workflow

### Upload a build (app files only, ~3MB)
```cmd
upload-build.cmd
```

### Upload full build (first time or runtime changes, ~170MB)
```cmd
upload-full.cmd
```

## Testing

### Debug Credentials
In DEBUG builds, pre-filled:
- **Session ID**: `123456789`
- **Password**: `TESTPASS`

### Test Scenarios

**Remote views Dev's screen:**
1. Dev machine runs app (host, ID: 123456789)
2. Remote runs app, enters host Session ID + password, clicks Connect

**Dev views Remote's screen:**
1. Remote runs app, note its Session ID
2. Dev enters remote's Session ID + password, clicks Connect

### What to Verify
- [ ] Signaling connects (status shows "Connected")
- [ ] WebRTC negotiation completes (SDP/ICE exchange)
- [ ] Video appears (see remote screen)
- [x] Input works (mouse movement, clicks, keyboard)
- [ ] Check if direct P2P or TURN relay was used
- [ ] Disconnect/reconnect works cleanly

## Key Config

- **Signaling URL**: `wss://steamviewer-signaling-production.up.railway.app/ws`
- **Repo**: `Paelsmoessan/SteamViewer.NET`
- **App version**: 0.0.10 (from `version.json`)

## Feature Branches (not yet merged to main)

| Branch | What It Does | Status |
|--------|-------------|--------|
| `fix/websocket-cancel` | Cleaner WebSocket disconnect (no error spam) | ✅ Merged to main |
| `fix/disconnect-handling` | Better disconnect cleanup + reconnect overlay | ✅ Merged to main |
| `feature/viewer-ui` | Menus, copy buttons, short test IDs, app icon | Ready to test |
| `feature/file-transfer` | File transfer dialog and wiring | Ready to test |
| `feature/clipboard` | Clipboard sharing foundation | Foundation only |
| `feature/elevation` | Admin detection badge + "Run as Admin" button | Ready to test |

**Note:** Builds from `upload-build.cmd` are built from whatever branch is checked out on the dev machine. If you want features from a specific branch in the test build, that branch needs to be checked out (or merged to main) before uploading.

## Troubleshooting

| Problem | Fix |
|---------|-----|
| `gh: command not found` | Install GitHub CLI, restart terminal |
| HTTP 404 on download | Dev machine hasn't uploaded yet - run `upload-full.cmd` or `upload-build.cmd` |
| "unauthorized" | Run `gh auth login` again |
| App can't connect to signaling | Check internet; verify `https://steamviewer-signaling-production.up.railway.app` responds in browser |
| WebRTC fails (no video) | Check Windows Firewall; check browser console (F12) for WebRTC errors; this is the NAT test - TURN should fall back |
| Black screen | Host needs to select a screen/window to share; use window capture on single-monitor setups |
| Input coordinates off | Check `%USERPROFILE%\SteamViewer_InputDebug.log` |

## Debug Tools

- **Browser dev tools**: F12 in the WebView window (JS console, network tab)
- **App logs**: Check console output where the app was launched

### Log File Paths

Both Claudes should read these with the `Read` tool at the start of debug sessions.

| Machine | Log Type | Path |
|---------|----------|------|
| Dev | Client log | `C:\_Development\SteamViewer.NET\logs\client-{MACHINE}.log` |
| Dev | Server log | `C:\_Development\SteamViewer.NET\logs\server-{MACHINE}.log` |
| Dev | Input debug | `C:\_Development\SteamViewer.NET\logs\` or `%USERPROFILE%\SteamViewer_InputDebug.log` |
| Remote | Client log | `C:\SteamViewer\logs\client-{MACHINE}.log` |
| Remote | Input debug | `C:\SteamViewer\SteamViewer_InputDebug.log` |

Replace `{MACHINE}` with the actual machine name (e.g., `MEDIASERVER`, `DESKTOP-ABC`).

---

## How This File Works

Chris carries this file between machines. Each Claude reads it at the start of a session.

**Rules for both Claudes:**
1. Read this entire file when starting a session
2. Update the **Status Dashboard** when something changes
3. Add entries to the **Activity Log** for anything notable
4. When you need the other Claude to do something, put it in **Action Items**
5. Keep language plain - Chris reads this too

---

## Status Dashboard

> **Quick glance** - what's happening right now on each machine?

| | Dev Machine | Remote Machine |
|---|---|---|
| **Status** | Idle - ready to test | App running - ready to test |
| **Branch** | `main` | N/A (built from `main`) |
| **Build** | `dev-full` uploaded to GitHub | Downloaded and extracted |
| **App running?** | No | Yes |
| **App location** | Built from source | `C:\Users\Furhat\SteamViewer.NET\tools\remote-client\` |
| **Last updated** | 2026-02-06 | 2026-02-06 |

---

## Action Items

> **What needs to happen next?** Either Claude can add items here. Mark done with [x].

- [ ] **Dev**: Start the app so remote can connect to it
- [ ] **Both**: Test cross-NAT connection using debug credentials (ID: `123456789`, Pass: `TESTPASS`)
- [ ] **Both**: Verify signaling connects through Railway
- [ ] **Both**: Verify WebRTC video stream works across NAT
- [ ] **Both**: Verify input forwarding works across NAT
- [ ] **Both**: Check if connection is direct P2P or TURN relay
- [ ] **Both**: Test disconnect and reconnect

---

## Test Results

> **Checklist** - update as tests are performed

| Test | Result | Notes |
|------|--------|-------|
| Signaling connects | Not tested | Should show "Connected" in app |
| WebRTC negotiation | Not tested | SDP/ICE exchange via Railway |
| Video streaming | Not tested | Should see remote screen |
| Input forwarding | Not tested | Mouse + keyboard |
| Direct P2P or TURN? | Not tested | Check browser console (F12) |
| Disconnect/reconnect | Not tested | Clean disconnect, no errors |

---

## Activity Log

> What happened, when, and on which machine. Most recent at the bottom.

### 2026-02-06 - Dev - Project initialized for cross-NAT testing
- Built from `main` branch (no feature branches merged yet)
- Uploaded full build (~170MB) to GitHub Releases as `dev-full`
- Communication file created

### 2026-02-06 - Remote - Setup complete
- GitHub CLI installed and authenticated
- Full runtime downloaded and extracted
- App downloaded and launched
- App is running at `C:\Users\Furhat\SteamViewer.NET\tools\remote-client\`
- Ready to test

### 2026-02-06 - Dev - Waiting for connection test
- Remote is set up and running
- Dev machine needs to start the app to begin testing
- Next: both machines run the app, attempt cross-NAT connection

### 2026-02-06 - Dev - Keyboard verified, bug fix branches merged
- Chris confirmed keyboard capture is working (JS → C# → data channel → Windows SendInput)
- Marked input (mouse + keyboard) as verified in testing checklists
- Merged `fix/websocket-cancel` into main (WebSocket close handshake error fix + cleanup order)
- Merged `fix/disconnect-handling` into main (disconnect cleanup + reconnect overlay)
- Both branches kept for reference, not deleted
- **Remote**: Next build from main will include both bug fixes. Re-download when dev uploads new build.
