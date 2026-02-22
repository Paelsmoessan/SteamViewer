using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SteamViewer.Client.Core.Capture;
using SteamViewer.Client.Core.Elevation;
using SteamViewer.Client.Core.Network;
using SteamViewer.Common.Protocol;
using System.Text.Json.Serialization;
using SteamViewer.Platform.Windows.Clipboard;
using SteamViewer.Platform.Windows.ScreenCapture;

namespace SteamViewer.App.Services.Models;

/// <summary>
/// Connection state for a host session.
/// </summary>
public enum HostSessionState
{
    /// <summary>Session is being initialized (WebRTC setup).</summary>
    Initializing,
    /// <summary>WebRTC initialized, waiting for data channel to open.</summary>
    WaitingForDataChannel,
    /// <summary>Data channel is open; ready for screen sharing and input.</summary>
    Connected,
    /// <summary>Session has been disconnected.</summary>
    Disconnected,
    /// <summary>Session encountered an error.</summary>
    Error
}

/// <summary>
/// Represents a single host session with a connected viewer peer.
/// Encapsulates WebRTC connection, screen sharing, input injection, and file transfer.
/// </summary>
public sealed class HostSession : IAsyncDisposable
{
    private readonly IJSRuntime _jsRuntime;
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IInputInjector _inputInjector;
    private readonly IMonitorEnumerator? _monitorEnumerator;
    private readonly IElevationService? _elevationService;
    private readonly IScreenCapture? _screenCapture;
#if WINDOWS
    private readonly Services.NativeFrameBridge? _frameBridge;
#endif
    private readonly IConfiguration _configuration;
    private readonly Func<SignalingMessage, Task> _sendSignaling;
    private readonly string _hostClientId;
    private readonly string _hostPasswordHash;
    private WebRTCManager? _webrtc;
    private bool _webrtcInitialized;
    private bool _disposed;
    private bool _elevationDetached;

    // Pending SDP/ICE (buffered if received before WebRTC init)
    private string? _pendingSdpOffer;
    private readonly List<(string candidate, string? sdpMid, int? sdpMLineIndex)> _pendingIceCandidates = new();

    // Track capture dimensions from viewer's mouse events (0 = not yet received)
    private int _lastCaptureWidth;
    private int _lastCaptureHeight;

    // Native DXGI capture state
    private bool _isNativeCapture;
    private DxgiScreenCapture? _activeDxgi;

    // Clipboard file transfer — host is both sender (monitors clipboard) and receiver (serves file chunks)
    private ClipboardMonitor? _clipboardMonitor;
    private ClipboardFileServer? _clipboardFileServer;
    private ClipboardFileWriter? _clipboardFileWriter;

    /// <summary>Session ID for JS interop — always "host".</summary>
    public string SessionId => "host";

    /// <summary>The connected viewer's peer ID.</summary>
    public string PeerId { get; }

    /// <summary>Current session state.</summary>
    public HostSessionState State { get; private set; } = HostSessionState.Initializing;

    /// <summary>Whether the data channel is open and ready.</summary>
    public bool IsDataChannelReady => _webrtc?.IsDataChannelOpen ?? false;

    /// <summary>Whether this host is sharing its screen to the viewer.</summary>
    public bool IsSharingScreen { get; private set; }

    /// <summary>Whether the connected viewer is sharing their screen.</summary>
    public bool IsPeerSharingScreen { get; private set; }

    /// <summary>When true, auto-start full screen sharing when the data channel opens (used for post-reboot reconnect).</summary>
    public bool AutoShareOnReady { get; set; }

    #region Events

    /// <summary>Raised when session state changes.</summary>
    public event Action<HostSessionState>? OnStateChanged;

    /// <summary>Raised when the data channel opens (ready for screen share/input).</summary>
    public event Action? OnReady;

    /// <summary>Raised when the session disconnects.</summary>
    public event Action<string?>? OnDisconnected;

    /// <summary>Raised when screen sharing was lost and all auto-restart attempts failed.</summary>
    public event Action? OnScreenShareLost;

    /// <summary>Raised when the peer starts/stops sharing their screen.</summary>
    public event Action<bool>? OnPeerSharingChanged;

    /// <summary>Raised when an ICE candidate needs to be sent via signaling.</summary>
    public event Func<string, string?, ushort?, Task>? OnIceCandidate;

    /// <summary>Raised when an SDP offer/answer needs to be sent via signaling.</summary>
    public event Func<string, string, Task>? OnSdpMessage;

    #endregion

    public HostSession(
        string peerId,
        IJSRuntime jsRuntime,
        ILoggerFactory loggerFactory,
        IInputInjector inputInjector,
        IConfiguration configuration,
        Func<SignalingMessage, Task> sendSignaling,
        IElevationService? elevationService = null,
        IMonitorEnumerator? monitorEnumerator = null,
        IScreenCapture? screenCapture = null,
#if WINDOWS
        Services.NativeFrameBridge? frameBridge = null,
#endif
        string hostClientId = "",
        string hostPasswordHash = "")
    {
        PeerId = peerId;
        _jsRuntime = jsRuntime;
        _logger = loggerFactory.CreateLogger<HostSession>();
        _loggerFactory = loggerFactory;
        _inputInjector = inputInjector;
        _monitorEnumerator = monitorEnumerator;
        _elevationService = elevationService;
        _screenCapture = screenCapture;
#if WINDOWS
        _frameBridge = frameBridge;
#endif
        _configuration = configuration;
        _sendSignaling = sendSignaling;
        _hostClientId = hostClientId;
        _hostPasswordHash = hostPasswordHash;

        // Subscribe to elevation events to forward to viewer
        if (_elevationService != null)
        {
            _elevationService.OnSecureDesktopFrame += HandleSecureDesktopFrame;
            _elevationService.OnSecureDesktopStateChanged += HandleSecureDesktopStateChanged;
            _elevationService.OnSystemStateChanged += HandleSystemStateChanged;
        }
    }

    /// <summary>
    /// Detach the elevation service so it survives this session's disposal.
    /// Unsubscribes event handlers but does NOT dispose the service.
    /// Returns the elevation service reference for reuse in a new session.
    /// </summary>
    public IElevationService? DetachElevationService()
    {
        if (_elevationService == null || _elevationDetached) return null;

        _elevationDetached = true;

        _elevationService.OnSecureDesktopFrame -= HandleSecureDesktopFrame;
        _elevationService.OnSecureDesktopStateChanged -= HandleSecureDesktopStateChanged;
        _elevationService.OnSystemStateChanged -= HandleSystemStateChanged;

        return _elevationService;
    }

    /// <summary>
    /// Initialize WebRTC as host: configure TURN, create peer connection,
    /// create data channel, create and send SDP offer.
    /// </summary>
    public async Task InitializeAsync()
    {
        _logger.LogInformation("Initializing host session for peer {PeerId}", PeerId);

        // Configure TURN server before WebRTC init
        await SetTurnConfigFromSettings();

        // Set logger to host mode
        await _jsRuntime.InvokeVoidAsync("SteamViewerLogger.setMode", true);

        _webrtc = new WebRTCManager(
            _jsRuntime,
            _loggerFactory.CreateLogger<WebRTCManager>(),
            SessionId,
            PeerId,
            _sendSignaling);

        // Subscribe to WebRTC events
        _webrtc.OnIceCandidate += HandleIceCandidate;
        _webrtc.OnDataChannelMessage += HandleDataChannelMessage;
        _webrtc.OnFileChannelMessage += HandleFileChannelMessage;
        _webrtc.OnRenegotiationNeeded += HandleRenegotiationNeeded;
        _webrtc.OnDataChannelOpen += HandleDataChannelOpen;
        _webrtc.OnDataChannelClose += HandleDataChannelClose;
        _webrtc.OnConnectionStateChange += HandleConnectionStateChange;
        _webrtc.OnScreenShareLost += HandleScreenShareLost;
        _webrtc.OnCaptureStarted += HandleCaptureStarted;

        await _webrtc.InitializeAsync();
        _webrtcInitialized = true;
        await _webrtc.CreateDataChannelsAsync();

        // Create and send initial SDP offer
        var offer = await _webrtc.CreateOfferAsync();
        if (OnSdpMessage != null)
            await OnSdpMessage.Invoke(PeerId, offer);

        SetState(HostSessionState.WaitingForDataChannel);
        _logger.LogInformation("SDP offer sent to {PeerId}", PeerId);

        // Flush any buffered signaling messages
        await FlushPendingSignalingAsync();
    }

    #region SDP/ICE Handling

    /// <summary>Handle incoming SDP offer (renegotiation from viewer).</summary>
    public async Task HandleSdpOfferAsync(string sdp)
    {
        if (_webrtc != null && _webrtcInitialized)
        {
            _logger.LogInformation("Received SDP offer from {PeerId}", PeerId);
            await _webrtc.SetRemoteDescriptionAsync(sdp);
            var answer = await _webrtc.CreateAnswerAsync();
            if (OnSdpMessage != null)
                await OnSdpMessage.Invoke(PeerId, answer);
            _logger.LogInformation("SDP answer sent");
        }
        else
        {
            _logger.LogWarning("WebRTC not ready, buffering SDP offer");
            _pendingSdpOffer = sdp;
        }
    }

    /// <summary>Handle incoming SDP answer from viewer.</summary>
    public async Task HandleSdpAnswerAsync(string sdp)
    {
        if (_webrtc == null) return;
        _logger.LogInformation("Received SDP answer from {PeerId}", PeerId);
        await _webrtc.SetRemoteDescriptionAsync(sdp);
    }

    /// <summary>Handle incoming ICE candidate from viewer.</summary>
    public async Task HandleIceCandidateAsync(string candidate, string? sdpMid, int? sdpMLineIndex)
    {
        if (_webrtc != null && _webrtcInitialized)
        {
            var candidateJson = JsonSerializer.Serialize(new { candidate, sdpMid, sdpMLineIndex });
            await _webrtc.AddIceCandidateAsync(candidateJson);
        }
        else
        {
            _logger.LogDebug("WebRTC not ready, buffering ICE candidate");
            _pendingIceCandidates.Add((candidate, sdpMid, sdpMLineIndex));
        }
    }

    private async Task FlushPendingSignalingAsync()
    {
        if (_webrtc == null || !_webrtcInitialized) return;

        if (_pendingSdpOffer != null)
        {
            _logger.LogInformation("Flushing buffered SDP offer");
            var sdp = _pendingSdpOffer;
            _pendingSdpOffer = null;
            await HandleSdpOfferAsync(sdp);
        }

        if (_pendingIceCandidates.Count > 0)
        {
            _logger.LogInformation("Flushing {Count} buffered ICE candidates", _pendingIceCandidates.Count);
            var candidates = _pendingIceCandidates.ToList();
            _pendingIceCandidates.Clear();
            foreach (var (candidate, sdpMid, sdpMLineIndex) in candidates)
            {
                await HandleIceCandidateAsync(candidate, sdpMid, sdpMLineIndex);
            }
        }
    }

    #endregion

    #region Screen Sharing

    /// <summary>
    /// Start sharing screen to the connected viewer.
    /// Tries DXGI native capture first (no picker!), falls back to getDisplayMedia.
    /// </summary>
    /// <param name="outputIndex">DXGI output index to capture (null = auto-select primary)</param>
    public async Task<bool> StartScreenShareAsync(uint? outputIndex = null)
    {
        if (_webrtc == null) return false;

        // Try DXGI native capture first (Windows only, no screen picker)
        if (_screenCapture is DxgiScreenCapture dxgi)
        {
            try
            {
                var targetOutput = outputIndex ?? 0; // Default to primary monitor
                _logger.LogInformation("Trying DXGI native capture on output {Output} (no picker)...", targetOutput);

                // Set up JS canvas bridge first
                var bridgeOk = await _webrtc.StartNativeCaptureAsync(30);
                if (!bridgeOk)
                {
                    _logger.LogWarning("JS canvas bridge setup failed, falling back to getDisplayMedia");
                    goto fallback;
                }

                // Subscribe to DXGI frame events — raw BGRA when SharedBuffer available, JPEG fallback
#if WINDOWS
                if (_frameBridge?.IsInitialized == true)
                {
                    dxgi.OnRawFrameCaptured += OnDxgiRawFrameCaptured;
                    _logger.LogInformation("Using raw BGRA pipeline (no JPEG encode)");
                }
                else
#endif
                {
                    dxgi.OnFrameCaptured += OnDxgiFrameCaptured;
                }

                // Subscribe to cursor shape changes for local overlay on viewer
                dxgi.OnCursorShapeChanged += OnCursorShapeChanged;

                // Start DXGI capture loop (fires OnFrameCaptured at ~30 FPS)
                dxgi.StartCaptureLoop(targetOutput);

                _isNativeCapture = true;
                _activeDxgi = dxgi;
                IsSharingScreen = true;
                _logger.LogInformation("DXGI native capture started on output {Output} — no picker!", targetOutput);

                // Notify peer
                await NotifyScreenShareStarted();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DXGI native capture failed, falling back to getDisplayMedia");
                // Clean up partial DXGI state
                try { dxgi.OnRawFrameCaptured -= OnDxgiRawFrameCaptured; } catch { }
                try { dxgi.OnFrameCaptured -= OnDxgiFrameCaptured; } catch { }
                try { dxgi.OnCursorShapeChanged -= OnCursorShapeChanged; } catch { }
                try { dxgi.StopCaptureLoop(); } catch { }
                try { await _webrtc.StopNativeCaptureAsync(); } catch { }
                _isNativeCapture = false;
            }
        }

        fallback:
        // Fallback: browser getDisplayMedia (shows picker)
        try
        {
            _logger.LogInformation("Starting screen share via getDisplayMedia (browser picker)...");
            var success = await _webrtc.StartScreenCaptureAsync();
            if (success)
            {
                _isNativeCapture = false;
                IsSharingScreen = true;
                _logger.LogInformation("Screen sharing started via getDisplayMedia");
                await NotifyScreenShareStarted();
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start screen sharing");
            return false;
        }
    }

    /// <summary>Stop sharing screen (handles both DXGI native and getDisplayMedia paths).</summary>
    public async Task StopScreenShareAsync()
    {
        if (_webrtc == null) return;

        try
        {
            _logger.LogInformation("Stopping screen share (native={IsNative})...", _isNativeCapture);

            if (_isNativeCapture && _screenCapture is DxgiScreenCapture dxgi)
            {
                // Stop DXGI capture loop and unsubscribe from both event paths
                dxgi.OnRawFrameCaptured -= OnDxgiRawFrameCaptured;
                dxgi.OnFrameCaptured -= OnDxgiFrameCaptured;
                dxgi.StopCaptureLoop();
                await _webrtc.StopNativeCaptureAsync();
                _isNativeCapture = false;
            }
            else
            {
                // Stop browser getDisplayMedia capture
                await _webrtc.StopScreenCaptureAsync();
            }

            IsSharingScreen = false;
            _inputInjector.ClearCapturedMonitor();
            await _webrtc.SendDataAsync(
                JsonSerializer.Serialize(new { type = "screenShareStopped" }));
            _logger.LogInformation("Screen sharing stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop screen sharing");
        }
    }

    /// <summary>
    /// Relay DXGI captured JPEG frames to JS WebRTC pipeline.
    /// SharedBuffer path: zero-copy via WebView2 shared memory (preferred).
    /// Fallback path: base64 + JSInterop (slower, higher latency).
    /// Called from DXGI capture thread — fire-and-forget (don't block capture).
    /// </summary>
    /// <summary>
    /// Raw BGRA path — skip JPEG encode entirely. SharedBuffer sends raw pixels to JS,
    /// which creates VideoFrame directly. Saves 20-40ms per frame.
    /// </summary>
    private void OnDxgiRawFrameCaptured(byte[] bgraData, int width, int height, int stride)
    {
        if (_webrtc == null || !_isNativeCapture) return;
#if WINDOWS
        _frameBridge!.PushRawFrame(bgraData, width, height, stride, SessionId);
#endif
    }

    /// <summary>
    /// JPEG fallback path — used when SharedBuffer not available.
    /// </summary>
    private void OnDxgiFrameCaptured(byte[] jpegData, int width, int height)
    {
        if (_webrtc == null || !_isNativeCapture) return;

#if WINDOWS
        if (_frameBridge?.IsInitialized == true)
        {
            _frameBridge.PushFrame(jpegData, width, height, SessionId);
            return;
        }
#endif

        var base64Normal = Convert.ToBase64String(jpegData);
        _ = _webrtc.PushNativeFrameAsync(base64Normal, width, height);
    }

    private string? _lastSentCursorShape;

    private void OnCursorShapeChanged(string cssValue)
    {
        // Deduplicate (already checked by HCURSOR handle, but guard against rapid re-fires)
        if (cssValue == _lastSentCursorShape) return;
        _lastSentCursorShape = cssValue;

        // Fire-and-forget over data channel — cursor shape is transient, loss is OK
        if (_webrtc?.IsDataChannelOpen == true)
        {
            _ = _webrtc.SendDataAsync(JsonSerializer.Serialize(new
            {
                type = "cursorShape",
                cursor = cssValue
            }));
        }
    }

    private async Task HandleToggleCursorAsync()
    {
        if (_activeDxgi != null)
        {
            _activeDxgi.ShowCursor = !_activeDxgi.ShowCursor;
            _logger.LogInformation("Host cursor visibility: {Visible}", _activeDxgi.ShowCursor);

            await _webrtc!.SendDataAsync(JsonSerializer.Serialize(new
            {
                type = "cursorVisibilityChanged",
                visible = _activeDxgi.ShowCursor
            }));
        }
    }

    private async Task NotifyScreenShareStarted()
    {
        // Wait for track to propagate, then notify peer
        await Task.Delay(500);
        for (int i = 0; i < 3; i++)
        {
            var sent = await _webrtc!.SendDataAsync(
                JsonSerializer.Serialize(new { type = "screenShareStarted" }));
            _logger.LogInformation("screenShareStarted message sent: {Sent}", sent);
            if (sent) break;
            await Task.Delay(200);
        }
    }

    #endregion

    #region Data Channel

    /// <summary>Send string data to the peer via data channel.</summary>
    public async Task<bool> SendDataAsync(string data)
    {
        if (_webrtc == null || !_webrtc.IsDataChannelOpen) return false;
        return await _webrtc.SendDataAsync(data);
    }

    private async Task HandleDataChannelOpen()
    {
        _logger.LogInformation("Host: Data channel opened - ready for communication");
        SetState(HostSessionState.Connected);
        OnReady?.Invoke();

        // Start clipboard file monitoring (detect CF_HDROP on host clipboard)
        StartClipboardFileTransfer();

        // Send elevation status to viewer (elevated = admin helper, systemLevel = SYSTEM helper)
        try
        {
            var elevated = _elevationService?.IsAdminConnected ?? false;
            var systemLevel = _elevationService?.IsSystemConnected ?? false;
            await _webrtc!.SendDataAsync(JsonSerializer.Serialize(new
            {
                type = "hostStatus",
                elevated,
                systemLevel
            }));
            _logger.LogInformation("Sent elevation status: elevated={Elevated}, systemLevel={SystemLevel}", elevated, systemLevel);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send elevation status");
        }

        // Send monitor layout to viewer (so they see the monitor picker)
        await SendMonitorLayoutAsync();

        // Auto-start full screen sharing on reconnect after reboot
        if (AutoShareOnReady)
        {
            AutoShareOnReady = false;
            try
            {
                _logger.LogInformation("Auto-sharing screen after reboot reconnect");
                await StartScreenShareAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-start screen share after reconnect");
            }
        }
    }

    private async Task HandleDataChannelClose()
    {
        _logger.LogWarning("Host: Data channel closed");
        if (State == HostSessionState.Connected)
        {
            SetState(HostSessionState.Disconnected);
            OnDisconnected?.Invoke("Data channel closed");
        }
        await Task.CompletedTask;
    }

    private async Task HandleDataChannelMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var typeElement))
            {
                var type = typeElement.GetString();
                switch (type)
                {
                    case "rebootHost":
                        _logger.LogInformation("Received reboot request from viewer");
                        await HandleRebootAsync();
                        return;

                    case "ctrlAltDel":
                        _logger.LogInformation("Received Ctrl+Alt+Del request from viewer");
                        await HandleCtrlAltDelAsync();
                        return;

                    case "lockWorkstation":
                        _logger.LogInformation("Received lock workstation request from viewer");
                        await HandleLockWorkstationAsync();
                        return;

                    case "requestElevation":
                        await HandleRequestElevationAsync();
                        return;

                    case "runElevated":
                        await HandleRunElevatedAsync(root);
                        return;

                    case "requestSystemElevation":
                        await HandleRequestSystemElevationAsync();
                        return;

                    case "runAsSystem":
                        await HandleRunAsSystemAsync(root);
                        return;

                    case "clipboard_request":
                        await HandleClipboardRequestAsync();
                        return;

                    case "clipboard_set":
                        await HandleClipboardSetAsync(root);
                        return;

                    case "clipboard_paste":
                        await HandleClipboardPasteAsync(root);
                        return;

                    case "switchDisplay":
                        var monitorId = root.TryGetProperty("monitorId", out var midProp) ? midProp.GetInt32() : -1;
                        if (monitorId >= 0)
                            await HandleSwitchDisplayAsync(monitorId);
                        return;

                    case "toggleCursor":
                        await HandleToggleCursorAsync();
                        return;

                    case "inputLockChanged":
                        var locked = root.TryGetProperty("locked", out var lockedProp) && lockedProp.GetBoolean();
                        if (_activeDxgi != null)
                        {
                            // Hide host cursor in video when viewer is controlling (local overlay replaces it)
                            _activeDxgi.ShowCursor = !locked;
                            _logger.LogInformation("Viewer input lock: {Locked} → host cursor in video: {Visible}", locked, !locked);
                        }
                        return;

                    case "screenShareStarted":
                        _logger.LogInformation("Peer started sharing their screen");
                        IsPeerSharingScreen = true;
                        OnPeerSharingChanged?.Invoke(true);
                        return;

                    case "screenShareStopped":
                        _logger.LogInformation("Peer stopped sharing their screen");
                        IsPeerSharingScreen = false;
                        OnPeerSharingChanged?.Invoke(false);
                        return;
                }
            }

            // Not a control message — treat as input event
            HandleInputMessage(json);
        }
        catch (JsonException)
        {
            // Not JSON, try as input
            HandleInputMessage(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle data channel message");
        }
    }

    #endregion

    #region Monitor Layout

    /// <summary>
    /// Send the host's monitor layout to the viewer via data channel.
    /// Includes monitor positions, sizes, names, and which one is actively captured.
    /// </summary>
    private async Task SendMonitorLayoutAsync(int? activeMonitorId = null)
    {
        if (_webrtc == null || !IsDataChannelReady || _monitorEnumerator == null) return;

        try
        {
            var monitors = _monitorEnumerator.GetMonitors();
            if (monitors.Count == 0) return;

            var layout = new
            {
                type = "monitorLayout",
                monitors = monitors.Select(m => new
                {
                    id = (int)m.Id,
                    name = m.Name,
                    width = (int)m.Width,
                    height = (int)m.Height,
                    x = m.X,
                    y = m.Y,
                    isPrimary = m.IsPrimary
                }),
                activeMonitorId = activeMonitorId
                    ?? (int)(monitors.FirstOrDefault(m => m.IsPrimary)?.Id ?? monitors[0].Id)
            };

            var json = JsonSerializer.Serialize(layout);
            await _webrtc.SendDataAsync(json);
            _logger.LogInformation("Sent monitor layout to viewer: {Count} monitors, active={Active}",
                monitors.Count, layout.activeMonitorId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send monitor layout");
        }
    }

    /// <summary>
    /// Match capture dimensions to a monitor in the enumerated list.
    /// Returns the monitor ID, or null if no match.
    /// </summary>
    private int? MatchCaptureToMonitor(int captureWidth, int captureHeight)
    {
        if (_monitorEnumerator == null) return null;

        var monitors = _monitorEnumerator.GetMonitors();

        // Exact match — unique resolution
        MonitorInfo? firstMatch = null;
        var matchCount = 0;
        foreach (var m in monitors)
        {
            if (m.Width == captureWidth && m.Height == captureHeight)
            {
                firstMatch ??= m;
                matchCount++;
            }
        }

        if (matchCount == 1) return (int)firstMatch!.Id;

        // Multiple matches — prefer primary
        if (matchCount > 1)
        {
            var primary = monitors.FirstOrDefault(m => m.Width == captureWidth && m.Height == captureHeight && m.IsPrimary);
            return (int)(primary?.Id ?? firstMatch!.Id);
        }

        return null; // No match (downscaled capture or full desktop)
    }

    /// <summary>
    /// Handle viewer's request to switch which monitor is being captured.
    /// With DXGI native capture: programmatic switch, no picker.
    /// With getDisplayMedia: stops and restarts (browser picker appears).
    /// </summary>
    private async Task HandleSwitchDisplayAsync(int monitorId)
    {
        _requestedMonitorId = monitorId;
        var monitor = _monitorEnumerator?.GetMonitors().FirstOrDefault(m => m.Id == (uint)monitorId);
        var name = monitor?.Name ?? $"Display {monitorId}";
        _logger.LogInformation("Viewer requested switch to {Monitor} (id={Id})", name, monitorId);

        // Stop current capture, restart on requested monitor
        await StopScreenShareAsync();
        await StartScreenShareAsync(outputIndex: (uint)monitorId);
    }

    private int? _requestedMonitorId;

    #endregion

    #region Input Injection

    private int _inputCount;

    private void HandleInputMessage(string json)
    {
        // Only process input if we're sharing our screen
        if (!IsSharingScreen) return;

        _inputCount++;

        try
        {
            TrackCaptureDimensions(json);

            // Don't inject until we know real capture dimensions (avoids wrong coordinate mapping)
            if (_lastCaptureWidth <= 0 || _lastCaptureHeight <= 0) return;

            // If an elevated helper is connected, route through the elevation service (async, fire-and-forget)
            if (_elevationService != null && (_elevationService.IsAdminConnected || _elevationService.IsSystemConnected))
            {
                if (_inputCount <= 3 || _inputCount % 500 == 0)
                    _logger.LogInformation("Input #{Count}: routing via elevation (admin={Admin}, system={System})",
                        _inputCount, _elevationService.IsAdminConnected, _elevationService.IsSystemConnected);
                _ = InjectInputViaElevationAsync(json);
                return;
            }

            if (_inputCount <= 3 || _inputCount % 500 == 0)
                _logger.LogInformation("Input #{Count}: local injection", _inputCount);

            // No elevation active — local injection (synchronous, no async overhead)
            var inputEvent = JsonSerializer.Deserialize<InputEvent>(json);
            if (inputEvent != null)
            {
                _inputInjector.InjectInput(inputEvent, _lastCaptureWidth, _lastCaptureHeight);
            }
        }
        catch
        {
            // Silently ignore parse errors to reduce latency
        }
    }

    private async Task InjectInputViaElevationAsync(string json)
    {
        try
        {
            var success = await _elevationService!.InjectInputAsync(json, _lastCaptureWidth, _lastCaptureHeight);
            if (!success)
            {
                _logger.LogWarning("Elevation service returned false — falling back to local injection");
                FallbackToLocalInjection(json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Elevated input failed — falling back to local injection");
            FallbackToLocalInjection(json);
        }
    }

    private void FallbackToLocalInjection(string json)
    {
        try
        {
            var inputEvent = JsonSerializer.Deserialize<InputEvent>(json);
            if (inputEvent != null)
                _inputInjector.InjectInput(inputEvent, _lastCaptureWidth, _lastCaptureHeight);
        }
        catch { }
    }

    private void TrackCaptureDimensions(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("captureWidth", out var cw) && cw.ValueKind == JsonValueKind.Number)
            {
                var w = cw.GetInt32();
                if (w > 0) _lastCaptureWidth = w;
            }
            if (root.TryGetProperty("captureHeight", out var ch) && ch.ValueKind == JsonValueKind.Number)
            {
                var h = ch.GetInt32();
                if (h > 0) _lastCaptureHeight = h;
            }
        }
        catch { }
    }

    #endregion

    #region Secure Desktop (Phase 2)

    private int _sdHostFrameCount;

    private void HandleSecureDesktopFrame(byte[] jpegData, int width, int height)
    {
        _sdHostFrameCount++;
        if (_sdHostFrameCount <= 3 || _sdHostFrameCount % 100 == 0)
            _logger.LogInformation("SD frame #{Count}: {Bytes}b {W}x{H}, webrtc={Webrtc}, dcReady={DcReady}",
                _sdHostFrameCount, jpegData.Length, width, height, _webrtc != null, IsDataChannelReady);

        if (_webrtc == null || !IsDataChannelReady) return;

        try
        {
            var base64 = Convert.ToBase64String(jpegData);
            _ = _webrtc.SendDataAsync(JsonSerializer.Serialize(new
            {
                type = "secureDesktopFrame",
                data = base64,
                width,
                height
            }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send secure desktop frame");
        }
    }

    private void HandleSecureDesktopStateChanged(bool active)
    {
        _logger.LogInformation("SD state handler: active={Active}, webrtc={Webrtc}, dcReady={DcReady}",
            active, _webrtc != null, IsDataChannelReady);

        if (_webrtc == null || !IsDataChannelReady) return;

        try
        {
            var message = active
                ? JsonSerializer.Serialize(new { type = "secureDesktopActive" })
                : JsonSerializer.Serialize(new { type = "secureDesktopInactive" });

            _ = _webrtc.SendDataAsync(message);

            // Pause/resume video track to free bandwidth for Secure Desktop JPEG frames
            _ = active ? _webrtc.PauseVideoTrackAsync() : _webrtc.ResumeVideoTrackAsync();

            _logger.LogInformation("Sent {Type} to viewer, video track {Action}",
                active ? "secureDesktopActive" : "secureDesktopInactive",
                active ? "paused" : "resumed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send secure desktop state change");
        }
    }

    private void HandleSystemStateChanged(bool connected)
    {
        if (_webrtc == null || !IsDataChannelReady) return;

        try
        {
            // Notify viewer that SYSTEM helper connected (auto-launched with admin)
            _ = _webrtc.SendDataAsync(JsonSerializer.Serialize(new
            {
                type = "hostStatus",
                elevated = _elevationService?.IsAdminConnected ?? false,
                systemLevel = connected
            }));
            _logger.LogInformation("SYSTEM helper state changed: {Connected} — notified viewer", connected);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send SYSTEM state change to viewer");
        }
    }

    #endregion

    #region Clipboard

    private async Task HandleClipboardRequestAsync()
    {
        if (_webrtc == null) return;

        string? text = null;
        // Try browser API first
        try
        {
            text = await _jsRuntime.InvokeAsync<string>("navigator.clipboard.readText");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Browser clipboard.readText failed — trying native Win32");
        }

        // Fall back to native Win32
        if (string.IsNullOrEmpty(text))
            text = TryGetClipboardNative();

        if (!string.IsNullOrEmpty(text))
        {
            var response = JsonSerializer.Serialize<ClipboardMessage>(
                new ClipboardMessage.Response("text", text));
            await _webrtc.SendDataAsync(response);
            _logger.LogDebug("Sent clipboard to viewer: {Length} chars", text.Length);
        }
    }

    private async Task HandleClipboardSetAsync(JsonElement root)
    {
        var data = root.TryGetProperty("data", out var d) ? d.GetString() : null;
        if (data == null) return;
        try
        {
            await _jsRuntime.InvokeVoidAsync("navigator.clipboard.writeText", data);
            _logger.LogDebug("Set clipboard from viewer: {Length} chars", data.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set clipboard from viewer");
        }
    }

    private async Task HandleClipboardPasteAsync(JsonElement root)
    {
        var data = root.TryGetProperty("data", out var d) ? d.GetString() : null;
        if (data == null) return;

        // Step 1: Set host clipboard — try browser API first, fall back to native Win32
        bool clipboardSet = false;
        try
        {
            await _jsRuntime.InvokeVoidAsync("navigator.clipboard.writeText", data);
            clipboardSet = true;
            _logger.LogDebug("Clipboard paste: set via browser API ({Length} chars)", data.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Browser clipboard.writeText failed — trying native Win32");
        }

        if (!clipboardSet)
        {
            clipboardSet = TrySetClipboardNative(data);
            if (clipboardSet)
                _logger.LogDebug("Clipboard paste: set via Win32 ({Length} chars)", data.Length);
            else
                _logger.LogWarning("Failed to set clipboard via both browser API and Win32");
        }

        if (!clipboardSet) return; // Don't inject Ctrl+V if clipboard wasn't set

        // Step 2: Inject Ctrl+V to paste from the clipboard we just set
        try
        {
            var ctrlMod = new KeyModifiers(Ctrl: true);
            var noMod = KeyModifiers.None;
            InputEvent[] keystrokes =
            [
                new InputEvent.KeyDown("Control", ctrlMod),
                new InputEvent.KeyDown("v", ctrlMod),
                new InputEvent.KeyUp("v", ctrlMod),
                new InputEvent.KeyUp("Control", noMod),
            ];

            foreach (var keystroke in keystrokes)
            {
                var json = JsonSerializer.Serialize(keystroke);
                if (_elevationService != null && (_elevationService.IsAdminConnected || _elevationService.IsSystemConnected))
                {
                    await _elevationService.InjectInputAsync(json, _lastCaptureWidth, _lastCaptureHeight);
                }
                else
                {
                    _inputInjector.InjectInput(keystroke, _lastCaptureWidth, _lastCaptureHeight);
                }
            }
            _logger.LogDebug("Clipboard paste: Ctrl+V injected");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inject Ctrl+V for clipboard paste");
        }
    }

    /// <summary>
    /// Native Win32 clipboard read — works without WebView focus.
    /// </summary>
    private static string? TryGetClipboardNative()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            if (!OpenClipboard(IntPtr.Zero)) return null;
            try
            {
                var hData = GetClipboardData(CF_UNICODETEXT);
                if (hData == IntPtr.Zero) return null;

                var pData = GlobalLock(hData);
                if (pData == IntPtr.Zero) return null;
                try
                {
                    return System.Runtime.InteropServices.Marshal.PtrToStringUni(pData);
                }
                finally { GlobalUnlock(hData); }
            }
            finally { CloseClipboard(); }
        }
        catch { return null; }
    }

    /// <summary>
    /// Native Win32 clipboard write — works without WebView focus.
    /// </summary>
    private static bool TrySetClipboardNative(string text)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            if (!OpenClipboard(IntPtr.Zero)) return false;
            try
            {
                EmptyClipboard();
                // Clipboard requires GlobalAlloc(GMEM_MOVEABLE) — NOT Marshal.StringToHGlobal
                int byteCount = (text.Length + 1) * 2; // UTF-16 + null terminator
                var hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)byteCount);
                if (hGlobal == IntPtr.Zero) return false;

                var pGlobal = GlobalLock(hGlobal);
                if (pGlobal == IntPtr.Zero) { GlobalFree(hGlobal); return false; }
                try
                {
                    System.Runtime.InteropServices.Marshal.Copy(text.ToCharArray(), 0, pGlobal, text.Length);
                    // Write null terminator
                    System.Runtime.InteropServices.Marshal.WriteInt16(pGlobal + text.Length * 2, 0);
                }
                finally { GlobalUnlock(hGlobal); }

                if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
                {
                    GlobalFree(hGlobal);
                    return false;
                }
                // SetClipboardData takes ownership of hGlobal — do NOT free it
                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch { return false; }
    }

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);


    #endregion

    #region Clipboard File Transfer

    /// <summary>
    /// Initialize clipboard file transfer infrastructure.
    /// Host monitors clipboard for CF_HDROP, serves file chunks, and receives remote files.
    /// </summary>
    private void StartClipboardFileTransfer()
    {
        if (!OperatingSystem.IsWindows() || _webrtc == null) return;

        try
        {
            // File server — serves file chunks to remote when they paste
            _clipboardFileServer = new ClipboardFileServer(
                _loggerFactory.CreateLogger<ClipboardFileServer>(),
                async (data) => await _webrtc.SendFileChannelDataAsync(
                    System.Text.Encoding.UTF8.GetString(data)));

            // Clipboard monitor — detects when user copies files (CF_HDROP)
            _clipboardMonitor = new ClipboardMonitor(_loggerFactory.CreateLogger<ClipboardMonitor>());
            _clipboardMonitor.ClipboardFilesDetected += OnClipboardFilesDetected;
            _clipboardMonitor.Start();

            // Clipboard file writer — receives FormatList from viewer and presents virtual files
            _clipboardFileWriter = new ClipboardFileWriter(
                _loggerFactory.CreateLogger<ClipboardFileWriter>(),
                async (request) =>
                {
                    var json = System.Text.Json.JsonSerializer.Serialize<ClipboardFileMessage>(request);
                    await _webrtc.SendFileChannelDataAsync(json);
                },
                _clipboardMonitor);
            _clipboardFileWriter.Start();

            _logger.LogInformation("Clipboard file transfer initialized");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize clipboard file transfer");
        }
    }

    private void OnClipboardFilesDetected(ClipboardFileInfo[] files, string[] localPaths)
    {
        if (_webrtc == null || !_webrtc.IsDataChannelOpen) return;

        try
        {
            // Update file server with new paths
            _clipboardFileServer?.SetFilePaths(localPaths);

            // Send format list to remote so they can present virtual files
            var formatList = new ClipboardFileMessage.FormatList(files);
            var json = System.Text.Json.JsonSerializer.Serialize<ClipboardFileMessage>(formatList);

            _ = Task.Run(async () =>
            {
                try
                {
                    await _webrtc.SendFileChannelDataAsync(json);
                    _logger.LogInformation("Sent clipboard file format list: {Count} files", files.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send clipboard file format list");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling clipboard files detected");
        }
    }

    private async Task HandleFileChannelMessage(string json)
    {
        try
        {
            var message = System.Text.Json.JsonSerializer.Deserialize<ClipboardFileMessage>(json);
            if (message == null) return;

            switch (message)
            {
                case ClipboardFileMessage.FormatList formatList:
                    // Remote has files on clipboard — present them as virtual files on our clipboard
                    _clipboardFileWriter?.SetClipboard(formatList.Files);
                    break;

                case ClipboardFileMessage.FileContentsRequest request:
                    // Remote is pasting — they need file data from us
                    if (_clipboardFileServer != null)
                        await _clipboardFileServer.HandleRequestAsync(request);
                    break;

                case ClipboardFileMessage.FileContentsResponse response:
                    // We requested file data (we're pasting remote files) — resolve pending stream
                    _clipboardFileWriter?.HandleFileContentsResponse(response);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle file channel message");
        }
    }

    private void StopClipboardFileTransfer()
    {
        _clipboardMonitor?.Dispose();
        _clipboardMonitor = null;

        _clipboardFileServer?.Dispose();
        _clipboardFileServer = null;

        _clipboardFileWriter?.Dispose();
        _clipboardFileWriter = null;
    }

    #endregion

    #region Elevation & System Controls

    /// <summary>
    /// Launch the elevated helper process (triggers UAC on host).
    /// No app restart — main app stays running, WebRTC stays connected.
    /// </summary>
    private Task HandleRequestElevationAsync()
    {
        if (_webrtc == null || _elevationService == null) return Task.CompletedTask;

        if (_elevationService.IsAdminConnected)
        {
            _logger.LogInformation("Elevated helper already connected");
            return _webrtc.SendDataAsync(JsonSerializer.Serialize(new
            {
                type = "elevationAlready"
            }));
        }

        // Run on background thread so we don't block data channel message processing
        // (which handles input events). The pipe connect can take up to 10 seconds.
        _ = Task.Run(async () =>
        {
            _logger.LogInformation("Requesting admin elevation...");
            try
            {
                var success = await _elevationService.RequestAdminElevationAsync();

                if (success)
                {
                    _logger.LogInformation("Admin elevation succeeded — admin features enabled");
                    await _webrtc!.SendDataAsync(JsonSerializer.Serialize(new
                    {
                        type = "hostStatus",
                        elevated = true
                    }));
                }
                else
                {
                    _logger.LogWarning("Admin elevation failed (UAC denied or error)");
                    await _webrtc!.SendDataAsync(JsonSerializer.Serialize(new
                    {
                        type = "elevationDenied",
                        message = "UAC prompt was denied or helper failed to start"
                    }));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to request admin elevation");
                try
                {
                    await _webrtc!.SendDataAsync(JsonSerializer.Serialize(new
                    {
                        type = "elevationDenied",
                        message = ex.Message
                    }));
                }
                catch { /* webrtc may be gone */ }
            }
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Send Ctrl+Alt+Del (SAS) via elevation service if available.
    /// </summary>
    private async Task HandleCtrlAltDelAsync()
    {
        if (_webrtc == null) return;

        if (_elevationService != null && (_elevationService.IsAdminConnected || _elevationService.IsSystemConnected))
        {
            var success = await _elevationService.SendSASAsync();
            if (success)
            {
                _logger.LogInformation("Ctrl+Alt+Del sent via elevation service");
            }
            else
            {
                _logger.LogWarning("Ctrl+Alt+Del failed via elevation service");
                await _webrtc.SendDataAsync(JsonSerializer.Serialize(new
                {
                    type = "ctrlAltDelFailed",
                    message = "SendSAS failed via elevated helper"
                }));
            }
        }
        else
        {
            _logger.LogWarning("Ctrl+Alt+Del requested but no elevated helper connected");
            await _webrtc.SendDataAsync(JsonSerializer.Serialize(new
            {
                type = "ctrlAltDelFailed",
                message = "Admin features not enabled — request elevation first"
            }));
        }
    }

    private async Task HandleLockWorkstationAsync()
    {
        if (_elevationService != null)
        {
            var success = await _elevationService.LockWorkStationAsync();
            if (success)
            {
                _logger.LogInformation("Workstation locked via elevation service");
                return;
            }
        }

        // Fallback: rundll32 (works without elevation service being wired up)
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = "user32.dll,LockWorkStation",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            _logger.LogInformation("Workstation locked via rundll32 fallback");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to lock workstation");
        }
    }

    /// <summary>
    /// Reboot the host machine with auto-restart. Routes through elevation service
    /// for RunOnceEx registry write, falls back to direct reboot if not elevated.
    /// </summary>
    private async Task HandleRebootAsync()
    {
        if (_elevationService?.IsAdminConnected == true)
        {
            // Pass server URL + STUN/TURN config for boot relay WebRTC reconnection
            var serverUrl = _configuration["SignalingServer"];
            var stunUrls = new[] { "stun:stun.l.google.com:19302", "stun:stun1.l.google.com:19302" };
            var turnUrls = _configuration.GetSection("TurnServer:Urls").Get<string[]>();
            var turnUser = _configuration["TurnServer:Username"];
            var turnCred = _configuration["TurnServer:Credential"];
            var success = await _elevationService.RebootAsync(_hostClientId, _hostPasswordHash, PeerId,
                serverUrl, stunUrls, turnUrls, turnUser, turnCred);
            if (success)
            {
                _logger.LogInformation("Reboot initiated via elevation service (with auto-restart)");
            }
            else
            {
                _logger.LogWarning("Reboot failed via elevation service");
                if (_webrtc != null)
                    await _webrtc.SendDataAsync(JsonSerializer.Serialize(new
                    {
                        type = "rebootFailed",
                        message = "Reboot command failed"
                    }));
            }
        }
        else
        {
            // No elevated helper — reboot without RunOnceEx (no auto-restart)
            _logger.LogWarning("Reboot requested without elevated helper — rebooting without auto-restart");
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "shutdown",
                    Arguments = "/r /t 0",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initiate reboot");
                if (_webrtc != null)
                    await _webrtc.SendDataAsync(JsonSerializer.Serialize(new
                    {
                        type = "rebootFailed",
                        message = ex.Message
                    }));
            }
        }
    }

    /// <summary>
    /// Run a process elevated via the helper pipe (no additional UAC prompt).
    /// </summary>
    private async Task HandleRunElevatedAsync(JsonElement root)
    {
        if (_webrtc == null) return;

        var path = root.TryGetProperty("path", out var p) ? p.GetString() : null;
        var args = root.TryGetProperty("args", out var a) ? a.GetString() : null;

        if (string.IsNullOrEmpty(path))
        {
            await _webrtc.SendDataAsync(JsonSerializer.Serialize(new
            {
                type = "runElevatedFailed",
                message = "No path specified"
            }));
            return;
        }

        if (_elevationService?.IsAdminConnected == true)
        {
            var success = await _elevationService.RunElevatedAsync(path, args);
            if (success)
            {
                _logger.LogInformation("RunElevated succeeded: {Path}", path);
                await _webrtc.SendDataAsync(JsonSerializer.Serialize(new
                {
                    type = "runElevatedSuccess",
                    path
                }));
            }
            else
            {
                await _webrtc.SendDataAsync(JsonSerializer.Serialize(new
                {
                    type = "runElevatedFailed",
                    message = $"Failed to launch: {path}"
                }));
            }
        }
        else
        {
            await _webrtc.SendDataAsync(JsonSerializer.Serialize(new
            {
                type = "runElevatedFailed",
                message = "Admin features not enabled — request elevation first"
            }));
        }
    }

    /// <summary>
    /// Launch the SYSTEM-level helper via the admin helper (schtask as SYSTEM).
    /// Requires admin helper to be connected first.
    /// </summary>
    private Task HandleRequestSystemElevationAsync()
    {
        if (_webrtc == null || _elevationService == null) return Task.CompletedTask;

        if (_elevationService.IsSystemConnected)
        {
            _logger.LogInformation("SYSTEM helper already connected");
            return _webrtc.SendDataAsync(JsonSerializer.Serialize(new
            {
                type = "systemElevationAlready"
            }));
        }

        if (!_elevationService.IsAdminConnected)
        {
            _logger.LogWarning("Cannot launch SYSTEM helper: admin helper not connected");
            return _webrtc.SendDataAsync(JsonSerializer.Serialize(new
            {
                type = "systemElevationDenied",
                message = "Admin features must be enabled first"
            }));
        }

        // Run on background thread (schtask creation + pipe connect can take 15+ seconds)
        _ = Task.Run(async () =>
        {
            _logger.LogInformation("Requesting SYSTEM elevation...");
            try
            {
                var success = await _elevationService.RequestSystemElevationAsync();

                if (success)
                {
                    _logger.LogInformation("SYSTEM elevation succeeded — SYSTEM features enabled");
                    await _webrtc!.SendDataAsync(JsonSerializer.Serialize(new
                    {
                        type = "hostStatus",
                        elevated = true,
                        systemLevel = true
                    }));
                }
                else
                {
                    _logger.LogWarning("SYSTEM elevation failed");
                    await _webrtc!.SendDataAsync(JsonSerializer.Serialize(new
                    {
                        type = "systemElevationFailed",
                        message = "Failed to create SYSTEM helper (scheduled task or pipe error)"
                    }));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to request SYSTEM elevation");
                try
                {
                    await _webrtc!.SendDataAsync(JsonSerializer.Serialize(new
                    {
                        type = "systemElevationFailed",
                        message = ex.Message
                    }));
                }
                catch { /* webrtc may be gone */ }
            }
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Run a process as SYSTEM in the user's desktop session via the SYSTEM helper.
    /// </summary>
    private async Task HandleRunAsSystemAsync(JsonElement root)
    {
        if (_webrtc == null) return;

        var path = root.TryGetProperty("path", out var p) ? p.GetString() : null;
        var args = root.TryGetProperty("args", out var a) ? a.GetString() : null;

        if (string.IsNullOrEmpty(path))
        {
            await _webrtc.SendDataAsync(JsonSerializer.Serialize(new
            {
                type = "runAsSystemFailed",
                message = "No path specified"
            }));
            return;
        }

        if (_elevationService?.IsSystemConnected == true)
        {
            var success = await _elevationService.RunAsSystemAsync(path, args);
            if (success)
            {
                _logger.LogInformation("RunAsSystem succeeded: {Path}", path);
                await _webrtc.SendDataAsync(JsonSerializer.Serialize(new
                {
                    type = "runAsSystemSuccess",
                    path
                }));
            }
            else
            {
                await _webrtc.SendDataAsync(JsonSerializer.Serialize(new
                {
                    type = "runAsSystemFailed",
                    message = $"Failed to launch: {path}"
                }));
            }
        }
        else
        {
            await _webrtc.SendDataAsync(JsonSerializer.Serialize(new
            {
                type = "runAsSystemFailed",
                message = "SYSTEM features not enabled — request system elevation first"
            }));
        }
    }

    #endregion

    #region WebRTC Event Handlers

    private async Task HandleIceCandidate(string candidateJson)
    {
        try
        {
            var candidate = JsonSerializer.Deserialize<IceCandidateData>(candidateJson);
            if (candidate != null && OnIceCandidate != null)
            {
                await OnIceCandidate.Invoke(candidate.candidate, candidate.sdpMid, (ushort?)candidate.sdpMLineIndex);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle ICE candidate");
        }
    }

    private async Task HandleRenegotiationNeeded(string offerJson)
    {
        _logger.LogInformation("Renegotiation: sending new offer to {PeerId}", PeerId);
        if (OnSdpMessage != null)
            await OnSdpMessage.Invoke(PeerId, offerJson);
    }

    private async Task HandleConnectionStateChange(string state)
    {
        _logger.LogInformation("Connection state changed to {State}", state);
        switch (state)
        {
            case "connected":
                SetState(HostSessionState.Connected);
                break;
            case "disconnected":
                // Temporary ICE state — don't tear down. ICE will usually recover within seconds.
                // JS side already handles this (pauses capture, keeps input lock).
                _logger.LogWarning("WebRTC temporarily disconnected (ICE recovering)");
                break;
            case "failed":
            case "closed":
                SetState(HostSessionState.Disconnected);
                OnDisconnected?.Invoke(state);
                break;
        }
        await Task.CompletedTask;
    }

    private void HandleScreenShareLost()
    {
        _logger.LogWarning("Screen sharing lost — all auto-restart attempts failed");
        IsSharingScreen = false;
        OnScreenShareLost?.Invoke();
    }

    private void HandleCaptureStarted(int width, int height)
    {
        if (width > 0 && height > 0)
        {
            _lastCaptureWidth = width;
            _lastCaptureHeight = height;
            _inputInjector.SetCapturedMonitor(width, height);
            var source = _isNativeCapture ? "DXGI native" : "getDisplayMedia";
            _logger.LogInformation("Host capture dimensions set: {W}x{H} (from {Source}), monitor cached", width, height, source);

            // Send updated monitor layout with the matched active monitor
            var matchedId = MatchCaptureToMonitor(width, height);
            _ = SendMonitorLayoutAsync(matchedId);
        }
    }

    #endregion

    #region TURN Configuration

    private async Task SetTurnConfigFromSettings()
    {
        var turnEnabled = _configuration.GetValue<bool>("TurnServer:Enabled");
        if (!turnEnabled)
        {
            _logger.LogInformation("TURN server disabled in config");
            return;
        }

        var urls = _configuration.GetSection("TurnServer:Urls").Get<string[]>();
        var username = _configuration["TurnServer:Username"];
        var credential = _configuration["TurnServer:Credential"];

        if (urls == null || urls.Length == 0 || string.IsNullOrEmpty(username))
        {
            _logger.LogWarning("TURN server enabled but not configured");
            return;
        }

        if (urls[0].Contains("YOUR_TURN_SERVER"))
        {
            _logger.LogWarning("TURN server URL is placeholder");
            return;
        }

        _logger.LogInformation("Configuring TURN server: {Urls}", string.Join(", ", urls));
        await _jsRuntime.InvokeVoidAsync("SteamViewerWebRTC.setTurnConfig", urls, username, credential);
    }

    #endregion

    private void SetState(HostSessionState newState)
    {
        if (State != newState)
        {
            State = newState;
            OnStateChanged?.Invoke(newState);
        }
    }

    /// <summary>Disconnect and clean up the session.</summary>
    public async Task DisconnectAsync()
    {
        if (_webrtc != null)
        {
            await _webrtc.CloseAsync();
        }
        IsSharingScreen = false;
        IsPeerSharingScreen = false;
        SetState(HostSessionState.Disconnected);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_elevationService != null && !_elevationDetached)
        {
            _elevationService.OnSecureDesktopFrame -= HandleSecureDesktopFrame;
            _elevationService.OnSecureDesktopStateChanged -= HandleSecureDesktopStateChanged;
            _elevationService.OnSystemStateChanged -= HandleSystemStateChanged;
            await _elevationService.DisposeAsync();
        }

        // Stop clipboard file transfer
        StopClipboardFileTransfer();

        // Stop DXGI capture if active
        if (_isNativeCapture && _screenCapture is DxgiScreenCapture dxgi)
        {
            dxgi.OnRawFrameCaptured -= OnDxgiRawFrameCaptured;
            dxgi.OnFrameCaptured -= OnDxgiFrameCaptured;
            dxgi.StopCaptureLoop();
            _isNativeCapture = false;
        }

        if (_webrtc != null)
        {
            _webrtc.OnIceCandidate -= HandleIceCandidate;
            _webrtc.OnDataChannelMessage -= HandleDataChannelMessage;
            _webrtc.OnFileChannelMessage -= HandleFileChannelMessage;
            _webrtc.OnRenegotiationNeeded -= HandleRenegotiationNeeded;
            _webrtc.OnDataChannelOpen -= HandleDataChannelOpen;
            _webrtc.OnDataChannelClose -= HandleDataChannelClose;
            _webrtc.OnConnectionStateChange -= HandleConnectionStateChange;
            _webrtc.OnScreenShareLost -= HandleScreenShareLost;
            _webrtc.OnCaptureStarted -= HandleCaptureStarted;

            await _webrtc.DisposeAsync();
            _webrtc = null;
        }

        _pendingSdpOffer = null;
        _pendingIceCandidates.Clear();
    }

    private record IceCandidateData(string candidate, string? sdpMid, int? sdpMLineIndex);
}
