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
    private bool _disposed;
    protected bool _connected;
    private int _controlSendFailures;

    // Channel IDs for multiplexing over a single transport
    protected const byte ChannelControl = 0;
    protected const byte ChannelVideo = 1;
    protected const byte ChannelFileData = 2;
    protected const byte ChannelFileSignaling = 3;

    /// <summary>Raised when a JSON control message is received.</summary>
    public event Func<string, Task>? OnControlMessage;

    /// <summary>Raised when H.264 video frame NALUs are received.</summary>
    public event Action<byte[], int>? OnVideoData;

    /// <summary>Raised when binary file data is received.</summary>
    public event Func<byte[], Task>? OnFileData;

    /// <summary>Raised when a JSON file signaling message is received.</summary>
    public event Func<string, Task>? OnFileSignalingMessage;

    /// <summary>Raised when the transport connects or disconnects.</summary>
    public event Action<string>? OnConnectionStateChanged;

    public bool IsConnected => _connected && !_disposed;

    /// <summary>Whether the transport is currently using a direct UDP backend (vs WebSocket relay).</summary>
    public bool IsDirectUdp => _backend is UdpTransportBackend;

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

    private async ValueTask<bool> SendFrameAsync(byte channel, byte[] payload, int offset, int length)
    {
        if (!_connected || _disposed || _backend == null) return false;

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

    private void HandleDataReceived(byte[] data, int length)
    {
        if (_disposed || !_connected) return;

        try
        {
            // Decrypt
            byte[] plaintext;
            if (_encryption != null)
                plaintext = _encryption.Decrypt(data, 0, length);
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
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            _logger.LogWarning("Decryption failed (bad key or corrupted data): {Error}", ex.Message);
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
            _videoSendTask = Task.Run(() => VideoSendLoopAsync(_cts.Token));

        _connected = true;
        OnConnectionStateChanged?.Invoke("connected");
        _logger.LogInformation("Transport started (backend={Backend}, videoSend={VideoSend})",
            backend.GetType().Name, enableVideoSend);
    }

    /// <summary>
    /// Switch to a different transport backend (e.g., upgrade from WebSocket relay to direct UDP).
    /// The old backend is disposed after switching.
    /// </summary>
    protected async Task SwitchBackendAsync(ITransportBackend newBackend)
    {
        var oldBackend = _backend;
        if (oldBackend != null)
        {
            oldBackend.OnDataReceived -= HandleDataReceived;
            oldBackend.OnDisconnected -= HandleBackendDisconnected;
        }

        _backend = newBackend;
        newBackend.OnDataReceived += HandleDataReceived;
        newBackend.OnDisconnected += HandleBackendDisconnected;

        if (oldBackend != null)
        {
            await oldBackend.DisposeAsync();
        }

        _logger.LogInformation("Transport backend switched to {Backend}", newBackend.GetType().Name);
    }

    private void HandleBackendDisconnected()
    {
        if (!_connected || _disposed) return;
        _connected = false;
        _logger.LogWarning("Transport backend disconnected");
        _videoSendQueue.Writer.TryComplete();
        _cts?.Cancel();
        OnConnectionStateChanged?.Invoke("disconnected");
    }

    private async Task VideoSendLoopAsync(CancellationToken ct)
    {
        var consecutiveFailures = 0;
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
                        _logger.LogError("Video send: {Count} consecutive failures — disconnecting", consecutiveFailures);
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
    }

    #endregion

    public virtual async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _connected = false;

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
