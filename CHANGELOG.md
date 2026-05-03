# Changelog

## v0.1.2-alpha (2026-05-03)

### Security
- TURN credentials moved server-side (fetched at runtime from signaling server)
- Rotated TURN credentials, switched from Metered.ca to own Railway coturn
- Stripped all secrets from app binary and config files

### Improvements
- Instant window close (Win32 hide before cleanup)
- BootRelayOrchestrator uses TurnConfigService for runtime TURN provisioning
- Auto-versioned releases with changelog generation from git commits

### Fixes
- Fix Release build: pin .NET 8 SDK, fix #if DEBUG in Razor markup

## v0.1.0-alpha.1 (2026-04-30)

First public alpha release.

### Features
- Peer-to-peer remote desktop over LAN or internet
- H.264 video encoding with hardware-accelerated decoding
- Mouse, keyboard, and clipboard sync
- End-to-end encryption (DTLS-SRTP + BLAKE3 password hashing)
- Elevated access (admin tools, UAC prompts, lock screen)
- Multi-session tabs
- Auto-reconnect with saved credentials
- NAT traversal with relay fallback
- Self-extracting launcher with auto-update

### Notable fixes leading up to release
- Fix reconnect video quality: ready gate, IDR burst, decoder thread safety
- Fix WebSocket error toast shown during active P2P session
- Fix AltGr keyboard combos for all layouts (WebView2/WinUI workaround)
- Fix phantom packet loss: FEC ghost buffers inflated loss metric to 10-27%
- Fix asymmetric UDP switch killing video (death loop root cause)
- Fix viewer disconnect not reaching host on window close
- Fix host UI stuck after disconnect + reconnect race condition
- Graceful disconnect, clipboard retry, and build fixes
