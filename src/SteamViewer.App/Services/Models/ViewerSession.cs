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
                break;
            case "disconnected":
            case "failed":
            case "closed":
                SetState(ViewerSessionState.Disconnected);
                OnDisconnected?.Invoke(state);
                break;
        }

        await Task.CompletedTask;
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
