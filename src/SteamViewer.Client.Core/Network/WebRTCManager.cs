using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Client.Core.Network;

/// <summary>
/// Manages WebRTC peer connections via JavaScript interop.
/// Uses browser WebRTC API through MAUI Blazor WebView.
/// </summary>
public sealed class WebRTCManager : IWebRTCManager, IAsyncDisposable
{
    private readonly IJSRuntime _jsRuntime;
    private readonly ILogger<WebRTCManager> _logger;
    private readonly Func<SignalingMessage, Task>? _sendSignaling;
    private readonly string _sessionId;
    private DotNetObjectReference<WebRTCManager>? _dotNetRef;
    private bool _isInitialized;
    private bool _disposed;
    private string? _peerId;
    private string _clientId = "";

    /// <summary>
    /// Raised when an ICE candidate is gathered.
    /// </summary>
    public event Func<string, Task>? OnIceCandidate;

    /// <summary>
    /// Raised when the connection state changes.
    /// </summary>
    public event Func<string, Task>? OnConnectionStateChange;

    /// <summary>
    /// Raised when the data channel opens.
    /// </summary>
    public event Func<Task>? OnDataChannelOpen;

    /// <summary>
    /// Raised when the data channel closes.
    /// </summary>
    public event Func<Task>? OnDataChannelClose;

    /// <summary>
    /// Raised when a message is received on the data channel.
    /// </summary>
    public event Func<string, Task>? OnDataChannelMessage;

    /// <summary>
    /// Raised when binary data is received on the data channel.
    /// </summary>
    public event Func<byte[], Task>? OnDataChannelBinaryMessage;

    /// <summary>
    /// Raised when renegotiation is needed (e.g., after adding video track).
    /// The string parameter is the SDP offer JSON.
    /// </summary>
    public event Func<string, Task>? OnRenegotiationNeeded;

    /// <summary>
    /// Raised when stats data is relayed from JS (for cross-window overlay).
    /// </summary>
    public event Action<string>? OnStatsUpdated;

    /// <summary>
    /// Raised when the first video frame is rendered via direct rendering.
    /// Used by RemoteViewer to dismiss the "Waiting for host screen" overlay.
    /// </summary>
    public event Action? OnVideoStarted;

    /// <summary>
    /// Raised when screen sharing was lost and all JS auto-restart attempts failed.
    /// </summary>
    public event Action? OnScreenShareLost;

    /// <summary>
    /// Raised when a JSON message is received on the file signaling channel (FormatList, FileContentsRequest).
    /// </summary>
    public event Func<string, Task>? OnFileChannelMessage;

    /// <summary>
    /// Raised when raw binary data is received on the file-data channel (FileContentsResponse bytes).
    /// </summary>
    public event Func<byte[], Task>? OnFileDataBinaryMessage;

    /// <summary>
    /// Raised when screen capture starts, reporting actual physical capture dimensions.
    /// </summary>
    public event Action<int, int>? OnCaptureStarted;

    // IWebRTCManager events
    event EventHandler<string>? IWebRTCManager.ConnectionStateChanged
    {
        add => _connectionStateChangedEvent += value;
        remove => _connectionStateChangedEvent -= value;
    }
    private event EventHandler<string>? _connectionStateChangedEvent;

    event EventHandler? IWebRTCManager.DataChannelOpened
    {
        add => _dataChannelOpenedEvent += value;
        remove => _dataChannelOpenedEvent -= value;
    }
    private event EventHandler? _dataChannelOpenedEvent;

    event EventHandler? IWebRTCManager.DataChannelClosed
    {
        add => _dataChannelClosedEvent += value;
        remove => _dataChannelClosedEvent -= value;
    }
    private event EventHandler? _dataChannelClosedEvent;

    event EventHandler<byte[]>? IWebRTCManager.VideoDataReceived
    {
        add => _videoDataReceivedEvent += value;
        remove => _videoDataReceivedEvent -= value;
    }
    private event EventHandler<byte[]>? _videoDataReceivedEvent;

    event EventHandler<byte[]>? IWebRTCManager.InputDataReceived
    {
        add => _inputDataReceivedEvent += value;
        remove => _inputDataReceivedEvent -= value;
    }
    private event EventHandler<byte[]>? _inputDataReceivedEvent;

    /// <summary>
    /// Current connection state.
    /// </summary>
    public string ConnectionState { get; private set; } = "new";

    /// <summary>
    /// Whether the data channel is open.
    /// </summary>
    public bool IsDataChannelOpen { get; private set; }

    public WebRTCManager(IJSRuntime jsRuntime, ILogger<WebRTCManager> logger, string sessionId, string clientId = "", Func<SignalingMessage, Task>? sendSignaling = null)
    {
        _jsRuntime = jsRuntime;
        _logger = logger;
        _sessionId = sessionId;
        _clientId = clientId;
        _sendSignaling = sendSignaling;
    }

    /// <summary>
    /// Sets the client ID for signaling.
    /// </summary>
    public void SetClientId(string clientId)
    {
        _clientId = clientId;
    }

    /// <summary>
    /// Sets a callback for sending signaling messages.
    /// </summary>
    public void SetSignalingCallback(Func<SignalingMessage, Task> sendSignaling)
    {
        // This method exists for external configuration, but we store in constructor
    }

    /// <summary>
    /// Initialize the WebRTC peer connection.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            throw new InvalidOperationException("WebRTC already initialized");
        }

        _dotNetRef = DotNetObjectReference.Create(this);

        var success = await _jsRuntime.InvokeAsync<bool>(
            "SteamViewerWebRTC.initialize",
            _sessionId, _dotNetRef);

        if (!success)
        {
            throw new InvalidOperationException("Failed to initialize WebRTC");
        }

        _isInitialized = true;
        _logger.LogInformation("WebRTC initialized");
    }

    /// <summary>
    /// Create a data channel (for host initiating connection).
    /// </summary>
    public async Task CreateDataChannelAsync(string name = "data")
    {
        EnsureInitialized();
        await _jsRuntime.InvokeVoidAsync("SteamViewerWebRTC.createDataChannel", _sessionId, name);
        _logger.LogDebug("Data channel '{Name}' created", name);
    }

    /// <summary>
    /// Create dual data channels: control (reliable) + mouse (unreliable).
    /// </summary>
    public async Task CreateDataChannelsAsync()
    {
        EnsureInitialized();
        await _jsRuntime.InvokeVoidAsync("SteamViewerWebRTC.createDataChannels", _sessionId);
        _logger.LogDebug("Dual data channels created (control + mouse)");
    }

    /// <summary>
    /// Create an SDP offer (for viewer initiating connection).
    /// </summary>
    public async Task<string> CreateOfferAsync()
    {
        EnsureInitialized();
        var offer = await _jsRuntime.InvokeAsync<string>("SteamViewerWebRTC.createOffer", _sessionId);
        _logger.LogDebug("SDP offer created");
        return offer;
    }

    /// <summary>
    /// Create an SDP answer (for host responding to connection).
    /// </summary>
    public async Task<string> CreateAnswerAsync()
    {
        EnsureInitialized();
        var answer = await _jsRuntime.InvokeAsync<string>("SteamViewerWebRTC.createAnswer", _sessionId);
        _logger.LogDebug("SDP answer created");
        return answer;
    }

    /// <summary>
    /// Set the remote SDP description.
    /// </summary>
    public async Task SetRemoteDescriptionAsync(string sdpJson)
    {
        EnsureInitialized();
        await _jsRuntime.InvokeVoidAsync("SteamViewerWebRTC.setRemoteDescription", _sessionId, sdpJson);
        _logger.LogDebug("Remote description set");
    }

    /// <summary>
    /// Add an ICE candidate from the remote peer.
    /// </summary>
    public async Task AddIceCandidateAsync(string candidateJson)
    {
        EnsureInitialized();
        await _jsRuntime.InvokeVoidAsync("SteamViewerWebRTC.addIceCandidate", _sessionId, candidateJson);
        _logger.LogDebug("ICE candidate added");
    }

    /// <summary>
    /// Start screen capture and add video track (for host).
    /// Always captures primary monitor (no picker).
    /// </summary>
    public async Task<bool> StartScreenCaptureAsync()
    {
        EnsureInitialized();
        var success = await _jsRuntime.InvokeAsync<bool>("SteamViewerWebRTC.startScreenCapture", _sessionId);
        _logger.LogInformation("Screen capture started: {Success}", success);
        return success;
    }

    /// <summary>
    /// Stop screen capture and remove video track.
    /// </summary>
    public async Task StopScreenCaptureAsync()
    {
        EnsureInitialized();
        await _jsRuntime.InvokeVoidAsync("SteamViewerWebRTC.stopScreenCapture", _sessionId);
        _logger.LogInformation("Screen capture stopped");
    }

    /// <summary>
    /// Start native DXGI capture via canvas bridge (no screen picker).
    /// Creates hidden canvas + captureStream → MediaStream → addTrack.
    /// </summary>
    public async Task<bool> StartNativeCaptureAsync(int fps = 30)
    {
        EnsureInitialized();
        var success = await _jsRuntime.InvokeAsync<bool>("SteamViewerWebRTC.startNativeCapture", _sessionId, fps);
        _logger.LogInformation("Native DXGI capture started: {Success}", success);
        return success;
    }

    /// <summary>
    /// Push a JPEG frame from DXGI capture to the JS canvas bridge.
    /// Fire-and-forget from capture thread — do not await on hot path.
    /// </summary>
    public ValueTask PushNativeFrameAsync(string base64Jpeg, int width, int height)
    {
        return _jsRuntime.InvokeVoidAsync("SteamViewerWebRTC.pushNativeFrame", _sessionId, base64Jpeg, width, height);
    }

    /// <summary>
    /// Stop native DXGI capture and clean up canvas bridge.
    /// </summary>
    public async Task StopNativeCaptureAsync()
    {
        EnsureInitialized();
        await _jsRuntime.InvokeVoidAsync("SteamViewerWebRTC.stopNativeCapture", _sessionId);
        _logger.LogInformation("Native DXGI capture stopped");
    }

    /// <summary>
    /// Pause video track sender (frees bandwidth for data channel during Secure Desktop).
    /// </summary>
    public async Task PauseVideoTrackAsync()
    {
        EnsureInitialized();
        await _jsRuntime.InvokeVoidAsync("SteamViewerWebRTC.pauseVideoTrack", _sessionId);
    }

    /// <summary>
    /// Resume video track sender after Secure Desktop deactivates.
    /// </summary>
    public async Task ResumeVideoTrackAsync()
    {
        EnsureInitialized();
        await _jsRuntime.InvokeVoidAsync("SteamViewerWebRTC.resumeVideoTrack", _sessionId);
    }

    /// <summary>
    /// Send string data over the control data channel.
    /// </summary>
    public async Task<bool> SendDataAsync(string data)
    {
        EnsureInitialized();
        return await _jsRuntime.InvokeAsync<bool>("SteamViewerWebRTC.sendData", _sessionId, data);
    }

    /// <summary>
    /// Send mouse data over the unreliable mouse channel (falls back to control channel).
    /// </summary>
    public async Task<bool> SendMouseDataAsync(string data)
    {
        EnsureInitialized();
        return await _jsRuntime.InvokeAsync<bool>("SteamViewerWebRTC.sendMouseData", _sessionId, data);
    }

    /// <summary>
    /// Send string data over the file signaling channel (FormatList, FileContentsRequest).
    /// </summary>
    public async Task<bool> SendFileChannelDataAsync(string data)
    {
        EnsureInitialized();
        return await _jsRuntime.InvokeAsync<bool>("SteamViewerWebRTC.sendFileChannelData", _sessionId, data);
    }

    /// <summary>
    /// Send raw binary data over the file-data channel (FileContentsResponse bytes).
    /// </summary>
    public async Task<bool> SendFileDataBinaryAsync(byte[] data)
    {
        EnsureInitialized();
        return await _jsRuntime.InvokeAsync<bool>("SteamViewerWebRTC.sendFileDataBinary", _sessionId, data);
    }

    /// <summary>
    /// Send binary data over the data channel.
    /// </summary>
    public async Task<bool> SendBinaryDataAsync(byte[] data)
    {
        EnsureInitialized();
        return await _jsRuntime.InvokeAsync<bool>("SteamViewerWebRTC.sendBinaryData", _sessionId, data);
    }

    #region IWebRTCManager Implementation

    /// <summary>
    /// Initialize WebRTC as host (creates offer and sends it).
    /// </summary>
    public async Task InitializeAsHostAsync(string peerId)
    {
        _peerId = peerId;

        if (!_isInitialized)
        {
            await InitializeAsync();
        }

        // Create dual data channels: control (reliable) + mouse (unreliable)
        await CreateDataChannelsAsync();

        // Create offer
        var offer = await CreateOfferAsync();

        // Send offer via signaling
        if (_sendSignaling != null)
        {
            var msg = new SignalingMessage.SdpOffer(peerId, offer);
            await _sendSignaling(msg);
        }

        _logger.LogInformation("Initialized as host, offer sent to {PeerId}", peerId);
    }

    /// <summary>
    /// Initialize WebRTC as viewer (waits for offer).
    /// </summary>
    public async Task InitializeAsViewerAsync()
    {
        if (!_isInitialized)
        {
            await InitializeAsync();
        }

        _logger.LogInformation("Initialized as viewer, waiting for offer");
    }

    /// <summary>
    /// Handle incoming SDP offer.
    /// </summary>
    public async Task HandleOfferAsync(string sdp, string peerId)
    {
        _peerId = peerId;
        await SetRemoteDescriptionAsync(sdp);

        // Create and send answer
        var answer = await CreateAnswerAsync();

        if (_sendSignaling != null)
        {
            var msg = new SignalingMessage.SdpAnswer(peerId, answer);
            await _sendSignaling(msg);
        }

        _logger.LogInformation("Handled offer from {PeerId}, answer sent", peerId);
    }

    /// <summary>
    /// Handle incoming SDP answer.
    /// </summary>
    public async Task HandleAnswerAsync(string sdp)
    {
        await SetRemoteDescriptionAsync(sdp);
        _logger.LogInformation("Handled SDP answer");
    }

    /// <summary>
    /// Handle incoming ICE candidate.
    /// </summary>
    public async Task HandleIceCandidateAsync(string candidate, string? sdpMid, int? sdpMLineIndex)
    {
        var candidateJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            candidate,
            sdpMid,
            sdpMLineIndex
        });

        await AddIceCandidateAsync(candidateJson);
    }

    /// <summary>
    /// Send video data.
    /// </summary>
    public async Task SendVideoDataAsync(byte[] data)
    {
        await SendBinaryDataAsync(data);
    }

    /// <summary>
    /// Send input data.
    /// </summary>
    public async Task SendInputDataAsync(byte[] data)
    {
        await SendBinaryDataAsync(data);
    }

    #endregion

    /// <summary>
    /// Close the WebRTC connection.
    /// </summary>
    public async Task CloseAsync()
    {
        if (!_isInitialized)
        {
            return;
        }

        await _jsRuntime.InvokeVoidAsync("SteamViewerWebRTC.close", _sessionId);
        _isInitialized = false;
        IsDataChannelOpen = false;
        ConnectionState = "closed";
        _logger.LogInformation("WebRTC connection closed");
    }

    #region JS Callbacks

    [JSInvokable]
    public async Task OnIceCandidateCallback(string candidateJson)
    {
        // Parse candidate type for logging (host, srflx, relay)
        var candidateType = "unknown";
        try
        {
            var json = System.Text.Json.JsonDocument.Parse(candidateJson);
            if (json.RootElement.TryGetProperty("candidate", out var candidateProp))
            {
                var candidate = candidateProp.GetString() ?? "";
                var match = System.Text.RegularExpressions.Regex.Match(candidate, @"typ (\w+)");
                if (match.Success)
                {
                    candidateType = match.Groups[1].Value;
                }
            }
        }
        catch { }

        // Log with type - RELAY means TURN server is working!
        _logger.LogInformation("ICE candidate gathered: {Type} {Preview}",
            candidateType.ToUpperInvariant(),
            candidateType == "relay" ? "✓ TURN WORKING" : "");

        if (OnIceCandidate != null)
        {
            await OnIceCandidate.Invoke(candidateJson);
        }
    }

    [JSInvokable]
    public async Task OnConnectionStateChangeCallback(string state)
    {
        ConnectionState = state;
        _logger.LogInformation("Connection state changed to: {State}", state);
        _connectionStateChangedEvent?.Invoke(this, state);
        if (OnConnectionStateChange != null)
        {
            await OnConnectionStateChange.Invoke(state);
        }
    }

    [JSInvokable]
    public async Task OnDataChannelOpenCallback()
    {
        IsDataChannelOpen = true;
        _logger.LogInformation("Data channel opened");
        _dataChannelOpenedEvent?.Invoke(this, EventArgs.Empty);
        if (OnDataChannelOpen != null)
        {
            await OnDataChannelOpen.Invoke();
        }
    }

    [JSInvokable]
    public async Task OnDataChannelCloseCallback()
    {
        IsDataChannelOpen = false;
        _logger.LogInformation("Data channel closed");
        _dataChannelClosedEvent?.Invoke(this, EventArgs.Empty);
        if (OnDataChannelClose != null)
        {
            await OnDataChannelClose.Invoke();
        }
    }

    [JSInvokable]
    public async Task OnDataChannelMessageCallback(string data)
    {
        if (OnDataChannelMessage != null)
        {
            await OnDataChannelMessage.Invoke(data);
        }
    }

    [JSInvokable]
    public async Task OnDataChannelBinaryMessageCallback(byte[] data)
    {

        // Fire interface event for video data
        _videoDataReceivedEvent?.Invoke(this, data);

        if (OnDataChannelBinaryMessage != null)
        {
            await OnDataChannelBinaryMessage.Invoke(data);
        }
    }

    [JSInvokable]
    public async Task OnFileChannelMessageCallback(string data)
    {
        if (OnFileChannelMessage != null)
        {
            await OnFileChannelMessage.Invoke(data);
        }
    }

    [JSInvokable]
    public async Task OnFileChannelBinaryCallback(byte[] data)
    {
        // Legacy binary on file channel — unused, kept for compat
    }

    [JSInvokable]
    public async Task OnFileDataBinaryCallback(string base64Data)
    {
        if (OnFileDataBinaryMessage != null)
        {
            var data = Convert.FromBase64String(base64Data);
            await OnFileDataBinaryMessage.Invoke(data);
        }
    }

    [JSInvokable]
    public async Task OnRenegotiationNeededCallback(string offerJson)
    {
        _logger.LogInformation("Renegotiation needed - new offer created after track added");
        if (OnRenegotiationNeeded != null)
        {
            await OnRenegotiationNeeded.Invoke(offerJson);
        }
    }

    [JSInvokable]
    public void OnStatsUpdate(string json)
    {
        OnStatsUpdated?.Invoke(json);
    }

    [JSInvokable]
    public void OnScreenShareLostCallback()
    {
        _logger.LogWarning("Screen sharing lost — all JS auto-restart attempts failed");
        OnScreenShareLost?.Invoke();
    }

    [JSInvokable]
    public void OnCaptureStartedCallback(int width, int height)
    {
        _logger.LogInformation("Capture started: {Width}x{Height} (physical pixels)", width, height);
        OnCaptureStarted?.Invoke(width, height);
    }

    [JSInvokable]
    public void OnVideoStartedCallback()
    {
        _logger.LogInformation("First video frame rendered via direct rendering");
        OnVideoStarted?.Invoke();
    }

    #endregion

    private void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("WebRTC not initialized. Call InitializeAsync first.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await CloseAsync();
        _dotNetRef?.Dispose();
    }
}
