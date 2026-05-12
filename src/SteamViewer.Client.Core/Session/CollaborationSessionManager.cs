using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SteamViewer.Client.Core.Network;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Client.Core.Session;

/// <summary>
/// Manages a collaboration session with multiple participants.
/// Orchestrates SignalingClient and MeshWebRTCManager.
/// </summary>
public sealed class CollaborationSessionManager : IAsyncDisposable
{
    private readonly SignalingClient _signaling;
    private readonly IJSRuntime _jsRuntime;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CollaborationSessionManager> _logger;
    private readonly CollaborationSessionState _state;
    private MeshWebRTCManager? _meshWebRTC;
    private string? _myClientId;
    private bool _disposed;

    /// <summary>Session state.</summary>
    public CollaborationSessionState State => _state;

    /// <summary>Whether we're currently sharing our screen.</summary>
    public bool IsSharing => _meshWebRTC?.IsSharing ?? false;

    /// <summary>Raised when session is created successfully.</summary>
    public event EventHandler<string>? SessionCreated;

    /// <summary>Raised when successfully joined a session.</summary>
    public event EventHandler? SessionJoined;

    /// <summary>Raised when a participant joins.</summary>
    public event EventHandler<ParticipantInfo>? ParticipantJoined;

    /// <summary>Raised when a participant leaves.</summary>
    public event EventHandler<string>? ParticipantLeft;

    /// <summary>Raised when a peer's WebRTC connection state changes.</summary>
    public event EventHandler<(string PeerId, string State)>? PeerConnectionChanged;

    /// <summary>Raised when a peer's video is ready to display.</summary>
    public event EventHandler<(string PeerId, int Width, int Height)>? PeerVideoReady;

    /// <summary>Raised when screen share ends (by user or peer disconnect).</summary>
    public event EventHandler? ScreenShareEnded;

    /// <summary>Raised on errors.</summary>
    public event EventHandler<string>? Error;

    public CollaborationSessionManager(
        string serverUrl,
        IJSRuntime jsRuntime,
        ILoggerFactory loggerFactory)
    {
        _jsRuntime = jsRuntime;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<CollaborationSessionManager>();
        _signaling = new SignalingClient(serverUrl, loggerFactory.CreateLogger<SignalingClient>());
        _state = new CollaborationSessionState();

        // Subscribe to signaling events
        _signaling.OnMessageReceived += HandleSignalingMessage;
        _signaling.OnDisconnected += reason => _logger.LogWarning("Signaling disconnected: {Reason}", reason);
    }

    /// <summary>
    /// Connect to signaling server and register.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        // Generate a random client ID
        _myClientId = Guid.NewGuid().ToString("N")[..12];

        await _signaling.ConnectAsync(cancellationToken);

        // Register with a random password hash (not used for session auth)
        var passwordHash = Guid.NewGuid().ToString("N");
        var success = await _signaling.RegisterAsync(_myClientId, passwordHash, cancellationToken);

        if (!success)
        {
            throw new InvalidOperationException("Failed to register with signaling server");
        }

        _logger.LogInformation("Connected and registered as {ClientId}", _myClientId);
    }

    /// <summary>
    /// Create a new collaboration session.
    /// </summary>
    public async Task CreateSessionAsync(string displayName, string? sessionName = null, CancellationToken cancellationToken = default)
    {
        if (_myClientId == null)
        {
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");
        }

        await _signaling.SendAsync(new SignalingMessage.CreateSession(displayName, sessionName), cancellationToken);

        // Wait for response
        var response = await WaitForSessionResponseAsync<SignalingMessage.SessionCreated>(
            TimeSpan.FromSeconds(10), cancellationToken);

        _state.CreateSession(response.SessionCode, response.SessionName, _myClientId, displayName);

        // Initialize mesh WebRTC
        await InitializeMeshWebRTCAsync();

        _logger.LogInformation("Session created: {SessionCode}", response.SessionCode);
        SessionCreated?.Invoke(this, response.SessionCode);
    }

    /// <summary>
    /// Join an existing session.
    /// </summary>
    public async Task JoinSessionAsync(string sessionCode, string displayName, CancellationToken cancellationToken = default)
    {
        if (_myClientId == null)
        {
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");
        }

        await _signaling.SendAsync(new SignalingMessage.JoinSession(sessionCode, displayName), cancellationToken);

        // Wait for response
        SignalingMessage response;
        try
        {
            response = await WaitForSessionResponseAsync<SignalingMessage>(
                m => m is SignalingMessage.JoinedSession or SignalingMessage.JoinSessionFailed,
                TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("Timeout waiting for join response");
        }

        if (response is SignalingMessage.JoinSessionFailed failed)
        {
            throw new InvalidOperationException($"Failed to join session: {failed.Reason}");
        }

        var joined = (SignalingMessage.JoinedSession)response;
        _state.JoinSession(joined.SessionCode, _myClientId, displayName, joined.Participants);

        // Initialize mesh WebRTC
        await InitializeMeshWebRTCAsync();

        // Establish mesh connections to all existing participants
        await EstablishMeshConnectionsAsync(cancellationToken);

        _logger.LogInformation("Joined session: {SessionCode} with {Count} participants",
            sessionCode, joined.Participants.Count);
        SessionJoined?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Leave the current session.
    /// </summary>
    public async Task LeaveSessionAsync(CancellationToken cancellationToken = default)
    {
        if (!_state.InSession) return;

        await _signaling.SendAsync(new SignalingMessage.LeaveSession(), cancellationToken);

        if (_meshWebRTC != null)
        {
            await _meshWebRTC.CloseAllAsync();
        }

        _state.LeaveSession();
        _logger.LogInformation("Left session");
    }

    /// <summary>
    /// Start sharing our screen to all participants.
    /// </summary>
    public async Task<bool> StartScreenShareAsync()
    {
        if (_meshWebRTC == null || !_state.InSession)
        {
            _logger.LogWarning("Cannot start screen share: not in session");
            return false;
        }

        var success = await _meshWebRTC.StartScreenShareAsync();

        if (success && _myClientId != null)
        {
            // Notify others that we're sharing
            await _signaling.SendAsync(new SignalingMessage.ScreenShareStateChanged(_myClientId, true));
            _state.SetParticipantSharing(_myClientId, true);
        }

        return success;
    }

    /// <summary>
    /// Stop sharing our screen.
    /// </summary>
    public async Task StopScreenShareAsync()
    {
        if (_meshWebRTC == null) return;

        await _meshWebRTC.StopScreenShareAsync();

        if (_myClientId != null && _state.InSession)
        {
            await _signaling.SendAsync(new SignalingMessage.ScreenShareStateChanged(_myClientId, false));
            _state.SetParticipantSharing(_myClientId, false);
        }
    }

    /// <summary>
    /// Render a peer's video to a canvas element.
    /// </summary>
    public async Task<bool> RenderPeerToCanvasAsync(string peerId, string canvasId)
    {
        if (_meshWebRTC == null) return false;
        return await _meshWebRTC.RenderPeerToCanvasAsync(peerId, canvasId);
    }

    /// <summary>
    /// Send data to a specific peer.
    /// </summary>
    public async Task<bool> SendDataToPeerAsync(string peerId, string data)
    {
        if (_meshWebRTC == null) return false;
        return await _meshWebRTC.SendDataToPeerAsync(peerId, data);
    }

    /// <summary>
    /// Broadcast data to all peers.
    /// </summary>
    public async Task<int> BroadcastDataAsync(string data)
    {
        if (_meshWebRTC == null) return 0;
        return await _meshWebRTC.BroadcastDataAsync(data);
    }

    private async Task InitializeMeshWebRTCAsync()
    {
        _meshWebRTC = new MeshWebRTCManager(
            _jsRuntime,
            _loggerFactory.CreateLogger<MeshWebRTCManager>(),
            msg => _signaling.SendAsync(msg));

        await _meshWebRTC.InitializeAsync();

        // Subscribe to mesh events
        _meshWebRTC.PeerConnectionStateChanged += (_, e) =>
        {
            PeerConnectionChanged?.Invoke(this, e);
        };

        _meshWebRTC.PeerVideoReady += (_, e) =>
        {
            PeerVideoReady?.Invoke(this, e);
        };

        _meshWebRTC.ScreenShareEnded += (_, _) =>
        {
            if (_myClientId != null && _state.InSession)
            {
                _state.SetParticipantSharing(_myClientId, false);
                _ = _signaling.SendAsync(new SignalingMessage.ScreenShareStateChanged(_myClientId, false));
            }
            ScreenShareEnded?.Invoke(this, EventArgs.Empty);
        };
    }

    private async Task EstablishMeshConnectionsAsync(CancellationToken cancellationToken)
    {
        if (_meshWebRTC == null || _myClientId == null) return;

        // Connect to all existing participants (except self)
        foreach (var participantId in _state.GetOtherParticipantIds())
        {
            try
            {
                await _meshWebRTC.ConnectToPeerAsync(participantId);
                _logger.LogInformation("Initiated mesh connection to {PeerId}", participantId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to peer {PeerId}", participantId);
            }
        }
    }

    private void HandleSignalingMessage(SignalingMessage message)
    {
        _ = HandleSignalingMessageAsync(message);
    }

    private async Task HandleSignalingMessageAsync(SignalingMessage message)
    {
        try
        {
            switch (message)
            {
                case SignalingMessage.ParticipantJoined joined:
                    await HandleParticipantJoinedAsync(joined);
                    break;

                case SignalingMessage.ParticipantLeft left:
                    await HandleParticipantLeftAsync(left);
                    break;

                case SignalingMessage.ScreenShareStateChanged shareState:
                    HandleScreenShareStateChanged(shareState);
                    break;

                case SignalingMessage.MeshSdpOffer offer:
                    await HandleMeshSdpOfferAsync(offer);
                    break;

                case SignalingMessage.MeshSdpAnswer answer:
                    await HandleMeshSdpAnswerAsync(answer);
                    break;

                case SignalingMessage.MeshIceCandidate candidate:
                    await HandleMeshIceCandidateAsync(candidate);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling signaling message: {Type}", message.GetType().Name);
            Error?.Invoke(this, ex.Message);
        }
    }

    private async Task HandleParticipantJoinedAsync(SignalingMessage.ParticipantJoined joined)
    {
        await Task.CompletedTask; // currently sync body; reserve async sugar for future awaits
        _state.AddParticipant(joined.Participant);
        ParticipantJoined?.Invoke(this, joined.Participant);

        _logger.LogInformation("Participant joined: {DisplayName} ({Id})",
            joined.Participant.DisplayName, joined.Participant.Id);

        // The new participant will initiate the WebRTC connection to us
        // (they connect to all existing participants when they join)
    }

    private async Task HandleParticipantLeftAsync(SignalingMessage.ParticipantLeft left)
    {
        _state.RemoveParticipant(left.ParticipantId);
        ParticipantLeft?.Invoke(this, left.ParticipantId);

        // Close WebRTC connection to this peer
        if (_meshWebRTC != null)
        {
            await _meshWebRTC.DisconnectFromPeerAsync(left.ParticipantId);
        }

        _logger.LogInformation("Participant left: {Id}", left.ParticipantId);
    }

    private void HandleScreenShareStateChanged(SignalingMessage.ScreenShareStateChanged shareState)
    {
        _state.SetParticipantSharing(shareState.ParticipantId, shareState.IsSharing);
        _logger.LogInformation("Participant {Id} screen share: {IsSharing}",
            shareState.ParticipantId, shareState.IsSharing);
    }

    private async Task HandleMeshSdpOfferAsync(SignalingMessage.MeshSdpOffer offer)
    {
        if (_meshWebRTC == null) return;

        // The TargetId in the forwarded message is actually the sender's ID
        var senderId = offer.TargetId;
        await _meshWebRTC.HandlePeerOfferAsync(senderId, offer.Sdp);
    }

    private async Task HandleMeshSdpAnswerAsync(SignalingMessage.MeshSdpAnswer answer)
    {
        if (_meshWebRTC == null) return;

        var senderId = answer.TargetId;
        await _meshWebRTC.HandlePeerAnswerAsync(senderId, answer.Sdp);
    }

    private async Task HandleMeshIceCandidateAsync(SignalingMessage.MeshIceCandidate candidate)
    {
        if (_meshWebRTC == null) return;

        var senderId = candidate.TargetId;
        await _meshWebRTC.HandlePeerIceCandidateAsync(
            senderId,
            candidate.Candidate,
            candidate.SdpMid,
            candidate.SdpMLineIndex.HasValue ? (int)candidate.SdpMLineIndex.Value : null);
    }

    private async Task<T> WaitForSessionResponseAsync<T>(TimeSpan timeout, CancellationToken cancellationToken) where T : SignalingMessage
    {
        return await _signaling.WaitForMessageAsync<T>(_ => true, timeout, cancellationToken);
    }

    private async Task<SignalingMessage> WaitForSessionResponseAsync<T>(
        Func<T, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken) where T : SignalingMessage
    {
        return await _signaling.WaitForMessageAsync(predicate, timeout, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_state.InSession)
        {
            await LeaveSessionAsync();
        }

        if (_meshWebRTC != null)
        {
            await _meshWebRTC.DisposeAsync();
        }

        await _signaling.DisposeAsync();
    }
}
