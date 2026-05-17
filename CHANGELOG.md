# Changelog

## v0.2.8-alpha (2026-05-17)

### Clipboard reliability + viewer→host text auto-push

Fixes post-reconnect clipboard loss (root cause: Win32 window-class
name collision in both `ClipboardMonitor` and `ClipboardFileWriter`) and
wires the previously missing viewer→host text auto-push.

#### Bug fixes
- **Post-reconnect clipboard silently lost.** Every `ClipboardMonitor` /
  `ClipboardFileWriter` instance registered its Win32 window class under
  the same hardcoded name. `RegisterClassEx` succeeds only for the first
  caller; subsequent calls return `ERROR_CLASS_ALREADY_EXISTS=1410` and
  are silently ignored. The class-level `lpfnWndProc` stays bound to the
  FIRST instance, so post-reconnect `WM_CLIPBOARDUPDATE` and
  `WM_SET_CLIPBOARD` messages dispatched through the disposed first
  session's WndProc — events fired on dead sessions, virtual-file
  clipboard writes operated on stale state, host clipboard ended up
  empty even though `OleSetClipboard succeeded`. Fix: per-instance
  GUID-suffixed class name + `UnregisterClass` on dispose.
- **Hung `InvokeVoidAsync` swallowed paste post-reconnect.** WebView2's
  JS context for `navigator.clipboard.writeText` could hang indefinitely
  on the first call after a reconnect. The catch block never ran, Win32
  fallback never ran, paste was silently lost. Fix: 500ms `WaitAsync`
  timeout falls through to Win32 fallback on hang.

#### New feature
- **Viewer→host text clipboard auto-push.** Subscribing
  `ViewerSession.OnClipboardTextDetected` was never wired (only
  `ClipboardFilesDetected` was). Copying text on the viewer machine now
  syncs to host's clipboard the same way the reverse direction already
  worked. Sends `ClipboardMessage.Set("text", data)`; host's existing
  `HandleClipboardSetAsync` handles the receive side.

#### Internal refactor
- **Replaced flag-based echo prevention with content-hash dedup** in
  `ClipboardMonitor`. The previous `SetEchoFlag → Thread.Sleep(100) →
  ClearEchoFlag` ceremony in `ClipboardFileWriter` had a 100ms blocking
  sleep on every clipboard write, a timing race (relied on
  `WM_CLIPBOARDUPDATE` arriving while the flag was set), and a
  duplication-coordination anti-pattern (every writer had to remember
  the ceremony — three of four sites had silently forgotten it). New
  `RecordSelfWriteText` / `RecordSelfWriteFiles` API stores a content
  hash on the monitor; `CheckClipboard` compares the hash of the
  current clipboard against the last self-write and single-fire-
  suppresses on match. All four write sites converge on one method
  call. No blocking sleep, no timing race.

#### Tooling + policy
- **CLAUDE.md "Gate-logging rule (hard)"** under Code Change Discipline.
  Any new guard, gate, or short-circuit return that can be hit at
  runtime MUST log entry, guard-fail with reason, and success path.
  Codifies what surfaced the class-name collision bug in one
  reproduction cycle (vs ~90 minutes of static review pre-policy).
- **Permanent verbose logging** at every clipboard send gate
  (`OnClipboardFilesDetected`, `OnClipboardTextDetected`,
  `SendFrameAsync`).
- **Private Content Gate** workflow now correctly skips on the private
  repo via `if: github.repository == 'Paelsmoessan/SteamViewer'`. Was
  failing on every push to private since added (silently ignored, but
  visible noise on PRs).

## v0.2.7-alpha (2026-05-12)

### Zero build warnings

Cleaned up all pre-existing build warnings (18 -> 0) across the solution.
Four independent commits, each behavior-preserving:

- Removed dead code: 4 unused members (HostSession.OnScreenShareLost,
  SessionManager.WebRTCConnected from the WebRTC era, RemoteViewer
  _resizeDebounceTimer C# field whose JS counterpart is still alive,
  Home.razor AppMode/currentMode/SelectJoinMode/GoBack orphan UI cluster)
- Silenced CS1998 on 5 sync method bodies with async signatures
  (await Task.CompletedTask preserves async sugar for future awaits)
- Silenced CS8604 possible-null-ref on 2 sites where runtime guarantees
  non-null but analyzer can't prove it
- Pragma-suppressed CS0618 obsolete STUNXORAddressAttribute (x2) - SIPSorcery
  RFC5389 migration deferred; suppression is documented in source

Full clean build: 0 errors, 0 warnings. App.dll shrunk 512 bytes from
dead-code removal.

## v0.2.6-alpha (2026-05-12)

### Full Clean Delivery refactor cycle (8 sub-branches, all behavior-preserving)

Codebase health overhaul driven by CodeScene Community Edition feedback. Eight
sub-branches addressing duplication, low cohesion, and complex-method findings
on the worst-Health files. Average Code Health trend turned positive.

#### Dispatcher consolidation (Stages 1-5: C.1, A, B, C.2, C.3)
- C.1: default-arm warnings on host/viewer control switches surface unknown
  message types instead of dropping silently
- A: ControlMessageSender static helper - 31+ JsonSerializer.Serialize + transport
  send sites consolidated to one canonical path with logging
- B: SignalingHandler ForwardToTarget helper - 9 protocol forwarding handlers
  collapsed to expression-bodied one-liners
- C.2: JsonAccessors value-returning helpers (GetString/Int/Bool/UInt) replace
  TryGetProperty boilerplate
- C.3: ControlMessageDispatcher generic handler-table - HandleControlMessage
  twin (HostSession cc=32, ViewerSession cc=43) replaced with per-session
  handler dictionaries

#### Worst-Health residuals (Stage D)
- D#1: video-interop.js sharedbufferreceived (cc 45 -> 10) decomposed into
  paintLosslessFrame/paintRawFrame/paintJpegFrame painters + computeDisplayFit
  geometry helper
- D#2: Win32Input.cs (1078 LoC monolith, Low Cohesion) split into partial-class
  files by concern: entry, Display, Mouse, Keyboard, Constants
- D#3: UdpTransportBackend - ReceiveLoopAsync phase extract (cc 37 -> 20),
  FragmentBuffer.TryRecover row/column twin collapse (cc 31 -> <10)

#### Release artifact reliability
- v0.2.5-alpha smoke-check CI step (zip-layout validation) + launcher
  extraction hardening guard against future SDK output-path shifts

## v0.1 (2026-05-04)

### Native Keyboard Capture
- Replace WebView2/JS keyboard pipeline with native Win32 WH_KEYBOARD_LL hook (Sunshine pattern)
- Raw scan codes sent via KEYEVENTF_SCANCODE - host keyboard driver composes characters naturally
- AltGr phantom Ctrl (scan code 0x21D) dropped at hook level - eliminates the entire class of AltGr bugs
- JS keyboard path preserved as automatic fallback for UAC-elevated foreground apps
- PID-based foreground detection (fixes MAUI/WinUI3 window hierarchy HWND mismatch)
- Race-free modifier bitfield replaces GetAsyncKeyState

### Improvements
- ILogger replaces Console.WriteLine in keyboard hook (Console output silently swallowed in MAUI Windows)
- Two-step capture enable/disable: native capture activates before JS silenced, JS re-enabled before capture disabled
- Release tagger script for tag-triggered GitHub Actions builds

### Fixes
- Fix AltGr-hold stuck Ctrl on host (WebView2 autorepeat phantom ControlLeft)
- Fix modifier keys stuck on host after disconnect (ReleaseAllModifiers safety net)

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
