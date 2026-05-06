using Microsoft.Extensions.Logging;
using SteamViewer.Client.Core.Capture;
using SteamViewer.Client.Core.FileTransfer;
using SteamViewer.Client.Core.Network;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Client.Core.Session;

/// <summary>
/// Manages the overall state and coordination of a remote desktop session.
/// </summary>
public sealed class SessionManager : IAsyncDisposable
{
    private readonly ILogger<SessionManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SessionConfig _config;
    private readonly SessionState _state;
    private readonly object _stateLock = new();

    private SignalingClient? _signalingClient;
    private IScreenCapture? _screenCapture;
    private VideoEncoder? _encoder;
    private VideoDecoder? _decoder;
    private IInputInjector? _inputInjector;

    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;
    private FileTransferManager? _fileTransferManager;
    private bool _disposed;

    #region Events

    /// <summary>
    /// Fired when connection state changes.
    /// </summary>
    public event EventHandler<ConnectionState>? ConnectionStateChanged;

    /// <summary>
    /// Fired when an incoming connection request is received.
    /// </summary>
    public event EventHandler<string>? IncomingConnectionRequest;

    /// <summary>
    /// Fired when a connection request is approved.
    /// </summary>
    public event EventHandler<string>? ConnectionApproved;

    /// <summary>
    /// Fired when a connection request is rejected.
    /// </summary>
    public event EventHandler<string>? ConnectionRejected;

    /// <summary>
    /// Fired when the peer disconnects.
    /// </summary>
    public event EventHandler<(string PeerId, string? Reason)>? PeerDisconnected;

    /// <summary>
    /// Fired when a signaling error occurs.
    /// </summary>
    public event EventHandler<string>? SignalingError;

    /// <summary>
    /// Fired when WebRTC connection is established.
    /// </summary>
    public event EventHandler<string>? WebRTCConnected;

    /// <summary>
    /// Fired when a video frame is decoded (for viewer).
    /// </summary>
    public event EventHandler<DecodedFrame>? VideoFrameDecoded;

    /// <summary>
    /// Fired when registered with signaling server.
    /// </summary>
    public event EventHandler? Registered;

    /// <summary>
    /// Fired when an incoming file transfer request is received.
    /// </summary>
    public event EventHandler<FileTransferState>? IncomingFileTransfer;

    /// <summary>
    /// Fired when file transfer progress updates.
    /// </summary>
    public event EventHandler<FileTransferState>? FileTransferProgress;

    /// <summary>
    /// Fired when a file transfer completes.
    /// </summary>
    public event EventHandler<FileTransferState>? FileTransferCompleted;

    /// <summary>
    /// Fired when a file transfer fails.
    /// </summary>
    public event EventHandler<FileTransferState>? FileTransferFailed;

    #endregion

    /// <summary>
    /// Creates a new session manager.
    /// </summary>
    public SessionManager(
        SessionConfig config,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SessionManager>();
        _state = SessionState.NewHost(ClientCredentials.Generate());

        _state.ConnectionStateChanged += (_, state) =>
        {
            _logger.LogInformation("Connection state changed to {State}", state);
            ConnectionStateChanged?.Invoke(this, state);
        };
    }

    /// <summary>
    /// Gets the current session configuration.
    /// </summary>
    public SessionConfig Config => _config;

    /// <summary>
    /// Gets the current session state (read-only snapshot).
    /// </summary>
    public SessionState State => _state;

    /// <summary>
    /// Gets the client ID.
    /// </summary>
    public string ClientId => _state.Credentials.ClientId;

    /// <summary>
    /// Gets the password.
    /// </summary>
    public string Password => _state.Credentials.Password;

    /// <summary>
    /// Gets whether the session is in host mode.
    /// </summary>
    public bool IsHost => _state.IsHost;

    /// <summary>
    /// Gets whether the session is in viewer mode.
    /// </summary>
    public bool IsViewer => _state.IsViewer;

    /// <summary>
    /// Gets whether connected to a peer.
    /// </summary>
    public bool IsConnected => _state.IsConnected;

    /// <summary>
    /// Gets all active file transfers.
    /// </summary>
    public IEnumerable<FileTransferState> ActiveTransfers =>
        _fileTransferManager?.GetTransfers() ?? Enumerable.Empty<FileTransferState>();

    /// <summary>
    /// Sets the role (host or viewer).
    /// </summary>
    public void SetRole(Role role)
    {
        _state.SetRole(role);
        _logger.LogInformation("Role set to {Role}", role);
    }

    /// <summary>
    /// Connects to the signaling server.
    /// </summary>
    public async Task ConnectSignalingAsync(CancellationToken cancellationToken = default)
    {
        if (_signalingClient != null)
        {
            throw new InvalidOperationException("Already connected to signaling server");
        }

        _logger.LogInformation("Connecting to signaling server at {Url}", _config.SignalingServerUrl);

        _signalingClient = new SignalingClient(_config.SignalingServerUrl, _loggerFactory.CreateLogger<SignalingClient>());
        _signalingClient.OnMessageReceived += OnSignalingMessageReceivedAction;
        _signalingClient.OnDisconnected += OnSignalingDisconnectedAction;

        await _signalingClient.ConnectAsync(cancellationToken);

        _state.SetConnectionState(ConnectionState.Registering);

        // Register with the server
        var registerMsg = new SignalingMessage.Register(
            _state.Credentials.ClientId,
            _state.Credentials.PasswordHash());

        await _signalingClient.SendAsync(registerMsg, cancellationToken);
    }

    /// <summary>
    /// Disconnects from the signaling server.
    /// </summary>
    public async Task DisconnectSignalingAsync()
    {
        if (_signalingClient != null)
        {
            await _signalingClient.DisconnectAsync();
            _signalingClient.OnMessageReceived -= OnSignalingMessageReceivedAction;
            _signalingClient.OnDisconnected -= OnSignalingDisconnectedAction;
            _signalingClient = null;
        }

        _state.SetConnectionState(ConnectionState.Disconnected);
    }

    private void OnSignalingMessageReceivedAction(SignalingMessage message) => OnSignalingMessageReceived(this, message);
    private void OnSignalingDisconnectedAction(string? reason) => OnSignalingDisconnected(this, EventArgs.Empty);

    /// <summary>
    /// Requests a connection to a peer.
    /// </summary>
    public async Task ConnectToPeerAsync(string peerId, string password, CancellationToken cancellationToken = default)
    {
        if (_signalingClient == null)
        {
            throw new InvalidOperationException("Not connected to signaling server");
        }

        _logger.LogInformation("Requesting connection to peer {PeerId}", peerId);

        var passwordHash = PasswordHash.Compute(peerId, password);
        var msg = new SignalingMessage.ConnectRequest(peerId, passwordHash);
        await _signalingClient.SendAsync(msg, cancellationToken);

        _state.ConnectToPeer(peerId);
    }

    /// <summary>
    /// Approves an incoming connection request.
    /// </summary>
    public async Task ApproveConnectionAsync(string peerId, CancellationToken cancellationToken = default)
    {
        if (_signalingClient == null)
        {
            throw new InvalidOperationException("Not connected to signaling server");
        }

        _logger.LogInformation("Approving connection from peer {PeerId}", peerId);

        var msg = new SignalingMessage.ConnectionResponse(peerId, true);
        await _signalingClient.SendAsync(msg, cancellationToken);

        _state.ConnectToPeer(peerId);
    }

    /// <summary>
    /// Rejects an incoming connection request.
    /// </summary>
    public async Task RejectConnectionAsync(string peerId, CancellationToken cancellationToken = default)
    {
        if (_signalingClient == null)
        {
            throw new InvalidOperationException("Not connected to signaling server");
        }

        _logger.LogInformation("Rejecting connection from peer {PeerId}", peerId);

        var msg = new SignalingMessage.ConnectionResponse(peerId, false);
        await _signalingClient.SendAsync(msg, cancellationToken);
    }

    /// <summary>
    /// Disconnects from the current peer.
    /// </summary>
    public async Task DisconnectFromPeerAsync(CancellationToken cancellationToken = default)
    {
        if (_signalingClient == null || _state.PeerId == null)
        {
            return;
        }

        _logger.LogInformation("Disconnecting from peer {PeerId}", _state.PeerId);

        var msg = new SignalingMessage.Disconnect(_state.PeerId);
        await _signalingClient.SendAsync(msg, cancellationToken);

        await StopCaptureAsync();
        _state.Disconnect();
    }

    /// <summary>
    /// Sets the screen capture implementation.
    /// </summary>
    public void SetScreenCapture(IScreenCapture screenCapture)
    {
        _screenCapture = screenCapture;
    }

    /// <summary>
    /// Sets the input injector implementation.
    /// </summary>
    public void SetInputInjector(IInputInjector inputInjector)
    {
        _inputInjector = inputInjector;
    }

    /// <summary>
    /// Starts screen capture and streaming (host only).
    /// </summary>
    public async Task StartCaptureAsync(uint monitorId, CancellationToken cancellationToken = default)
    {
        if (!_state.IsHost)
        {
            throw new InvalidOperationException("Only host can start capture");
        }

        if (_screenCapture == null)
        {
            throw new InvalidOperationException("Screen capture not configured");
        }

        _logger.LogInformation("Starting screen capture for monitor {MonitorId}", monitorId);

        // Stop any existing capture
        await StopCaptureAsync();

        // Initialize screen capture
        await _screenCapture.InitializeAsync(monitorId, cancellationToken);

        var (width, height) = _screenCapture.Resolution;

        // Initialize encoder
        _encoder = new VideoEncoder(_loggerFactory.CreateLogger<VideoEncoder>());
        _encoder.Initialize(width, height, _config.TargetFps, _config.VideoBitrate);

        // Start capture loop
        _captureCts = new CancellationTokenSource();
        _captureTask = RunCaptureLoopAsync(_captureCts.Token);

        _logger.LogInformation("Screen capture started ({Width}x{Height} @ {Fps}fps)",
            width, height, _config.TargetFps);
    }

    /// <summary>
    /// Stops screen capture.
    /// </summary>
    public async Task StopCaptureAsync()
    {
        if (_captureCts != null)
        {
            _logger.LogInformation("Stopping screen capture");

            _captureCts.Cancel();

            if (_captureTask != null)
            {
                try
                {
                    await _captureTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
            }

            _captureCts.Dispose();
            _captureCts = null;
            _captureTask = null;
        }

        _encoder?.Dispose();
        _encoder = null;

        _screenCapture?.Dispose();
        _screenCapture = null;
    }

    /// <summary>
    /// Initializes the video decoder (viewer only).
    /// </summary>
    public void InitializeDecoder()
    {
        if (_decoder != null)
        {
            return;
        }

        _decoder = new VideoDecoder(_loggerFactory.CreateLogger<VideoDecoder>());
        _decoder.Initialize();
        _logger.LogInformation("Video decoder initialized");
    }

    /// <summary>
    /// Processes incoming video data (viewer only).
    /// </summary>
    public void ProcessVideoData(byte[] data)
    {
        if (_decoder == null)
        {
            InitializeDecoder();
        }

        var frames = _decoder!.DecodeFrame(data);
        foreach (var frame in frames)
        {
            VideoFrameDecoded?.Invoke(this, frame);
        }
    }

    /// <summary>
    /// Processes incoming input events (host only).
    /// </summary>
    public void ProcessInputEvent(InputEvent inputEvent)
    {
        if (!_state.IsHost || _inputInjector == null)
        {
            return;
        }

        if (_screenCapture == null) return; // Can't inject without knowing capture dimensions

        var (width, height) = _screenCapture.Resolution;

        // Use the unified InjectInput method
        _inputInjector.InjectInput(inputEvent, width, height);
    }

    #region File Transfer

    /// <summary>
    /// Initializes file transfer support with a data channel send function.
    /// </summary>
    public void InitializeFileTransfer(Func<byte[], Task> sendDataAsync)
    {
        if (_fileTransferManager != null)
        {
            return;
        }

        _fileTransferManager = new FileTransferManager(
            _loggerFactory.CreateLogger<FileTransferManager>(),
            sendDataAsync);

        _fileTransferManager.IncomingTransferRequest += (_, state) =>
            IncomingFileTransfer?.Invoke(this, state);
        _fileTransferManager.TransferProgress += (_, state) =>
            FileTransferProgress?.Invoke(this, state);
        _fileTransferManager.TransferCompleted += (_, state) =>
            FileTransferCompleted?.Invoke(this, state);
        _fileTransferManager.TransferFailed += (_, state) =>
            FileTransferFailed?.Invoke(this, state);

        _logger.LogInformation("File transfer manager initialized");
    }

    /// <summary>
    /// Handles an incoming file transfer message from the data channel.
    /// </summary>
    public async Task HandleFileTransferMessageAsync(FileTransferMessage message, CancellationToken ct = default)
    {
        if (_fileTransferManager == null)
        {
            _logger.LogWarning("Received file transfer message but manager not initialized");
            return;
        }

        await _fileTransferManager.HandleMessageAsync(message, ct);
    }

    /// <summary>
    /// Sends a file to the connected peer.
    /// </summary>
    public async Task SendFileAsync(string filePath, CancellationToken ct = default)
    {
        if (_fileTransferManager == null)
        {
            throw new InvalidOperationException("File transfer not initialized");
        }

        await _fileTransferManager.SendFileAsync(filePath, ct);
    }

    /// <summary>
    /// Accepts an incoming file transfer request.
    /// </summary>
    public async Task AcceptFileTransferAsync(Guid transferId, string savePath, CancellationToken ct = default)
    {
        if (_fileTransferManager == null)
        {
            throw new InvalidOperationException("File transfer not initialized");
        }

        await _fileTransferManager.AcceptTransferAsync(transferId, savePath, ct);
    }

    /// <summary>
    /// Rejects an incoming file transfer request.
    /// </summary>
    public async Task RejectFileTransferAsync(Guid transferId, CancellationToken ct = default)
    {
        if (_fileTransferManager == null)
        {
            throw new InvalidOperationException("File transfer not initialized");
        }

        await _fileTransferManager.RejectTransferAsync(transferId, "Rejected by user", ct);
    }

    /// <summary>
    /// Cancels an active file transfer.
    /// </summary>
    public async Task CancelFileTransferAsync(Guid transferId, CancellationToken ct = default)
    {
        if (_fileTransferManager == null)
        {
            throw new InvalidOperationException("File transfer not initialized");
        }

        await _fileTransferManager.CancelTransferAsync(transferId, ct);
    }

    #endregion

    private async Task RunCaptureLoopAsync(CancellationToken cancellationToken)
    {
        var frameInterval = TimeSpan.FromMilliseconds(1000.0 / _config.TargetFps);
        var isFirstFrame = true;

        _logger.LogDebug("Starting capture loop with {Interval}ms interval", frameInterval.TotalMilliseconds);

        while (!cancellationToken.IsCancellationRequested)
        {
            var frameStart = DateTime.UtcNow;

            try
            {
                var frame = await _screenCapture!.CaptureFrameAsync(cancellationToken);

                if (frame != null && _encoder != null)
                {
                    var encodedFrames = _encoder.EncodeFrame(frame.Data, frame.Stride, isFirstFrame);
                    isFirstFrame = false;

                    // TODO: Send encoded frames via transport
                    foreach (var encoded in encodedFrames)
                    {
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in capture loop");
            }

            // Maintain frame rate
            var elapsed = DateTime.UtcNow - frameStart;
            var delay = frameInterval - elapsed;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }

        _logger.LogDebug("Capture loop ended");
    }

    private void OnSignalingMessageReceived(object? sender, SignalingMessage message)
    {
        _logger.LogDebug("Received signaling message: {Type}", message.GetType().Name);

        switch (message)
        {
            case SignalingMessage.RegisterSuccess success:
                _state.SetConnectionState(ConnectionState.Registered);
                Registered?.Invoke(this, EventArgs.Empty);
                break;

            case SignalingMessage.RegisterFailed failed:
                _logger.LogError("Registration failed: {Reason}", failed.Reason);
                _state.SetConnectionState(ConnectionState.Error);
                SignalingError?.Invoke(this, failed.Reason);
                break;

            case SignalingMessage.IncomingConnection incoming:
                _logger.LogInformation("Incoming connection from {FromId}", incoming.FromId);
                IncomingConnectionRequest?.Invoke(this, incoming.FromId);
                break;

            case SignalingMessage.ConnectionResponse response:
                _logger.LogInformation("Connection response from {TargetId}: {Approved}",
                    response.TargetId, response.Approved);

                if (response.Approved)
                {
                    _state.ConnectToPeer(response.TargetId);
                    ConnectionApproved?.Invoke(this, response.TargetId);
                }
                else
                {
                    ConnectionRejected?.Invoke(this, response.TargetId);
                }
                break;

            case SignalingMessage.Disconnected disconnected:
                _logger.LogInformation("Peer {PeerId} disconnected: {Reason}",
                    disconnected.PeerId, disconnected.Reason);
                _state.Disconnect();
                PeerDisconnected?.Invoke(this, (disconnected.PeerId, disconnected.Reason));
                break;

            case SignalingMessage.Error error:
                _logger.LogError("Signaling error: {Message}", error.Message);
                SignalingError?.Invoke(this, error.Message);
                break;

            case SignalingMessage.Pong:
                // Ignore pong responses
                break;
        }
    }

    private void OnSignalingDisconnected(object? sender, EventArgs e)
    {
        _logger.LogWarning("Disconnected from signaling server");
        _state.SetConnectionState(ConnectionState.Disconnected);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await StopCaptureAsync();
        await DisconnectSignalingAsync();

        _decoder?.Dispose();
        _decoder = null;

        if (_fileTransferManager != null)
        {
            await _fileTransferManager.DisposeAsync();
            _fileTransferManager = null;
        }

        _logger.LogDebug("SessionManager disposed");
    }
}
