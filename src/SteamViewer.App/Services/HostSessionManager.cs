using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SteamViewer.App.Services.Models;
using SteamViewer.Client.Core.Capture;
using SteamViewer.Client.Core.Elevation;
using SteamViewer.Client.Core.Network;
using SteamViewer.Common.Protocol;

namespace SteamViewer.App.Services;

/// <summary>
/// Why CleanupSessionAsync is being called - drives whether elevation is preserved
/// for an expected reconnect or disposed for a graceful disconnect.
///
/// Only two values are defined because only two code paths route through
/// CleanupSessionAsync today: HostSession.OnDisconnected (transport layer) and
/// HandleSignalingDisconnected (signaling layer). Explicit user disconnect and
/// app shutdown bypass CleanupSessionAsync entirely (DisconnectAsync / DisposeAsync
/// do their own teardown). Add new values only when a new caller is wired up.
/// </summary>
internal enum CleanupTrigger
{
    /// <summary>
    /// The data transport (UDP / relay) reported disconnect via HostSession.OnDisconnected.
    /// Elevation is preserved because the peer is likely to reconnect on a fresh transport.
    /// </summary>
    TransportDisconnect,

    /// <summary>
    /// Signaling layer notified us "peer disconnected" via HandleSignalingDisconnected.
    /// The claim may be stale - if the peer's signaling WS dropped while data transport
    /// is still flowing, the cleanup decision checks _hostSession.IsDataChannelReady
    /// and SUPPRESSES the cleanup entirely (early-return) when transport is alive,
    /// leaving the working session running. If transport later dies for real, the
    /// TransportDisconnect path handles it. For user-initiated graceful close, the
    /// ExplicitClientDisconnect trigger is the right path (in-band confirmation via
    /// a data-channel control message) - not this one.
    /// </summary>
    SignalingPeerClaim,

    /// <summary>
    /// Viewer sent an explicit `client_disconnecting` control message via the data channel
    /// before tearing down. Distinct from SignalingPeerClaim (which can be a stale Railway
    /// notification) because here we have direct in-band confirmation that the peer is
    /// closing. Cleanup proceeds with elevation DISPOSED (no auto-reconnect expected since
    /// the user gracefully left). Closes the 2026-05-21 regression where graceful disconnect
    /// fell into the SignalingPeerClaim+transport-alive path and got SUPPRESSED until UDP
    /// keepalive timed out (~9s delay + wrong elevation preservation).
    /// </summary>
    ExplicitClientDisconnect
}

public sealed class HostSessionManager : IAsyncDisposable
{
    private readonly ILogger<HostSessionManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IConfiguration _configuration;
    private readonly SignalingClient _signalingClient;
    private readonly IServiceProvider _serviceProvider;
    private readonly TurnConfigService? _turnConfigService;

    private readonly SemaphoreSlim _cleanupLock = new(1, 1);

    private HostSession? _hostSession;
    private IElevationService? _preservedElevationService;
    private string _connectedPeerId = "";
    private string _incomingPeerId = "";
    // Separate from _connectedPeerId (which is cleared on every cleanup). Tracks the peer
    // we were LAST paired with, so on SIG-RECONNECT we can send `host_recovered` and let the
    // viewer cancel its grace timer. Cleared only on explicit user disconnect / app shutdown
    // / pairing with a different peer - NOT on transport-disconnect cleanup. See
    // plans/close-out-followups.md.
    private string? _lastPairedPeerId;

    // Host-side max-outage cooldown (Bug G fix). Started when CleanupSessionAsync runs with
    // preserveElevation=true. On expiry (120s), drops the preserved elevation + clears
    // _isReconnect/_lastPairedPeerId/_pendingReconnectPeerId so a subsequent
    // IncomingConnection from the same clientId goes through full consent (no auto-accept,
    // no preserved elevation, fresh password). Closes the security hole where host kept
    // admin warm indefinitely. Symmetric to viewer's 120s max-outage in
    // ViewerSessionManager. Cancelled on legitimate reconnect (AcceptConnectionAsync),
    // explicit DisconnectAsync, or DisposeAsync.
    private System.Threading.Timer? _hostMaxOutageTimer;
    private const int HostMaxOutageMs = 120_000;
    private bool _isReconnect;
    private bool _wasSharingBeforeDisconnect;
    private string? _pendingReconnectPeerId;
    private bool _signalingSubscribed;
    private bool _disposed;

    private string _hostClientId = "";
    private string _hostPasswordHash = "";
    private IJSRuntime? _jsRuntime;

    public ConnectionState State { get; private set; } = ConnectionState.Idle;
    public HostSession? CurrentSession => _hostSession;
    public string ConnectedPeerId => _connectedPeerId;
    public string IncomingPeerId => _incomingPeerId;
    public bool IsDataChannelReady => _hostSession?.IsDataChannelReady ?? false;
    public bool IsSharingScreen => _hostSession?.IsSharingScreen ?? false;
    public bool IsPeerSharingScreen => _hostSession?.IsPeerSharingScreen ?? false;

    public event Action<ConnectionState>? OnStateChanged;
    public event Action<string>? OnIncomingConnection;
    public event Action<string>? OnError;
    public event Action? OnSessionReady;
    public event Action? OnSessionDisposed;
    public event Action? OnPeerSharingChanged;
    public event Action<bool>? OnLocalSharingChanged;

    public HostSessionManager(
        ILogger<HostSessionManager> logger,
        ILoggerFactory loggerFactory,
        IConfiguration configuration,
        SignalingClient signalingClient,
        IServiceProvider serviceProvider,
        TurnConfigService? turnConfigService = null)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _configuration = configuration;
        _signalingClient = signalingClient;
        _serviceProvider = serviceProvider;
        _turnConfigService = turnConfigService;
    }

    public void Initialize(string clientId, string passwordHash, IJSRuntime jsRuntime, bool isReconnect = false)
    {
        _hostClientId = clientId;
        _hostPasswordHash = passwordHash;
        _jsRuntime = jsRuntime;
        _isReconnect = isReconnect;
        EnsureSignalingSubscribed();
        _logger.LogInformation("HostSessionManager initialized for client {ClientId}, reconnect={IsReconnect}", clientId, isReconnect);
    }

    public async Task AcceptConnectionAsync()
    {
        if (string.IsNullOrEmpty(_incomingPeerId))
        {
            _logger.LogWarning("AcceptConnection called but no incoming peer");
            return;
        }

        try
        {
            await _signalingClient.RespondToConnectionAsync(_incomingPeerId, true);
            _connectedPeerId = _incomingPeerId;
            _lastPairedPeerId = _incomingPeerId; // remember across cleanups for host_recovered
            SetState(ConnectionState.Connected);

            var elevationService = _preservedElevationService;
            if (elevationService != null)
            {
                _logger.LogInformation("Reusing preserved elevation service (admin={Admin}, system={System})",
                    elevationService.IsAdminConnected, elevationService.IsSystemConnected);
                _preservedElevationService = null;
            }
            else
            {
                elevationService = _serviceProvider.GetService<IElevationService>();
            }

            var monitorEnumerator = _serviceProvider.GetService<IMonitorEnumerator>();
            var screenCapture = _serviceProvider.GetService<IScreenCapture>();
#if WINDOWS
            var frameBridge = _serviceProvider.GetService<NativeFrameBridge>();
#endif
            // Idempotent guard: if a prior accept left a _hostSession in place (per the
            // 2026-05-20 dual-click leak where IncomingConnection arrived before the matching
            // Disconnect and the new accept overwrote the live session), dispose it before
            // reassigning so its transport graph (UDP socket, BCL TimerQueue timers, video
            // send task) is properly torn down instead of orphaned and rooted forever.
            if (_hostSession != null)
            {
                _logger.LogWarning("AcceptConnectionAsync called with non-null _hostSession (peer was {OldPeer}, accepting {NewPeer}) - disposing prior instance to close the 2026-05-20 leak path",
                    _connectedPeerId, _incomingPeerId);
                var leakGuardSw = System.Diagnostics.Stopwatch.StartNew();
                await _hostSession.DisposeAsync();
                leakGuardSw.Stop();
                _hostSession = null;
                _logger.LogInformation("Leak-guard: prior _hostSession disposed in {Elapsed}ms before AcceptConnectionAsync continued",
                    leakGuardSw.ElapsedMilliseconds);
            }

            _hostSession = new HostSession(
                _connectedPeerId,
                _jsRuntime,
                _loggerFactory,
                _serviceProvider.GetRequiredService<IInputInjector>(),
                _configuration,
                msg => _signalingClient.SendAsync(msg),
                signalingClient: _signalingClient,
                elevationService: elevationService,
                monitorEnumerator: monitorEnumerator,
                screenCapture: screenCapture,
#if WINDOWS
                frameBridge: frameBridge,
#endif
                turnConfigService: _turnConfigService,
                hostClientId: _hostClientId,
                hostPasswordHash: _hostPasswordHash);

            if (_isReconnect && _wasSharingBeforeDisconnect)
            {
                _hostSession.AutoShareOnReady = true;
                _logger.LogInformation("AutoShareOnReady set: transport-disconnect reconnect resuming a previously-sharing session");
            }
            else if (_isReconnect)
            {
                _logger.LogInformation("AutoShareOnReady NOT set: prior session never reached sharing state, requiring fresh consent");
            }
            _isReconnect = false;
            _wasSharingBeforeDisconnect = false;
            // Viewer reconnected within window: cancel host-side max-outage so it doesn't
            // fire later and drop newly-restored elevation.
            CancelHostMaxOutageTimer();

            _hostSession.OnStateChanged += _ => OnSessionReady?.Invoke();
            _hostSession.OnReady += () => OnSessionReady?.Invoke();
            _hostSession.OnDisconnected += reason => _ = CleanupSessionAsync(reason, CleanupTrigger.TransportDisconnect);
            _hostSession.OnClientDisconnecting += reason => _ = CleanupSessionAsync(reason, CleanupTrigger.ExplicitClientDisconnect);
            _hostSession.OnPeerSharingChanged += _ => OnPeerSharingChanged?.Invoke();
            _hostSession.OnLocalSharingChanged += sharing => OnLocalSharingChanged?.Invoke(sharing);

            await _hostSession.InitializeAsync();

            _logger.LogInformation("Host session created for peer {PeerId}", _connectedPeerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to accept connection");
            OnError?.Invoke(ex.Message);
        }
    }

    public async Task RejectConnectionAsync()
    {
        if (string.IsNullOrEmpty(_incomingPeerId)) return;

        await _signalingClient.RespondToConnectionAsync(_incomingPeerId, false);
        _incomingPeerId = "";
        SetState(ConnectionState.Registered);
    }

    public async Task DisconnectAsync()
    {
        // Explicit user-initiated close - cancel any pending host max-outage timer so it
        // doesn't fire post-shutdown and try to drop already-gone state.
        CancelHostMaxOutageTimer();

        // Send graceful-close control message via DATA CHANNEL before signaling Disconnect.
        // Mirror of viewer's client_disconnecting (commit 73bc168). Lets the viewer
        // distinguish a host-initiated graceful close from a network-driven disconnect, so
        // the viewer can close its window without flashing the reconnect overlay or
        // bouncing through "waiting for host" state.
        // Best-effort: if transport is gone we fall through to the signaling-only path.
        if (_hostSession != null)
        {
            try
            {
                var hostDisconnectingJson = System.Text.Json.JsonSerializer.Serialize(new { type = "host_disconnecting" });
                var sent = await _hostSession.SendDataAsync(hostDisconnectingJson);
                _logger.LogInformation("Sent host_disconnecting control to peer {PeerId} (success={Success})",
                    _connectedPeerId, sent);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send host_disconnecting control (best effort)");
            }
        }

        if (!string.IsNullOrEmpty(_connectedPeerId))
        {
            await _signalingClient.DisconnectFromPeerAsync(_connectedPeerId);
        }

        if (_hostSession != null)
        {
            await _hostSession.DisposeAsync();
            _hostSession = null;
        }

        if (_preservedElevationService != null)
        {
            await _preservedElevationService.DisposeAsync();
            _preservedElevationService = null;
        }

        _connectedPeerId = "";
        _incomingPeerId = "";
        // User-initiated close: forget the previously-paired peer so a later SIG-RECONNECT
        // (e.g., after a network blip while idle) doesn't send host_recovered for a peer the
        // user explicitly disconnected from. Transport-disconnect cleanup at CleanupSessionAsync
        // intentionally PRESERVES _lastPairedPeerId so the recovery handshake can fire.
        _lastPairedPeerId = null;
        SetState(ConnectionState.Registered);
        OnSessionDisposed?.Invoke();
    }

    private async Task CleanupSessionAsync(string? reason, CleanupTrigger trigger)
    {
        if (!await _cleanupLock.WaitAsync(0))
        {
            _logger.LogDebug("CleanupSession skipped - already in progress (reason={Reason}, trigger={Trigger})",
                reason, trigger);
            return;
        }

        try
        {
            // Stale signaling claim + live data transport: SUPPRESS cleanup entirely.
            //
            // Mechanism: signaling layer says peer is gone, but UDP/relay is still flowing.
            // The claim is stale (peer's signaling WS dropped while data path kept working,
            // and Railway delivers a delayed "peer_disconnected" notification AFTER the new
            // session has been re-handshaked over a fresh WS). Tearing down a working session
            // in this case kills active video.
            //
            // History: 2026-05-20 a half-fix shipped that only "preserved elevation" while
            // still disposing the session. Today's smoke (2026-05-21, adapter off/restart)
            // caught that bug - bilateral UDP completed at 17:35:24.382, video flowed for
            // ~600ms, then this path fired and disposed the live host session at 17:35:24.831.
            // This commit replaces the half-fix with a true early-return.
            //
            // If transport actually dies later, the TransportDisconnect path will fire and
            // handle the cleanup correctly (preserve elevation, dispose session, transition
            // to Registered for auto-accept on reconnect).
            var transportStillAlive = _hostSession?.IsDataChannelReady ?? false;
            if (trigger == CleanupTrigger.SignalingPeerClaim && transportStillAlive)
            {
                _logger.LogWarning("SignalingPeerClaim with live transport (IsDataChannelReady=true) - " +
                    "SUPPRESSING cleanup. Session stays alive; signaling claim treated as stale " +
                    "(peer's signaling WS dropped while UDP/relay kept flowing). " +
                    "If transport actually dies later, the TransportDisconnect path will handle it. " +
                    "reason={Reason}", reason);
                return;
            }

            // Remaining cases: TransportDisconnect (preserve elevation for auto-reconnect)
            // OR SignalingPeerClaim with DEAD transport (true graceful disconnect, dispose elevation).
            var preserveElevation = trigger == CleanupTrigger.TransportDisconnect;
            _logger.LogDebug("CleanupSession decision: trigger={Trigger}, transportAlive={Alive}, preserveElevation={Preserve}",
                trigger, transportStillAlive, preserveElevation);

            _logger.LogWarning("Cleaning up host session: reason={Reason}, trigger={Trigger}, preserveElevation={Preserve}",
                reason, trigger, preserveElevation);

            if (_hostSession != null)
            {
                _wasSharingBeforeDisconnect = _hostSession.IsSharingScreen;
                var detached = _hostSession.DetachElevationService();

                // Dispose the host session FIRST so its transport (and keepalive timer)
                // is killed promptly. Otherwise a stale keepalive timer can fire "dead"
                // from the old transport while we are still awaiting elevation disposal,
                // and that fires HostSession.OnDisconnected on the just-replaced session.
                var sessionDisposeSw = System.Diagnostics.Stopwatch.StartNew();
                await _hostSession.DisposeAsync();
                sessionDisposeSw.Stop();
                _logger.LogInformation("Host session disposed in {Elapsed}ms (transport + timers killed)",
                    sessionDisposeSw.ElapsedMilliseconds);
                _hostSession = null;

                if (detached != null)
                {
                    if (preserveElevation)
                    {
                        _preservedElevationService = detached;
                        _logger.LogInformation("Elevation service preserved for reconnect (admin={Admin}, system={System})",
                            detached.IsAdminConnected, detached.IsSystemConnected);
                        // Start the security cooldown: if viewer doesn't reconnect within
                        // 120s, drop preserved elevation + clear _isReconnect so a future
                        // IncomingConnection (potentially from a different actor reusing the
                        // clientId) requires fresh consent. Bug G fix.
                        StartHostMaxOutageTimer();
                    }
                    else
                    {
                        _logger.LogInformation("Disposing elevation service (graceful disconnect, not preserving) (admin={Admin}, system={System})",
                            detached.IsAdminConnected, detached.IsSystemConnected);
                        var elevDisposeSw = System.Diagnostics.Stopwatch.StartNew();
                        try
                        {
                            await detached.DisposeAsync();
                            elevDisposeSw.Stop();
                            _logger.LogInformation("Elevation service disposed in {Elapsed}ms",
                                elevDisposeSw.ElapsedMilliseconds);
                        }
                        catch (Exception ex)
                        {
                            elevDisposeSw.Stop();
                            _logger.LogWarning(ex, "Elevation service disposal threw after {Elapsed}ms - cleanup will continue but elevation may be in an unclean state",
                                elevDisposeSw.ElapsedMilliseconds);
                        }
                    }
                }
            }

            _connectedPeerId = "";
            // Auto-accept the next IncomingConnection only when we preserved elevation.
            // Same boundary as before for the pure transport-disconnect case; new case is
            // the stale-signaling-with-live-UDP path, which also expects a reconnect.
            _isReconnect = preserveElevation;
            SetState(ConnectionState.Registered);
            OnSessionDisposed?.Invoke();

            if (!string.IsNullOrEmpty(_pendingReconnectPeerId))
            {
                _incomingPeerId = _pendingReconnectPeerId;
                _pendingReconnectPeerId = null;
                _logger.LogInformation("Processing deferred reconnect from {PeerId}, isReconnect={IsReconnect}",
                    _incomingPeerId, _isReconnect);

                if (_isReconnect)
                {
                    await AcceptConnectionAsync();
                }
                else
                {
                    SetState(ConnectionState.Connecting);
                    OnIncomingConnection?.Invoke(_incomingPeerId);
                }
            }
        }
        finally
        {
            _cleanupLock.Release();
        }
    }

    private void EnsureSignalingSubscribed()
    {
        if (_signalingSubscribed) return;

        _signalingClient.OnMessageReceived += HandleSignalingMessage;
        _signalingClient.OnSignalingReconnected += HandleSignalingReconnected;
        _signalingSubscribed = true;
        _logger.LogDebug("HostSessionManager subscribed to signaling messages + reconnect event");
    }

    /// <summary>
    /// Fires after SignalingClient's SIG-RECONNECT loop re-registers successfully on a fresh WS.
    /// If we were paired with a peer before the outage, send `host_recovered` to that peer so
    /// the viewer can cancel any grace timer it started from the Railway stale-WS prune
    /// Disconnected notification. Fire-and-forget; if the viewer is already gone, the message
    /// is dropped by the server's forward-to-target helper.
    /// </summary>
    private void HandleSignalingReconnected()
    {
        var peer = _lastPairedPeerId;
        if (string.IsNullOrEmpty(peer))
        {
            _logger.LogDebug("OnSignalingReconnected fired but no previously-paired peer to notify");
            return;
        }
        _logger.LogInformation("OnSignalingReconnected: notifying previously-paired peer {Peer} via host_recovered",
            peer);
        _ = Task.Run(async () =>
        {
            try { await _signalingClient.SendHostRecoveredAsync(peer); }
            catch (Exception ex) { _logger.LogWarning(ex, "host_recovered send failed for peer {Peer}", peer); }
        });
    }

    private void HandleSignalingMessage(SignalingMessage message)
    {
        switch (message)
        {
            case SignalingMessage.IncomingConnection incoming:
                HandleIncomingConnection(incoming);
                break;

            case SignalingMessage.TransportEndpoint endpoint:
                if (_hostSession != null)
                {
                    _logger.LogInformation("Routing TransportEndpoint to HostSession ({CandidateCount} candidates)",
                        endpoint.Candidates.Length);
                    _ = _hostSession.HandleViewerTransportEndpointAsync(endpoint.Candidates);
                }
                break;

            case SignalingMessage.TransportConfirmed:
                if (_hostSession != null)
                {
                    _logger.LogInformation("Routing TransportConfirmed to HostSession");
                    _ = _hostSession.HandleTransportConfirmedAsync();
                }
                break;

            case SignalingMessage.Disconnected disconnected:
                HandleSignalingDisconnected(disconnected);
                break;

            case SignalingMessage.Error error:
                _logger.LogWarning("Signaling error: {Message}", error.Message);
                OnError?.Invoke(error.Message);
                break;
        }
    }

    private void HandleIncomingConnection(SignalingMessage.IncomingConnection incoming)
    {
        if (_cleanupLock.CurrentCount == 0)
        {
            _logger.LogInformation("IncomingConnection from {PeerId} deferred - cleanup in progress", incoming.FromId);
            _pendingReconnectPeerId = incoming.FromId;
            return;
        }

        _incomingPeerId = incoming.FromId;

        if (_isReconnect)
        {
            _logger.LogInformation("Auto-accepting reconnect from {PeerId}", incoming.FromId);
            _ = AcceptConnectionAsync();
            return;
        }

        SetState(ConnectionState.Connecting);
        OnIncomingConnection?.Invoke(incoming.FromId);
    }

    private void HandleSignalingDisconnected(SignalingMessage.Disconnected disconnected)
    {
        if (disconnected.PeerId != _connectedPeerId)
        {
            _logger.LogDebug("Ignoring disconnect from {PeerId} (connected to {ConnectedPeer})",
                disconnected.PeerId, _connectedPeerId);
            return;
        }

        _logger.LogWarning("Peer {PeerId} disconnected via signaling: {Reason}",
            disconnected.PeerId, disconnected.Reason);
        _ = CleanupSessionAsync(disconnected.Reason, CleanupTrigger.SignalingPeerClaim);
    }

    private void SetState(ConnectionState newState)
    {
        if (State == newState) return;
        _logger.LogDebug("HostSessionManager state: {OldState} -> {NewState}", State, newState);
        State = newState;
        OnStateChanged?.Invoke(newState);
    }

    private void StartHostMaxOutageTimer()
    {
        // Cancel any existing one (re-entry safety - e.g. two TransportDisconnect cleanups
        // in quick succession; only the latest 120s budget applies).
        CancelHostMaxOutageTimer();
        _hostMaxOutageTimer = new System.Threading.Timer(_ =>
        {
            _ = OnHostMaxOutageExpired();
        }, null, HostMaxOutageMs, Timeout.Infinite);
        _logger.LogInformation("Host max-outage timer STARTED ({Ms}ms) - after this window the preserved elevation + reconnect-friendly state will be dropped",
            HostMaxOutageMs);
    }

    private void CancelHostMaxOutageTimer()
    {
        var t = _hostMaxOutageTimer;
        _hostMaxOutageTimer = null;
        if (t != null)
        {
            try { t.Dispose(); } catch { }
            _logger.LogDebug("Host max-outage timer CANCELLED");
        }
    }

    private async Task OnHostMaxOutageExpired()
    {
        // No viewer reconnected within budget. Drop security-sensitive preserved state so a
        // subsequent IncomingConnection from same clientId goes through full consent (no
        // auto-accept, no preserved elevation, fresh password + fresh elevation prompt).
        _hostMaxOutageTimer = null;
        _logger.LogWarning("Host max-outage timer EXPIRED after {Ms}ms - dropping preserved elevation + clearing _isReconnect/_lastPairedPeerId. Subsequent IncomingConnection from this clientId will require fresh consent.",
            HostMaxOutageMs);

        if (_preservedElevationService != null)
        {
            var detached = _preservedElevationService;
            _preservedElevationService = null;
            try
            {
                await detached.DisposeAsync();
                _logger.LogInformation("Host max-outage: preserved elevation service disposed (security cooldown)");
            }
            catch (Exception ex)
            {
                // The helper force-kill now runs in ElevatedHelperClient.CleanupAsync's finally (B5),
                // and the parent-death watchdog reaps any survivor on host exit - so a throw here no
                // longer means a lingering privileged helper, only that graceful teardown was partial.
                _logger.LogWarning(ex, "Host max-outage: elevation service disposal threw - force-kill/watchdog backstops still apply");
            }
        }

        _isReconnect = false;
        _wasSharingBeforeDisconnect = false;
        _lastPairedPeerId = null;
        // Pending reconnect peer becomes stale too - if a reconnect was deferred during
        // cleanup and we hit max-outage, that peer's claim is no longer trusted.
        _pendingReconnectPeerId = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Cancel any pending host max-outage timer so it can't fire post-dispose.
        CancelHostMaxOutageTimer();

        if (_signalingSubscribed)
        {
            _signalingClient.OnMessageReceived -= HandleSignalingMessage;
        }

        // App-shutdown path: still treat as graceful from the viewer's POV - the user
        // closed the host app. Send host_disconnecting before tearing down (best-effort,
        // same shape as DisconnectAsync above). Lets the viewer skip the reconnect overlay.
        if (_hostSession != null)
        {
            try
            {
                var hostDisconnectingJson = System.Text.Json.JsonSerializer.Serialize(new { type = "host_disconnecting" });
                var sent = await _hostSession.SendDataAsync(hostDisconnectingJson);
                _logger.LogInformation("Sent host_disconnecting control on shutdown to peer {PeerId} (success={Success})",
                    _connectedPeerId, sent);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send host_disconnecting control on shutdown (best effort)");
            }
        }

        if (_hostSession != null)
        {
            await _hostSession.DisposeAsync();
            _hostSession = null;
        }

        if (_preservedElevationService != null)
        {
            await _preservedElevationService.DisposeAsync();
            _preservedElevationService = null;
        }

        _cleanupLock.Dispose();
    }
}
