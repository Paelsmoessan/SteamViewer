using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace SteamViewer.Client.Core.Network;

/// <summary>
/// Transport layer replacing WebRTC PeerConnection + data channels.
/// Sends encrypted, multiplexed frames through an ITransportBackend.
/// Phase 1: WebSocketRelayBackend (via signaling server)
/// Phase 2: UdpTransportBackend (direct P2P or TURN relay)
///
/// Wire format per frame (before encryption):
///   [1 byte channel][payload]
///
/// Channel 0 = JSON control (commands, keyboard, clipboard, cursor, mouse)
/// Channel 1 = H.264 video NALUs (host → viewer only)
/// Channel 2 = Binary file data
/// Channel 3 = JSON file signaling
/// Channel 4 = Lossless QOI frame (host → viewer, one-shot on screen settle)
/// Channel 5 = Binary Secure Desktop JPEG frame (host → viewer)
///
/// Each frame is AES-256-GCM encrypted before sending through the backend.
/// </summary>
public abstract class StreamTransport : IAsyncDisposable
{
    protected readonly ILogger _logger;
    protected ITransportBackend? _backend;
    protected TransportEncryption? _encryption;
    protected CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Channel<(byte[] data, int length)> _videoSendQueue;
    private Task? _videoSendTask;
    private TaskCompletionSource? _videoSendReady;
    private bool _disposed;
    protected bool _connected;
    private int _controlSendFailures;
    private bool _firstDataLogged;

    // Decryption failure tracking (RFC 9147 pattern: silent drop + counted failures)
    private int _decryptionFailures;
    private readonly Stopwatch _transportStartTime = new();

    // Synchronized UDP switch state
    protected ITransportBackend? _pendingUdpBackend;
    protected bool _localUdpReady;
    protected bool _remoteUdpReady;

    // Channel IDs for multiplexing over a single transport
    protected const byte ChannelControl = 0;
    protected const byte ChannelVideo = 1;
    protected const byte ChannelFileData = 2;
    protected const byte ChannelFileSignaling = 3;
    protected const byte ChannelLossless = 4;

    /// <summary>Raised when a JSON control message is received.</summary>
    public event Func<string, Task>? OnControlMessage;

    /// <summary>Raised when H.264 video frame NALUs are received.</summary>
    public event Action<byte[], int>? OnVideoData;

    /// <summary>Raised when a lossless QOI frame is received (screen settle snapshot).</summary>
    public event Action<byte[], int>? OnLosslessFrame;

    /// <summary>Raised when binary file data is received.</summary>
    public event Func<byte[], Task>? OnFileData;

    /// <summary>Raised when a JSON file signaling message is received.</summary>
    public event Func<string, Task>? OnFileSignalingMessage;

    /// <summary>Raised when the transport connects or disconnects.</summary>
    public event Action<string>? OnConnectionStateChanged;

    /// <summary>Raised when connection quality changes (Good/Fair/Poor). Only fires on UDP transport.</summary>
    public event Action<ConnectionQuality>? OnConnectionQualityChanged;

    public bool IsConnected => _connected && !_disposed;

    /// <summary>Whether the transport is currently using a direct UDP backend (vs WebSocket relay).</summary>
    public bool IsDirectUdp => _backend is UdpTransportBackend;

    /// <summary>Connection quality monitor - active only when on UDP transport.</summary>
    public ConnectionQualityMonitor? QualityMonitor => _qualityMonitor;
    protected ConnectionQualityMonitor? _qualityMonitor;
    private Timer? _qualityUpdateTimer;

    protected StreamTransport(ILogger logger)
    {
        _logger = logger;
        // Bounded queue: drop oldest video frame if encoder outpaces network
        _videoSendQueue = Channel.CreateBounded<(byte[], int)>(
            new BoundedChannelOptions(3)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });
    }

    #region Send Methods

    /// <summary>Send a JSON control message (commands, keyboard, clipboard, cursor, mouse).</summary>
    public async ValueTask<bool> SendControlAsync(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        return await SendFrameAsync(ChannelControl, payload, 0, payload.Length);
    }

    /// <summary>Send binary file data.</summary>
    public async ValueTask<bool> SendFileDataAsync(byte[] data)
    {
        return await SendFrameAsync(ChannelFileData, data, 0, data.Length);
    }

    /// <summary>Send a lossless QOI frame (host → viewer, one-shot on screen settle).</summary>
    public async ValueTask<bool> SendLosslessFrameAsync(byte[] data, int offset, int length)
    {
        return await SendFrameAsync(ChannelLossless, data, offset, length);
    }

    /// <summary>Send JSON file signaling message (FormatList, FileContentsRequest, etc.).</summary>
    public async ValueTask<bool> SendFileSignalingAsync(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        return await SendFrameAsync(ChannelFileSignaling, payload, 0, payload.Length);
    }

    /// <summary>
    /// Enqueue a video frame for sending. Non-blocking — drops oldest frame if queue is full.
    /// Caller's byte array is copied (encoder reuses its buffer).
    /// </summary>
    public void EnqueueVideoFrame(byte[] data, int length)
    {
        // Copy because encoder reuses its output buffer
        var copy = new byte[length];
        Buffer.BlockCopy(data, 0, copy, 0, length);
        _videoSendQueue.Writer.TryWrite((copy, length));
    }

    /// <summary>
    /// Wait until the video send loop is ready to consume frames.
    /// Returns immediately if video send is not enabled (viewer side).
    /// </summary>
    public async Task WaitForVideoSendReadyAsync(CancellationToken ct = default)
    {
        if (_videoSendReady == null) return;
        try
        {
            await _videoSendReady.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        catch (TimeoutException)
        {
            _logger.LogError("Video send loop did NOT become ready within 5s - auto-share may fail");
        }
    }

    private async ValueTask<bool> SendFrameAsync(byte channel, byte[] payload, int offset, int length)
    {
        if (!_connected || _disposed || _backend == null)
        {
            // Gate visibility per verbose-logging policy: surface which condition short-circuited,
            // because callers (e.g., OnClipboardFilesDetected) ignore the false return and the
            // silent drop is otherwise indistinguishable from a successful send.
            _logger.LogDebug("SendFrameAsync ch={Channel} len={Length} short-circuit: connected={Connected} disposed={Disposed} backend={Backend}",
                channel, length, _connected, _disposed, _backend != null ? "set" : "null");
            return false;
        }

        try
        {
            // Build frame: [1 byte channel][payload]
            var frame = new byte[1 + length];
            frame[0] = channel;
            Buffer.BlockCopy(payload, offset, frame, 1, length);

            // Encrypt
            byte[] encrypted;
            if (_encryption != null)
                encrypted = _encryption.Encrypt(frame, 0, frame.Length);
            else
                encrypted = frame;

            await _sendLock.WaitAsync();
            try
            {
                await _backend.SendAsync(encrypted, 0, encrypted.Length);
                _logger.LogDebug("SendFrameAsync ch={Channel} len={Length} sent via {Backend}",
                    channel, length, _backend.GetType().Name);
                return true;
            }
            finally { _sendLock.Release(); }
        }
        catch (Exception ex)
        {
            _controlSendFailures++;
            if (_controlSendFailures <= 3)
                _logger.LogWarning(ex, "Frame send failed (channel {Channel})", channel);

            if (_controlSendFailures >= 10)
            {
                _logger.LogError("Control send: {Count} consecutive failures — disconnecting", _controlSendFailures);
                HandleBackendDisconnected();
            }
            return false;
        }
    }

    #endregion

    #region Receive

    protected void HandleDataReceived(byte[] data, int length)
    {
        if (_disposed || !_connected) return;

        if (!_firstDataLogged)
        {
            _firstDataLogged = true;
            _logger.LogInformation("[TRANSPORT] First data received: {Length} bytes via {Backend}",
                length, _backend?.GetType().Name ?? "null");
        }

        try
        {
            // Decrypt (returns null if session tag mismatches = stale data from old session)
            byte[]? plaintext;
            if (_encryption != null)
            {
                plaintext = _encryption.Decrypt(data, 0, length);
                if (plaintext == null)
                {
                    // Session tag mismatch: stale data from old session, fast-rejected without crypto cost
                    var staleCount = Interlocked.Increment(ref _decryptionFailures);
                    if (staleCount <= 3 || staleCount % 100 == 0)
                        _logger.LogDebug("Stale session data dropped (tag mismatch, count={Count})", staleCount);
                    return;
                }
            }
            else
            {
                plaintext = new byte[length];
                Buffer.BlockCopy(data, 0, plaintext, 0, length);
            }

            if (plaintext.Length < 1) return;

            var channel = plaintext[0];

            switch (channel)
            {
                case ChannelControl:
                {
                    var json = Encoding.UTF8.GetString(plaintext, 1, plaintext.Length - 1);
                    if (OnControlMessage != null)
                        _ = Task.Run(async () =>
                        {
                            try { await OnControlMessage.Invoke(json); }
                            catch (Exception ex) { _logger.LogWarning(ex, "Control handler error"); }
                        });
                    break;
                }
                case ChannelVideo:
                {
                    var videoLen = plaintext.Length - 1;
                    var videoData = new byte[videoLen];
                    Buffer.BlockCopy(plaintext, 1, videoData, 0, videoLen);
                    OnVideoData?.Invoke(videoData, videoLen);
                    break;
                }
                case ChannelLossless:
                {
                    var losslessLen = plaintext.Length - 1;
                    var losslessData = new byte[losslessLen];
                    Buffer.BlockCopy(plaintext, 1, losslessData, 0, losslessLen);
                    // Lossless frames are infrequent one-shots — Task.Run is fine
                    if (OnLosslessFrame != null)
                    {
                        var handler = OnLosslessFrame;
                        _ = Task.Run(() =>
                        {
                            try { handler.Invoke(losslessData, losslessLen); }
                            catch (Exception ex) { _logger.LogWarning(ex, "Lossless frame handler error"); }
                        });
                    }
                    break;
                }
                case ChannelFileData:
                {
                    var fileData = new byte[plaintext.Length - 1];
                    Buffer.BlockCopy(plaintext, 1, fileData, 0, fileData.Length);
                    if (OnFileData != null)
                        _ = Task.Run(async () =>
                        {
                            try { await OnFileData.Invoke(fileData); }
                            catch (Exception ex) { _logger.LogWarning(ex, "File data handler error"); }
                        });
                    break;
                }
                case ChannelFileSignaling:
                {
                    var json = Encoding.UTF8.GetString(plaintext, 1, plaintext.Length - 1);
                    if (OnFileSignalingMessage != null)
                        _ = Task.Run(async () =>
                        {
                            try { await OnFileSignalingMessage.Invoke(json); }
                            catch (Exception ex) { _logger.LogWarning(ex, "File signaling handler error"); }
                        });
                    break;
                }
                default:
                    _logger.LogWarning("Unknown channel byte: {Channel}", channel);
                    break;
            }
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // RFC 9147 (DTLS 1.3) pattern: silently drop invalid records, count failures.
            // Stale data from old sessions is expected during reconnect and fails decryption
            // because each session derives a unique key via HKDF with session nonce.
            var count = Interlocked.Increment(ref _decryptionFailures);
            var elapsed = _transportStartTime.Elapsed;

            if (elapsed.TotalSeconds < 5)
            {
                // Grace window: reconnect transient, log sparingly
                if (count == 1 || count % 50 == 0)
                    _logger.LogDebug("Decryption failed during grace window (count={Count}, elapsed={Elapsed:F1}s)", count, elapsed.TotalSeconds);
            }
            else if (count <= 5 || count % 100 == 0)
            {
                // Steady state: log periodically
                _logger.LogWarning("Decryption failed in steady state (count={Count})", count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Data receive handler error");
        }
    }

    #endregion

    #region Transport Lifecycle

    /// <summary>
    /// Start the transport with the given backend.
    /// Called by subclasses after encryption is configured.
    /// </summary>
    protected void StartTransport(ITransportBackend backend, bool enableVideoSend)
    {
        _backend = backend;
        _cts = new CancellationTokenSource();

        // Subscribe to data from backend
        _backend.OnDataReceived += HandleDataReceived;
        _backend.OnDisconnected += HandleBackendDisconnected;

        // Start video send loop (host only)
        if (enableVideoSend)
        {
            _videoSendReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _videoSendTask = Task.Run(() => VideoSendLoopAsync(_cts.Token));
        }

        _connected = true;
        _transportStartTime.Restart();
        _decryptionFailures = 0;
        OnConnectionStateChanged?.Invoke("connected");
        _logger.LogInformation("Transport started (backend={Backend}, videoSend={VideoSend})",
            backend.GetType().Name, enableVideoSend);
    }

    /// <summary>
    /// Switch to a different transport backend (e.g., upgrade from WebSocket relay to direct UDP).
    /// The old backend is disposed after switching.
    /// </summary>
    protected Task SwitchBackendAsync(ITransportBackend newBackend)
    {
        var oldBackend = _backend;
        _logger.LogInformation("[TRANSPORT] Switching backend: {Old} → {New}",
            oldBackend?.GetType().Name ?? "null", newBackend.GetType().Name);

        if (oldBackend != null)
        {
            // Keep OnDataReceived subscribed — peer may still be sending on old transport
            // Stale data from prior sessions is handled by encryption tag validation (silent drop)
            oldBackend.OnDisconnected -= HandleBackendDisconnected;
        }

        _backend = newBackend;
        _firstDataLogged = false; // Log first data on new backend too
        // Prevent double-subscribe if this was the pending backend (already subscribed in AcceptUdpPath)
        newBackend.OnDataReceived -= HandleDataReceived;
        newBackend.OnDataReceived += HandleDataReceived;
        newBackend.OnDisconnected += HandleBackendDisconnected;
        if (oldBackend != null)
        {
            _ = DisposeAfterGracePeriodAsync(oldBackend);
        }

        _logger.LogInformation("[TRANSPORT] Backend switch complete → {Backend}", newBackend.GetType().Name);
        return Task.CompletedTask;
    }

    private async Task DisposeAfterGracePeriodAsync(ITransportBackend oldBackend)
    {
        await Task.Delay(10_000);
        oldBackend.OnDataReceived -= HandleDataReceived;
        try { await oldBackend.DisposeAsync(); }
        catch { }
        _logger.LogDebug("[RELAY] {Backend} disposed (grace period ended)", oldBackend.GetType().Name);
    }

    /// <summary>
    /// Check if both sides have confirmed UDP readiness and complete the switch if so.
    /// Called after setting either _localUdpReady or _remoteUdpReady.
    /// </summary>
    protected async Task TryCompleteSwitchAsync()
    {
        if (_localUdpReady && _remoteUdpReady && _pendingUdpBackend != null)
        {
            var pending = _pendingUdpBackend;
            _pendingUdpBackend = null;
            await SwitchBackendAsync(pending);
            _logger.LogInformation("Both sides confirmed UDP - backend switched to direct");
            OnConnectionStateChanged?.Invoke("udp-upgraded");

            // Start quality monitoring on the new UDP backend
            if (pending is UdpTransportBackend udp)
                StartQualityMonitor(udp, udp.ProbeRtt);
        }
    }

    /// <summary>
    /// Start the connection quality monitor. Called after switching to UDP backend.
    /// Feeds loss rate from UdpTransportBackend every 10 seconds.
    /// </summary>
    protected void StartQualityMonitor(UdpTransportBackend udpBackend, TimeSpan? probeRtt)
    {
        _qualityMonitor = new ConnectionQualityMonitor(_logger);
        _qualityMonitor.OnQualityChanged += q => OnConnectionQualityChanged?.Invoke(q);
        if (probeRtt.HasValue)
            _qualityMonitor.RecordProbeRtt(probeRtt.Value);

        _qualityUpdateTimer = new Timer(_ =>
        {
            if (_disposed || !_connected) return;
            try
            {
                var (lossRate, messageCount) = udpBackend.GetAndResetLossStats();
                _qualityMonitor.Update(lossRate, messageCount);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Quality monitor update failed");
            }
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

        _logger.LogInformation("[TRANSPORT] Connection quality monitor started (10s interval)");
    }

    private void StopQualityMonitor()
    {
        _qualityUpdateTimer?.Dispose();
        _qualityUpdateTimer = null;
        _qualityMonitor = null;
    }

    private void HandleBackendDisconnected()
    {
        if (!_connected || _disposed)
        {
            _logger.LogWarning("[DISCONNECT-DIAG] HandleBackendDisconnected SUPPRESSED: connected={Connected}, disposed={Disposed}",
                _connected, _disposed);
            return;
        }
        _connected = false;
        _logger.LogWarning("Transport backend disconnected");
        _videoSendQueue.Writer.TryComplete();
        _cts?.Cancel();
        OnConnectionStateChanged?.Invoke("disconnected");
    }

    private async Task VideoSendLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Video send loop started");
        _videoSendReady?.TrySetResult();

        var consecutiveFailures = 0;
        var firstFrameSent = false;
        try
        {
            await foreach (var (data, length) in _videoSendQueue.Reader.ReadAllAsync(ct))
            {
                if (!_connected || _disposed || _backend == null) break;

                try
                {
                    // Build frame: [1 byte channel=video][video data]
                    var frame = new byte[1 + length];
                    frame[0] = ChannelVideo;
                    Buffer.BlockCopy(data, 0, frame, 1, length);

                    // Encrypt
                    byte[] encrypted;
                    if (_encryption != null)
                        encrypted = _encryption.Encrypt(frame, 0, frame.Length);
                    else
                        encrypted = frame;

                    await _sendLock.WaitAsync(ct);
                    try
                    {
                        await _backend.SendAsync(encrypted, 0, encrypted.Length, ct);
                    }
                    finally { _sendLock.Release(); }

                    if (!firstFrameSent)
                    {
                        _logger.LogInformation("Video send loop: first frame sent ({Length} bytes)", length);
                        firstFrameSent = true;
                    }

                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    if (consecutiveFailures <= 3)
                        _logger.LogWarning(ex, "Video send error ({Count})", consecutiveFailures);

                    if (consecutiveFailures >= 10)
                    {
                        _logger.LogError("Video send: {Count} consecutive failures - disconnecting", consecutiveFailures);
                        HandleBackendDisconnected();
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Video send loop error");
        }
        _logger.LogInformation("Video send loop exited (firstFrameSent={FirstFrame})", firstFrameSent);
    }

    #endregion

    public virtual async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _connected = false;

        StopQualityMonitor();

        // Dispose pending UDP backend if switch never completed
        if (_pendingUdpBackend != null)
        {
            await _pendingUdpBackend.DisposeAsync();
            _pendingUdpBackend = null;
        }

        // Unsubscribe from backend
        if (_backend != null)
        {
            _backend.OnDataReceived -= HandleDataReceived;
            await _backend.DisposeAsync();
        }

        _cts?.Cancel();
        _videoSendQueue.Writer.TryComplete();

        // Wait for video send loop to finish
        if (_videoSendTask != null) try { await _videoSendTask; } catch { }

        // Dispose encryption
        _encryption?.Dispose();

        _cts?.Dispose();
        _sendLock.Dispose();

        _logger.LogInformation("StreamTransport disposed");
    }
}
