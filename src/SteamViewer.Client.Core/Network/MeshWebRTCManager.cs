using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SteamViewer.Common.Protocol;
using System.Collections.Concurrent;

namespace SteamViewer.Client.Core.Network;

/// <summary>
/// Info about a peer connection in the mesh.
/// </summary>
public sealed class PeerConnectionInfo
{
    public required string PeerId { get; init; }
    public string ConnectionState { get; set; } = "new";
    public bool IsDataChannelOpen { get; set; }
    public bool IsReceivingVideo { get; set; }
    public int VideoWidth { get; set; }
    public int VideoHeight { get; set; }
}

/// <summary>
/// Manages multiple WebRTC peer connections for mesh topology.
/// Each peer in a collaboration session gets its own connection.
/// </summary>
public sealed class MeshWebRTCManager : IAsyncDisposable
{
    private readonly IJSRuntime _jsRuntime;
    private readonly ILogger<MeshWebRTCManager> _logger;
    private readonly Func<SignalingMessage, Task>? _sendSignaling;
    private readonly ConcurrentDictionary<string, PeerConnectionInfo> _peers = new();
    private DotNetObjectReference<MeshWebRTCManager>? _dotNetRef;
    private bool _isInitialized;
    private bool _disposed;
    private bool _isSharing;

    /// <summary>Raised when a peer's connection state changes.</summary>
    public event EventHandler<(string PeerId, string State)>? PeerConnectionStateChanged;

    /// <summary>Raised when a peer's data channel opens.</summary>
    public event EventHandler<string>? PeerDataChannelOpened;

    /// <summary>Raised when a peer's data channel closes.</summary>
    public event EventHandler<string>? PeerDataChannelClosed;

    /// <summary>Raised when a peer's video is ready (with dimensions).</summary>
    public event EventHandler<(string PeerId, int Width, int Height)>? PeerVideoReady;

    /// <summary>Raised when data is received from a peer.</summary>
    public event EventHandler<(string PeerId, string Data)>? PeerDataReceived;

    /// <summary>Raised when binary data is received from a peer.</summary>
    public event EventHandler<(string PeerId, byte[] Data)>? PeerBinaryDataReceived;

    /// <summary>Raised when an ICE candidate needs to be sent to a peer.</summary>
    public event EventHandler<(string PeerId, string CandidateJson)>? IceCandidateGenerated;

    /// <summary>Raised when screen share is stopped by user.</summary>
    public event EventHandler? ScreenShareEnded;

    /// <summary>Whether we're currently sharing our screen.</summary>
    public bool IsSharing => _isSharing;

    /// <summary>All connected peers.</summary>
    public IReadOnlyCollection<PeerConnectionInfo> Peers => _peers.Values.ToList();

    public MeshWebRTCManager(IJSRuntime jsRuntime, ILogger<MeshWebRTCManager> logger, Func<SignalingMessage, Task>? sendSignaling = null)
    {
        _jsRuntime = jsRuntime;
        _logger = logger;
        _sendSignaling = sendSignaling;
    }

    /// <summary>
    /// Initialize the mesh WebRTC manager.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        _dotNetRef = DotNetObjectReference.Create(this);
        var success = await _jsRuntime.InvokeAsync<bool>("SteamViewerMeshWebRTC.initialize", _dotNetRef);

        if (!success)
        {
            throw new InvalidOperationException("Failed to initialize MeshWebRTC");
        }

        _isInitialized = true;
        _logger.LogInformation("[Mesh] Initialized");
    }

    /// <summary>
    /// Create a peer connection and initiate connection (we are the offerer).
    /// </summary>
    public async Task ConnectToPeerAsync(string peerId)
    {
        EnsureInitialized();

        await _jsRuntime.InvokeAsync<bool>("SteamViewerMeshWebRTC.createPeerConnection", peerId);
        _peers[peerId] = new PeerConnectionInfo { PeerId = peerId };

        // Create data channel (we're the offerer)
        await _jsRuntime.InvokeAsync<bool>("SteamViewerMeshWebRTC.createDataChannel", peerId, "data");

        // Create and send offer
        var offerJson = await _jsRuntime.InvokeAsync<string>("SteamViewerMeshWebRTC.createOffer", peerId);

        if (_sendSignaling != null && !string.IsNullOrEmpty(offerJson))
        {
            await _sendSignaling(new SignalingMessage.MeshSdpOffer(peerId, offerJson));
        }

        _logger.LogInformation("[Mesh] Connected to peer {PeerId}, offer sent", peerId);
    }

    /// <summary>
    /// Handle an incoming offer from a peer (we are the answerer).
    /// </summary>
    public async Task HandlePeerOfferAsync(string peerId, string sdpJson)
    {
        EnsureInitialized();

        // Create peer connection if not exists
        if (!_peers.ContainsKey(peerId))
        {
            await _jsRuntime.InvokeAsync<bool>("SteamViewerMeshWebRTC.createPeerConnection", peerId);
            _peers[peerId] = new PeerConnectionInfo { PeerId = peerId };
        }

        // Set remote description
        await _jsRuntime.InvokeAsync<bool>("SteamViewerMeshWebRTC.setRemoteDescription", peerId, sdpJson);

        // Create and send answer
        var answerJson = await _jsRuntime.InvokeAsync<string>("SteamViewerMeshWebRTC.createAnswer", peerId);

        if (_sendSignaling != null && !string.IsNullOrEmpty(answerJson))
        {
            await _sendSignaling(new SignalingMessage.MeshSdpAnswer(peerId, answerJson));
        }

        _logger.LogInformation("[Mesh] Handled offer from {PeerId}, answer sent", peerId);
    }

    /// <summary>
    /// Handle an incoming answer from a peer.
    /// </summary>
    public async Task HandlePeerAnswerAsync(string peerId, string sdpJson)
    {
        EnsureInitialized();
        await _jsRuntime.InvokeAsync<bool>("SteamViewerMeshWebRTC.setRemoteDescription", peerId, sdpJson);
        _logger.LogInformation("[Mesh] Handled answer from {PeerId}", peerId);
    }

    /// <summary>
    /// Handle an incoming ICE candidate from a peer.
    /// </summary>
    public async Task HandlePeerIceCandidateAsync(string peerId, string candidate, string? sdpMid, int? sdpMLineIndex)
    {
        EnsureInitialized();

        var candidateJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            candidate,
            sdpMid,
            sdpMLineIndex
        });

        await _jsRuntime.InvokeAsync<bool>("SteamViewerMeshWebRTC.addIceCandidate", peerId, candidateJson);
        _logger.LogDebug("[Mesh] Added ICE candidate from {PeerId}", peerId);
    }

    /// <summary>
    /// Start sharing screen to all connected peers.
    /// </summary>
    public async Task<bool> StartScreenShareAsync()
    {
        EnsureInitialized();
        var success = await _jsRuntime.InvokeAsync<bool>("SteamViewerMeshWebRTC.startScreenCapture");
        _isSharing = success;
        _logger.LogInformation("[Mesh] Screen share started: {Success}", success);
        return success;
    }

    /// <summary>
    /// Stop sharing screen.
    /// </summary>
    public async Task StopScreenShareAsync()
    {
        EnsureInitialized();
        await _jsRuntime.InvokeVoidAsync("SteamViewerMeshWebRTC.stopScreenCapture");
        _isSharing = false;
        _logger.LogInformation("[Mesh] Screen share stopped");
    }

    /// <summary>
    /// Send data to a specific peer.
    /// </summary>
    public async Task<bool> SendDataToPeerAsync(string peerId, string data)
    {
        EnsureInitialized();
        return await _jsRuntime.InvokeAsync<bool>("SteamViewerMeshWebRTC.sendData", peerId, data);
    }

    /// <summary>
    /// Broadcast data to all connected peers.
    /// </summary>
    public async Task<int> BroadcastDataAsync(string data)
    {
        EnsureInitialized();
        return await _jsRuntime.InvokeAsync<int>("SteamViewerMeshWebRTC.broadcastData", data);
    }

    /// <summary>
    /// Send binary data to a specific peer.
    /// </summary>
    public async Task<bool> SendBinaryDataToPeerAsync(string peerId, byte[] data)
    {
        EnsureInitialized();
        return await _jsRuntime.InvokeAsync<bool>("SteamViewerMeshWebRTC.sendBinaryData", peerId, data);
    }

    /// <summary>
    /// Render a peer's video to a canvas element.
    /// </summary>
    public async Task<bool> RenderPeerToCanvasAsync(string peerId, string canvasId)
    {
        EnsureInitialized();
        return await _jsRuntime.InvokeAsync<bool>("SteamViewerMeshWebRTC.renderPeerToCanvas", peerId, canvasId);
    }

    /// <summary>
    /// Disconnect from a specific peer.
    /// </summary>
    public async Task DisconnectFromPeerAsync(string peerId)
    {
        EnsureInitialized();
        await _jsRuntime.InvokeVoidAsync("SteamViewerMeshWebRTC.closePeer", peerId);
        _peers.TryRemove(peerId, out _);
        _logger.LogInformation("[Mesh] Disconnected from {PeerId}", peerId);
    }

    /// <summary>
    /// Get peer connection info.
    /// </summary>
    public PeerConnectionInfo? GetPeer(string peerId)
    {
        _peers.TryGetValue(peerId, out var peer);
        return peer;
    }

    /// <summary>
    /// Get all peer IDs with open data channels.
    /// </summary>
    public IEnumerable<string> GetConnectedPeerIds()
    {
        return _peers.Values
            .Where(p => p.ConnectionState == "connected" && p.IsDataChannelOpen)
            .Select(p => p.PeerId);
    }

    /// <summary>
    /// Close all peer connections.
    /// </summary>
    public async Task CloseAllAsync()
    {
        if (!_isInitialized) return;

        await _jsRuntime.InvokeVoidAsync("SteamViewerMeshWebRTC.closeAll");
        _peers.Clear();
        _isSharing = false;
        _isInitialized = false;
        _logger.LogInformation("[Mesh] All connections closed");
    }

    #region JS Callbacks

    [JSInvokable]
    public Task OnMeshIceCandidateCallback(string peerId, string candidateJson)
    {
        _logger.LogDebug("[Mesh] ICE candidate generated for {PeerId}", peerId);
        IceCandidateGenerated?.Invoke(this, (peerId, candidateJson));

        // Send via signaling if available
        if (_sendSignaling != null)
        {
            try
            {
                var json = System.Text.Json.JsonDocument.Parse(candidateJson);
                var candidate = json.RootElement.GetProperty("candidate").GetString() ?? "";
                string? sdpMid = json.RootElement.TryGetProperty("sdpMid", out var mid) ? mid.GetString() : null;
                ushort? sdpMLineIndex = json.RootElement.TryGetProperty("sdpMLineIndex", out var idx) ? (ushort?)idx.GetUInt16() : null;

                return _sendSignaling(new SignalingMessage.MeshIceCandidate(peerId, candidate, sdpMid, sdpMLineIndex));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Mesh] Failed to parse ICE candidate");
            }
        }

        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnMeshConnectionStateChangeCallback(string peerId, string state)
    {
        _logger.LogInformation("[Mesh] Connection state for {PeerId}: {State}", peerId, state);

        if (_peers.TryGetValue(peerId, out var peer))
        {
            peer.ConnectionState = state;
        }

        PeerConnectionStateChanged?.Invoke(this, (peerId, state));
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnMeshDataChannelOpenCallback(string peerId)
    {
        _logger.LogInformation("[Mesh] Data channel opened for {PeerId}", peerId);

        if (_peers.TryGetValue(peerId, out var peer))
        {
            peer.IsDataChannelOpen = true;
        }

        PeerDataChannelOpened?.Invoke(this, peerId);
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnMeshDataChannelCloseCallback(string peerId)
    {
        _logger.LogInformation("[Mesh] Data channel closed for {PeerId}", peerId);

        if (_peers.TryGetValue(peerId, out var peer))
        {
            peer.IsDataChannelOpen = false;
        }

        PeerDataChannelClosed?.Invoke(this, peerId);
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnMeshDataChannelMessageCallback(string peerId, string data)
    {
        _logger.LogDebug("[Mesh] Message from {PeerId}: {Length} chars", peerId, data.Length);
        PeerDataReceived?.Invoke(this, (peerId, data));
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnMeshDataChannelBinaryMessageCallback(string peerId, byte[] data)
    {
        _logger.LogDebug("[Mesh] Binary message from {PeerId}: {Length} bytes", peerId, data.Length);
        PeerBinaryDataReceived?.Invoke(this, (peerId, data));
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnMeshVideoReadyCallback(string peerId, int width, int height)
    {
        _logger.LogInformation("[Mesh] Video ready from {PeerId}: {Width}x{Height}", peerId, width, height);

        if (_peers.TryGetValue(peerId, out var peer))
        {
            peer.IsReceivingVideo = true;
            peer.VideoWidth = width;
            peer.VideoHeight = height;
        }

        PeerVideoReady?.Invoke(this, (peerId, width, height));
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnScreenShareEndedCallback()
    {
        _logger.LogInformation("[Mesh] Screen share ended by user");
        _isSharing = false;
        ScreenShareEnded?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    #endregion

    private void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("MeshWebRTC not initialized. Call InitializeAsync first.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await CloseAllAsync();
        _dotNetRef?.Dispose();
    }
}
