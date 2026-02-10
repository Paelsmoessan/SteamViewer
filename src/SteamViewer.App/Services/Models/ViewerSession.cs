using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SteamViewer.App.Services;
using SteamViewer.Client.Core.Network;
using SteamViewer.Common.Protocol;
using System.Text.Json;

namespace SteamViewer.App.Services.Models;

/// <summary>
/// Represents a single viewer session with a remote peer.
/// Encapsulates WebRTC connection, video frames, and input handling.
/// </summary>
public sealed class ViewerSession : IAsyncDisposable
{
    private readonly IJSRuntime _jsRuntime;
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Func<SignalingMessage, Task> _sendSignaling;
    private WebRTCManager? _webrtc;
    private DotNetObjectReference<ViewerSession>? _dotNetRef;
    private bool _frameCaptureStarted;
    private bool _disposed;

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
    /// Raised when a JPEG video frame is received.
    /// </summary>
    public event Action<JpegFrame>? OnFrame;

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

        await _webrtc.InitializeAsync();

        SetState(ViewerSessionState.WaitingForOffer);
    }

    /// <summary>
    /// Handle incoming SDP offer from the host.
    /// </summary>
    public async Task HandleSdpOfferAsync(string sdp)
    {
        if (_webrtc == null) return;

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
        if (_webrtc == null) return;

        _logger.LogInformation("Session {SessionId}: Received SDP answer", SessionId);
        await _webrtc.SetRemoteDescriptionAsync(sdp);
    }

    /// <summary>
    /// Handle incoming ICE candidate from the host.
    /// </summary>
    public async Task HandleIceCandidateAsync(string candidate, string? sdpMid, int? sdpMLineIndex)
    {
        if (_webrtc == null) return;

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
    /// </summary>
    public async Task SendInputAsync(InputEvent inputEvent)
    {
        if (_webrtc == null || !_webrtc.IsDataChannelOpen) return;

        try
        {
            var json = JsonSerializer.Serialize(inputEvent);
            await _webrtc.SendDataAsync(json);
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
        _logger.LogInformation("Session {SessionId}: Data channel opened", SessionId);
        SetState(ViewerSessionState.Connected);
        OnReady?.Invoke();
        await StartFrameCaptureAsync();
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
                        var frameData = root.TryGetProperty("data", out var frameProp) ? frameProp.GetString() : null;
                        var frameW = root.TryGetProperty("width", out var fwProp) ? fwProp.GetInt32() : 0;
                        var frameH = root.TryGetProperty("height", out var fhProp) ? fhProp.GetInt32() : 0;
                        if (frameData != null && frameW > 0 && frameH > 0)
                        {
                            OnSecureDesktopFrame?.Invoke(frameData, frameW, frameH);
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
                if (_frameCaptureStarted)
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
