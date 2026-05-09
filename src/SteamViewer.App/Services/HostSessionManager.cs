using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SteamViewer.App.Services.Models;
using SteamViewer.Client.Core.Capture;
using SteamViewer.Client.Core.Elevation;
using SteamViewer.Client.Core.Network;
using SteamViewer.Common.Protocol;

namespace SteamViewer.App.Services;

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

            _hostSession.OnStateChanged += _ => OnSessionReady?.Invoke();
            _hostSession.OnReady += () => OnSessionReady?.Invoke();
            _hostSession.OnDisconnected += reason => _ = CleanupSessionAsync(reason, isTransportDisconnect: true);
            _hostSession.OnPeerSharingChanged += _ => OnPeerSharingChanged?.Invoke();

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
        SetState(ConnectionState.Registered);
        OnSessionDisposed?.Invoke();
    }

    private async Task CleanupSessionAsync(string? reason, bool isTransportDisconnect)
    {
        if (!await _cleanupLock.WaitAsync(0))
        {
            _logger.LogDebug("CleanupSession skipped - already in progress (reason={Reason}, transport={IsTransport})",
                reason, isTransportDisconnect);
            return;
        }

        try
        {
            _logger.LogWarning("Cleaning up host session: reason={Reason}, transport={IsTransport}",
                reason, isTransportDisconnect);

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
                    if (isTransportDisconnect)
                    {
                        _preservedElevationService = detached;
                        _logger.LogInformation("Elevation service preserved for reconnect (admin={Admin}, system={System})",
                            detached.IsAdminConnected, detached.IsSystemConnected);
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
                            _logger.LogError(ex, "Elevation service disposal threw after {Elapsed}ms — cleanup will continue but elevation may be in an unclean state",
                                elevDisposeSw.ElapsedMilliseconds);
                        }
                    }
                }
            }

            _connectedPeerId = "";
            _isReconnect = isTransportDisconnect;
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
        _signalingSubscribed = true;
        _logger.LogDebug("HostSessionManager subscribed to signaling messages");
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
        _ = CleanupSessionAsync(disconnected.Reason, isTransportDisconnect: false);
    }

    private void SetState(ConnectionState newState)
    {
        if (State == newState) return;
        _logger.LogDebug("HostSessionManager state: {OldState} -> {NewState}", State, newState);
        State = newState;
        OnStateChanged?.Invoke(newState);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_signalingSubscribed)
        {
            _signalingClient.OnMessageReceived -= HandleSignalingMessage;
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
