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
/// Routes signaling messages (including TransportEndpoint) to the correct session.
/// </summary>
public sealed class ViewerSessionManager : IAsyncDisposable
{
    private readonly ILogger<ViewerSessionManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IConfiguration _configuration;
    private readonly SignalingClient _signalingClient;
    private readonly TurnConfigService? _turnConfigService;

    private readonly ConcurrentDictionary<string, ViewerSession> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _peerToSession = new(); // peerId -> sessionId
    // Per-session callback invoked when the host approves the connection. Used to defer
    // opening the viewer window until we know the password was correct AND the host accepted -
    // otherwise a wrong-password attempt flashes a viewer window that opens and immediately closes.
    private readonly ConcurrentDictionary<string, Action<ViewerSession>> _onApprovedCallbacks = new();
    private bool _signalingSubscribed;
    private bool _disposed;
    private string _localClientId = "";

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
        SignalingClient signalingClient,
        TurnConfigService? turnConfigService = null)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _configuration = configuration;
        _signalingClient = signalingClient;
        _turnConfigService = turnConfigService;
    }

    /// <summary>
    /// Create a new viewer session and connect to the specified peer.
    /// </summary>
    /// <param name="peerId">The peer ID to connect to.</param>
    /// <param name="password">The password for the peer.</param>
    /// <param name="jsRuntime">The JS runtime from the calling Blazor context.</param>
    /// <param name="onApproved">
    /// Optional callback invoked when the host approves the connection (server returns
    /// ConnectionResponse with approved=true). The caller uses this to open the viewer
    /// window only AFTER approval - so a wrong-password attempt or rejection by the
    /// host never causes a viewer window to flash open and closed.
    /// </param>
    /// <returns>The created session, or null if max sessions reached or connection failed.</returns>
    public async Task<ViewerSession?> CreateSessionAsync(string peerId, string password, IJSRuntime jsRuntime,
        Action<ViewerSession>? onApproved = null)
    {
        if (_sessions.Count >= MaxSessions)
        {
            _logger.LogWarning("Cannot create session: max sessions ({Max}) reached", MaxSessions);
            OnConnectionFailed?.Invoke(peerId, $"Maximum {MaxSessions} sessions allowed");
            return null;
        }

        // If a stale session exists for this peer, clean it up first
        if (_peerToSession.TryGetValue(peerId, out var existingSessionId))
        {
            _logger.LogWarning("Stale session {SessionId} for peer {PeerId} — cleaning up before reconnect",
                existingSessionId, peerId);
            await RemoveSessionAsync(existingSessionId);
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
            // Crypto RNG so attackers can't enumerate or predict joiner IDs.
            var joinerId = System.Security.Cryptography.RandomNumberGenerator
                .GetInt32(100_000_000, 1_000_000_000).ToString();
#endif
            var joinerPasswordHash = Convert.ToHexString(
                Hasher.Hash(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString())).AsSpan()
            ).ToLowerInvariant();

            await _signalingClient.RegisterAsync(joinerId, joinerPasswordHash);
            _localClientId = joinerId;
        }

        // Create session
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        var session = new ViewerSession(
            sessionId,
            peerId,
            _localClientId,
            jsRuntime,
            _loggerFactory,
            _configuration,
            SendSignalingMessage,
            _signalingClient,
            _turnConfigService);
        session.StoredPassword = password;

        // Subscribe to session events
        session.OnStateChanged += state => HandleSessionStateChanged(sessionId, state);
        session.OnDisconnected += reason => HandleSessionDisconnected(sessionId, reason);

        _sessions[sessionId] = session;
        _peerToSession[peerId] = sessionId;
        if (onApproved != null)
        {
            _onApprovedCallbacks[sessionId] = onApproved;
        }

        _logger.LogInformation("Created session {SessionId} for peer {PeerId}", sessionId, peerId);

        // Transport initialization is DEFERRED — host sends RelayReady via signaling,
        // which triggers HandleRelayReadyAsync to setup encrypted relay.
        // RemoteViewer calls session.BindToViewerAsync() for rendering setup.

        // Request connection via signaling. Window won't open until host approves
        // (HandleConnectionResponse fires onApproved callback). If password is wrong
        // or host rejects, the callback never fires and no window appears.
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

            // Notify host via signaling server before tearing down transport
            try
            {
                await _signalingClient.SendAsync(new SignalingMessage.Disconnect(session.PeerId));
                _logger.LogInformation("Sent disconnect signal for peer {PeerId}", session.PeerId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send disconnect signal (best effort)");
            }

            await session.DisconnectAsync();
            await session.DisposeAsync();

            _logger.LogInformation("Removed session {SessionId}", sessionId);
            OnSessionRemoved?.Invoke(sessionId);
        }
    }

    /// <summary>
    /// Reconnect an existing session (e.g., after elevation restart).
    /// Disposes the old WebRTC connection and creates a new one with the same session ID and peer.
    /// </summary>
    public async Task<ViewerSession?> ReconnectSessionAsync(string sessionId, IJSRuntime jsRuntime)
    {
        if (!_sessions.TryRemove(sessionId, out var oldSession))
        {
            _logger.LogWarning("Cannot reconnect: session {SessionId} not found", sessionId);
            return null;
        }

        var peerId = oldSession.PeerId;
        var password = oldSession.StoredPassword;

        // Clean up old session
        _peerToSession.TryRemove(peerId, out _);
        try { await oldSession.DisposeAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error disposing old session during reconnect"); }

        if (string.IsNullOrEmpty(password))
        {
            _logger.LogError("Cannot reconnect session {SessionId}: no stored password", sessionId);
            return null;
        }

        _logger.LogInformation("Reconnecting session {SessionId} to peer {PeerId}", sessionId, peerId);

        EnsureSignalingSubscribed();

        // Ensure signaling is connected
        if (!_signalingClient.IsConnected)
        {
            await _signalingClient.ConnectAsync();

#if DEBUG
            var joinerId = $"VIEWER{_debugViewerIdCounter++}";
#else
            // Crypto RNG so attackers can't enumerate or predict joiner IDs.
            var joinerId = System.Security.Cryptography.RandomNumberGenerator
                .GetInt32(100_000_000, 1_000_000_000).ToString();
#endif
            var joinerPasswordHash = Convert.ToHexString(
                Hasher.Hash(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString())).AsSpan()
            ).ToLowerInvariant();

            await _signalingClient.RegisterAsync(joinerId, joinerPasswordHash);
            _localClientId = joinerId;
        }

        // Create new session with the SAME session ID (preserves tab tracking)
        var session = new ViewerSession(
            sessionId,
            peerId,
            _localClientId,
            jsRuntime,
            _loggerFactory,
            _configuration,
            SendSignalingMessage,
            _signalingClient,
            _turnConfigService);
        session.StoredPassword = password;

        // Subscribe to session events
        session.OnStateChanged += state => HandleSessionStateChanged(sessionId, state);
        session.OnDisconnected += reason => HandleSessionDisconnected(sessionId, reason);

        _sessions[sessionId] = session;
        _peerToSession[peerId] = sessionId;

        // Transport initialization is DEFERRED — host sends TransportEndpoint via signaling

        // Request connection via signaling
        await _signalingClient.RequestConnectionAsync(peerId, password);

        _logger.LogInformation("Reconnect session {SessionId} created, awaiting host response", sessionId);

        return session;
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

            case SignalingMessage.RelayReady relayReady:
                HandleRelayReady(relayReady);
                break;

            case SignalingMessage.TransportEndpoint endpoint:
                HandleTransportEndpoint(endpoint);
                break;

            case SignalingMessage.TransportConfirmed confirmed:
                HandleTransportConfirmed(confirmed);
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

            // Now that the host has approved, fire the deferred onApproved callback
            // which opens the viewer window. We do NOT open the window earlier (e.g.
            // on send) so that wrong-password or rejection paths never produce a flash.
            if (_onApprovedCallbacks.TryRemove(session.SessionId, out var cb))
            {
                try { cb(session); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "onApproved callback failed for session {SessionId}", session.SessionId);
                }
            }
        }
        else
        {
            _logger.LogWarning("Session {SessionId}: Connection rejected by peer {PeerId}",
                session.SessionId, response.TargetId);
            _onApprovedCallbacks.TryRemove(session.SessionId, out _);
            OnConnectionFailed?.Invoke(response.TargetId, "Connection rejected");
            _ = RemoveSessionAsync(session.SessionId);
        }
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

        // Server error likely means connection request failed (e.g. "Target client X is not online"
        // or "Invalid password"). Clean up any sessions that haven't established transport yet -
        // they're the ones that failed. Their pending onApproved callbacks are dropped without
        // firing so no viewer window opens for a failed attempt.
        var staleSessionIds = _sessions
            .Where(kvp => !kvp.Value.IsInitialized)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var sessionId in staleSessionIds)
        {
            _logger.LogInformation("Cleaning up stale session {SessionId} after signaling error", sessionId);
            _onApprovedCallbacks.TryRemove(sessionId, out _);
            _ = RemoveSessionAsync(sessionId);
        }

        // Surface the error to UI. JoinSession's catch handler doesn't see this - the Error
        // arrives via the signaling receive loop, not as a return value from RequestConnectionAsync.
        OnConnectionFailed?.Invoke("", error.Message);
    }

    private void HandleSessionStateChanged(string sessionId, ViewerSessionState state)
    {
        _logger.LogDebug("Session {SessionId} state changed to {State}", sessionId, state);
        OnSessionStateChanged?.Invoke(sessionId, state);
    }

    private void HandleRelayReady(SignalingMessage.RelayReady relayReady)
    {
        var session = GetSessionByPeerId(relayReady.TargetId);
        if (session == null)
        {
            _logger.LogWarning("Received RelayReady for unknown peer {PeerId}", relayReady.TargetId);
            return;
        }

        _logger.LogInformation("Session {SessionId}: Received RelayReady from {PeerId}",
            session.SessionId, relayReady.TargetId);
        _ = session.HandleRelayReadyAsync(relayReady.EncryptionNonce);
    }

    private void HandleTransportEndpoint(SignalingMessage.TransportEndpoint endpoint)
    {
        var session = GetSessionByPeerId(endpoint.TargetId);
        if (session == null)
        {
            _logger.LogWarning("Received TransportEndpoint for unknown peer {PeerId}", endpoint.TargetId);
            return;
        }

        _logger.LogInformation("Session {SessionId}: Received transport endpoint from {PeerId} ({CandidateCount} candidates)",
            session.SessionId, endpoint.TargetId, endpoint.Candidates.Length);
        _ = session.HandleTransportEndpointAsync(endpoint.Candidates);
    }

    private void HandleTransportConfirmed(SignalingMessage.TransportConfirmed confirmed)
    {
        var session = GetSessionByPeerId(confirmed.TargetId);
        if (session == null)
        {
            _logger.LogWarning("Received TransportConfirmed for unknown peer {PeerId}", confirmed.TargetId);
            return;
        }

        _logger.LogInformation("Session {SessionId}: Received TransportConfirmed from {PeerId}",
            session.SessionId, confirmed.TargetId);
        _ = session.HandleTransportConfirmedAsync();
    }

    private void HandleSessionDisconnected(string sessionId, string? reason)
    {
        _logger.LogInformation("Session {SessionId} transport disconnected: {Reason}", sessionId, reason ?? "unknown");
        // Don't remove - transport disconnects can be temporary.
        // Removal via: HandlePeerDisconnected (signaling) or user closing the tab.
    }

    private async Task SendSignalingMessage(SignalingMessage message)
    {
        await _signalingClient.SendAsync(message);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_signalingSubscribed)
        {
            _signalingClient.OnMessageReceived -= HandleSignalingMessage;
        }

        // Send Disconnect for each active session, then dispose
        foreach (var session in _sessions.Values)
        {
            try
            {
                await _signalingClient.SendAsync(new SignalingMessage.Disconnect(session.PeerId));
            }
            catch { }
            await session.DisconnectAsync();
            await session.DisposeAsync();
        }

        _sessions.Clear();
        _peerToSession.Clear();
    }
}
