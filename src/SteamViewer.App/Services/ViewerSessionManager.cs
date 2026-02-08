using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SteamViewer.App.Services.Models;
using SteamViewer.Client.Core.Network;
using SteamViewer.Common.Protocol;
using System.Collections.Concurrent;
using Blake3;
using System.Text;

namespace SteamViewer.App.Services;

/// <summary>
/// Manages multiple viewer sessions for the multi-tab viewer feature.
/// Holds WebRTC connections and routes signaling messages to the correct session.
/// </summary>
public sealed class ViewerSessionManager : IAsyncDisposable
{
    private readonly ILogger<ViewerSessionManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IConfiguration _configuration;
    private readonly SignalingClient _signalingClient;

    private readonly ConcurrentDictionary<string, ViewerSession> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _peerToSession = new(); // peerId -> sessionId
    private bool _signalingSubscribed;
    private bool _disposed;

#if DEBUG
    // Counter for generating short test viewer IDs
    private static int _debugViewerIdCounter = 100;
#endif

    /// <summary>
    /// Maximum number of concurrent sessions allowed.
    /// </summary>
    public const int MaxSessions = 6;

    /// <summary>
    /// All active sessions.
    /// </summary>
    public IReadOnlyCollection<ViewerSession> Sessions => _sessions.Values.ToList();

    /// <summary>
    /// Raised when a new session is created.
    /// </summary>
    public event Action<ViewerSession>? OnSessionCreated;

    /// <summary>
    /// Raised when a session is removed.
    /// </summary>
    public event Action<string>? OnSessionRemoved;

    /// <summary>
    /// Raised when a session's state changes.
    /// </summary>
    public event Action<string, ViewerSessionState>? OnSessionStateChanged;

    /// <summary>
    /// Raised when connection fails (for error display).
    /// </summary>
    public event Action<string, string>? OnConnectionFailed;

    public ViewerSessionManager(
        ILogger<ViewerSessionManager> logger,
        ILoggerFactory loggerFactory,
        IConfiguration configuration,
        SignalingClient signalingClient)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _configuration = configuration;
        _signalingClient = signalingClient;
    }

    /// <summary>
    /// Create a new viewer session and connect to the specified peer.
    /// </summary>
    /// <param name="peerId">The peer ID to connect to.</param>
    /// <param name="password">The password for the peer.</param>
    /// <param name="jsRuntime">The JS runtime from the calling Blazor context.</param>
    /// <returns>The created session, or null if max sessions reached or connection failed.</returns>
    public async Task<ViewerSession?> CreateSessionAsync(string peerId, string password, IJSRuntime jsRuntime)
    {
        if (_sessions.Count >= MaxSessions)
        {
            _logger.LogWarning("Cannot create session: max sessions ({Max}) reached", MaxSessions);
            OnConnectionFailed?.Invoke(peerId, $"Maximum {MaxSessions} sessions allowed");
            return null;
        }

        // Check if already connected to this peer
        if (_peerToSession.ContainsKey(peerId))
        {
            _logger.LogWarning("Already connected to peer {PeerId}", peerId);
            OnConnectionFailed?.Invoke(peerId, "Already connected to this peer");
            return null;
        }

        EnsureSignalingSubscribed();

        // Ensure signaling is connected
        if (!_signalingClient.IsConnected)
        {
            await _signalingClient.ConnectAsync();

#if DEBUG
            // Use short test IDs for easier debugging (VIEWER100, VIEWER101, etc.)
            var joinerId = $"VIEWER{_debugViewerIdCounter++}";
#else
            // Register with a random ID for joining
            var joinerId = new Random().Next(100000000, 999999999).ToString();
#endif
            var joinerPasswordHash = Convert.ToHexString(
                Hasher.Hash(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString())).AsSpan()
            ).ToLowerInvariant();

            await _signalingClient.RegisterAsync(joinerId, joinerPasswordHash);
        }

        // Create session
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        var session = new ViewerSession(
            sessionId,
            peerId,
            jsRuntime,
            _loggerFactory,
            SendSignalingMessage);

        // Subscribe to session events
        session.OnStateChanged += state => HandleSessionStateChanged(sessionId, state);
        session.OnDisconnected += reason => HandleSessionDisconnected(sessionId, reason);
        session.OnIceCandidate += (candidate, sdpMid, sdpMLineIndex) =>
            _signalingClient.SendIceCandidateAsync(peerId, candidate, sdpMid, sdpMLineIndex);
        session.OnSdpMessage += (targetPeerId, sdp) =>
            _signalingClient.SendSdpAnswerAsync(targetPeerId, sdp);

        _sessions[sessionId] = session;
        _peerToSession[peerId] = sessionId;

        _logger.LogInformation("Created session {SessionId} for peer {PeerId}", sessionId, peerId);

        // Initialize WebRTC
        await session.InitializeAsync();

        // Configure TURN server
        await ConfigureTurnServerAsync(jsRuntime);

        // Request connection via signaling
        await _signalingClient.RequestConnectionAsync(peerId, password);

        OnSessionCreated?.Invoke(session);

        return session;
    }

    /// <summary>
    /// Get a session by its ID.
    /// </summary>
    public ViewerSession? GetSession(string sessionId)
    {
        return _sessions.TryGetValue(sessionId, out var session) ? session : null;
    }

    /// <summary>
    /// Get a session by peer ID.
    /// </summary>
    public ViewerSession? GetSessionByPeerId(string peerId)
    {
        if (_peerToSession.TryGetValue(peerId, out var sessionId))
        {
            return GetSession(sessionId);
        }
        return null;
    }

    /// <summary>
    /// Remove and disconnect a session.
    /// </summary>
    public async Task RemoveSessionAsync(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            _peerToSession.TryRemove(session.PeerId, out _);

            await session.DisconnectAsync();
            await session.DisposeAsync();

            _logger.LogInformation("Removed session {SessionId}", sessionId);
            OnSessionRemoved?.Invoke(sessionId);
        }
    }

    /// <summary>
    /// Relay a JPEG frame to the specified session.
    /// </summary>
    public void RelayFrame(string sessionId, JpegFrame frame)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.RelayFrame(frame);
        }
    }

    private void EnsureSignalingSubscribed()
    {
        if (_signalingSubscribed) return;

        _signalingClient.OnMessageReceived += HandleSignalingMessage;
        _signalingSubscribed = true;
        _logger.LogDebug("Subscribed to signaling messages");
    }

    private void HandleSignalingMessage(SignalingMessage message)
    {
        // Route messages to the correct session
        switch (message)
        {
            case SignalingMessage.ConnectionResponse response:
                HandleConnectionResponse(response);
                break;

            case SignalingMessage.SdpOffer offer:
                HandleSdpOffer(offer);
                break;

            case SignalingMessage.SdpAnswer answer:
                HandleSdpAnswer(answer);
                break;

            case SignalingMessage.IceCandidate ice:
                HandleIceCandidate(ice);
                break;

            case SignalingMessage.Disconnected disconnected:
                HandlePeerDisconnected(disconnected);
                break;

            case SignalingMessage.Error error:
                HandleError(error);
                break;
        }
    }

    private void HandleConnectionResponse(SignalingMessage.ConnectionResponse response)
    {
        var session = GetSessionByPeerId(response.TargetId);
        if (session == null) return;

        if (response.Approved)
        {
            _logger.LogInformation("Session {SessionId}: Connection approved by peer {PeerId}",
                session.SessionId, response.TargetId);
        }
        else
        {
            _logger.LogWarning("Session {SessionId}: Connection rejected by peer {PeerId}",
                session.SessionId, response.TargetId);
            OnConnectionFailed?.Invoke(response.TargetId, "Connection rejected");
            _ = RemoveSessionAsync(session.SessionId);
        }
    }

    private void HandleSdpOffer(SignalingMessage.SdpOffer offer)
    {
        var session = GetSessionByPeerId(offer.TargetId);
        if (session == null)
        {
            _logger.LogWarning("Received SDP offer for unknown peer {PeerId}", offer.TargetId);
            return;
        }

        _ = session.HandleSdpOfferAsync(offer.Sdp);
    }

    private void HandleSdpAnswer(SignalingMessage.SdpAnswer answer)
    {
        var session = GetSessionByPeerId(answer.TargetId);
        if (session == null)
        {
            _logger.LogWarning("Received SDP answer for unknown peer {PeerId}", answer.TargetId);
            return;
        }

        _ = session.HandleSdpAnswerAsync(answer.Sdp);
    }

    private void HandleIceCandidate(SignalingMessage.IceCandidate ice)
    {
        var session = GetSessionByPeerId(ice.TargetId);
        if (session == null)
        {
            _logger.LogDebug("Received ICE candidate for unknown peer {PeerId}", ice.TargetId);
            return;
        }

        _ = session.HandleIceCandidateAsync(ice.Candidate, ice.SdpMid, ice.SdpMLineIndex);
    }

    private void HandlePeerDisconnected(SignalingMessage.Disconnected disconnected)
    {
        var session = GetSessionByPeerId(disconnected.PeerId);
        if (session == null) return;

        _logger.LogInformation("Session {SessionId}: Peer {PeerId} disconnected",
            session.SessionId, disconnected.PeerId);
        _ = RemoveSessionAsync(session.SessionId);
    }

    private void HandleError(SignalingMessage.Error error)
    {
        _logger.LogWarning("Signaling error: {Message}", error.Message);
        // Try to find which session this error relates to
        // For now, just log it
    }

    private void HandleSessionStateChanged(string sessionId, ViewerSessionState state)
    {
        _logger.LogDebug("Session {SessionId} state changed to {State}", sessionId, state);
        OnSessionStateChanged?.Invoke(sessionId, state);
    }

    private void HandleSessionDisconnected(string sessionId, string? reason)
    {
        _logger.LogInformation("Session {SessionId} WebRTC disconnected: {Reason}", sessionId, reason ?? "unknown");
        // Don't remove - WebRTC disconnects can be temporary.
        // Removal via: HandlePeerDisconnected (signaling) or user closing the tab.
    }

    private async Task SendSignalingMessage(SignalingMessage message)
    {
        await _signalingClient.SendAsync(message);
    }

    private async Task ConfigureTurnServerAsync(IJSRuntime jsRuntime)
    {
        var turnEnabled = _configuration.GetValue<bool>("TurnServer:Enabled");
        if (!turnEnabled) return;

        var urls = _configuration.GetSection("TurnServer:Urls").Get<string[]>();
        var username = _configuration["TurnServer:Username"];
        var credential = _configuration["TurnServer:Credential"];

        if (urls == null || urls.Length == 0 || string.IsNullOrEmpty(username))
        {
            return;
        }

        if (urls[0].Contains("YOUR_TURN_SERVER"))
        {
            return;
        }

        _logger.LogInformation("Configuring TURN server for session manager");
        await jsRuntime.InvokeVoidAsync("SteamViewerWebRTC.setTurnConfig", urls, username, credential);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_signalingSubscribed)
        {
            _signalingClient.OnMessageReceived -= HandleSignalingMessage;
        }

        // Dispose all sessions
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync();
        }

        _sessions.Clear();
        _peerToSession.Clear();
    }
}
