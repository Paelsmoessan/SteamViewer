using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SteamViewer.App.Services;
using SteamViewer.Client.Core.Network;
using SteamViewer.Client.Core.Video;
using SteamViewer.Common.Protocol;
using SteamViewer.Platform.Windows.Clipboard;
using System.Collections.Concurrent;
using System.Text.Json;

namespace SteamViewer.App.Services.Models;

/// <summary>
/// Represents a single viewer session with a remote peer.
/// Encapsulates TCP transport, FFmpeg decoding, video rendering, and input handling.
/// </summary>
public sealed partial class ViewerSession : IAsyncDisposable
{
    private IJSRuntime _jsRuntime;
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IConfiguration _configuration;
    private readonly TurnConfigService? _turnConfigService;
    private readonly Func<SignalingMessage, Task> _sendSignaling;
    private readonly SignalingClient _signalingClient;
    private DotNetObjectReference<ViewerSession>? _dotNetRef;
    private bool _disposed;

    // Lossless settle — request QOI snapshot when input is idle and screen is static
    // (shared between Input and Video partials; stays in core per field-sharing rule).
    private DateTime _lastInputTime = DateTime.UtcNow;
    private bool _losslessActive;
    private bool _losslessRequestPending;

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
    /// Whether transport is connected and ready.
    /// </summary>
    public bool IsInitialized => _transport?.IsConnected ?? false;

    /// <summary>
    /// Wall-clock timestamp when this session was constructed. Used by
    /// ViewerSessionManager.HandleError to give freshly-created sessions a grace window
    /// (5 seconds) before the stale-session sweep can kill them. Without this, a transient
    /// signaling error ("Target not online") during host's recovery window murders a session
    /// that's still mid-handshake - the 16-click reconnect-churn observed 2026-05-22 20:08.
    /// </summary>
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    /// <summary>
    /// True once this session-instance has observed a successful transport handshake
    /// (state == "connected" or "udp-upgraded"). Used by ViewerSessionManager.HandlePeerDisconnected
    /// to discriminate "live-session prune" (start grace timer for host_recovered) from
    /// "fresh-reconnect that never came up" (skip grace, let max-outage handle).
    /// ReconnectSessionAsync's atomic-swap creates a NEW ViewerSession instance with this
    /// flag back at false - the new instance hasn't confirmed transport yet for the new
    /// epoch. Closes the smoke 2026-05-23 10:45 case where grace timer killed the post-swap
    /// session before host's SIG-RECONNECT could complete.
    /// </summary>
    public bool HasTransportConfirmedThisEpoch { get; private set; }

    /// <summary>
    /// Set by HandleTransportStateChanged on the first transport-up signal. Cross-partial
    /// internal write surface (same class, different file).
    /// </summary>
    internal void MarkTransportConfirmed() => HasTransportConfirmedThisEpoch = true;

    /// <summary>
    /// Whether the remote peer is sharing their screen.
    /// </summary>
    public bool IsPeerSharing { get; private set; }

    /// <summary>
    /// Raised when the session state changes.
    /// </summary>
    public event Action<ViewerSessionState>? OnStateChanged;

    /// <summary>
    /// Raised when the remote peer starts/stops sharing.
    /// </summary>
    public event Action<bool>? OnPeerSharingChanged;

    /// <summary>
    /// Raised when the transport connects (ready for input).
    /// </summary>
    public event Action? OnReady;

    /// <summary>
    /// Raised when the session disconnects or errors.
    /// </summary>
    public event Action<string?>? OnDisconnected;

    /// <summary>
    /// Raised when the host sends an explicit `host_disconnecting` control message via
    /// the data channel - signals a graceful peer-initiated close, distinct from a
    /// network-driven disconnect. Subscribers should close the session UI without
    /// showing the reconnect overlay.
    /// </summary>
    public event Action<string>? OnPeerDisconnecting;

    /// <summary>
    /// Raised when a control message is received from the host.
    /// </summary>
    public event Action<string, string?>? OnControlMessage;

    /// <summary>
    /// Raised when the Secure Desktop state changes on the host.
    /// </summary>
    public event Action<bool>? OnSecureDesktopStateChanged;

    /// <summary>
    /// Whether the Secure Desktop is currently active on the host.
    /// </summary>
    public bool IsSecureDesktopActive { get; private set; }

    /// <summary>
    /// The host's monitor layout.
    /// </summary>
    public List<MonitorInfo>? HostMonitors { get; private set; }

    /// <summary>
    /// Which monitor the host is actively capturing.
    /// </summary>
    public int ActiveMonitorId { get; private set; }

    /// <summary>
    /// Whether the host is running elevated.
    /// </summary>
    public bool? IsHostElevated { get; private set; }

    /// <summary>
    /// Whether the host has SYSTEM-level helper connected.
    /// </summary>
    public bool? IsHostSystemLevel { get; private set; }

    /// <summary>
    /// Stored password for reconnection.
    /// </summary>
    public string? StoredPassword { get; set; }

    /// <summary>The viewer's own clientId (registered with the signaling server).
    /// Required for fetching TURN credentials, which are now bound to the registered clientId.</summary>
    private readonly string _localClientId;

    public ViewerSession(
        string sessionId,
        string peerId,
        string localClientId,
        IJSRuntime jsRuntime,
        ILoggerFactory loggerFactory,
        IConfiguration configuration,
        Func<SignalingMessage, Task> sendSignaling,
        SignalingClient signalingClient,
        TurnConfigService? turnConfigService = null)
    {
        SessionId = sessionId;
        PeerId = peerId;
        _localClientId = localClientId;
        Title = peerId;
        _jsRuntime = jsRuntime;
        _logger = loggerFactory.CreateLogger<ViewerSession>();
        _loggerFactory = loggerFactory;
        _configuration = configuration;
        _turnConfigService = turnConfigService;
        _sendSignaling = sendSignaling;
        _signalingClient = signalingClient;
    }

    /// <summary>
    /// Initialize is now a no-op — transport connects via HandleTransportEndpointAsync.
    /// </summary>
    public Task InitializeAsync()
    {
        _logger.LogInformation("Session {SessionId}: Initialized (waiting for TransportEndpoint)", SessionId);
        SetState(ViewerSessionState.WaitingForOffer);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Bind the session to a viewer window's JSRuntime.
    /// Sets up video rendering target (NativeFrameBridge) and input capture.
    /// (NativeFrameBridge field is owned by the Video partial; this method acquires
    /// it from DI on first bind. Cross-partial field write is fine in same class.)
    /// </summary>
    public async Task BindToViewerAsync(IJSRuntime viewerJsRuntime)
    {
        await Task.CompletedTask; // currently sync body; reserve async sugar for future awaits
        _jsRuntime = viewerJsRuntime;

#if WINDOWS
        // Get NativeFrameBridge from DI for decoded frame rendering
        // (shared singleton — initialized from MainPage.xaml.cs with CoreWebView2)
        if (_frameBridge == null)
        {
            // Try to get from the app's service provider
            _frameBridge = App.Current?.Handler?.MauiContext?.Services?.GetService<Services.NativeFrameBridge>();
            if (_frameBridge?.IsInitialized == true)
            {
                _logger.LogInformation("Session {SessionId}: NativeFrameBridge acquired for video rendering", SessionId);
            }
            else
            {
                _logger.LogWarning("Session {SessionId}: NativeFrameBridge not available or not initialized", SessionId);
            }
        }
#endif

        _logger.LogInformation("Session {SessionId}: Bound to viewer JSRuntime", SessionId);
    }

    // Per-session wrapper binding _transport / _logger / SessionId to the shared helper.
    // Call sites: 6 Send*Async methods on this class. See ControlMessageSender.cs.
    private Task SendAsync<T>(T payload, string label)
        => ControlMessageSender.SendAsync(_transport, _logger, SessionId, payload, label);

    /// <summary>
    /// Disconnect this session. Cleans up all state so reconnect works without app restart.
    /// </summary>
    public async Task DisconnectAsync()
    {
        _logger.LogInformation("Session {SessionId}: DisconnectAsync — cleaning up", SessionId);

        // Stop clipboard file transfer
        StopClipboardFileTransfer();

        // Stop quality reporting
        StopQualityReporting();

        // Stop stats relay
        _ = DisableStatsRelayAsync();

        // Dispose decoder (will be recreated on reconnect)
        _decoder?.Dispose();
        _decoder = null;

#if WINDOWS
        // Clear frame bridge reference (will be re-bound on reconnect)
        _frameBridge = null;
#endif

        // Dispose DotNetRef (will be recreated on reconnect)
        _dotNetRef?.Dispose();
        _dotNetRef = null;

        // Unsubscribe and dispose transport
        if (_transport != null)
        {
            _transport.OnControlMessage -= HandleControlMessage;
            _transport.OnVideoData -= HandleVideoData;
            _transport.OnLosslessFrame -= HandleLosslessFrame;
            _transport.OnFileData -= HandleFileDataBinary;
            _transport.OnFileSignalingMessage -= HandleFileChannelMessage;
            _transport.OnConnectionStateChanged -= HandleTransportStateChanged;
            _transport.OnConnectionQualityChanged -= HandleConnectionQualityChanged;
            await _transport.DisposeAsync();
            _transport = null;
        }

        SetState(ViewerSessionState.Disconnected);
        OnDisconnected?.Invoke(null);
    }

    private void SetState(ViewerSessionState newState)
    {
        if (State != newState)
        {
            State = newState;
            OnStateChanged?.Invoke(newState);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        StopClipboardFileTransfer();
        _ = DisableStatsRelayAsync();

        _dotNetRef?.Dispose();
        _dotNetRef = null;

        _decoder?.Dispose();
        _decoder = null;

        if (_transport != null)
        {
            _transport.OnControlMessage -= HandleControlMessage;
            _transport.OnVideoData -= HandleVideoData;
            _transport.OnLosslessFrame -= HandleLosslessFrame;
            _transport.OnFileData -= HandleFileDataBinary;
            _transport.OnFileSignalingMessage -= HandleFileChannelMessage;
            _transport.OnConnectionStateChanged -= HandleTransportStateChanged;
            _transport.OnConnectionQualityChanged -= HandleConnectionQualityChanged;
            await _transport.DisposeAsync();
            _transport = null;
        }
    }
}

/// <summary>
/// Connection state for a viewer session.
/// </summary>
public enum ViewerSessionState
{
    /// <summary>Session is being set up.</summary>
    Connecting,
    /// <summary>Waiting for transport endpoint from host.</summary>
    WaitingForOffer,
    /// <summary>Session is connected and active.</summary>
    Connected,
    /// <summary>Session has been disconnected.</summary>
    Disconnected,
    /// <summary>Session encountered an error.</summary>
    Error
}
