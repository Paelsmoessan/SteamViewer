# Remote Client Setup

Instructions for setting up SteamViewer on a remote machine (outside LAN, e.g., laptop on mobile hotspot).

## Prerequisites

### 1. Install VS Code
https://code.visualstudio.com/download

### 2. Install Node.js (required for Claude Code)
https://nodejs.org/ (LTS version)

### 3. Install Claude Code
```batch
npm i -g @anthropic-ai/claude-code
```

### 4. Install GitHub CLI
```batch
winget install GitHub.cli
```

### 5. Authenticate GitHub CLI
```batch
gh auth login
```
- Choose "GitHub.com"
- Choose "HTTPS"
- Choose "Login with browser"

## Setup SteamViewer

### 1. Create app folder
```batch
mkdir C:\SteamViewer
cd C:\SteamViewer
```

### 2. Copy scripts
Copy these files from this folder to `C:\SteamViewer`:
- `download-setup.cmd`
- `download-run.cmd`

### 3. Download runtime (first time only, ~170MB)
```batch
cd C:\SteamViewer
download-setup.cmd
```

## Daily Usage

To get the latest build and run:
```batch
cd C:\SteamViewer
download-run.cmd
```

## Using Claude Code for Assistance

1. Open VS Code
2. Open terminal and run:
   ```batch
   claude
   ```
3. Authenticate with your Anthropic account
4. Tell Claude "I'm on the remote laptop" for guided setup

## Troubleshooting

**"gh: command not found"**
- Restart terminal after installing GitHub CLI
- Or use full path: `"%ProgramFiles%\GitHub CLI\gh.exe"`

**"HTTP 404" on download**
- The dev machine hasn't uploaded a build yet
- Ask dev to run `upload-full.cmd` first

**"unauthorized"**
- Run `gh auth login` again to refresh token

**App won't start**
- Make sure you ran `download-setup.cmd` first (gets the runtime)
- Check Windows Defender isn't blocking it
