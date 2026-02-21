using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SteamViewer.App.Services;
using SteamViewer.Client.Core.Network;
using SteamViewer.Common.Protocol;
using System.Collections.Concurrent;
using System.Text.Json;

namespace SteamViewer.App.Services.Models;

/// <summary>
/// Represents a single viewer session with a remote peer.
/// Encapsulates WebRTC connection, video frames, and input handling.
/// </summary>
public sealed class ViewerSession : IAsyncDisposable
{
    private IJSRuntime _jsRuntime;
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Func<SignalingMessage, Task> _sendSignaling;
    private int _sdViewerFrameCount;
    private WebRTCManager? _webrtc;
    private DotNetObjectReference<ViewerSession>? _dotNetRef;
    private bool _frameCaptureStarted;
    private bool _disposed;
    private bool _directRenderBound;
    private readonly ConcurrentQueue<Func<Task>> _pendingSignaling = new();

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
    /// Whether WebRTC is initialized and ready.
    /// </summary>
    public bool IsInitialized => _webrtc?.IsDataChannelOpen ?? false;

    /// <summary>
    /// Whether the remote peer is sharing their screen.
    /// </summary>
    public bool IsPeerSharing { get; private set; }

    /// <summary>
    /// Raised when a JPEG video frame is received (JPEG relay path).
    /// </summary>
    public event Action<JpegFrame>? OnFrame;

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
    /// Raised when the data channel opens (ready for input).
    /// </summary>
    public event Action? OnReady;

    /// <summary>
    /// Raised when the session disconnects or errors.
    /// </summary>
    public event Action<string?>? OnDisconnected;

    /// <summary>
    /// Raised when WebRTC stats are relayed from JS.
    /// </summary>
    public event Action<string>? OnStatsUpdated;

    /// <summary>
    /// Raised when a control message is received from the host (e.g., hostStatus, ctrlAltDelError, rebootError).
    /// </summary>
    public event Action<string, string?>? OnControlMessage;

    /// <summary>
    /// Raised when clipboard data is received from the host.
    /// Parameters: (format, data)
    /// </summary>
    public event Action<string, string>? OnClipboardReceived;

    /// <summary>
    /// Raised when the host sends its monitor layout.
    /// Parameters: (monitors list, active monitor ID).
    /// </summary>
    public event Action<List<MonitorInfo>, int>? OnMonitorLayoutReceived;

    /// <summary>
    /// Raised when the Secure Desktop state changes on the host.
    /// Parameter: true = active (UAC prompt visible), false = inactive.
    /// </summary>
    public event Action<bool>? OnSecureDesktopStateChanged;

    /// <summary>
    /// Raised when a Secure Desktop frame is received.
    /// Parameters: (base64JpegData, width, height).
    /// </summary>
    public event Action<string, int, int>? OnSecureDesktopFrame;

    /// <summary>
    /// Whether the Secure Desktop is currently active on the host.
    /// </summary>
    public bool IsSecureDesktopActive { get; private set; }

    /// <summary>
    /// The host's monitor layout (populated on connect and capture start).
    /// </summary>
    public List<MonitorInfo>? HostMonitors { get; private set; }

    /// <summary>
    /// Which monitor the host is actively capturing.
    /// </summary>
    public int ActiveMonitorId { get; private set; }

    /// <summary>
    /// Whether the host is running elevated (as admin).
    /// </summary>
    public bool? IsHostElevated { get; private set; }

    /// <summary>
    /// Whether the host has SYSTEM-level helper connected.
    /// </summary>
    public bool? IsHostSystemLevel { get; private set; }

    /// <summary>
    /// Stored password for reconnection after disconnect.
    /// </summary>
    public string? StoredPassword { get; set; }

    /// <summary>
    /// TURN server config to apply when binding to viewer JS context.
    /// </summary>
    public (string[] Urls, string Username, string Credential)? TurnConfig { get; set; }

    /// <summary>
    /// Raised when an ICE candidate needs to be sent via signaling.
    /// </summary>
    public event Func<string, string?, ushort?, Task>? OnIceCandidate;

    /// <summary>
    /// Raised when an SDP offer/answer needs to be sent via signaling.
    /// </summary>
    public event Func<string, string, Task>? OnSdpMessage;

    public ViewerSession(
        string sessionId,
        string peerId,
        IJSRuntime jsRuntime,
        ILoggerFactory loggerFactory,
        Func<SignalingMessage, Task> sendSignaling)
    {
        SessionId = sessionId;
        PeerId = peerId;
        Title = peerId;
        _jsRuntime = jsRuntime;
        _logger = loggerFactory.CreateLogger<ViewerSession>();
        _loggerFactory = loggerFactory;
        _sendSignaling = sendSignaling;
    }

    /// <summary>
    /// Initialize the session as a viewer (waits for offer from host).
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_webrtc != null)
        {
            throw new InvalidOperationException("Session already initialized");
        }

        _logger.LogInformation("Initializing viewer session {SessionId} for peer {PeerId}", SessionId, PeerId);

        _webrtc = new WebRTCManager(_jsRuntime, _loggerFactory.CreateLogger<WebRTCManager>(), SessionId, "", _sendSignaling);

        // Subscribe to WebRTC events
        _webrtc.OnIceCandidate += HandleIceCandidate;
        _webrtc.OnDataChannelOpen += HandleDataChannelOpen;
        _webrtc.OnDataChannelClose += HandleDataChannelClose;
        _webrtc.OnDataChannelMessage += HandleDataChannelMessage;
        _webrtc.OnRenegotiationNeeded += HandleRenegotiationNeeded;
        _webrtc.OnConnectionStateChange += HandleConnectionStateChange;
        _webrtc.OnStatsUpdated += json => OnStatsUpdated?.Invoke(json);
        _webrtc.OnVideoStarted += () => OnVideoStarted?.Invoke();

        await _webrtc.InitializeAsync();

        SetState(ViewerSessionState.WaitingForOffer);
    }

    /// <summary>
    /// Bind the session to a viewer window's JSRuntime and initialize WebRTC there.
    /// Called by RemoteViewer when a tab is activated — creates the PeerConnection
    /// in the viewer's JS context so direct rendering works (same window = same DOM).
    /// Processes any signaling messages (SDP/ICE) that arrived before binding.
    /// </summary>
    public async Task BindToViewerAsync(IJSRuntime viewerJsRuntime)
    {
        if (_webrtc != null)
        {
            if (ReferenceEquals(_jsRuntime, viewerJsRuntime))
            {
                // Already initialized in the correct JS context (e.g., ConnectionDialog path or tab re-activation)
                _directRenderBound = true;
                return;
            }

            // PeerConnection exists in a different window — can't migrate (tab-detach edge case)
            _logger.LogWarning("Session {SessionId}: BindToViewer skipped — WebRTC already initialized in different JS context", SessionId);
            return;
        }

        _logger.LogInformation("Session {SessionId}: Binding to viewer JSRuntime", SessionId);
        _jsRuntime = viewerJsRuntime;
        _directRenderBound = true;

        // Configure TURN server in the viewer's JS context before creating PeerConnection
        if (TurnConfig is var (urls, username, credential))
        {
            await _jsRuntime.InvokeVoidAsync("SteamViewerWebRTC.setTurnConfig", urls, username, credential);
        }

        await InitializeAsync();

        // Process queued signaling messages (SDP offers/answers, ICE candidates)
        while (_pendingSignaling.TryDequeue(out var action))
        {
            await action();
        }
    }

    /// <summary>
    /// Handle incoming SDP offer from the host.
    /// </summary>
    public async Task HandleSdpOfferAsync(string sdp)
    {
        if (_webrtc == null)
        {
            _logger.LogInformation("Session {SessionId}: Queuing SDP offer (waiting for viewer bind)", SessionId);
            _pendingSignaling.Enqueue(() => HandleSdpOfferAsync(sdp));
            return;
        }

        _logger.LogInformation("Session {SessionId}: Received SDP offer", SessionId);
        await _webrtc.SetRemoteDescriptionAsync(sdp);
        var answer = await _webrtc.CreateAnswerAsync();

        OnSdpMessage?.Invoke(PeerId, answer);
        _logger.LogInformation("Session {SessionId}: SDP answer sent", SessionId);
    }

    /// <summary>
    /// Handle incoming SDP answer from the host.
    /// </summary>
    public async Task HandleSdpAnswerAsync(string sdp)
    {
        if (_webrtc == null)
        {
            _logger.LogInformation("Session {SessionId}: Queuing SDP answer (waiting for viewer bind)", SessionId);
            _pendingSignaling.Enqueue(() => HandleSdpAnswerAsync(sdp));
            return;
        }

        _logger.LogInformation("Session {SessionId}: Received SDP answer", SessionId);
        await _webrtc.SetRemoteDescriptionAsync(sdp);
    }

    /// <summary>
    /// Handle incoming ICE candidate from the host.
    /// </summary>
    public async Task HandleIceCandidateAsync(string candidate, string? sdpMid, int? sdpMLineIndex)
    {
        if (_webrtc == null)
        {
            _pendingSignaling.Enqueue(() => HandleIceCandidateAsync(candidate, sdpMid, sdpMLineIndex));
            return;
        }

        var candidateJson = JsonSerializer.Serialize(new
        {
            candidate,
            sdpMid,
            sdpMLineIndex
        });

        await _webrtc.AddIceCandidateAsync(candidateJson);
    }

    /// <summary>
    /// Send a raw string message to the remote peer via WebRTC data channel.
    /// </summary>
    public async Task<bool> SendDataAsync(string data)
    {
        if (_webrtc == null || !_webrtc.IsDataChannelOpen) return false;
        return await _webrtc.SendDataAsync(data);
    }

    /// <summary>
    /// Send an input event to the remote peer.
    /// MouseMove goes over the unreliable mouse channel (no head-of-line blocking).
    /// All other input goes over the reliable control channel.
    /// </summary>
    public async Task SendInputAsync(InputEvent inputEvent)
    {
        if (_webrtc == null || !_webrtc.IsDataChannelOpen) return;

        try
        {
            var json = JsonSerializer.Serialize(inputEvent);
            if (inputEvent is InputEvent.MouseMove)
            {
                await _webrtc.SendMouseDataAsync(json);
            }
            else
            {
                await _webrtc.SendDataAsync(json);
            }
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
        if (_webrtc == null || !_webrtc.IsDataChannelOpen) return;

        try
        {
            var json = JsonSerializer.Serialize<ClipboardMessage>(new ClipboardMessage.Request());
            await _webrtc.SendDataAsync(json);
            _logger.LogDebug("Session {SessionId}: Sent clipboard request", SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send clipboard request for session {SessionId}", SessionId);
        }
    }

    /// <summary>
    /// Send clipboard data to the host to set on their clipboard.
    /// </summary>
    public async Task SendClipboardAsync(string format, string data)
    {
        if (_webrtc == null || !_webrtc.IsDataChannelOpen) return;

        try
        {
            var json = JsonSerializer.Serialize<ClipboardMessage>(new ClipboardMessage.Set(format, data));
            await _webrtc.SendDataAsync(json);
            _logger.LogDebug("Session {SessionId}: Sent clipboard set ({Format}, {Length} chars)",
                SessionId, format, data.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send clipboard for session {SessionId}", SessionId);
        }
    }

    /// <summary>
    /// Enable stats relay polling in JS for this session.
    /// </summary>
    public async Task EnableStatsRelayAsync()
    {
        if (_webrtc != null)
            await _jsRuntime.InvokeVoidAsync("SteamViewerWebRTC.enableStatsRelay", SessionId);
    }

    /// <summary>
    /// Disable stats relay polling in JS for this session.
    /// </summary>
    public async Task DisableStatsRelayAsync()
    {
        if (_webrtc != null)
            await _jsRuntime.InvokeVoidAsync("SteamViewerWebRTC.disableStatsRelay", SessionId);
    }

    /// <summary>
    /// Disconnect this session.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_webrtc != null)
        {
            await _webrtc.CloseAsync();
        }

        SetState(ViewerSessionState.Disconnected);
        OnDisconnected?.Invoke(null);
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

            _logger.LogInformation("Session {SessionId}: Received monitor layout: {Count} monitors, active={Active}",
                SessionId, monitors.Count, activeId);

            OnMonitorLayoutReceived?.Invoke(monitors, activeId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session {SessionId}: Failed to parse monitor layout", SessionId);
        }
    }

    /// <summary>
    /// Notify the host that viewer input lock state changed.
    /// Host hides cursor in video when locked (local overlay takes over).
    /// </summary>
    public async Task SendInputLockStateAsync(bool locked)
    {
        if (_webrtc == null || !_webrtc.IsDataChannelOpen) return;

        try
        {
            var json = JsonSerializer.Serialize(new { type = "inputLockChanged", locked });
            await _webrtc.SendDataAsync(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session {SessionId}: Failed to send inputLockChanged", SessionId);
        }
    }

    /// <summary>
    /// Toggle host cursor visibility in the captured video stream.
    /// </summary>
    public async Task SendToggleCursorAsync()
    {
        if (_webrtc == null || !_webrtc.IsDataChannelOpen) return;

        try
        {
            var json = JsonSerializer.Serialize(new { type = "toggleCursor" });
            await _webrtc.SendDataAsync(json);
            _logger.LogInformation("Session {SessionId}: Sent toggleCursor", SessionId);
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
        if (_webrtc == null || !_webrtc.IsDataChannelOpen) return;

        try
        {
            var json = JsonSerializer.Serialize(new { type = "switchDisplay", monitorId });
            await _webrtc.SendDataAsync(json);
            _logger.LogInformation("Session {SessionId}: Requested switch to display {MonitorId}", SessionId, monitorId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session {SessionId}: Failed to send switch display request", SessionId);
        }
    }

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
            _logger.LogWarning(ex, "Failed to handle ICE candidate for session {SessionId}", SessionId);
        }
    }

    private async Task HandleDataChannelOpen()
    {
        _logger.LogInformation("Session {SessionId}: Data channel opened (directRender={DirectRender})", SessionId, _directRenderBound);
        SetState(ViewerSessionState.Connected);
        OnReady?.Invoke();

        // Skip JPEG relay frame capture when direct rendering is active
        // (PeerConnection is in the viewer's JS context — renders directly to canvas)
        if (!_directRenderBound)
        {
            await StartFrameCaptureAsync();
        }
    }

    private async Task HandleDataChannelClose()
    {
        _logger.LogWarning("Session {SessionId}: Data channel closed unexpectedly", SessionId);
        // Treat data channel close as disconnect (fix: OnDataChannelClose handler)
        if (State == ViewerSessionState.Connected)
        {
            SetState(ViewerSessionState.Disconnected);
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
                        _logger.LogInformation("Session {SessionId}: Host is already elevated", SessionId);
                        OnControlMessage?.Invoke(type, null);
                        break;

                    case "systemElevationAlready":
                    case "systemElevationDenied":
                    case "systemElevationFailed":
                    case "runAsSystemSuccess":
                    case "runAsSystemFailed":
                        var sysMessage = root.TryGetProperty("message", out var sysMsgProp) ? sysMsgProp.GetString() : null;
                        _logger.LogInformation("Session {SessionId}: {Type}: {Message}", SessionId, type, sysMessage);
                        OnControlMessage?.Invoke(type, sysMessage);
                        break;

                    case "cursorVisibilityChanged":
                        var visible = root.TryGetProperty("visible", out var visProp) && visProp.GetBoolean();
                        _logger.LogInformation("Session {SessionId}: Host cursor visibility: {Visible}", SessionId, visible);
                        OnControlMessage?.Invoke(type, visible.ToString());
                        break;

                    case "clipboard_data":
                        var cbFormat = root.TryGetProperty("format", out var fProp) ? fProp.GetString() : null;
                        var cbData = root.TryGetProperty("data", out var dProp) ? dProp.GetString() : null;
                        if (cbFormat != null && cbData != null)
                        {
                            _logger.LogDebug("Session {SessionId}: Received clipboard ({Format}, {Length} chars)",
                                SessionId, cbFormat, cbData.Length);
                            OnClipboardReceived?.Invoke(cbFormat, cbData);
                        }
                        break;

                    case "secureDesktopActive":
                        _logger.LogInformation("Session {SessionId}: Secure Desktop ACTIVE", SessionId);
                        IsSecureDesktopActive = true;
                        OnSecureDesktopStateChanged?.Invoke(true);
                        break;

                    case "secureDesktopInactive":
                        _logger.LogInformation("Session {SessionId}: Secure Desktop INACTIVE", SessionId);
                        IsSecureDesktopActive = false;
                        OnSecureDesktopStateChanged?.Invoke(false);
                        break;

                    case "secureDesktopFrame":
                        _sdViewerFrameCount++;
                        var frameData = root.TryGetProperty("data", out var frameProp) ? frameProp.GetString() : null;
                        var frameW = root.TryGetProperty("width", out var fwProp) ? fwProp.GetInt32() : 0;
                        var frameH = root.TryGetProperty("height", out var fhProp) ? fhProp.GetInt32() : 0;
                        if (_sdViewerFrameCount <= 3 || _sdViewerFrameCount % 100 == 0)
                            _logger.LogInformation("Session {SessionId}: SD frame #{Count}: data={HasData} {W}x{H}",
                                SessionId, _sdViewerFrameCount, frameData != null, frameW, frameH);
                        if (frameData != null && frameW > 0 && frameH > 0)
                        {
                            OnSecureDesktopFrame?.Invoke(frameData, frameW, frameH);
                        }
                        else
                        {
                            _logger.LogWarning("Session {SessionId}: SD frame DROPPED (data={HasData}, w={W}, h={H})",
                                SessionId, frameData != null, frameW, frameH);
                        }
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // Not a control message, ignore
        }

        await Task.CompletedTask;
    }

    private async Task HandleRenegotiationNeeded(string offerJson)
    {
        _logger.LogInformation("Session {SessionId}: Renegotiation needed", SessionId);
        OnSdpMessage?.Invoke(PeerId, offerJson);
        await Task.CompletedTask;
    }

    private async Task HandleConnectionStateChange(string state)
    {
        _logger.LogInformation("Session {SessionId}: Connection state changed to {State}", SessionId, state);

        switch (state)
        {
            case "connected":
                SetState(ViewerSessionState.Connected);
                // Restart frame capture if it was stopped by a previous disconnect
                // (only needed for JPEG relay path — skip when direct rendering is active)
                if (_frameCaptureStarted && !_directRenderBound)
                {
                    _logger.LogInformation("Session {SessionId}: Connection recovered, restarting frame capture", SessionId);
                    _frameCaptureStarted = false;
                    await StartFrameCaptureAsync();
                }
                break;
            case "disconnected":
            case "failed":
            case "closed":
                SetState(ViewerSessionState.Disconnected);
                OnDisconnected?.Invoke(state);
                break;
        }
    }

    private void SetState(ViewerSessionState newState)
    {
        if (State != newState)
        {
            State = newState;
            OnStateChanged?.Invoke(newState);
        }
    }

    /// <summary>
    /// Relay a JPEG frame to this session's listeners.
    /// Called by frame capture mechanism.
    /// </summary>
    public void RelayFrame(JpegFrame frame)
    {
        OnFrame?.Invoke(frame);
    }

    /// <summary>
    /// Called from JS when a video frame is captured.
    /// </summary>
    [JSInvokable]
    public void OnFrameCaptured(string base64Data, int width, int height)
    {
        OnFrame?.Invoke(new JpegFrame(base64Data, width, height));
    }

    /// <summary>
    /// Enable direct rendering to a visible canvas (bypasses JPEG relay).
    /// Only works when PeerConnection's JSRuntime matches the viewer's window.
    /// </summary>
    public async Task<bool> TryEnableDirectRenderingAsync(string canvasId, IJSRuntime viewerJsRuntime)
    {
        // Direct rendering only works if the PeerConnection is in the same JS context
        if (!ReferenceEquals(_jsRuntime, viewerJsRuntime))
        {
            _logger.LogInformation("Session {SessionId}: JSRuntime mismatch — using JPEG relay", SessionId);
            return false;
        }

        try
        {
            var result = await _jsRuntime.InvokeAsync<bool>(
                "SteamViewerWebRTC.setDirectRenderTarget", SessionId, canvasId);
            if (result)
            {
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

    /// <summary>
    /// Start capturing frames from the JS WebRTC video track.
    /// </summary>
    private async Task StartFrameCaptureAsync()
    {
        if (_frameCaptureStarted || _disposed) return;
        _frameCaptureStarted = true;

        try
        {
            _dotNetRef = DotNetObjectReference.Create(this);
            await _jsRuntime.InvokeVoidAsync("SteamViewerWebRTC.startFrameCapture", SessionId, _dotNetRef);
            _logger.LogInformation("Session {SessionId}: Frame capture started", SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session {SessionId}: Failed to start frame capture", SessionId);
            _frameCaptureStarted = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Stop frame capture
        try
        {
            await _jsRuntime.InvokeVoidAsync("SteamViewerWebRTC.stopFrameCapture", SessionId);
        }
        catch
        {
            // Ignore if JS not available during shutdown
        }

        _dotNetRef?.Dispose();
        _dotNetRef = null;

        if (_webrtc != null)
        {
            _webrtc.OnIceCandidate -= HandleIceCandidate;
            _webrtc.OnDataChannelOpen -= HandleDataChannelOpen;
            _webrtc.OnDataChannelClose -= HandleDataChannelClose;
            _webrtc.OnDataChannelMessage -= HandleDataChannelMessage;
            _webrtc.OnRenegotiationNeeded -= HandleRenegotiationNeeded;
            _webrtc.OnConnectionStateChange -= HandleConnectionStateChange;

            await _webrtc.DisposeAsync();
            _webrtc = null;
        }
    }

    private record IceCandidateData(string candidate, string? sdpMid, int? sdpMLineIndex);
}

/// <summary>
/// Connection state for a viewer session.
/// </summary>
public enum ViewerSessionState
{
    /// <summary>Session is being set up.</summary>
    Connecting,
    /// <summary>Waiting for SDP offer from host.</summary>
    WaitingForOffer,
    /// <summary>Session is connected and active.</summary>
    Connected,
    /// <summary>Session has been disconnected.</summary>
    Disconnected,
    /// <summary>Session encountered an error.</summary>
    Error
}
