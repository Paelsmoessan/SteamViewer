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
    private readonly Func<SignalingMessage, Task> _sendSignaling;
    private readonly SignalingClient _signalingClient;
    private int _sdViewerFrameCount;
    private ViewerStreamTransport? _transport;
    private FFmpegDecoder? _decoder;
    private DotNetObjectReference<ViewerSession>? _dotNetRef;
    private bool _disposed;

    // Clipboard file transfer — viewer monitors clipboard and receives remote files
    private ClipboardMonitor? _clipboardMonitor;
    private ClipboardFileServer? _clipboardFileServer;
    private ClipboardFileWriter? _clipboardFileWriter;

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
    /// Raised when a Secure Desktop frame is received.
    /// </summary>
    public event Action<string, int, int>? OnSecureDesktopFrame;

    /// <summary>
    /// Whether the Secure Desktop is currently active on the host.
    /// </summary>
    public bool IsSecureDesktopActive { get; private set; }

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

    // Keep OnIceCandidate/OnSdpMessage properties for ViewerSessionManager compat (unused now)
    public event Func<string, string?, ushort?, Task>? OnIceCandidate;
    public event Func<string, string, Task>? OnSdpMessage;

    public ViewerSession(
        string sessionId,
        string peerId,
        IJSRuntime jsRuntime,
        ILoggerFactory loggerFactory,
        Func<SignalingMessage, Task> sendSignaling,
        SignalingClient signalingClient)
    {
        SessionId = sessionId;
        PeerId = peerId;
        Title = peerId;
        _jsRuntime = jsRuntime;
        _logger = loggerFactory.CreateLogger<ViewerSession>();
        _loggerFactory = loggerFactory;
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
            // Compute password hash from stored password
            var passwordHash = Convert.ToHexString(
                Blake3.Hasher.Hash(System.Text.Encoding.UTF8.GetBytes(StoredPassword ?? "")).AsSpan()
            ).ToLowerInvariant();

            _transport = new ViewerStreamTransport(_signalingClient, _loggerFactory.CreateLogger<ViewerStreamTransport>());
            _transport.OnControlMessage += HandleControlMessage;
            _transport.OnVideoData += HandleVideoData;
            _transport.OnFileData += HandleFileDataBinary;
            _transport.OnFileSignalingMessage += HandleFileChannelMessage;
            _transport.OnConnectionStateChanged += HandleTransportStateChanged;

            // Connect relay (derives encryption key, subscribes to binary messages)
            _transport.ConnectRelay(encryptionNonce, passwordHash);

            _logger.LogInformation("Session {SessionId}: Relay transport connected", SessionId);

            // Initialize FFmpeg decoder
            FFmpegInit.EnsureInitialized();
            _decoder = new FFmpegDecoder(_loggerFactory.CreateLogger<FFmpegDecoder>());
            _decoder.Initialize();

            SetState(ViewerSessionState.Connected);
            OnReady?.Invoke();

            // Start clipboard file monitoring
            StartClipboardFileTransfer();
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
    /// Handle TransportEndpoint from host — legacy, kept for backward compatibility.
    /// Phase 2 direct UDP will reuse this.
    /// </summary>
    public Task HandleTransportEndpointAsync(string[] ips, int port)
    {
        _logger.LogDebug("Session {SessionId}: Received TransportEndpoint (not used in relay mode)", SessionId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Bind the session to a viewer window's JSRuntime.
    /// Sets up video rendering target (NativeFrameBridge) and input capture.
    /// </summary>
    public async Task BindToViewerAsync(IJSRuntime viewerJsRuntime)
    {
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

    // Legacy SDP/ICE handlers — queue TransportEndpoint instead
    public Task HandleSdpOfferAsync(string sdp)
    {
        _logger.LogDebug("Session {SessionId}: Ignoring legacy SDP offer (using transport endpoint)", SessionId);
        return Task.CompletedTask;
    }

    public Task HandleSdpAnswerAsync(string sdp)
    {
        _logger.LogDebug("Session {SessionId}: Ignoring legacy SDP answer (using transport endpoint)", SessionId);
        return Task.CompletedTask;
    }

    public Task HandleIceCandidateAsync(string candidate, string? sdpMid, int? sdpMLineIndex)
    {
        _logger.LogDebug("Session {SessionId}: Ignoring legacy ICE candidate (using transport endpoint)", SessionId);
        return Task.CompletedTask;
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
    public async Task SendInputAsync(InputEvent inputEvent)
    {
        if (_transport == null || !_transport.IsConnected) return;

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

    /// <summary>
    /// Request the host's clipboard contents.
    /// </summary>
    public async Task RequestClipboardAsync()
    {
        if (_transport == null || !_transport.IsConnected) return;

        try
        {
            var json = JsonSerializer.Serialize<ClipboardMessage>(new ClipboardMessage.Request());
            await _transport.SendControlAsync(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send clipboard request for session {SessionId}", SessionId);
        }
    }

    /// <summary>
    /// Send clipboard data to the host.
    /// </summary>
    public async Task SendClipboardAsync(string format, string data)
    {
        if (_transport == null || !_transport.IsConnected) return;

        try
        {
            var json = JsonSerializer.Serialize<ClipboardMessage>(new ClipboardMessage.Set(format, data));
            await _transport.SendControlAsync(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send clipboard for session {SessionId}", SessionId);
        }
    }

    /// <summary>
    /// Send clipboard data to the host and trigger paste.
    /// </summary>
    public async Task SendClipboardPasteAsync(string format, string data)
    {
        if (_transport == null || !_transport.IsConnected) return;

        try
        {
            var json = JsonSerializer.Serialize<ClipboardMessage>(new ClipboardMessage.Paste(format, data));
            await _transport.SendControlAsync(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send clipboard paste for session {SessionId}", SessionId);
        }
    }

    /// <summary>
    /// Enable stats (placeholder — stats pushed from C# side now).
    /// </summary>
    public Task EnableStatsRelayAsync() => Task.CompletedTask;

    /// <summary>
    /// Disable stats (placeholder).
    /// </summary>
    public Task DisableStatsRelayAsync() => Task.CompletedTask;

    /// <summary>
    /// Disconnect this session.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_transport != null)
        {
            await _transport.DisposeAsync();
        }

        SetState(ViewerSessionState.Disconnected);
        OnDisconnected?.Invoke(null);
    }

    /// <summary>
    /// Notify the host that viewer input lock state changed.
    /// </summary>
    public async Task SendInputLockStateAsync(bool locked)
    {
        if (_transport == null || !_transport.IsConnected) return;

        try
        {
            var json = JsonSerializer.Serialize(new { type = "inputLockChanged", locked });
            await _transport.SendControlAsync(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session {SessionId}: Failed to send inputLockChanged", SessionId);
        }
    }

    /// <summary>
    /// Toggle host cursor visibility.
    /// </summary>
    public async Task SendToggleCursorAsync()
    {
        if (_transport == null || !_transport.IsConnected) return;

        try
        {
            var json = JsonSerializer.Serialize(new { type = "toggleCursor" });
            await _transport.SendControlAsync(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session {SessionId}: Failed to send toggleCursor", SessionId);
        }
    }

    /// <summary>
    /// Request the host to switch which display is being captured.
    /// </summary>
    public async Task SendSwitchDisplayAsync(int monitorId)
    {
        if (_transport == null || !_transport.IsConnected) return;

        try
        {
            var json = JsonSerializer.Serialize(new { type = "switchDisplay", monitorId });
            await _transport.SendControlAsync(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session {SessionId}: Failed to send switch display request", SessionId);
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
                    case "screenShareStarted":
                        _logger.LogInformation("Session {SessionId}: Peer started sharing", SessionId);
                        IsPeerSharing = true;
                        OnPeerSharingChanged?.Invoke(true);
                        break;

                    case "screenShareStopped":
                        _logger.LogInformation("Session {SessionId}: Peer stopped sharing", SessionId);
                        IsPeerSharing = false;
                        OnPeerSharingChanged?.Invoke(false);
                        break;

                    case "hostStatus":
                        var elevated = root.TryGetProperty("elevated", out var elProp) && elProp.GetBoolean();
                        var systemLevel = root.TryGetProperty("systemLevel", out var slProp) && slProp.GetBoolean();
                        IsHostElevated = elevated;
                        IsHostSystemLevel = systemLevel;
                        _logger.LogInformation("Session {SessionId}: Host elevated={Elevated}, systemLevel={SystemLevel}", SessionId, elevated, systemLevel);
                        OnControlMessage?.Invoke(type, null);
                        break;

                    case "monitorLayout":
                        HandleMonitorLayout(root);
                        break;

                    case "ctrlAltDelFailed":
                    case "rebootFailed":
                    case "elevationDenied":
                        var message = root.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : null;
                        _logger.LogWarning("Session {SessionId}: {Type}: {Message}", SessionId, type, message);
                        OnControlMessage?.Invoke(type, message);
                        break;

                    case "elevationAlready":
                        OnControlMessage?.Invoke(type, null);
                        break;

                    case "systemElevationAlready":
                    case "systemElevationDenied":
                    case "systemElevationFailed":
                    case "runAsSystemSuccess":
                    case "runAsSystemFailed":
                        var sysMessage = root.TryGetProperty("message", out var sysMsgProp) ? sysMsgProp.GetString() : null;
                        OnControlMessage?.Invoke(type, sysMessage);
                        break;

                    case "cursorVisibilityChanged":
                        var visible = root.TryGetProperty("visible", out var visProp) && visProp.GetBoolean();
                        OnControlMessage?.Invoke(type, visible.ToString());
                        break;

                    case "cursorShape":
                        var cursor = root.TryGetProperty("cursor", out var cursorProp) ? cursorProp.GetString() : null;
                        if (cursor != null)
                            OnControlMessage?.Invoke(type, cursor);
                        break;

                    case "clipboard_data":
                        var cbFormat = root.TryGetProperty("format", out var fProp) ? fProp.GetString() : null;
                        var cbData = root.TryGetProperty("data", out var dProp) ? dProp.GetString() : null;
                        if (cbFormat != null && cbData != null)
                            OnClipboardReceived?.Invoke(cbFormat, cbData);
                        break;

                    case "secureDesktopActive":
                        IsSecureDesktopActive = true;
                        OnSecureDesktopStateChanged?.Invoke(true);
                        break;

                    case "secureDesktopInactive":
                        IsSecureDesktopActive = false;
                        OnSecureDesktopStateChanged?.Invoke(false);
                        break;

                    case "secureDesktopFrame":
                        _sdViewerFrameCount++;
                        var frameData = root.TryGetProperty("data", out var frameProp) ? frameProp.GetString() : null;
                        var frameW = root.TryGetProperty("width", out var fwProp) ? fwProp.GetInt32() : 0;
                        var frameH = root.TryGetProperty("height", out var fhProp) ? fhProp.GetInt32() : 0;
                        if (frameData != null && frameW > 0 && frameH > 0)
                            OnSecureDesktopFrame?.Invoke(frameData, frameW, frameH);
                        break;
                }
            }
        }
        catch (JsonException) { }

        await Task.CompletedTask;
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
            }
        }
        catch (Exception ex)
        {
            if (_decodeErrorCount++ % 300 == 0)
                _logger.LogWarning(ex, "Session {SessionId}: Decode error (sample)", SessionId);
        }
    }

    private int _decodeErrorCount;

    private void HandleTransportStateChanged(string state)
    {
        _logger.LogInformation("Session {SessionId}: Transport state changed to {State}", SessionId, state);
        if (state == "disconnected")
        {
            SetState(ViewerSessionState.Disconnected);
            OnDisconnected?.Invoke("Transport disconnected");
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
                    var id = m.TryGetProperty("id", out var idP) ? (uint)idP.GetInt32() : 0u;
                    var name = m.TryGetProperty("name", out var nP) ? nP.GetString() ?? "" : "";
                    var width = m.TryGetProperty("width", out var wP) ? (uint)wP.GetInt32() : 0u;
                    var height = m.TryGetProperty("height", out var hP) ? (uint)hP.GetInt32() : 0u;
                    var x = m.TryGetProperty("x", out var xP) ? xP.GetInt32() : 0;
                    var y = m.TryGetProperty("y", out var yP) ? yP.GetInt32() : 0;
                    var isPrimary = m.TryGetProperty("isPrimary", out var pP) && pP.GetBoolean();
                    monitors.Add(new MonitorInfo(id, name, width, height, x, y, isPrimary));
                }
            }

            var activeId = root.TryGetProperty("activeMonitorId", out var aProp) ? aProp.GetInt32() : 0;

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
                });
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

        _dotNetRef?.Dispose();
        _dotNetRef = null;

        _decoder?.Dispose();
        _decoder = null;

        if (_transport != null)
        {
            _transport.OnControlMessage -= HandleControlMessage;
            _transport.OnVideoData -= HandleVideoData;
            _transport.OnFileData -= HandleFileDataBinary;
            _transport.OnFileSignalingMessage -= HandleFileChannelMessage;
            _transport.OnConnectionStateChanged -= HandleTransportStateChanged;
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
