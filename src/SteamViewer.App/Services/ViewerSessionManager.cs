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
    // Grace timers started when a peer Disconnected signal arrives. Gives the host's SIG-RECONNECT
    // a 5-second window to send `host_recovered` and cancel the timer; otherwise RemoveSessionAsync
    // fires when the timer expires. Closes the Railway-prune race (TODO §5 P1 "Host-recovered handshake").
    private readonly ConcurrentDictionary<string, System.Threading.Timer> _gracePeriodTimers = new();
    private const int GracePeriodMs = 5000;

    // Max-outage wall-clock cap. Per Chris's design (2026-05-22): persist sessions through
    // transient outages (transport-dead, signaling errors, Railway "not online" during host
    // recovery) but bound the persistence to 2 minutes. After that, give up + RemoveSession.
    // Started on first state->Disconnected/Error for a session. Cancelled when state->Connected.
    // Distinct from grace timer (5s, host_recovered-specific). With EPOCH 5's skip-grace logic,
    // max-outage is the ONLY safety net for the fresh-reconnect path where transport never
    // came up - without it, sessions hang indefinitely on "waiting for host."
    private readonly ConcurrentDictionary<string, System.Threading.Timer> _maxOutageTimers = new();
    private const int MaxOutageMs = 120_000;

    // Session epoch versioning. Increments every time a sessionId's underlying ViewerSession
    // is replaced (either via RemoveSessionAsync deletion OR ReconnectSessionAsync atomic-swap).
    // Timer callbacks + late-arriving signal handlers capture the epoch at creation/dispatch
    // and verify against the current value at fire time - stale callbacks (where the session
    // they were created for has been replaced) no-op.
    // Closes the race surface identified in rm chapter "Reconnect-logic FULL audit (round 2)"
    // (2026-05-22) - smoke 18:49 / 19:13 showed grace timer killing in-flight TryReconnect
    // because the timer fired with a stale view of the sessionId mapping.
    private readonly ConcurrentDictionary<string, int> _sessionEpochs = new();

    private int IncrementEpoch(string sessionId)
    {
        return _sessionEpochs.AddOrUpdate(sessionId, 1, (_, prev) => prev + 1);
    }

    private int CurrentEpoch(string sessionId)
    {
        return _sessionEpochs.TryGetValue(sessionId, out var v) ? v : 0;
    }
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
    /// Raised when a session is reconnected via ReconnectSessionAsync - same sessionId,
    /// new underlying ViewerSession object. Subscribers (e.g., RemoteViewer.razor) must
    /// rebind their event subscriptions to the new session even though the id is unchanged.
    /// Closes TODO §5 P1 "Stats overlay does not survive an adapter cycle" - BindToSessionAsync's
    /// `if (sessionId != _activeSessionId)` early-return previously skipped rebinding when
    /// the id was reused.
    /// </summary>
    public event Action<string>? OnSessionReconnected;

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
        // Bump epoch FIRST. Any in-flight timer callback or signal handler that captured
        // the previous epoch will see the bump and no-op. Guards against stale callbacks
        // acting on a removed session.
        var newEpoch = IncrementEpoch(sessionId);
        _logger.LogDebug("Session {SessionId}: RemoveSessionAsync entered, epoch bumped to {Epoch}", sessionId, newEpoch);

        // Dispose any pending grace timer for this session (no-op if no timer, e.g. user-initiated
        // close path). Prevents the timer firing later and trying to remove an already-removed session.
        if (_gracePeriodTimers.TryRemove(sessionId, out var graceTimer))
        {
            try { graceTimer.Dispose(); } catch { }
        }
        // Same for max-outage timer.
        if (_maxOutageTimers.TryRemove(sessionId, out var outageTimer))
        {
            try { outageTimer.Dispose(); } catch { }
        }

        if (_sessions.TryRemove(sessionId, out var session))
        {
            _peerToSession.TryRemove(session.PeerId, out _);

            // Send graceful-close control message via DATA CHANNEL before signaling Disconnect.
            // Lets the host distinguish a user-initiated close from a network-driven disconnect:
            // - With this message + signaling Disconnect: host runs CleanupSessionAsync with
            //   ExplicitClientDisconnect trigger -> elevation DISPOSED (no auto-reconnect).
            // - Without (e.g., transport already dead, or this send fails): host's existing
            //   SignalingPeerClaim + UDP-keepalive path eventually fires TransportDisconnect ->
            //   elevation PRESERVED (assumes a reconnect may follow).
            // Best-effort: if transport is gone we silently fall through to the signaling-only path.
            try
            {
                var disconnectingJson = System.Text.Json.JsonSerializer.Serialize(new { type = "client_disconnecting" });
                var sent = await session.SendDataAsync(disconnectingJson);
                _logger.LogInformation("Sent client_disconnecting control for session {SessionId} peer {PeerId} (success={Success})",
                    sessionId, session.PeerId, sent);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send client_disconnecting control (best effort)");
            }

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
    /// Reconnect an existing session (e.g., after elevation restart or adapter cycle).
    /// Builds a NEW ViewerSession with the same sessionId + peerId, atomically swaps the
    /// old one out, disposes the old in the background.
    ///
    /// Failure-safe: if the build fails before the swap, the OLD session stays in _sessions.
    /// Pre-fix 2026-05-21 the method called TryRemove at the TOP unconditionally; if the
    /// new connect attempt failed (e.g., during a long SIG-RECONNECT outage), the old
    /// session was already gone and subsequent retries returned null. Closes TODO §5 P1
    /// "TryReconnect attempt 1 destroys the session via TryRemove on failure."
    ///
    /// On success, fires OnSessionReconnected so subscribers can rebind to the new
    /// ViewerSession instance (closes TODO §5 P1 "Stats overlay frozen after transport rebuild").
    /// </summary>
    public async Task<ViewerSession?> ReconnectSessionAsync(string sessionId, IJSRuntime jsRuntime)
    {
        // PEEK old session, do NOT remove yet. The swap happens atomically at the bottom
        // when the new session is fully constructed. Any failure before that point leaves
        // _sessions untouched so subsequent retries see the old session and can try again.
        if (!_sessions.TryGetValue(sessionId, out var oldSession))
        {
            _logger.LogWarning("Cannot reconnect: session {SessionId} not found", sessionId);
            return null;
        }

        var peerId = oldSession.PeerId;
        var password = oldSession.StoredPassword;

        if (string.IsNullOrEmpty(password))
        {
            _logger.LogError("Cannot reconnect session {SessionId}: no stored password", sessionId);
            return null;
        }

        _logger.LogInformation("Reconnecting session {SessionId} to peer {PeerId} (old session preserved until swap)", sessionId, peerId);

        EnsureSignalingSubscribed();

        // Ensure signaling is connected
        try
        {
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
        }
        catch (Exception ex)
        {
            // Signaling rebuild failed - oldSession stays in _sessions, caller's exponential
            // backoff TryReconnect will retry. Pre-fix this path destroyed the session.
            _logger.LogWarning(ex, "Reconnect signaling phase failed for session {SessionId} - leaving old session in place for retry",
                sessionId);
            return null;
        }

        // Build NEW session AFTER signaling is confirmed up. Same sessionId preserves tab tracking.
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

        // ATOMIC SWAP: replace _sessions[sessionId] entry. ConcurrentDictionary handles this
        // atomically. Old session reference captured into oldSessionToDispose for background
        // teardown so the caller doesn't block on it.
        // Bump epoch BEFORE the swap so any in-flight grace timer / signal handler that
        // captured the previous epoch sees the bump and no-ops. Closes the smoke 18:49 race
        // where grace timer killed the in-flight reconnect session.
        var newReconnectEpoch = IncrementEpoch(sessionId);
        _logger.LogDebug("Session {SessionId}: ReconnectSessionAsync atomic-swap, epoch bumped to {Epoch}", sessionId, newReconnectEpoch);
        var oldSessionToDispose = oldSession;
        _sessions[sessionId] = session;
        _peerToSession[peerId] = sessionId;

        // Dispose old session in background (don't block the caller)
        _ = Task.Run(async () =>
        {
            try { await oldSessionToDispose.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disposing old session during reconnect"); }
        });

        // Transport initialization is DEFERRED - host sends TransportEndpoint via signaling

        // Request connection via signaling
        await _signalingClient.RequestConnectionAsync(peerId, password);

        _logger.LogInformation("Reconnect session {SessionId} created, awaiting host response", sessionId);

        // Notify subscribers (e.g., RemoteViewer.razor) to rebind to the new instance even
        // though sessionId is unchanged. Without this, event subscriptions on the old object
        // dangle and stats / control messages stop reaching the UI.
        OnSessionReconnected?.Invoke(sessionId);

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

            case SignalingMessage.HostRecovered hostRecovered:
                HandleHostRecovered(hostRecovered);
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

        var sessionId = session.SessionId;

        // SKIP grace timer when transport is already dead. The grace timer's purpose is to
        // wait 5 seconds for a host_recovered handshake after a Railway false-positive prune
        // (signaling Disconnected arrives but UDP/relay is still flowing). When transport is
        // ALREADY dead (UDP keepalive fired transport-dead), host's SIG-RECONNECT cannot
        // possibly complete in 5s for a real wifi outage (typical 30-120s). Grace timer
        // expires first, kills the in-flight TryReconnect that was actually about to succeed.
        // Smoke 2026-05-22 20:32 captured this: transport-dead at 20:32:14, reconnect at
        // 20:32:24, Railway prune at 20:32:27 started grace, 5s later session murdered.
        // The user-visible reconnect overlay + retry timer handles recovery for the
        // transport-dead case; grace timer is only meaningful when transport is alive.
        if (session.State == ViewerSessionState.Disconnected)
        {
            _logger.LogInformation("Session {SessionId}: Peer {PeerId} disconnected (reason={Reason}) but session.State=Disconnected (transport already dead) - SKIPPING grace timer; user-visible reconnect overlay handles recovery",
                sessionId, disconnected.PeerId, disconnected.Reason);
            return;
        }

        // Also SKIP grace timer when this session-instance has never confirmed transport
        // for its current epoch. Discriminator that survives ReconnectSessionAsync's atomic-swap:
        // the new ViewerSession instance is in Initializing/WaitingForOffer (not Disconnected),
        // but its HasTransportConfirmedThisEpoch is still false. A signaling Disconnected
        // arriving in this window is the Railway-prune-of-old-session, not a live-session
        // event - starting the grace timer would just murder the in-flight reconnect.
        // Smoke 2026-05-23 10:45 captured this: transport-dead 10:45:21, atomic-swap 10:45:31
        // (epoch=1, new session in Initializing), Railway prune 10:45:33 started grace,
        // 5s later session murdered, HostRecovered arrived 10:45:47 to nothing.
        // Let max-outage timer (or user-visible reconnect overlay) handle recovery.
        if (!session.HasTransportConfirmedThisEpoch)
        {
            _logger.LogInformation("Session {SessionId}: Peer {PeerId} disconnected (reason={Reason}) but HasTransportConfirmedThisEpoch=false (fresh-reconnect, transport never came up this epoch) - SKIPPING grace timer; max-outage / overlay handles recovery",
                sessionId, disconnected.PeerId, disconnected.Reason);
            return;
        }

        // Capture the session's current epoch when starting the timer. When the timer fires,
        // it verifies the epoch is still current - if the session has been replaced by
        // RemoveSessionAsync or ReconnectSessionAsync's atomic-swap, the captured epoch is
        // stale and the callback no-ops. Closes the smoke 18:49 / 19:13 race where grace timer
        // killed in-flight TryReconnect.
        var capturedEpoch = CurrentEpoch(sessionId);

        _logger.LogInformation("Session {SessionId}: Peer {PeerId} disconnected (reason={Reason}) - starting {GraceMs}ms grace timer for possible host_recovered handshake (epoch={Epoch})",
            sessionId, disconnected.PeerId, disconnected.Reason, GracePeriodMs, capturedEpoch);

        var newTimer = new System.Threading.Timer(_ =>
        {
            // Stale-callback guard: if the session this timer was created for has been
            // replaced (by ReconnectSessionAsync atomic-swap or earlier RemoveSessionAsync),
            // the timer is operating on a sessionId mapping that no longer corresponds to
            // the same ViewerSession object. NO-OP.
            var nowEpoch = CurrentEpoch(sessionId);
            if (nowEpoch != capturedEpoch)
            {
                _logger.LogInformation("Session {SessionId}: grace timer fired but is STALE (epoch was {Captured}, now {Now}) - no-op",
                    sessionId, capturedEpoch, nowEpoch);
                if (_gracePeriodTimers.TryRemove(sessionId, out var staleT))
                {
                    try { staleT.Dispose(); } catch { }
                }
                return;
            }
            if (_gracePeriodTimers.TryRemove(sessionId, out var t))
            {
                try { t.Dispose(); } catch { }
            }
            _logger.LogWarning("Session {SessionId}: grace timer expired (no host_recovered received, epoch={Epoch}) - running RemoveSessionAsync now",
                sessionId, capturedEpoch);
            _ = RemoveSessionAsync(sessionId);
        }, null, GracePeriodMs, Timeout.Infinite);

        // Cancel any previous grace timer for this session (duplicate Disconnected events possible).
        if (_gracePeriodTimers.TryRemove(sessionId, out var existing))
        {
            try { existing.Dispose(); } catch { }
            _logger.LogDebug("Session {SessionId}: cancelled previous grace timer in favor of fresh one", sessionId);
        }
        _gracePeriodTimers[sessionId] = newTimer;
    }

    /// <summary>
    /// Server forwards this from a recovered host (after its SIG-RECONNECT succeeded). If a grace
    /// timer is pending for the matching session, cancel it - the host is back, the session
    /// should survive. If no grace timer (we already removed, or no Disconnected was received),
    /// log informational and ignore.
    /// </summary>
    private void HandleHostRecovered(SignalingMessage.HostRecovered hostRecovered)
    {
        var fromPeer = hostRecovered.FromId;
        if (string.IsNullOrEmpty(fromPeer))
        {
            _logger.LogWarning("HostRecovered received with empty FromId - server-side bug? Dropping.");
            return;
        }

        var session = GetSessionByPeerId(fromPeer);
        if (session == null)
        {
            _logger.LogInformation("HostRecovered for peer {Peer}: no active session - ignoring (already removed, or never paired)",
                fromPeer);
            return;
        }

        var sessionId = session.SessionId;
        if (_gracePeriodTimers.TryRemove(sessionId, out var timer))
        {
            try { timer.Dispose(); } catch { }
            _logger.LogInformation("Session {SessionId}: HostRecovered for peer {Peer} - grace timer CANCELLED, session preserved",
                sessionId, fromPeer);
        }
        else
        {
            _logger.LogInformation("Session {SessionId}: HostRecovered for peer {Peer} arrived without a pending grace timer - re-pairing via fresh ConnectRequest",
                sessionId, fromPeer);
        }

        // Re-pair: host is back at Registered state expecting an IncomingConnection. The
        // viewer's session may have been "bound" earlier by TryReconnect but transport never
        // came up because host was offline. Send fresh ConnectRequest so host's auto-accept
        // re-establishes the pairing. Idempotent: if transport is somehow already live, the
        // duplicate IncomingConnection is handled by host's SignalingPeerClaim SUPPRESS path.
        // Closes the smoke 20:43:45 case where viewer sat idle for 54s after host SIG-RECONNECT
        // because HostRecovered handler only logged.
        var password = session.StoredPassword;
        if (string.IsNullOrEmpty(password))
        {
            _logger.LogWarning("Session {SessionId}: HostRecovered but session has no StoredPassword - cannot re-pair",
                sessionId);
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await _signalingClient.RequestConnectionAsync(fromPeer, password);
                _logger.LogInformation("Session {SessionId}: HostRecovered re-pair ConnectRequest sent to peer {Peer}",
                    sessionId, fromPeer);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session {SessionId}: HostRecovered re-pair ConnectRequest send failed", sessionId);
            }
        });
    }

    private void HandleError(SignalingMessage.Error error)
    {
        _logger.LogWarning("Signaling error: {Message}", error.Message);

        // Server error likely means connection request failed (e.g. "Target client X is not online"
        // or "Invalid password"). Clean up any sessions that haven't established transport yet -
        // they're the ones that failed. Their pending onApproved callbacks are dropped without
        // firing so no viewer window opens for a failed attempt.
        //
        // Grace window: only sweep sessions older than 5 seconds. Transient signaling errors
        // during host's SIG-RECONNECT window (e.g., Railway returns "Target not online" for ~10s
        // while host's WS is rebuilding) used to murder fresh in-flight reconnect sessions during
        // their handshake (1-3s window where IsInitialized is still false). Smoke 2026-05-22 20:08
        // captured this as a 16-click reconnect-churn loop. 5s gives the handshake time to
        // complete; truly-stuck sessions are still cleaned up.
        const int StaleSweepGraceSeconds = 5;
        var now = DateTime.UtcNow;
        var staleSessionIds = _sessions
            .Where(kvp => !kvp.Value.IsInitialized
                       && (now - kvp.Value.CreatedAt).TotalSeconds > StaleSweepGraceSeconds)
            .Select(kvp => kvp.Key)
            .ToList();

        // Cleanup awaited inside a background Task.Run so the signaling handler returns
        // promptly but disposal completes before the Task finishes. Each per-session result
        // is logged so failures are no longer silent (was: bare `_ = RemoveSessionAsync(id)`,
        // discarded exceptions, no signal that cleanup ran). Defensive fix for the leak
        // pattern documented at .claude/research/post-tidy-hardening/transport-disposal-leak.md
        // Option A bullet 3 even though the IsInitialized filter at L400 makes this
        // specific path unlikely to leak a QualityMonitor today.
        if (staleSessionIds.Count > 0)
        {
            _logger.LogInformation("Cleaning up {Count} stale sessions after signaling error", staleSessionIds.Count);
            _ = Task.Run(async () =>
            {
                foreach (var sessionId in staleSessionIds)
                {
                    _logger.LogInformation("Cleaning up stale session {SessionId} after signaling error", sessionId);
                    _onApprovedCallbacks.TryRemove(sessionId, out _);
                    try
                    {
                        await RemoveSessionAsync(sessionId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to remove stale session {SessionId} after signaling error", sessionId);
                    }
                }
            });
        }

        // Surface the error to UI. JoinSession's catch handler doesn't see this - the Error
        // arrives via the signaling receive loop, not as a return value from RequestConnectionAsync.
        OnConnectionFailed?.Invoke("", error.Message);
    }

    private void HandleSessionStateChanged(string sessionId, ViewerSessionState state)
    {
        _logger.LogDebug("Session {SessionId} state changed to {State}", sessionId, state);
        OnSessionStateChanged?.Invoke(sessionId, state);
        ManageMaxOutageTimerForState(sessionId, state);
    }

    /// <summary>
    /// Max-outage timer management driven by session state transitions:
    /// - State Connected: cancel any running max-outage timer (session is back).
    /// - State Disconnected/Error: start the timer if not already running. Wall-clock
    ///   from the FIRST outage - subsequent Disconnected events don't reset the budget.
    /// - Other states (Connecting, WaitingForOffer): no-op.
    /// With EPOCH 5's skip-grace logic, this is the ONLY teardown for the fresh-reconnect
    /// path where transport never came up this epoch.
    /// </summary>
    private void ManageMaxOutageTimerForState(string sessionId, ViewerSessionState state)
    {
        // Guard: if the session has already been removed from _sessions (e.g., we're
        // in the middle of RemoveSessionAsync and a Disconnected state event is firing
        // during teardown), don't start a max-outage timer for a session that no longer
        // exists. Otherwise the timer fires 120s later and tries to RemoveSessionAsync
        // a session that's already gone - harmless but noisy. Smoke 2026-05-23 12:03:55
        // captured this: max-outage timer STARTED log fired immediately after Removed
        // session log.
        if (!_sessions.ContainsKey(sessionId))
        {
            return;
        }

        if (state == ViewerSessionState.Connected)
        {
            CancelMaxOutageTimer(sessionId);
            return;
        }
        if (IsOutageState(state) && !_maxOutageTimers.ContainsKey(sessionId))
        {
            StartMaxOutageTimer(sessionId);
        }
    }

    private static bool IsOutageState(ViewerSessionState state)
        => state == ViewerSessionState.Disconnected || state == ViewerSessionState.Error;

    private void CancelMaxOutageTimer(string sessionId)
    {
        if (_maxOutageTimers.TryRemove(sessionId, out var t))
        {
            try { t.Dispose(); } catch { }
            _logger.LogDebug("Session {SessionId}: state Connected - max-outage timer cancelled", sessionId);
        }
    }

    private static void DisposeAllTimers(ConcurrentDictionary<string, System.Threading.Timer> timers)
    {
        foreach (var key in timers.Keys.ToList())
        {
            if (timers.TryRemove(key, out var t))
            {
                try { t.Dispose(); } catch { }
            }
        }
    }

    private void StartMaxOutageTimer(string sessionId)
    {
        // Max-outage timer is wall-clock from the FIRST outage. It must SURVIVE
        // ReconnectSessionAsync's atomic-swap epoch bumps - the user is still in the same
        // outage budget regardless of how many TryReconnect retries fire. Grace-timer-style
        // epoch-stale guards are WRONG here (they'd invalidate the timer on every retry).
        // The legitimate cancel paths are:
        // - state Connected -> ManageMaxOutageTimerForState removes the timer
        // - RemoveSessionAsync removes the timer (user closed window, etc.)
        // - DisposeAsync (app shutdown) disposes all timers
        var timer = new System.Threading.Timer(_ =>
        {
            if (_maxOutageTimers.TryRemove(sessionId, out var t))
            {
                try { t.Dispose(); } catch { }
            }
            _logger.LogWarning("Session {SessionId}: max-outage timer EXPIRED after {Ms}ms - giving up on recovery, running RemoveSessionAsync",
                sessionId, MaxOutageMs);
            _ = RemoveSessionAsync(sessionId);
        }, null, MaxOutageMs, Timeout.Infinite);

        if (!_maxOutageTimers.TryAdd(sessionId, timer))
        {
            // Lost the race - another thread added one. Dispose ours.
            try { timer.Dispose(); } catch { }
            return;
        }
        _logger.LogInformation("Session {SessionId}: max-outage timer STARTED ({Ms}ms wall-clock cap on persistence)",
            sessionId, MaxOutageMs);
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

        // Cancel + dispose any outstanding grace + max-outage timers so they don't fire
        // during/after teardown.
        DisposeAllTimers(_gracePeriodTimers);
        DisposeAllTimers(_maxOutageTimers);

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
