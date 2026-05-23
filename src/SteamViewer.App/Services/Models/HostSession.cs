using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SteamViewer.Client.Core.Capture;
using SteamViewer.Client.Core.Elevation;
using SteamViewer.Client.Core.Network;
using SteamViewer.Client.Core.Video;
using SteamViewer.Common.Protocol;
using System.Text.Json.Serialization;
using SteamViewer.Platform.Windows.Clipboard;
using SteamViewer.Platform.Windows.Input;
using SteamViewer.Platform.Windows.ScreenCapture;

namespace SteamViewer.App.Services.Models;

/// <summary>
/// Connection state for a host session.
/// </summary>
public enum HostSessionState
{
    /// <summary>Session is being initialized (transport setup).</summary>
    Initializing,
    /// <summary>Transport listening, waiting for viewer to connect.</summary>
    WaitingForViewer,
    /// <summary>Viewer connected; ready for screen sharing and input.</summary>
    Connected,
    /// <summary>Session has been disconnected.</summary>
    Disconnected,
    /// <summary>Session encountered an error.</summary>
    Error
}

/// <summary>
/// Represents a single host session with a connected viewer peer.
/// Encapsulates TCP transport, FFmpeg encoding, screen sharing, input injection, and file transfer.
/// </summary>
public sealed partial class HostSession : IAsyncDisposable
{
    private readonly IJSRuntime? _jsRuntime;
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IInputInjector _inputInjector;
    private readonly IMonitorEnumerator? _monitorEnumerator;
    private readonly IElevationService? _elevationService;
    private readonly IScreenCapture? _screenCapture;
    private readonly IConfiguration _configuration;
    private readonly TurnConfigService? _turnConfigService;
    private readonly Func<SignalingMessage, Task> _sendSignaling;
    private readonly SignalingClient _signalingClient;
    private readonly string _hostClientId;
    private readonly string _hostPasswordHash;
    private bool _disposed;

    // Video pipeline: encoder, capture events, quality adaptation, lossless settle, cursor
    private readonly HostVideoPipeline _videoPipeline;

    /// <summary>Session ID for JS interop â€” always "host".</summary>
    public string SessionId => "host";

    /// <summary>The connected viewer's peer ID.</summary>
    public string PeerId { get; }

    /// <summary>Current session state.</summary>
    public HostSessionState State { get; private set; } = HostSessionState.Initializing;

    /// <summary>Whether the transport is connected and ready.</summary>
    public bool IsDataChannelReady => _transport?.IsConnected ?? false;

    /// <summary>Whether this host is sharing its screen to the viewer.</summary>
    public bool IsSharingScreen { get; private set; }

    /// <summary>Whether the connected viewer is sharing their screen.</summary>
    public bool IsPeerSharingScreen { get; private set; }

    /// <summary>When true, auto-start full screen sharing when transport connects (used for post-reboot reconnect).</summary>
    public bool AutoShareOnReady { get; set; }

    #region Events

    /// <summary>Raised when session state changes.</summary>
    public event Action<HostSessionState>? OnStateChanged;

    /// <summary>Raised when the transport connects (ready for screen share/input).</summary>
    public event Action? OnReady;

    /// <summary>Raised when the session disconnects.</summary>
    public event Action<string?>? OnDisconnected;

    /// <summary>
    /// Raised when the viewer sends an explicit `client_disconnecting` control message
    /// via the data channel - signals a graceful user-initiated close, distinct from a
    /// network-driven disconnect. Subscribers should fire cleanup with the
    /// ExplicitClientDisconnect trigger so elevation is disposed (no auto-reconnect).
    /// </summary>
    public event Action<string>? OnClientDisconnecting;

    /// <summary>Raised when the peer starts/stops sharing their screen.</summary>
    public event Action<bool>? OnPeerSharingChanged;

    /// <summary>
    /// Raised when THIS host starts/stops sharing its own screen (IsSharingScreen flips).
    /// Symmetric to OnPeerSharingChanged but for the local side. Allows Home.razor to
    /// re-render after auto-share-on-reconnect (which sets IsSharingScreen=true with no
    /// other event firing). Closes TODO §5 P1 "Host UI doesn't reflect auto-share-on-reconnect state."
    /// </summary>
    public event Action<bool>? OnLocalSharingChanged;

    #endregion

    public HostSession(
        string peerId,
        IJSRuntime? jsRuntime,
        ILoggerFactory loggerFactory,
        IInputInjector inputInjector,
        IConfiguration configuration,
        Func<SignalingMessage, Task> sendSignaling,
        SignalingClient signalingClient,
        IElevationService? elevationService = null,
        IMonitorEnumerator? monitorEnumerator = null,
        IScreenCapture? screenCapture = null,
#if WINDOWS
        Services.NativeFrameBridge? frameBridge = null,
#endif
        TurnConfigService? turnConfigService = null,
        string hostClientId = "",
        string hostPasswordHash = "")
    {
        PeerId = peerId;
        _jsRuntime = jsRuntime;
        _logger = loggerFactory.CreateLogger<HostSession>();
        _loggerFactory = loggerFactory;
        _inputInjector = inputInjector;
        _monitorEnumerator = monitorEnumerator;
        _elevationService = elevationService;
        _screenCapture = screenCapture;
        _configuration = configuration;
        _turnConfigService = turnConfigService;
        _sendSignaling = sendSignaling;
        _signalingClient = signalingClient;
        _hostClientId = hostClientId;
        _hostPasswordHash = hostPasswordHash;

        // Create video pipeline (encoder, capture events, quality adaptation, lossless settle)
        _videoPipeline = new HostVideoPipeline(loggerFactory);

        // Subscribe to elevation events to forward to viewer
        if (_elevationService != null)
        {
            _elevationService.OnSecureDesktopFrame += HandleSecureDesktopFrame;
            _elevationService.OnSecureDesktopStateChanged += HandleSecureDesktopStateChanged;
            _elevationService.OnSystemStateChanged += HandleSystemStateChanged;
        }
    }

    /// <summary>
    /// Initialize transport: setup WebSocket relay with AES-GCM encryption,
    /// send RelayReady to viewer via signaling.
    /// </summary>
    public async Task InitializeAsync()
    {
        _logger.LogInformation("Initializing host session for peer {PeerId}", PeerId);

        // Set logger to host mode (no-op when running without WebView, e.g. boot relay)
        if (_jsRuntime != null)
            await _jsRuntime.InvokeVoidAsync("SteamViewerLogger.setMode", true);

        // Create relay transport (binary frames through signaling WebSocket)
        _transport = new HostStreamTransport(_signalingClient, _loggerFactory.CreateLogger<HostStreamTransport>());

        // Subscribe to transport events
        _transport.OnControlMessage += HandleControlMessage;
        _transport.OnFileData += HandleFileDataBinary;
        _transport.OnFileSignalingMessage += HandleFileChannelMessage;
        _transport.OnConnectionStateChanged += HandleTransportStateChanged;
        // Quality adaptation driven by viewer's qualityReport messages, not host's own monitor
        // (host receives mostly single-fragment input - not representative of video path quality)

        // Start relay: generate nonce, setup encryption, send RelayReady to viewer
        await _transport.StartRelayAsync(PeerId, _hostPasswordHash, _sendSignaling);

        _logger.LogInformation("Host transport relay started for peer {PeerId}", PeerId);

        // Don't send initial state yet â€” wait for viewer's "viewerReady" ack
        // (viewer sends it after receiving RelayReady and connecting their relay)
        SetState(HostSessionState.WaitingForViewer);
    }

    private void SetState(HostSessionState newState)
    {
        if (State != newState)
        {
            State = newState;
            OnStateChanged?.Invoke(newState);
        }
    }

    /// <summary>Disconnect and clean up the session.</summary>
    public async Task DisconnectAsync()
    {
        _inputInjector.ReleaseAllModifiers();
        _inputInjector.RestoreKeyboardLayout();

        if (_transport != null)
        {
            await _transport.DisposeAsync();
        }
        if (IsSharingScreen)
        {
            IsSharingScreen = false;
            OnLocalSharingChanged?.Invoke(false);
        }
        IsPeerSharingScreen = false;
        SetState(HostSessionState.Disconnected);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Per-step timing logs to diagnose the "cleanup holds _cleanupLock for 10+ seconds"
        // P1 in TODO §5. Each await/dispose step gets its own Stopwatch so we can identify
        // which one is the slow path during transport-disconnect cleanup.
        var totalSw = System.Diagnostics.Stopwatch.StartNew();

        if (_elevationService != null && !_elevationDetached)
        {
            _elevationService.OnSecureDesktopFrame -= HandleSecureDesktopFrame;
            _elevationService.OnSecureDesktopStateChanged -= HandleSecureDesktopStateChanged;
            _elevationService.OnSystemStateChanged -= HandleSystemStateChanged;
            var elevSw = System.Diagnostics.Stopwatch.StartNew();
            await _elevationService.DisposeAsync();
            elevSw.Stop();
            _logger.LogInformation("HostSession.Dispose: elevation service disposed in {Elapsed}ms (inline, not detached)",
                elevSw.ElapsedMilliseconds);
        }

        var clipSw = System.Diagnostics.Stopwatch.StartNew();
        StopClipboardFileTransfer();
        clipSw.Stop();
        _logger.LogInformation("HostSession.Dispose: StopClipboardFileTransfer in {Elapsed}ms",
            clipSw.ElapsedMilliseconds);

        // Dispose video pipeline (stops capture, disposes encoder, clears sleep prevention)
        var vidSw = System.Diagnostics.Stopwatch.StartNew();
        _videoPipeline.Dispose();
        _dxgiAdapter = null;
        vidSw.Stop();
        _logger.LogInformation("HostSession.Dispose: video pipeline disposed in {Elapsed}ms",
            vidSw.ElapsedMilliseconds);

        if (_transport != null)
        {
            _transport.OnControlMessage -= HandleControlMessage;
            _transport.OnFileData -= HandleFileDataBinary;
            _transport.OnFileSignalingMessage -= HandleFileChannelMessage;
            _transport.OnConnectionStateChanged -= HandleTransportStateChanged;
            var txSw = System.Diagnostics.Stopwatch.StartNew();
            await _transport.DisposeAsync();
            txSw.Stop();
            _logger.LogInformation("HostSession.Dispose: transport disposed in {Elapsed}ms",
                txSw.ElapsedMilliseconds);
            _transport = null;
        }

        totalSw.Stop();
        _logger.LogInformation("HostSession.Dispose: TOTAL {Elapsed}ms",
            totalSw.ElapsedMilliseconds);
    }
}
