using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
///
/// (Secure Desktop JPEG frames do NOT ride this mux - they use the SYSTEM helper's
///  separate video pipe. There is no channel 5 here.)
///
/// Each frame is AES-256-GCM encrypted before sending through the backend.
/// </summary>
public abstract class StreamTransport : IAsyncDisposable
{
    protected readonly ILogger _logger;
    // Per-instance short ID for log correlation. Generated at construction; constant for the
    // lifetime of this transport. Lets log lines from the same instance be grouped under a
    // single grep query and makes leaked-instance log streams identifiable.
    protected readonly string _instanceId = Guid.NewGuid().ToString("N").Substring(0, 8);
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
    private Task? _qualityMonitorTask;

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

    /// <summary>True when the transport can send a frame: connected, not disposed, backend present.
    /// Shared guard for the control-send and video-send paths. MemberNotNullWhen preserves the
    /// compiler's null-flow on _backend through the extracted check.</summary>
    [MemberNotNullWhen(true, nameof(_backend))]
    private bool CanSend() => _connected && !_disposed && _backend != null;

    private async ValueTask<bool> SendFrameAsync(byte channel, byte[] payload, int offset, int length)
    {
        if (!CanSend())
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
            var plaintext = DecryptFrame(data, length);
            if (plaintext == null || plaintext.Length < 1) return;
            DispatchFrame(plaintext);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            CountHardDecryptionFailure();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Data receive handler error");
        }
    }

    /// <summary>
    /// Decrypt a received frame. Returns the plaintext, or null when the AES-GCM session tag does not
    /// match (stale data from a prior session - fast-rejected without crypto cost, counted + logged).
    /// A hard CryptographicException propagates to the caller's catch (see CountHardDecryptionFailure).
    /// </summary>
    private byte[]? DecryptFrame(byte[] data, int length)
    {
        if (_encryption == null)
        {
            var copy = new byte[length];
            Buffer.BlockCopy(data, 0, copy, 0, length);
            return copy;
        }

        var plaintext = _encryption.Decrypt(data, 0, length);
        if (plaintext == null)
        {
            // Session tag mismatch: stale data from old session, fast-rejected without crypto cost
            var staleCount = Interlocked.Increment(ref _decryptionFailures);
            if (staleCount <= 3 || staleCount % 100 == 0)
                _logger.LogDebug("Stale session data dropped (tag mismatch, count={Count})", staleCount);
        }
        return plaintext;
    }

    /// <summary>Route a decrypted frame to the handler for the channel in its first byte.</summary>
    private void DispatchFrame(byte[] plaintext)
    {
        var channel = plaintext[0];
        switch (channel)
        {
            case ChannelControl:
                RaiseControlMessage(Encoding.UTF8.GetString(plaintext, 1, plaintext.Length - 1));
                break;
            case ChannelVideo:
                OnVideoData?.Invoke(ExtractPayload(plaintext), plaintext.Length - 1);
                break;
            case ChannelLossless:
                RaiseLosslessFrame(ExtractPayload(plaintext), plaintext.Length - 1);
                break;
            case ChannelFileData:
                RaiseFileData(ExtractPayload(plaintext));
                break;
            case ChannelFileSignaling:
                RaiseFileSignaling(Encoding.UTF8.GetString(plaintext, 1, plaintext.Length - 1));
                break;
            default:
                _logger.LogWarning("Unknown channel byte: {Channel}", channel);
                break;
        }
    }

    /// <summary>Copy the payload that follows the 1-byte channel header.</summary>
    private static byte[] ExtractPayload(byte[] plaintext)
    {
        var payload = new byte[plaintext.Length - 1];
        Buffer.BlockCopy(plaintext, 1, payload, 0, payload.Length);
        return payload;
    }

    private void RaiseControlMessage(string json)
    {
        if (OnControlMessage is not { } handler) return;
        _ = Task.Run(async () =>
        {
            try { await handler.Invoke(json); }
            catch (Exception ex) { _logger.LogWarning(ex, "Control handler error"); }
        });
    }

    private void RaiseLosslessFrame(byte[] data, int length)
    {
        // Lossless frames are infrequent one-shots - Task.Run is fine.
        if (OnLosslessFrame is not { } handler) return;
        _ = Task.Run(() =>
        {
            try { handler.Invoke(data, length); }
            catch (Exception ex) { _logger.LogWarning(ex, "Lossless frame handler error"); }
        });
    }

    private void RaiseFileData(byte[] fileData)
    {
        if (OnFileData is not { } handler) return;
        _ = Task.Run(async () =>
        {
            try { await handler.Invoke(fileData); }
            catch (Exception ex) { _logger.LogWarning(ex, "File data handler error"); }
        });
    }

    private void RaiseFileSignaling(string json)
    {
        if (OnFileSignalingMessage is not { } handler) return;
        _ = Task.Run(async () =>
        {
            try { await handler.Invoke(json); }
            catch (Exception ex) { _logger.LogWarning(ex, "File signaling handler error"); }
        });
    }

    /// <summary>
    /// Handle a hard decryption failure (CryptographicException). RFC 9147 (DTLS 1.3) pattern:
    /// silently drop invalid records, count failures. Stale data from old sessions is expected during
    /// reconnect and fails decryption because each session derives a unique key via HKDF + session nonce.
    /// </summary>
    private void CountHardDecryptionFailure()
    {
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
        if (!BothSidesUdpReady()) return;

        var pending = _pendingUdpBackend;
        _pendingUdpBackend = null;
        await SwitchBackendAsync(pending);
        _logger.LogInformation("Both sides confirmed UDP - backend switched to direct");
        OnConnectionStateChanged?.Invoke("udp-upgraded");

        // Start quality monitoring on the new UDP backend
        if (pending is UdpTransportBackend udp)
            StartQualityMonitor(udp, udp.ProbeRtt);
    }

    /// <summary>True when both sides have signalled UDP-ready and a pending backend is staged for switch.</summary>
    [MemberNotNullWhen(true, nameof(_pendingUdpBackend))]
    private bool BothSidesUdpReady() => _localUdpReady && _remoteUdpReady && _pendingUdpBackend != null;

    #region UDP-upgrade coordinator API

    // These members are driven by UdpUpgradeCoordinator (composition, not inheritance). Switch-state
    // (_pendingUdpBackend / _localUdpReady / _remoteUdpReady) stays owned here because the actual
    // backend swap (SwitchBackendAsync / TryCompleteSwitchAsync) lives here. The coordinator owns the
    // probe/accept logic and calls these to mutate switch-state.

    /// <summary>The coordinator's probe succeeded: subscribe our data handler to the (already-connected)
    /// UDP backend, stage it as the pending switch target, and mark our side ready.</summary>
    internal void RegisterPendingUdpBackend(UdpTransportBackend backend)
    {
        backend.OnDataReceived += HandleDataReceived; // Receive peer data before switch completes
        _pendingUdpBackend = backend;
        _localUdpReady = true;
    }

    /// <summary>Mark the remote side ready and complete the switch if both sides are now ready.</summary>
    internal async Task MarkRemoteUdpReadyAsync()
    {
        _remoteUdpReady = true;
        await TryCompleteSwitchAsync();
    }

    /// <summary>Complete the switch if both sides have already signalled ready (no-op otherwise).</summary>
    internal Task TryCompleteUdpSwitchAsync() => TryCompleteSwitchAsync();

    /// <summary>Abandon a pending UDP path (peer never confirmed): unsubscribe and drop it, staying on relay.</summary>
    internal void AbandonPendingUdpBackend()
    {
        if (_pendingUdpBackend != null)
            _pendingUdpBackend.OnDataReceived -= HandleDataReceived;
        _localUdpReady = false;
        _pendingUdpBackend = null;
    }

    internal bool LocalUdpReady => _localUdpReady;
    internal bool RemoteUdpReady => _remoteUdpReady;
    internal bool HasPendingUdp => _pendingUdpBackend != null;

    /// <summary>Logging context for collaborators (e.g. UdpUpgradeCoordinator) that reuse this
    /// transport's logger + correlation id instead of taking them as separate constructor args.</summary>
    internal ILogger Logger => _logger;
    internal string InstanceId => _instanceId;

    #endregion

    /// <summary>
    /// Start the connection quality monitor. Called after switching to UDP backend.
    /// Feeds loss rate from UdpTransportBackend every 10 seconds.
    ///
    /// Migrated from System.Threading.Timer to PeriodicTimer bound to _cts.Token
    /// so cancellation is deterministic: when _cts cancels (DisposeAsync), the loop
    /// exits via OperationCanceledException rather than relying on the BCL TimerQueue
    /// holding a root to the captured backend closure (which could keep the transport
    /// alive after disposal if a Dispose call was missed elsewhere).
    /// </summary>
    protected void StartQualityMonitor(UdpTransportBackend udpBackend, TimeSpan? probeRtt)
    {
        _qualityMonitor = new ConnectionQualityMonitor(_logger, _instanceId);
        _qualityMonitor.OnQualityChanged += q => OnConnectionQualityChanged?.Invoke(q);
        if (probeRtt.HasValue)
            _qualityMonitor.RecordProbeRtt(probeRtt.Value);

        var ct = _cts?.Token ?? CancellationToken.None;
        _qualityMonitorTask = Task.Run(() => QualityMonitorLoopAsync(udpBackend, ct), ct);

        _logger.LogInformation("[T:{InstanceId}] Connection quality monitor started (10s interval, PeriodicTimer bound to _cts)", _instanceId);
    }

    private async Task QualityMonitorLoopAsync(UdpTransportBackend udpBackend, CancellationToken ct)
    {
        _logger.LogDebug("[T:{InstanceId}] Quality monitor loop entered", _instanceId);
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (_disposed || !_connected)
                {
                    _logger.LogDebug("[T:{InstanceId}] Quality monitor tick guard fired: disposed={Disposed} connected={Connected} - exiting loop",
                        _instanceId, _disposed, _connected);
                    return;
                }
                try
                {
                    var (lossRate, messageCount) = udpBackend.GetAndResetLossStats();
                    _qualityMonitor?.Update(lossRate, messageCount);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[T:{InstanceId}] Quality monitor update failed", _instanceId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("[T:{InstanceId}] Quality monitor loop cancelled cleanly via _cts", _instanceId);
        }
        finally
        {
            _logger.LogDebug("[T:{InstanceId}] Quality monitor loop exited", _instanceId);
        }
    }

    private void StopQualityMonitor()
    {
        // Cancellation handled deterministically by _cts.Cancel() in DisposeAsync,
        // which causes PeriodicTimer.WaitForNextTickAsync to throw OperationCanceledException
        // and exit QualityMonitorLoopAsync. The task is awaited at the end of DisposeAsync.
        // Here we just null out the monitor reference so any in-flight tick sees null and skips.
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
                if (!CanSend()) break;

                try
                {
                    await SendEncryptedVideoFrameAsync(data, length, ct);

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

    /// <summary>Build [channel=video][data], encrypt, and send one frame under the send lock.
    /// Extracted from the send loop; the loop retains failure counting + the disconnect threshold.</summary>
    private async Task SendEncryptedVideoFrameAsync(byte[] data, int length, CancellationToken ct)
    {
        var frame = new byte[1 + length];
        frame[0] = ChannelVideo;
        Buffer.BlockCopy(data, 0, frame, 1, length);

        var encrypted = _encryption != null ? _encryption.Encrypt(frame, 0, frame.Length) : frame;

        await _sendLock.WaitAsync(ct);
        try
        {
            await _backend!.SendAsync(encrypted, 0, encrypted.Length, ct);
        }
        finally { _sendLock.Release(); }
    }

    #endregion

    public virtual async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _connected = false;

        StopQualityMonitor();

        // Dispose pending UDP backend if switch never completed.
        // (Unsubscribe first — folded in from the former subclass DisposeAsync override.)
        if (_pendingUdpBackend != null)
        {
            _pendingUdpBackend.OnDataReceived -= HandleDataReceived;
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

        // Wait for quality monitor loop to finish (cancelled via _cts)
        if (_qualityMonitorTask != null) try { await _qualityMonitorTask; } catch { }

        // Wait for video send loop to finish
        if (_videoSendTask != null) try { await _videoSendTask; } catch { }

        // Dispose encryption
        _encryption?.Dispose();

        _cts?.Dispose();
        _sendLock.Dispose();

        _logger.LogInformation("[T:{InstanceId}] StreamTransport disposed", _instanceId);
    }
}
