using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SteamViewer.App.Services;
using SteamViewer.Client.Core.Network;
using SteamViewer.Client.Core.Video;
using SteamViewer.Common.Protocol;
using SteamViewer.Platform.Windows.Clipboard;
using System.Collections.Concurrent;
using System.Text.Json;

namespace SteamViewer.App.Services.Models;

/// <summary>
/// Represents a single viewer session with a remote peer.
/// Encapsulates TCP transport, FFmpeg decoding, video rendering, and input handling.
/// </summary>
public sealed class ViewerSession : IAsyncDisposable
{
    private IJSRuntime _jsRuntime;
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IConfiguration _configuration;
    private readonly TurnConfigService? _turnConfigService;
    private readonly Func<SignalingMessage, Task> _sendSignaling;
    private readonly SignalingClient _signalingClient;
    private ViewerStreamTransport? _transport;
    private FFmpegDecoder? _decoder;
    private DotNetObjectReference<ViewerSession>? _dotNetRef;
    private bool _disposed;

    // Stats
    private PeriodicTimer? _statsTimer;
    private CancellationTokenSource? _statsCts;
    private long _lastFrameCount;
    private long _lastBytesDecoded;

    // Clipboard file transfer — viewer monitors clipboard and receives remote files
    private ClipboardMonitor? _clipboardMonitor;
    private ClipboardFileServer? _clipboardFileServer;
    private ClipboardFileWriter? _clipboardFileWriter;

    // Quality report — viewer measures loss and reports to host for adaptation
    private Timer? _qualityReportTimer;

    // Lossless settle — request QOI snapshot when input is idle and screen is static
    private DateTime _lastInputTime = DateTime.UtcNow;
    private bool _losslessActive;
    private bool _losslessRequestPending;

#if WINDOWS
    private Services.NativeFrameBridge? _frameBridge;
#endif

    /// <summary>
    /// Unique identifier for this session.
    /// </summary>
    public string SessionId { get; }

    /// <summary>
    /// The remote peer ID this session is connected to.
    /// </summary>
    public string PeerId { get; }

    /// <summary>
    /// Display title for the tab (usually peer ID or custom name).
    /// </summary>
    public string Title { get; set; }

    /// <summary>
    /// Current connection state.
    /// </summary>
    public ViewerSessionState State { get; private set; } = ViewerSessionState.Connecting;

    /// <summary>
    /// Whether transport is connected and ready.
    /// </summary>
    public bool IsInitialized => _transport?.IsConnected ?? false;

    /// <summary>
    /// Whether the remote peer is sharing their screen.
    /// </summary>
    public bool IsPeerSharing { get; private set; }

    /// <summary>
    /// Raised when the first video frame is rendered via direct rendering.
    /// Used to dismiss the "Waiting for host screen" overlay.
    /// </summary>
    public event Action? OnVideoStarted;

    /// <summary>
    /// Raised when the session state changes.
    /// </summary>
    public event Action<ViewerSessionState>? OnStateChanged;

    /// <summary>
    /// Raised when the remote peer starts/stops sharing.
    /// </summary>
    public event Action<bool>? OnPeerSharingChanged;

    /// <summary>
    /// Raised when the transport connects (ready for input).
    /// </summary>
    public event Action? OnReady;

    /// <summary>
    /// Raised when the session disconnects or errors.
    /// </summary>
    public event Action<string?>? OnDisconnected;

    /// <summary>
    /// Raised when transport stats are available.
    /// </summary>
    public event Action<string>? OnStatsUpdated;

    /// <summary>
    /// Raised when a control message is received from the host.
    /// </summary>
    public event Action<string, string?>? OnControlMessage;

    /// <summary>
    /// Raised when clipboard data is received from the host.
    /// </summary>
    public event Action<string, string>? OnClipboardReceived;

    /// <summary>
    /// Raised when the host sends its monitor layout.
    /// </summary>
    public event Action<List<MonitorInfo>, int>? OnMonitorLayoutReceived;

    /// <summary>
    /// Raised when the Secure Desktop state changes on the host.
    /// </summary>
    public event Action<bool>? OnSecureDesktopStateChanged;

    /// <summary>
    /// Raised when host sends capture dimensions (on first frame + capture change).
    /// Viewer should constrain canvas to this AR for 1:1 pixel mapping.
    /// </summary>
    public event Action<int, int>? OnCaptureInfoReceived;

    /// <summary>
    /// Whether the Secure Desktop is currently active on the host.
    /// </summary>
    public bool IsSecureDesktopActive { get; private set; }

    /// <summary>
    /// Host capture dimensions (for AR-aware canvas sizing).
    /// </summary>
    public int CaptureWidth { get; private set; }
    public int CaptureHeight { get; private set; }

    /// <summary>
    /// The host's monitor layout.
    /// </summary>
    public List<MonitorInfo>? HostMonitors { get; private set; }

    /// <summary>
    /// Which monitor the host is actively capturing.
    /// </summary>
    public int ActiveMonitorId { get; private set; }

    /// <summary>
    /// Whether the host is running elevated.
    /// </summary>
    public bool? IsHostElevated { get; private set; }

    /// <summary>
    /// Whether the host has SYSTEM-level helper connected.
    /// </summary>
    public bool? IsHostSystemLevel { get; private set; }

    /// <summary>
    /// Stored password for reconnection.
    /// </summary>
    public string? StoredPassword { get; set; }

    /// <summary>The viewer's own clientId (registered with the signaling server).
    /// Required for fetching TURN credentials, which are now bound to the registered clientId.</summary>
    private readonly string _localClientId;

    public ViewerSession(
        string sessionId,
        string peerId,
        string localClientId,
        IJSRuntime jsRuntime,
        ILoggerFactory loggerFactory,
        IConfiguration configuration,
        Func<SignalingMessage, Task> sendSignaling,
        SignalingClient signalingClient,
        TurnConfigService? turnConfigService = null)
    {
        SessionId = sessionId;
        PeerId = peerId;
        _localClientId = localClientId;
        Title = peerId;
        _jsRuntime = jsRuntime;
        _logger = loggerFactory.CreateLogger<ViewerSession>();
        _loggerFactory = loggerFactory;
        _configuration = configuration;
        _turnConfigService = turnConfigService;
        _sendSignaling = sendSignaling;
        _signalingClient = signalingClient;
    }

    /// <summary>
    /// Initialize is now a no-op — transport connects via HandleTransportEndpointAsync.
    /// </summary>
    public Task InitializeAsync()
    {
        _logger.LogInformation("Session {SessionId}: Initialized (waiting for TransportEndpoint)", SessionId);
        SetState(ViewerSessionState.WaitingForOffer);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handle RelayReady from host — setup encrypted WebSocket relay transport.
    /// Replaces the old TransportEndpoint/QUIC connection.
    /// </summary>
    public async Task HandleRelayReadyAsync(string encryptionNonce)
    {
        _logger.LogInformation("Session {SessionId}: Received RelayReady with encryption nonce", SessionId);

        try
        {
            // Compute salted password hash (must match what host uses for register and key derivation).
            var passwordHash = SteamViewer.Client.Core.Session.PasswordHash.Compute(PeerId, StoredPassword ?? "");

            _transport = new ViewerStreamTransport(_signalingClient, _loggerFactory.CreateLogger<ViewerStreamTransport>());
            _transport.OnControlMessage += HandleControlMessage;
            _transport.OnVideoData += HandleVideoData;
            _transport.OnLosslessFrame += HandleLosslessFrame;
            _transport.OnFileData += HandleFileDataBinary;
            _transport.OnFileSignalingMessage += HandleFileChannelMessage;
            // Channel 5 (SD JPEG) removed - SD frames now arrive via H.264 on channel 1
            _transport.OnConnectionStateChanged += HandleTransportStateChanged;
            _transport.OnConnectionQualityChanged += HandleConnectionQualityChanged;

            // Connect relay (derives encryption key, subscribes to binary messages)
            _transport.ConnectRelay(encryptionNonce, passwordHash);

            // Tell host we're ready — host waits for this before sending initial state
            _logger.LogInformation("Session {SessionId}: Sending viewerReady handshake", SessionId);
            await _transport.SendControlAsync(JsonSerializer.Serialize(new { type = "viewerReady" }));

            _logger.LogInformation("Session {SessionId}: Relay transport connected, viewerReady sent", SessionId);

            // Initialize FFmpeg decoder
            FFmpegInit.EnsureInitialized();
            _decoder = new FFmpegDecoder(_loggerFactory.CreateLogger<FFmpegDecoder>());
            _decoder.Initialize();

            SetState(ViewerSessionState.Connected);
            OnReady?.Invoke();

            // Start clipboard file monitoring
            StartClipboardFileTransfer();

            // Fire-and-forget UDP upgrade attempt (relay continues working in background)
            _ = Task.Run(async () =>
            {
                try
                {
                    var turnConfig = _turnConfigService != null
                        ? await _turnConfigService.GetConfigAsync(_localClientId)
                        : TurnConfig.Disabled;
                    var turnUri = turnConfig.Enabled ? turnConfig.Urls.FirstOrDefault() : null;
                    var turnUser = turnConfig.Username;
                    var turnCred = turnConfig.Credential;
                    _logger.LogInformation("Session {SessionId}: Starting UDP upgrade (TURN uri={TurnUri}, user={TurnUser}, cred={HasCred})",
                        SessionId, turnUri ?? "null", turnUser ?? "null", turnCred != null ? "yes" : "no");
                    await _transport!.AttemptUdpUpgradeAsync(
                        _sendSignaling, PeerId, turnUri, turnUser, turnCred);
                    _logger.LogInformation("Session {SessionId}: UDP upgrade completed (isDirectUdp={IsDirect})",
                        SessionId, _transport?.IsDirectUdp ?? false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Session {SessionId}: UDP upgrade attempt failed", SessionId);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session {SessionId}: Failed to setup relay transport", SessionId);
            SetState(ViewerSessionState.Error);
            OnDisconnected?.Invoke($"Relay transport setup failed: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Handle TransportEndpoint from host — contains host's UDP candidates.
    /// Probes each candidate and switches to direct UDP if successful.
    /// </summary>
    public async Task HandleTransportEndpointAsync(TransportCandidate[] candidates)
    {
        if (_transport == null)
        {
            _logger.LogWarning("Session {SessionId}: Received TransportEndpoint but transport is null", SessionId);
            return;
        }

        _logger.LogInformation("Session {SessionId}: Received host UDP candidates ({Count} candidates)",
            SessionId, candidates.Length);
        await _transport.HandleHostEndpointAsync(candidates);
    }

    /// <summary>
    /// Handle TransportConfirmed from host — host's UDP probe succeeded.
    /// </summary>
    public async Task HandleTransportConfirmedAsync()
    {
        if (_transport == null)
        {
            _logger.LogWarning("Session {SessionId}: Received TransportConfirmed but transport is null", SessionId);
            return;
        }

        _logger.LogInformation("Session {SessionId}: Received TransportConfirmed from host", SessionId);
        await _transport.HandleTransportConfirmedAsync();
    }

    /// <summary>
    /// Bind the session to a viewer window's JSRuntime.
    /// Sets up video rendering target (NativeFrameBridge) and input capture.
    /// </summary>
    public async Task BindToViewerAsync(IJSRuntime viewerJsRuntime)
    {
        await Task.CompletedTask; // currently sync body; reserve async sugar for future awaits
        _jsRuntime = viewerJsRuntime;

#if WINDOWS
        // Get NativeFrameBridge from DI for decoded frame rendering
        // (shared singleton — initialized from MainPage.xaml.cs with CoreWebView2)
        if (_frameBridge == null)
        {
            // Try to get from the app's service provider
            _frameBridge = App.Current?.Handler?.MauiContext?.Services?.GetService<Services.NativeFrameBridge>();
            if (_frameBridge?.IsInitialized == true)
            {
                _logger.LogInformation("Session {SessionId}: NativeFrameBridge acquired for video rendering", SessionId);
            }
            else
            {
                _logger.LogWarning("Session {SessionId}: NativeFrameBridge not available or not initialized", SessionId);
            }
        }
#endif

        _logger.LogInformation("Session {SessionId}: Bound to viewer JSRuntime", SessionId);
    }

    /// <summary>
    /// Send a raw string message to the remote peer via transport control channel.
    /// </summary>
    public async Task<bool> SendDataAsync(string data)
    {
        if (_transport == null || !_transport.IsConnected) return false;
        return await _transport.SendControlAsync(data);
    }

    /// <summary>
    /// Send an input event to the remote peer.
    /// All input goes over the control channel (TCP is already ordered/reliable).
    /// </summary>
    private int _inputDropCount;

    public async Task SendInputAsync(InputEvent inputEvent)
    {
        if (_transport == null)
        {
            if (++_inputDropCount <= 5)
                _logger.LogWarning("Session {SessionId}: Input dropped — transport is null (drop #{Count})", SessionId, _inputDropCount);
            return;
        }
        if (!_transport.IsConnected)
        {
            if (++_inputDropCount <= 5)
                _logger.LogWarning("Session {SessionId}: Input dropped — transport not connected (drop #{Count})", SessionId, _inputDropCount);
            return;
        }

        // Track input activity for lossless settle
        _lastInputTime = DateTime.UtcNow;
        if (_losslessActive)
        {
            _losslessActive = false;
            _losslessRequestPending = false;
        }

        try
        {
            var json = JsonSerializer.Serialize(inputEvent);
            await _transport.SendControlAsync(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send input for session {SessionId}", SessionId);
        }
    }

    // Per-session wrapper binding _transport / _logger / SessionId to the shared helper.
    // Call sites: 6 Send*Async methods on this class. See ControlMessageSender.cs.
    private Task SendAsync<T>(T payload, string label)
        => ControlMessageSender.SendAsync(_transport, _logger, SessionId, payload, label);

    /// <summary>
    /// Request the host's clipboard contents.
    /// </summary>
    public Task RequestClipboardAsync()
        => SendAsync<ClipboardMessage>(new ClipboardMessage.Request(), "clipboard request");

    /// <summary>
    /// Send clipboard data to the host.
    /// </summary>
    public Task SendClipboardAsync(string format, string data)
        => SendAsync<ClipboardMessage>(new ClipboardMessage.Set(format, data), "clipboard");

    /// <summary>
    /// Send clipboard data to the host and trigger paste.
    /// </summary>
    public Task SendClipboardPasteAsync(string format, string data)
        => SendAsync<ClipboardMessage>(new ClipboardMessage.Paste(format, data), "clipboard paste");

    /// <summary>
    /// Start collecting and pushing stats every 1 second.
    /// </summary>
    public Task EnableStatsRelayAsync()
    {
        if (_statsTimer != null) return Task.CompletedTask;

        _statsCts = new CancellationTokenSource();
        _statsTimer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        _lastFrameCount = _decoder?.FrameCount ?? 0;
        _lastBytesDecoded = _decoder?.TotalBytesDecoded ?? 0;

        _ = Task.Run(async () =>
        {
            try
            {
                while (await _statsTimer.WaitForNextTickAsync(_statsCts.Token))
                {
                    CollectAndPushStats();
                }
            }
            catch (OperationCanceledException) { }
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stop stats collection.
    /// </summary>
    public Task DisableStatsRelayAsync()
    {
        _statsCts?.Cancel();
        _statsTimer?.Dispose();
        _statsTimer = null;
        _statsCts?.Dispose();
        _statsCts = null;
        return Task.CompletedTask;
    }

    private void CollectAndPushStats()
    {
        var frameCount = _decoder?.FrameCount ?? 0;
        var bytesDecoded = _decoder?.TotalBytesDecoded ?? 0;
        var decodeMs = _decoder?.LastDecodeMs ?? 0;
        var width = _decoder?.Width ?? 0;
        var height = _decoder?.Height ?? 0;

        var fps = frameCount - _lastFrameCount; // frames in last 1 second
        var bytesPerSec = bytesDecoded - _lastBytesDecoded;
        _lastFrameCount = frameCount;
        _lastBytesDecoded = bytesDecoded;

        var json = JsonSerializer.Serialize(new
        {
            fps,
            decodeMs = Math.Round(decodeMs, 1),
            resolution = width > 0 ? $"{width}x{height}" : "?",
            bitrateMbps = Math.Round(bytesPerSec * 8.0 / 1_000_000, 1),
            totalFrames = frameCount,
            totalBytes = bytesDecoded,
            transport = _transport?.IsDirectUdp == true ? "FFmpeg+Direct" : "FFmpeg+Relay"
        });

        OnStatsUpdated?.Invoke(json);
    }

    #region Quality Reporting

    private void HandleConnectionQualityChanged(ConnectionQuality quality)
    {
        // Start periodic quality report timer on first classification
        if (_qualityReportTimer == null)
        {
            _qualityReportTimer = new Timer(SendQualityReport, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
            _logger.LogInformation("Session {SessionId}: Quality report timer started", SessionId);
        }
    }

    private void SendQualityReport(object? state)
    {
        if (_transport == null || !_transport.IsConnected) return;

        var monitor = _transport.QualityMonitor;
        if (monitor == null) return;

        var quality = monitor.CurrentQuality;
        var lossRate = monitor.SmoothedLossRate;
        var rtt = monitor.SmoothedRttMs;

        try
        {
            var json = JsonSerializer.Serialize(new
            {
                type = "qualityReport",
                quality = quality.ToString(),
                lossRate = Math.Round(lossRate, 4),
                rttMs = Math.Round(rtt, 1)
            });
            _ = _transport.SendControlAsync(json);
            _logger.LogDebug("Session {SessionId}: Sent quality report: {Quality}, loss={Loss:P1}, RTT={Rtt:F0}ms",
                SessionId, quality, lossRate, rtt);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send quality report");
        }
    }

    private void StopQualityReporting()
    {
        _qualityReportTimer?.Dispose();
        _qualityReportTimer = null;
    }

    #endregion

    /// <summary>
    /// Disconnect this session. Cleans up all state so reconnect works without app restart.
    /// </summary>
    public async Task DisconnectAsync()
    {
        _logger.LogInformation("Session {SessionId}: DisconnectAsync — cleaning up", SessionId);

        // Stop clipboard file transfer
        StopClipboardFileTransfer();

        // Stop quality reporting
        StopQualityReporting();

        // Stop stats relay
        _ = DisableStatsRelayAsync();

        // Dispose decoder (will be recreated on reconnect)
        _decoder?.Dispose();
        _decoder = null;

#if WINDOWS
        // Clear frame bridge reference (will be re-bound on reconnect)
        _frameBridge = null;
#endif

        // Dispose DotNetRef (will be recreated on reconnect)
        _dotNetRef?.Dispose();
        _dotNetRef = null;

        // Unsubscribe and dispose transport
        if (_transport != null)
        {
            _transport.OnControlMessage -= HandleControlMessage;
            _transport.OnVideoData -= HandleVideoData;
            _transport.OnLosslessFrame -= HandleLosslessFrame;
            _transport.OnFileData -= HandleFileDataBinary;
            _transport.OnFileSignalingMessage -= HandleFileChannelMessage;
            _transport.OnConnectionStateChanged -= HandleTransportStateChanged;
            _transport.OnConnectionQualityChanged -= HandleConnectionQualityChanged;
            await _transport.DisposeAsync();
            _transport = null;
        }

        SetState(ViewerSessionState.Disconnected);
        OnDisconnected?.Invoke(null);
    }

    /// <summary>
    /// Notify the host that viewer input lock state changed.
    /// </summary>
    public Task SendInputLockStateAsync(bool locked)
        => SendAsync(new { type = "inputLockChanged", locked }, "inputLockChanged");

    /// <summary>
    /// Toggle host cursor visibility.
    /// </summary>
    public Task SendToggleCursorAsync()
        => SendAsync(new { type = "toggleCursor" }, "toggleCursor");

    /// <summary>
    /// Request the host to switch which display is being captured.
    /// </summary>
    public Task SendSwitchDisplayAsync(int monitorId)
        => SendAsync(new { type = "switchDisplay", monitorId }, "switch display request");

    /// <summary>
    /// Send desired encode resolution to host. Host will downscale using Lanczos
    /// before encoding, so viewer receives frames at exact display size (zero scaling blur).
    /// Call on connect and on window resize (debounced).
    /// </summary>
    private int _lastDesiredWidth;
    private int _lastDesiredHeight;

    public async Task SendDesiredResolutionAsync(int width, int height)
    {
        if (_transport == null || !_transport.IsConnected) return;
        if (width <= 0 || height <= 0) return;
        _lastDesiredWidth = width;
        _lastDesiredHeight = height;

        try
        {
            var json = JsonSerializer.Serialize(new { type = "setResolution", width, height });
            await _transport.SendControlAsync(json);
            _logger.LogInformation("Session {SessionId}: Sent desired resolution {W}x{H}", SessionId, width, height);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session {SessionId}: Failed to send resolution", SessionId);
        }
    }

    /// <summary>
    /// Enable direct rendering to a visible DOM canvas element.
    /// Sets the render target in JS for SharedBuffer frames.
    /// </summary>
    public async Task<bool> TryEnableDirectRenderingAsync(string canvasId, IJSRuntime viewerJsRuntime)
    {
        try
        {
            // Initialize video session in JS
            await viewerJsRuntime.InvokeVoidAsync("SteamViewerVideo.initialize", SessionId);

            var result = await viewerJsRuntime.InvokeAsync<bool>(
                "SteamViewerVideo.setRenderTarget", SessionId, canvasId);

            if (result)
            {
                // Set DotNetRef for OnVideoStartedCallback
                _dotNetRef ??= DotNetObjectReference.Create(this);
                await viewerJsRuntime.InvokeVoidAsync("SteamViewerVideo.setDotNetRef", SessionId, _dotNetRef);

                _logger.LogInformation("Session {SessionId}: Direct rendering enabled → '{CanvasId}'", SessionId, canvasId);
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session {SessionId}: Failed to enable direct rendering", SessionId);
            return false;
        }
    }

    [JSInvokable]
    public void OnVideoStartedCallback()
    {
        _logger.LogInformation("Session {SessionId}: First video frame rendered", SessionId);
        OnVideoStarted?.Invoke();
    }

    #region Transport Event Handlers

    // Dictionary-based dispatch replaces the previous switch-on-type. Handler table built
    // lazily on first use; lambdas capture this-scoped state. 14 distinct handler bodies
    // mapped to 20 keys (two case-groups share handler instances: failure-log trio +
    // system-failure 5-tuple use shared handlers that read the matched `type` for logging).
    private Dictionary<string, Func<string, JsonElement, Task>>? _controlHandlers;
    private Dictionary<string, Func<string, JsonElement, Task>> ControlHandlers
        => _controlHandlers ??= BuildControlHandlers();

    private Dictionary<string, Func<string, JsonElement, Task>> BuildControlHandlers()
    {
        // Shared handler: ctrlAltDelFailed / rebootFailed / elevationDenied all log
        // 'Session X: {type}: {message}' and invoke OnControlMessage with the message.
        Func<string, JsonElement, Task> failureLogHandler = (type, root) =>
        {
            var message = JsonAccessors.GetString(root, "message");
            _logger.LogWarning("Session {SessionId}: {Type}: {Message}", SessionId, type, message);
            OnControlMessage?.Invoke(type, message);
            return Task.CompletedTask;
        };

        // Shared handler: systemElevationAlready/Denied/Failed + runAsSystemSuccess/Failed
        // all read 'message' and invoke OnControlMessage.
        Func<string, JsonElement, Task> systemFailureHandler = (type, root) =>
        {
            var sysMessage = JsonAccessors.GetString(root, "message");
            OnControlMessage?.Invoke(type, sysMessage);
            return Task.CompletedTask;
        };

        return new Dictionary<string, Func<string, JsonElement, Task>>
        {
            ["screenShareStarted"] = (_, _) =>
            {
                _logger.LogInformation("Session {SessionId}: Peer started sharing", SessionId);
                IsPeerSharing = true;
                OnPeerSharingChanged?.Invoke(true);
                return Task.CompletedTask;
            },
            ["screenShareStopped"] = (_, _) =>
            {
                _logger.LogInformation("Session {SessionId}: Peer stopped sharing", SessionId);
                IsPeerSharing = false;
                OnPeerSharingChanged?.Invoke(false);
                return Task.CompletedTask;
            },
            ["hostStatus"] = (type, root) =>
            {
                var elevated = JsonAccessors.GetBool(root, "elevated");
                var systemLevel = JsonAccessors.GetBool(root, "systemLevel");
                IsHostElevated = elevated;
                IsHostSystemLevel = systemLevel;
                _logger.LogInformation("Session {SessionId}: Host elevated={Elevated}, systemLevel={SystemLevel}", SessionId, elevated, systemLevel);
                OnControlMessage?.Invoke(type, null);
                return Task.CompletedTask;
            },
            ["monitorLayout"] = (_, root) =>
            {
                HandleMonitorLayout(root);
                return Task.CompletedTask;
            },
            ["ctrlAltDelFailed"] = failureLogHandler,
            ["rebootFailed"] = failureLogHandler,
            ["elevationDenied"] = failureLogHandler,
            ["elevationAlready"] = (type, _) =>
            {
                OnControlMessage?.Invoke(type, null);
                return Task.CompletedTask;
            },
            ["systemElevationAlready"] = systemFailureHandler,
            ["systemElevationDenied"] = systemFailureHandler,
            ["systemElevationFailed"] = systemFailureHandler,
            ["runAsSystemSuccess"] = systemFailureHandler,
            ["runAsSystemFailed"] = systemFailureHandler,
            ["cursorVisibilityChanged"] = (type, root) =>
            {
                var visible = JsonAccessors.GetBool(root, "visible");
                OnControlMessage?.Invoke(type, visible.ToString());
                return Task.CompletedTask;
            },
            ["cursorShape"] = (type, root) =>
            {
                var cursor = JsonAccessors.GetString(root, "cursor");
                if (cursor != null) OnControlMessage?.Invoke(type, cursor);
                return Task.CompletedTask;
            },
            ["clipboard_data"] = (_, root) =>
            {
                var cbFormat = JsonAccessors.GetString(root, "format");
                var cbData = JsonAccessors.GetString(root, "data");
                if (cbFormat != null && cbData != null)
                    OnClipboardReceived?.Invoke(cbFormat, cbData);
                return Task.CompletedTask;
            },
            ["captureInfo"] = (_, root) =>
            {
                var capW = JsonAccessors.GetInt(root, "width");
                var capH = JsonAccessors.GetInt(root, "height");
                if (capW > 0 && capH > 0)
                {
                    CaptureWidth = capW;
                    CaptureHeight = capH;
                    _logger.LogInformation("Session {SessionId}: Host capture {W}x{H}", SessionId, capW, capH);
                    OnCaptureInfoReceived?.Invoke(capW, capH);
                }
                return Task.CompletedTask;
            },
            // Lambda param named `type` (not `_`) because the body uses `_ = ...` discard
            // for the InvokeVoidAsync ValueTask; a single-underscore lambda param would
            // shadow the body's `_` discard and cause CS0029.
            ["encodeInfo"] = (type, root) =>
            {
                var encW = JsonAccessors.GetInt(root, "width");
                var encH = JsonAccessors.GetInt(root, "height");
                if (encW > 0 && encH > 0)
                {
                    _logger.LogInformation("Session {SessionId}: Host encode resolution {W}x{H}", SessionId, encW, encH);
                    try { _ = _jsRuntime.InvokeVoidAsync("SteamViewerVideo.setEncodeResolution", SessionId, encW, encH); }
                    catch { /* JS not ready yet - next frame will use fallback path */ }
                }
                return Task.CompletedTask;
            },
            ["secureDesktopActive"] = (_, _) =>
            {
                IsSecureDesktopActive = true;
                OnSecureDesktopStateChanged?.Invoke(true);
                _ = _transport?.SendControlAsync(
                    JsonSerializer.Serialize(new { type = "ack", ackType = "secureDesktopActive" }));
                return Task.CompletedTask;
            },
            ["secureDesktopInactive"] = (_, _) =>
            {
                IsSecureDesktopActive = false;
                OnSecureDesktopStateChanged?.Invoke(false);
                _ = _transport?.SendControlAsync(
                    JsonSerializer.Serialize(new { type = "ack", ackType = "secureDesktopInactive" }));
                return Task.CompletedTask;
            },
            // secureDesktopFrame: removed - SD frames now arrive via H.264 on channel 1
        };
    }

    private async Task HandleControlMessage(string json)
    {
        try
        {
            await ControlMessageDispatcher.DispatchAsync(json, ControlHandlers,
                onNoHandler: (type, _) =>
                {
                    if (type != null)
                        _logger.LogWarning("Session {SessionId}: Unknown control message type \"{Type}\" - dropped (no handler)", SessionId, type);
                    return Task.CompletedTask;
                });
        }
        catch (JsonException) { /* swallow - viewer has no input fallthrough */ }
    }

    private void HandleVideoData(byte[] data, int length)
    {
        if (_decoder == null) return;

        try
        {
            var result = _decoder.DecodeFrame(data, length);
            if (result is var (bgraData, width, height, stride))
            {
#if WINDOWS
                // Push decoded BGRA frame to JS canvas via SharedBuffer
                if (_frameBridge?.IsInitialized == true)
                {
                    _frameBridge.PushRawFrame(bgraData, width, height, stride, SessionId);
                }
#endif

                // Check if we should request a lossless frame (input idle)
                if (!_losslessActive && !_losslessRequestPending
                    && !IsSecureDesktopActive
                    && (DateTime.UtcNow - _lastInputTime).TotalMilliseconds > 150)
                {
                    RequestLosslessFrame();
                }
            }
        }
        catch (Exception ex)
        {
            if (_decodeErrorCount++ % 300 == 0)
                _logger.LogWarning(ex, "Session {SessionId}: Decode error (sample)", SessionId);
        }
    }

    private int _decodeErrorCount;

    private async void RequestLosslessFrame()
    {
        if (_transport == null || !_transport.IsConnected) return;

        // Use decoder dimensions — matches H.264 encode resolution (8-aligned)
        // This ensures lossless and H.264 frames are identical size → no canvas resize
        var w = _decoder?.Width ?? 0;
        var h = _decoder?.Height ?? 0;
        if (w <= 0 || h <= 0) return;

        _losslessRequestPending = true;
        try
        {
            var json = JsonSerializer.Serialize(new { type = "requestLosslessFrame", width = w, height = h });
            await _transport.SendControlAsync(json);
        }
        catch (Exception ex)
        {
            _losslessRequestPending = false;
            _logger.LogWarning(ex, "Session {SessionId}: Failed to request lossless frame", SessionId);
        }
    }

    private void HandleLosslessFrame(byte[] qoiData, int length)
    {
        _losslessRequestPending = false;

        // Discard if input resumed while frame was in-flight (race: encode takes 50-100ms)
        if ((DateTime.UtcNow - _lastInputTime).TotalMilliseconds < 150)
            return;

        _losslessActive = true;

        try
        {
            // Decode QOI to BGRA
            var actualData = qoiData;
            if (length < qoiData.Length)
            {
                actualData = new byte[length];
                Buffer.BlockCopy(qoiData, 0, actualData, 0, length);
            }

            var bgra = QoiCodec.Decode(actualData, out int w, out int h);

#if WINDOWS
            // Push lossless BGRA to JS canvas via SharedBuffer with lossless flag
            if (_frameBridge?.IsInitialized == true)
            {
                _frameBridge.PushLosslessFrame(bgra, w, h, w * 4, SessionId);
            }
#endif

            _logger.LogInformation("Session {SessionId}: Lossless frame rendered: {W}x{H}, QOI={Size}KB",
                SessionId, w, h, length / 1024);
        }
        catch (Exception ex)
        {
            _losslessActive = false;
            _logger.LogWarning(ex, "Session {SessionId}: Failed to decode/render lossless frame", SessionId);
        }
    }

    private void HandleTransportStateChanged(string state)
    {
        _logger.LogInformation("Session {SessionId}: Transport state changed to {State}", SessionId, state);
        if (state == "disconnected")
        {
            SetState(ViewerSessionState.Disconnected);
            OnDisconnected?.Invoke("Transport disconnected");
        }
        else if (state == "udp-upgraded")
        {
            // Re-send desired resolution on new UDP backend.
            // The initial setResolution was sent on the WS relay which the host may
            // have already unsubscribed from. Re-sending ensures the host gets it.
            if (_lastDesiredWidth > 0 && _lastDesiredHeight > 0)
            {
                _logger.LogInformation("Session {SessionId}: Re-sending resolution {W}x{H} after UDP upgrade",
                    SessionId, _lastDesiredWidth, _lastDesiredHeight);
                _ = SendDesiredResolutionAsync(_lastDesiredWidth, _lastDesiredHeight);
            }
        }
    }

    private void HandleMonitorLayout(JsonElement root)
    {
        try
        {
            var monitors = new List<MonitorInfo>();
            if (root.TryGetProperty("monitors", out var monArr) && monArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in monArr.EnumerateArray())
                {
                    var id = JsonAccessors.GetUInt(m, "id");
                    var name = JsonAccessors.GetString(m, "name") ?? "";
                    var width = JsonAccessors.GetUInt(m, "width");
                    var height = JsonAccessors.GetUInt(m, "height");
                    var x = JsonAccessors.GetInt(m, "x");
                    var y = JsonAccessors.GetInt(m, "y");
                    var isPrimary = JsonAccessors.GetBool(m, "isPrimary");
                    monitors.Add(new MonitorInfo(id, name, width, height, x, y, isPrimary));
                }
            }

            var activeId = JsonAccessors.GetInt(root, "activeMonitorId");

            HostMonitors = monitors;
            ActiveMonitorId = activeId;

            OnMonitorLayoutReceived?.Invoke(monitors, activeId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session {SessionId}: Failed to parse monitor layout", SessionId);
        }
    }

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

            _logger.LogInformation("Session {SessionId}: Clipboard file transfer initialized", SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session {SessionId}: Failed to initialize clipboard file transfer", SessionId);
        }
    }

    private void OnClipboardFilesDetected(ClipboardFileInfo[] files, string[] localPaths)
    {
        _logger.LogDebug("Session {SessionId}: OnClipboardFilesDetected entry: files={Count} transport={Transport} connected={Connected}",
            SessionId, files.Length,
            _transport != null ? "set" : "null",
            _transport?.IsConnected);
        if (_transport == null || !_transport.IsConnected)
        {
            _logger.LogWarning("Session {SessionId}: OnClipboardFilesDetected: dropping {Count} file(s) — transport not ready (transport={Transport}, connected={Connected})",
                SessionId, files.Length, _transport != null ? "set" : "null", _transport?.IsConnected);
            return;
        }

        try
        {
            _clipboardFileServer?.SetFilePaths(localPaths);

            var formatList = new ClipboardFileMessage.FormatList(files);
            var json = JsonSerializer.Serialize<ClipboardFileMessage>(formatList);

            _ = Task.Run(async () =>
            {
                try
                {
                    // Send 3x with 500ms gaps for UDP reliability (idempotent on receiver)
                    for (int i = 0; i < 3; i++)
                    {
                        if (_transport == null || !_transport.IsConnected)
                        {
                            _logger.LogWarning("Session {SessionId}: Clipboard format list send loop break at i={Iteration}: transport={Transport} connected={Connected}",
                                SessionId, i, _transport != null ? "set" : "null", _transport?.IsConnected);
                            break;
                        }
                        var sent = await _transport.SendFileSignalingAsync(json);
                        if (i == 0) _logger.LogInformation("Session {SessionId}: Sent clipboard file format list: {Count} files (sent={Sent}, attempt={Attempt})", SessionId, files.Length, sent, i);
                        else _logger.LogDebug("Session {SessionId}: Re-sent clipboard file format list (sent={Sent}, attempt={Attempt})", SessionId, sent, i);
                        if (i < 2) await Task.Delay(500);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Session {SessionId}: Failed to send clipboard file format list", SessionId);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session {SessionId}: Error handling clipboard files detected", SessionId);
        }
    }

    /// <summary>
    /// Auto-push viewer's local clipboard text to host on each WM_CLIPBOARDUPDATE
    /// the monitor flags as text. Mirrors the host-side OnClipboardTextDetected
    /// pattern but sends `clipboard_set` (which host's HandleClipboardSetAsync
    /// already handles) instead of `clipboard_data`. Echo loop is prevented by
    /// the monitor's hash-based suppression on both sides — viewer's own
    /// HandleClipboardReceived calls RecordSelfWriteText before writing.
    /// </summary>
    private void OnClipboardTextDetected(string text)
    {
        _logger.LogDebug("Session {SessionId}: OnClipboardTextDetected entry: len={Length} transport={Transport} connected={Connected}",
            SessionId, text.Length,
            _transport != null ? "set" : "null",
            _transport?.IsConnected);
        if (_transport == null || !_transport.IsConnected)
        {
            _logger.LogWarning("Session {SessionId}: OnClipboardTextDetected: dropping {Length}-char text — transport not ready (transport={Transport}, connected={Connected})",
                SessionId, text.Length, _transport != null ? "set" : "null", _transport?.IsConnected);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var msg = JsonSerializer.Serialize<ClipboardMessage>(new ClipboardMessage.Set("text", text));
                var sent = await _transport.SendControlAsync(msg);
                _logger.LogInformation("Session {SessionId}: Sent viewer clipboard text to host: {Length} chars (sent={Sent})",
                    SessionId, text.Length, sent);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session {SessionId}: Failed to send viewer clipboard text to host", SessionId);
            }
        });
    }

    /// <summary>
    /// Record that the viewer just wrote text to its own local clipboard
    /// (typically from a host->viewer clipboard_data sync). Forwards to the
    /// monitor so its next WM_CLIPBOARDUPDATE is suppressed by hash match
    /// instead of bouncing back to the host as a clipboard_set echo.
    /// Public surface lets RemoteViewer.razor call it before TrySetClipboardNative.
    /// </summary>
    public void RecordSelfWriteText(string text) => _clipboardMonitor?.RecordSelfWriteText(text);

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
                    _logger.LogInformation("Session {SessionId}: Remote transfer progress: {FileName} — {Transferred}/{Total} ({Speed} MB/s)",
                        SessionId, progress.FileName, FormatBytes(progress.BytesTransferred), FormatBytes(progress.TotalBytes), progress.SpeedMBps);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session {SessionId}: Failed to handle file channel message", SessionId);
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
                long bytesAcked = data.Length >= 16
                    ? System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(8, 8))
                    : 0;
                _clipboardFileServer?.HandlePushAck(fileIndex, bytesAcked);
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

    private void SetState(ViewerSessionState newState)
    {
        if (State != newState)
        {
            State = newState;
            OnStateChanged?.Invoke(newState);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        StopClipboardFileTransfer();
        _ = DisableStatsRelayAsync();

        _dotNetRef?.Dispose();
        _dotNetRef = null;

        _decoder?.Dispose();
        _decoder = null;

        if (_transport != null)
        {
            _transport.OnControlMessage -= HandleControlMessage;
            _transport.OnVideoData -= HandleVideoData;
            _transport.OnLosslessFrame -= HandleLosslessFrame;
            _transport.OnFileData -= HandleFileDataBinary;
            _transport.OnFileSignalingMessage -= HandleFileChannelMessage;
            _transport.OnConnectionStateChanged -= HandleTransportStateChanged;
            _transport.OnConnectionQualityChanged -= HandleConnectionQualityChanged;
            await _transport.DisposeAsync();
            _transport = null;
        }
    }
}

/// <summary>
/// Connection state for a viewer session.
/// </summary>
public enum ViewerSessionState
{
    /// <summary>Session is being set up.</summary>
    Connecting,
    /// <summary>Waiting for transport endpoint from host.</summary>
    WaitingForOffer,
    /// <summary>Session is connected and active.</summary>
    Connected,
    /// <summary>Session has been disconnected.</summary>
    Disconnected,
    /// <summary>Session encountered an error.</summary>
    Error
}
