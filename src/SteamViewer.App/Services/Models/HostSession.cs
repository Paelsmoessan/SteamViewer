using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SteamViewer.Client.Core.Capture;
using SteamViewer.Client.Core.Elevation;
using SteamViewer.Client.Core.Network;
using SteamViewer.Client.Core.Video;
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
    /// <summary>Session is being initialized (transport setup).</summary>
    Initializing,
    /// <summary>Transport listening, waiting for viewer to connect.</summary>
    WaitingForViewer,
    /// <summary>Viewer connected; ready for screen sharing and input.</summary>
    Connected,
    /// <summary>Session has been disconnected.</summary>
    Disconnected,
    /// <summary>Session encountered an error.</summary>
    Error
}

/// <summary>
/// Represents a single host session with a connected viewer peer.
/// Encapsulates TCP transport, FFmpeg encoding, screen sharing, input injection, and file transfer.
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
    private readonly IConfiguration _configuration;
    private readonly Func<SignalingMessage, Task> _sendSignaling;
    private readonly SignalingClient _signalingClient;
    private readonly string _hostClientId;
    private readonly string _hostPasswordHash;
    private HostStreamTransport? _transport;
    private FFmpegEncoder? _encoder;
    private bool _disposed;
    private bool _elevationDetached;

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

    /// <summary>Whether the transport is connected and ready.</summary>
    public bool IsDataChannelReady => _transport?.IsConnected ?? false;

    /// <summary>Whether this host is sharing its screen to the viewer.</summary>
    public bool IsSharingScreen { get; private set; }

    /// <summary>Whether the connected viewer is sharing their screen.</summary>
    public bool IsPeerSharingScreen { get; private set; }

    /// <summary>When true, auto-start full screen sharing when transport connects (used for post-reboot reconnect).</summary>
    public bool AutoShareOnReady { get; set; }

    #region Events

    /// <summary>Raised when session state changes.</summary>
    public event Action<HostSessionState>? OnStateChanged;

    /// <summary>Raised when the transport connects (ready for screen share/input).</summary>
    public event Action? OnReady;

    /// <summary>Raised when the session disconnects.</summary>
    public event Action<string?>? OnDisconnected;

    /// <summary>Raised when screen sharing was lost and all auto-restart attempts failed.</summary>
    public event Action? OnScreenShareLost;

    /// <summary>Raised when the peer starts/stops sharing their screen.</summary>
    public event Action<bool>? OnPeerSharingChanged;

    #endregion

    public HostSession(
        string peerId,
        IJSRuntime jsRuntime,
        ILoggerFactory loggerFactory,
        IInputInjector inputInjector,
        IConfiguration configuration,
        Func<SignalingMessage, Task> sendSignaling,
        SignalingClient signalingClient,
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
        _configuration = configuration;
        _sendSignaling = sendSignaling;
        _signalingClient = signalingClient;
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
    /// Initialize transport: setup WebSocket relay with AES-GCM encryption,
    /// send RelayReady to viewer via signaling.
    /// </summary>
    public async Task InitializeAsync()
    {
        _logger.LogInformation("Initializing host session for peer {PeerId}", PeerId);

        // Set logger to host mode
        await _jsRuntime.InvokeVoidAsync("SteamViewerLogger.setMode", true);

        // Create relay transport (binary frames through signaling WebSocket)
        _transport = new HostStreamTransport(_signalingClient, _loggerFactory.CreateLogger<HostStreamTransport>());

        // Subscribe to transport events
        _transport.OnControlMessage += HandleControlMessage;
        _transport.OnFileData += HandleFileDataBinary;
        _transport.OnFileSignalingMessage += HandleFileChannelMessage;
        _transport.OnConnectionStateChanged += HandleTransportStateChanged;

        // Start relay: generate nonce, setup encryption, send RelayReady to viewer
        await _transport.StartRelayAsync(PeerId, _hostPasswordHash, _sendSignaling);

        _logger.LogInformation("Host transport relay started for peer {PeerId}", PeerId);

        // Don't send initial state yet — wait for viewer's "viewerReady" ack
        // (viewer sends it after receiving RelayReady and connecting their relay)
        SetState(HostSessionState.WaitingForViewer);
    }

    /// <summary>
    /// Handle a TransportEndpoint from the viewer (their UDP candidate IPs/port).
    /// Called from Home.razor when signaling routes TransportEndpoint to this session.
    /// </summary>
    public async Task HandleViewerTransportEndpointAsync(string[] ips, int port)
    {
        if (_transport == null)
        {
            _logger.LogWarning("Received viewer TransportEndpoint but transport is null");
            return;
        }

        _logger.LogInformation("Host: Received viewer UDP endpoints ({Count} IPs, port {Port})", ips.Length, port);
        await _transport.HandleViewerEndpointAsync(ips, port);
    }

    /// <summary>
    /// Handle TransportConfirmed from the viewer — viewer's UDP probe succeeded.
    /// Called from Home.razor when signaling routes TransportConfirmed to this session.
    /// </summary>
    public async Task HandleTransportConfirmedAsync()
    {
        if (_transport == null)
        {
            _logger.LogWarning("Received TransportConfirmed but transport is null");
            return;
        }

        _logger.LogInformation("Host: Received TransportConfirmed from viewer");
        await _transport.HandleTransportConfirmedAsync();
    }

    private async Task HandleTransportConnected()
    {
        _logger.LogInformation("Host: Transport connected - ready for communication");
        SetState(HostSessionState.Connected);
        OnReady?.Invoke();

        // Start clipboard file monitoring (detect CF_HDROP on host clipboard)
        StartClipboardFileTransfer();

        // Fire-and-forget UDP upgrade attempt (relay continues working in background)
        _ = Task.Run(async () =>
        {
            try
            {
                var turnUri = _configuration["TurnServer:Urls:0"];
                var turnUser = _configuration["TurnServer:Username"];
                var turnCred = _configuration["TurnServer:Credential"];
                _logger.LogInformation("Host: Starting UDP upgrade (TURN uri={TurnUri}, user={TurnUser}, cred={HasCred})",
                    turnUri ?? "null", turnUser ?? "null", turnCred != null ? "yes" : "no");
                await _transport!.AttemptUdpUpgradeAsync(
                    PeerId, _sendSignaling, turnUri, turnUser, turnCred);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "UDP upgrade attempt failed");
            }
        });

        // Send elevation status to viewer
        try
        {
            var elevated = _elevationService?.IsAdminConnected ?? false;
            var systemLevel = _elevationService?.IsSystemConnected ?? false;
            await _transport!.SendControlAsync(JsonSerializer.Serialize(new
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

        // Send monitor layout to viewer
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

    #region Screen Sharing

    /// <summary>
    /// Start sharing screen to the connected viewer via DXGI capture + FFmpeg encoding.
    /// </summary>
    /// <param name="outputIndex">DXGI output index to capture (null = auto-select primary)</param>
    public async Task<bool> StartScreenShareAsync(uint? outputIndex = null)
    {
        if (_transport == null || !_transport.IsConnected) return false;

        if (_screenCapture is DxgiScreenCapture dxgi)
        {
            try
            {
                var targetOutput = outputIndex ?? 0;
                _logger.LogInformation("Starting DXGI capture on output {Output} → FFmpeg encode → transport...", targetOutput);

                // Subscribe to DXGI frame events — raw BGRA → FFmpeg encode → transport
                dxgi.OnRawFrameCaptured += OnDxgiRawFrameCaptured;
                dxgi.OnCursorShapeChanged += OnCursorShapeChanged;

                // Start DXGI capture loop (~30 FPS)
                dxgi.StartCaptureLoop(targetOutput);

                _isNativeCapture = true;
                _activeDxgi = dxgi;
                IsSharingScreen = true;
                _logger.LogInformation("DXGI capture started on output {Output} → FFmpeg encoder", targetOutput);

                // Notify peer
                await NotifyScreenShareStarted();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DXGI capture + FFmpeg encoding failed");
                try { dxgi.OnRawFrameCaptured -= OnDxgiRawFrameCaptured; } catch { }
                try { dxgi.OnCursorShapeChanged -= OnCursorShapeChanged; } catch { }
                try { dxgi.StopCaptureLoop(); } catch { }
                _isNativeCapture = false;
                _encoder?.Dispose();
                _encoder = null;
                return false;
            }
        }

        _logger.LogWarning("No DXGI screen capture available");
        return false;
    }

    /// <summary>Stop sharing screen.</summary>
    public async Task StopScreenShareAsync()
    {
        if (_transport == null) return;

        try
        {
            _logger.LogInformation("Stopping screen share...");

            if (_isNativeCapture && _screenCapture is DxgiScreenCapture dxgi)
            {
                dxgi.OnRawFrameCaptured -= OnDxgiRawFrameCaptured;
                dxgi.OnCursorShapeChanged -= OnCursorShapeChanged;
                dxgi.StopCaptureLoop();
                _isNativeCapture = false;
            }

            _encoder?.Dispose();
            _encoder = null;

            IsSharingScreen = false;
            _inputInjector.ClearCapturedMonitor();
            await _transport.SendControlAsync(
                JsonSerializer.Serialize(new { type = "screenShareStopped" }));
            _logger.LogInformation("Screen sharing stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop screen sharing");
        }
    }

    /// <summary>
    /// DXGI raw BGRA frame → FFmpeg encode → transport.
    /// Called from DXGI capture thread — must not block.
    /// </summary>
    private void OnDxgiRawFrameCaptured(byte[] bgraData, int width, int height, int stride)
    {
        if (_transport == null || !_transport.IsConnected || !_isNativeCapture) return;

        try
        {
            // Lazy-init encoder on first frame (need real dimensions)
            if (_encoder == null)
            {
                FFmpegInit.EnsureInitialized();
                var encoder = new FFmpegEncoder(_loggerFactory.CreateLogger<FFmpegEncoder>());
                encoder.Initialize(width, height, 30, 20_000_000, crf: 18); // 30fps, CRF 18 (visually lossless), VBV cap 20Mbps
                _encoder = encoder; // Assign only after successful init
                _logger.LogInformation("FFmpeg encoder initialized: {W}x{H}", width, height);
            }
            else
            {
                _encoder.ReinitializeIfNeeded(width, height);
            }

            _encodeSw.Restart();
            var result = _encoder.EncodeFrame(bgraData, stride);
            _encodeSw.Stop();

            if (result is var (naluData, naluLength))
            {
                _transport.EnqueueVideoFrame(naluData, naluLength);

                _encodeFrameCount++;
                if (_encodeFrameCount % 300 == 0)
                    _logger.LogInformation("Encode #{Count}: {Ms:F1}ms, {Size}KB",
                        _encodeFrameCount, _encodeSw.Elapsed.TotalMilliseconds, naluLength / 1024);
            }
        }
        catch (Exception ex)
        {
            if (_encoderErrorCount++ % 300 == 0)
                _logger.LogWarning(ex, "FFmpeg encode error (sample)");
        }
    }

    private int _encoderErrorCount;
    private long _encodeFrameCount;
    private readonly System.Diagnostics.Stopwatch _encodeSw = new();

    private string? _lastSentCursorShape;

    private void OnCursorShapeChanged(string cssValue)
    {
        if (cssValue == _lastSentCursorShape) return;
        _lastSentCursorShape = cssValue;

        if (_transport?.IsConnected == true)
        {
            _ = _transport.SendControlAsync(JsonSerializer.Serialize(new
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

            await _transport!.SendControlAsync(JsonSerializer.Serialize(new
            {
                type = "cursorVisibilityChanged",
                visible = _activeDxgi.ShowCursor
            }));
        }
    }

    private async Task NotifyScreenShareStarted()
    {
        await Task.Delay(500);
        for (int i = 0; i < 3; i++)
        {
            var sent = await _transport!.SendControlAsync(
                JsonSerializer.Serialize(new { type = "screenShareStarted" }));
            _logger.LogInformation("screenShareStarted message sent: {Sent}", sent);
            if (sent) break;
            await Task.Delay(200);
        }
    }

    #endregion

    #region Control Messages

    /// <summary>Send string data to the peer via control channel.</summary>
    public async Task<bool> SendDataAsync(string data)
    {
        if (_transport == null || !_transport.IsConnected) return false;
        return await _transport.SendControlAsync(data);
    }

    private async Task HandleControlMessage(string json)
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
                    case "viewerReady":
                        _logger.LogInformation("Viewer relay connected — sending initial state");
                        await HandleTransportConnected();
                        return;

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
            HandleInputMessage(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle control message");
        }
    }

    private void HandleTransportStateChanged(string state)
    {
        _logger.LogInformation("Transport state changed: {State}", state);
        if (state == "disconnected")
        {
            if (State == HostSessionState.Connected)
            {
                SetState(HostSessionState.Disconnected);
                OnDisconnected?.Invoke("Transport disconnected");
            }
        }
    }

    #endregion

    #region Monitor Layout

    private async Task SendMonitorLayoutAsync(int? activeMonitorId = null)
    {
        if (_transport == null || !IsDataChannelReady || _monitorEnumerator == null) return;

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
            await _transport.SendControlAsync(json);
            _logger.LogInformation("Sent monitor layout to viewer: {Count} monitors, active={Active}",
                monitors.Count, layout.activeMonitorId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send monitor layout");
        }
    }

    private int? MatchCaptureToMonitor(int captureWidth, int captureHeight)
    {
        if (_monitorEnumerator == null) return null;

        var monitors = _monitorEnumerator.GetMonitors();
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
        if (matchCount > 1)
        {
            var primary = monitors.FirstOrDefault(m => m.Width == captureWidth && m.Height == captureHeight && m.IsPrimary);
            return (int)(primary?.Id ?? firstMatch!.Id);
        }

        return null;
    }

    private async Task HandleSwitchDisplayAsync(int monitorId)
    {
        _requestedMonitorId = monitorId;
        var monitor = _monitorEnumerator?.GetMonitors().FirstOrDefault(m => m.Id == (uint)monitorId);
        var name = monitor?.Name ?? $"Display {monitorId}";
        _logger.LogInformation("Viewer requested switch to {Monitor} (id={Id})", name, monitorId);

        await StopScreenShareAsync();
        await StartScreenShareAsync(outputIndex: (uint)monitorId);
    }

    private int? _requestedMonitorId;

    #endregion

    #region Input Injection

    private int _inputCount;

    private void HandleInputMessage(string json)
    {
        if (!IsSharingScreen) return;

        _inputCount++;

        try
        {
            TrackCaptureDimensions(json);

            if (_lastCaptureWidth <= 0 || _lastCaptureHeight <= 0) return;

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
            _logger.LogInformation("SD frame #{Count}: {Bytes}b {W}x{H}, transport={Transport}, ready={Ready}",
                _sdHostFrameCount, jpegData.Length, width, height, _transport != null, IsDataChannelReady);

        if (_transport == null || !IsDataChannelReady) return;

        try
        {
            var base64 = Convert.ToBase64String(jpegData);
            _ = _transport.SendControlAsync(JsonSerializer.Serialize(new
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
        _logger.LogInformation("SD state handler: active={Active}, transport={Transport}, ready={Ready}",
            active, _transport != null, IsDataChannelReady);

        if (_transport == null || !IsDataChannelReady) return;

        try
        {
            var message = active
                ? JsonSerializer.Serialize(new { type = "secureDesktopActive" })
                : JsonSerializer.Serialize(new { type = "secureDesktopInactive" });

            _ = _transport.SendControlAsync(message);

            _logger.LogInformation("Sent {Type} to viewer",
                active ? "secureDesktopActive" : "secureDesktopInactive");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send secure desktop state change");
        }
    }

    private void HandleSystemStateChanged(bool connected)
    {
        if (_transport == null || !IsDataChannelReady) return;

        try
        {
            _ = _transport.SendControlAsync(JsonSerializer.Serialize(new
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
        if (_transport == null) return;

        string? text = null;
        try
        {
            text = await _jsRuntime.InvokeAsync<string>("navigator.clipboard.readText");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Browser clipboard.readText failed — trying native Win32");
        }

        if (string.IsNullOrEmpty(text))
            text = TryGetClipboardNative();

        if (!string.IsNullOrEmpty(text))
        {
            var response = JsonSerializer.Serialize<ClipboardMessage>(
                new ClipboardMessage.Response("text", text));
            await _transport.SendControlAsync(response);
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

        if (!clipboardSet) return;

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

    private static bool TrySetClipboardNative(string text)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            if (!OpenClipboard(IntPtr.Zero)) return false;
            try
            {
                EmptyClipboard();
                int byteCount = (text.Length + 1) * 2;
                var hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)byteCount);
                if (hGlobal == IntPtr.Zero) return false;
                var pGlobal = GlobalLock(hGlobal);
                if (pGlobal == IntPtr.Zero) { GlobalFree(hGlobal); return false; }
                try
                {
                    System.Runtime.InteropServices.Marshal.Copy(text.ToCharArray(), 0, pGlobal, text.Length);
                    System.Runtime.InteropServices.Marshal.WriteInt16(pGlobal + text.Length * 2, 0);
                }
                finally { GlobalUnlock(hGlobal); }
                if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
                {
                    GlobalFree(hGlobal);
                    return false;
                }
                return true;
            }
            finally { CloseClipboard(); }
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

    private void StartClipboardFileTransfer()
    {
        if (!OperatingSystem.IsWindows() || _transport == null) return;

        try
        {
            _clipboardFileServer = new ClipboardFileServer(
                _loggerFactory.CreateLogger<ClipboardFileServer>(),
                async (data) => { return await _transport!.SendFileDataAsync(data); },
                async (json) => await _transport!.SendFileSignalingAsync(json));

            _clipboardMonitor = new ClipboardMonitor(_loggerFactory.CreateLogger<ClipboardMonitor>());
            _clipboardMonitor.ClipboardFilesDetected += OnClipboardFilesDetected;
            _clipboardMonitor.ClipboardTextDetected += OnClipboardTextDetected;
            _clipboardMonitor.Start();

            _clipboardFileWriter = new ClipboardFileWriter(
                _loggerFactory.CreateLogger<ClipboardFileWriter>(),
                async (request) =>
                {
                    var json = JsonSerializer.Serialize<ClipboardFileMessage>(request);
                    await _transport.SendFileSignalingAsync(json);
                },
                _clipboardMonitor,
                async (startMsg) =>
                {
                    var json = JsonSerializer.Serialize<ClipboardFileMessage>(startMsg);
                    await _transport.SendFileSignalingAsync(json);
                },
                async (stopMsg) =>
                {
                    var json = JsonSerializer.Serialize<ClipboardFileMessage>(stopMsg);
                    await _transport.SendFileSignalingAsync(json);
                },
                async (data) => await _transport!.SendFileDataAsync(data));
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
        if (_transport == null || !_transport.IsConnected) return;

        try
        {
            _clipboardFileServer?.SetFilePaths(localPaths);

            var formatList = new ClipboardFileMessage.FormatList(files);
            var json = JsonSerializer.Serialize<ClipboardFileMessage>(formatList);

            _ = Task.Run(async () =>
            {
                try
                {
                    await _transport.SendFileSignalingAsync(json);
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

    private void OnClipboardTextDetected(string text)
    {
        if (_transport == null || !_transport.IsConnected) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var response = JsonSerializer.Serialize<ClipboardMessage>(
                    new ClipboardMessage.Response("text", text));
                await _transport.SendControlAsync(response);
                _logger.LogDebug("Auto-pushed clipboard text to viewer: {Length} chars", text.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-push clipboard text to viewer");
            }
        });
    }

    private async Task HandleFileChannelMessage(string json)
    {
        try
        {
            var message = JsonSerializer.Deserialize<ClipboardFileMessage>(json);
            if (message == null) return;

            switch (message)
            {
                case ClipboardFileMessage.FormatList formatList:
                    _clipboardFileWriter?.SetClipboard(formatList.Files);
                    break;
                case ClipboardFileMessage.FileContentsRequest request:
                    if (_clipboardFileServer != null)
                        await _clipboardFileServer.HandleRequestAsync(request);
                    break;
                case ClipboardFileMessage.StartStreaming startStreaming:
                    _clipboardFileServer?.HandleStartStreaming(startStreaming.FileIndex);
                    break;
                case ClipboardFileMessage.StopStreaming stopStreaming:
                    _clipboardFileServer?.HandleStopStreaming(stopStreaming.FileIndex);
                    break;
                case ClipboardFileMessage.TransferProgress progress:
                    _logger.LogInformation("Remote transfer progress: {FileName} — {Transferred}/{Total} ({Speed} MB/s)",
                        progress.FileName, FormatBytes(progress.BytesTransferred), FormatBytes(progress.TotalBytes), progress.SpeedMBps);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle file channel message");
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    private Task HandleFileDataBinary(byte[] data)
    {
        // Route ACKs to file server (sender), everything else to file writer (receiver)
        if (data.Length >= 8)
        {
            int flags = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4, 4));
            if (flags == ClipboardFileServer.FlagPushAck)
            {
                int fileIndex = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0, 4));
                _clipboardFileServer?.HandlePushAck(fileIndex);
                return Task.CompletedTask;
            }
        }
        _clipboardFileWriter?.HandleBinaryFileContentsResponse(data);
        return Task.CompletedTask;
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

    private Task HandleRequestElevationAsync()
    {
        if (_transport == null || _elevationService == null) return Task.CompletedTask;

        if (_elevationService.IsAdminConnected)
        {
            _logger.LogInformation("Elevated helper already connected");
            return _transport.SendControlAsync(JsonSerializer.Serialize(new
            {
                type = "elevationAlready"
            })).AsTask();
        }

        _ = Task.Run(async () =>
        {
            _logger.LogInformation("Requesting admin elevation...");
            try
            {
                var success = await _elevationService.RequestAdminElevationAsync();

                if (success)
                {
                    _logger.LogInformation("Admin elevation succeeded — admin features enabled");
                    await _transport!.SendControlAsync(JsonSerializer.Serialize(new
                    {
                        type = "hostStatus",
                        elevated = true
                    }));
                }
                else
                {
                    _logger.LogWarning("Admin elevation failed (UAC denied or error)");
                    await _transport!.SendControlAsync(JsonSerializer.Serialize(new
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
                    await _transport!.SendControlAsync(JsonSerializer.Serialize(new
                    {
                        type = "elevationDenied",
                        message = ex.Message
                    }));
                }
                catch { }
            }
        });

        return Task.CompletedTask;
    }

    private async Task HandleCtrlAltDelAsync()
    {
        if (_transport == null) return;

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
                await _transport.SendControlAsync(JsonSerializer.Serialize(new
                {
                    type = "ctrlAltDelFailed",
                    message = "SendSAS failed via elevated helper"
                }));
            }
        }
        else
        {
            _logger.LogWarning("Ctrl+Alt+Del requested but no elevated helper connected");
            await _transport.SendControlAsync(JsonSerializer.Serialize(new
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

    private async Task HandleRebootAsync()
    {
        if (_elevationService?.IsAdminConnected == true)
        {
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
                if (_transport != null)
                    await _transport.SendControlAsync(JsonSerializer.Serialize(new
                    {
                        type = "rebootFailed",
                        message = "Reboot command failed"
                    }));
            }
        }
        else
        {
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
                if (_transport != null)
                    await _transport.SendControlAsync(JsonSerializer.Serialize(new
                    {
                        type = "rebootFailed",
                        message = ex.Message
                    }));
            }
        }
    }

    private async Task HandleRunElevatedAsync(JsonElement root)
    {
        if (_transport == null) return;
        var path = root.TryGetProperty("path", out var p) ? p.GetString() : null;
        var args = root.TryGetProperty("args", out var a) ? a.GetString() : null;

        if (string.IsNullOrEmpty(path))
        {
            await _transport.SendControlAsync(JsonSerializer.Serialize(new
            { type = "runElevatedFailed", message = "No path specified" }));
            return;
        }

        if (_elevationService?.IsAdminConnected == true)
        {
            var success = await _elevationService.RunElevatedAsync(path, args);
            var responseType = success ? "runElevatedSuccess" : "runElevatedFailed";
            await _transport.SendControlAsync(JsonSerializer.Serialize(new
            { type = responseType, path, message = success ? (string?)null : $"Failed to launch: {path}" }));
        }
        else
        {
            await _transport.SendControlAsync(JsonSerializer.Serialize(new
            { type = "runElevatedFailed", message = "Admin features not enabled — request elevation first" }));
        }
    }

    private Task HandleRequestSystemElevationAsync()
    {
        if (_transport == null || _elevationService == null) return Task.CompletedTask;

        if (_elevationService.IsSystemConnected)
        {
            return _transport.SendControlAsync(JsonSerializer.Serialize(new { type = "systemElevationAlready" })).AsTask();
        }

        if (!_elevationService.IsAdminConnected)
        {
            return _transport.SendControlAsync(JsonSerializer.Serialize(new
            { type = "systemElevationDenied", message = "Admin features must be enabled first" })).AsTask();
        }

        _ = Task.Run(async () =>
        {
            _logger.LogInformation("Requesting SYSTEM elevation...");
            try
            {
                var success = await _elevationService.RequestSystemElevationAsync();
                if (success)
                {
                    await _transport!.SendControlAsync(JsonSerializer.Serialize(new
                    { type = "hostStatus", elevated = true, systemLevel = true }));
                }
                else
                {
                    await _transport!.SendControlAsync(JsonSerializer.Serialize(new
                    { type = "systemElevationFailed", message = "Failed to create SYSTEM helper" }));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to request SYSTEM elevation");
                try
                {
                    await _transport!.SendControlAsync(JsonSerializer.Serialize(new
                    { type = "systemElevationFailed", message = ex.Message }));
                }
                catch { }
            }
        });

        return Task.CompletedTask;
    }

    private async Task HandleRunAsSystemAsync(JsonElement root)
    {
        if (_transport == null) return;
        var path = root.TryGetProperty("path", out var p) ? p.GetString() : null;
        var args = root.TryGetProperty("args", out var a) ? a.GetString() : null;

        if (string.IsNullOrEmpty(path))
        {
            await _transport.SendControlAsync(JsonSerializer.Serialize(new
            { type = "runAsSystemFailed", message = "No path specified" }));
            return;
        }

        if (_elevationService?.IsSystemConnected == true)
        {
            var success = await _elevationService.RunAsSystemAsync(path, args);
            var responseType = success ? "runAsSystemSuccess" : "runAsSystemFailed";
            await _transport.SendControlAsync(JsonSerializer.Serialize(new
            { type = responseType, path, message = success ? (string?)null : $"Failed to launch: {path}" }));
        }
        else
        {
            await _transport.SendControlAsync(JsonSerializer.Serialize(new
            { type = "runAsSystemFailed", message = "SYSTEM features not enabled — request system elevation first" }));
        }
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
        if (_transport != null)
        {
            await _transport.DisposeAsync();
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

        StopClipboardFileTransfer();

        if (_isNativeCapture && _screenCapture is DxgiScreenCapture dxgi)
        {
            dxgi.OnRawFrameCaptured -= OnDxgiRawFrameCaptured;
            dxgi.OnCursorShapeChanged -= OnCursorShapeChanged;
            dxgi.StopCaptureLoop();
            _isNativeCapture = false;
        }

        _encoder?.Dispose();
        _encoder = null;

        if (_transport != null)
        {
            _transport.OnControlMessage -= HandleControlMessage;
            _transport.OnFileData -= HandleFileDataBinary;
            _transport.OnFileSignalingMessage -= HandleFileChannelMessage;
            _transport.OnConnectionStateChanged -= HandleTransportStateChanged;
            await _transport.DisposeAsync();
            _transport = null;
        }
    }
}
