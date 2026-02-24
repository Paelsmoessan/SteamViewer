using System.Buffers.Binary;
using System.Net.Quic;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace SteamViewer.Client.Core.Network;

/// <summary>
/// QUIC-based transport replacing WebRTC PeerConnection + data channels.
/// Uses QUIC streams over UDP: one bidirectional for control, one unidirectional for video.
///
/// Control protocol: [4 bytes big-endian: payload length][1 byte: channel][payload]
///   Channel 0 = JSON control (commands, keyboard, clipboard, cursor, mouse)
///   Channel 1 = Binary file data
///   Channel 2 = JSON file signaling
///
/// Video protocol: [4 bytes big-endian: payload length][payload = H.264 NALUs]
/// </summary>
public abstract class StreamTransport : IAsyncDisposable
{
    protected readonly ILogger _logger;
    protected QuicConnection? _connection;
    protected QuicStream? _controlStream;  // bidirectional
    protected QuicStream? _videoStream;    // unidirectional host→viewer
    protected CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _controlWriteLock = new(1, 1);
    private readonly SemaphoreSlim _videoWriteLock = new(1, 1);
    private readonly Channel<(byte[] data, int length)> _videoSendQueue;
    private Task? _controlReadTask;
    private Task? _videoReadTask;
    private Task? _videoSendTask;
    private bool _disposed;
    protected bool _connected;

    // Channel IDs for control connection multiplexing
    protected const byte ChannelControl = 0;
    protected const byte ChannelFileData = 1;
    protected const byte ChannelFileSignaling = 2;

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
        if (_controlStream == null) return false;
        var payload = Encoding.UTF8.GetBytes(json);
        return await WriteControlFrameAsync(ChannelControl, payload);
    }

    /// <summary>Send binary file data.</summary>
    public async ValueTask<bool> SendFileDataAsync(byte[] data)
    {
        if (_controlStream == null) return false;
        return await WriteControlFrameAsync(ChannelFileData, data);
    }

    /// <summary>Send JSON file signaling message (FormatList, FileContentsRequest, etc.).</summary>
    public async ValueTask<bool> SendFileSignalingAsync(string json)
    {
        if (_controlStream == null) return false;
        var payload = Encoding.UTF8.GetBytes(json);
        return await WriteControlFrameAsync(ChannelFileSignaling, payload);
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

    private async ValueTask<bool> WriteControlFrameAsync(byte channel, byte[] payload)
    {
        if (_controlStream == null) return false;

        await _controlWriteLock.WaitAsync();
        try
        {
            // [4 bytes: length (includes channel byte)][1 byte: channel][payload]
            var header = new byte[5];
            BinaryPrimitives.WriteInt32BigEndian(header, payload.Length + 1);
            header[4] = channel;

            await _controlStream.WriteAsync(header);
            await _controlStream.WriteAsync(payload);
            await _controlStream.FlushAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Control write failed");
            return false;
        }
        finally
        {
            _controlWriteLock.Release();
        }
    }

    #endregion

    #region Read Loops

    protected void StartReadLoops()
    {
        _cts = new CancellationTokenSource();
        _controlReadTask = Task.Run(() => ControlReadLoopAsync(_cts.Token));

        // Only start video read loop if the stream is readable (viewer side — inbound unidirectional)
        if (_videoStream?.CanRead == true)
            _videoReadTask = Task.Run(() => VideoReadLoopAsync(_cts.Token));

        // Only start video send loop if the stream is writable (host side — outbound unidirectional)
        if (_videoStream?.CanWrite == true)
            _videoSendTask = Task.Run(() => VideoSendLoopAsync(_cts.Token));

        _connected = true;
        OnConnectionStateChanged?.Invoke("connected");
    }

    private async Task ControlReadLoopAsync(CancellationToken ct)
    {
        var headerBuf = new byte[5]; // 4 bytes length + 1 byte channel
        try
        {
            while (!ct.IsCancellationRequested && _controlStream != null)
            {
                // Read header
                await ReadExactAsync(_controlStream, headerBuf, 0, 5, ct);
                var totalLength = BinaryPrimitives.ReadInt32BigEndian(headerBuf);
                var channel = headerBuf[4];
                var payloadLength = totalLength - 1;

                if (payloadLength <= 0 || payloadLength > 16 * 1024 * 1024) // 16MB max
                {
                    _logger.LogWarning("Invalid control frame length: {Length}", payloadLength);
                    break;
                }

                var payload = new byte[payloadLength];
                await ReadExactAsync(_controlStream, payload, 0, payloadLength, ct);

                switch (channel)
                {
                    case ChannelControl:
                        var json = Encoding.UTF8.GetString(payload);
                        if (OnControlMessage != null)
                            await OnControlMessage.Invoke(json);
                        break;

                    case ChannelFileData:
                        if (OnFileData != null)
                            await OnFileData.Invoke(payload);
                        break;

                    case ChannelFileSignaling:
                        var fileJson = Encoding.UTF8.GetString(payload);
                        if (OnFileSignalingMessage != null)
                            await OnFileSignalingMessage.Invoke(fileJson);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (QuicException qex)
        {
            _logger.LogWarning("QUIC control stream error: {Error}", qex.QuicError);
        }
        catch (IOException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Control read loop error");
        }

        _logger.LogInformation("Control read loop ended");
        _connected = false;
        OnConnectionStateChanged?.Invoke("disconnected");
    }

    private async Task VideoReadLoopAsync(CancellationToken ct)
    {
        var headerBuf = new byte[4];
        try
        {
            while (!ct.IsCancellationRequested && _videoStream != null)
            {
                // Read 4-byte length prefix
                await ReadExactAsync(_videoStream, headerBuf, 0, 4, ct);
                var length = BinaryPrimitives.ReadInt32BigEndian(headerBuf);

                if (length <= 0 || length > 4 * 1024 * 1024) // 4MB max per frame
                {
                    _logger.LogWarning("Invalid video frame length: {Length}", length);
                    break;
                }

                var frameData = new byte[length];
                await ReadExactAsync(_videoStream, frameData, 0, length, ct);

                OnVideoData?.Invoke(frameData, length);
            }
        }
        catch (OperationCanceledException) { }
        catch (QuicException qex)
        {
            _logger.LogWarning("QUIC video stream error: {Error}", qex.QuicError);
        }
        catch (IOException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Video read loop error");
        }

        _logger.LogInformation("Video read loop ended");
    }

    private async Task VideoSendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (data, length) in _videoSendQueue.Reader.ReadAllAsync(ct))
            {
                if (_videoStream == null) break;

                await _videoWriteLock.WaitAsync(ct);
                try
                {
                    var header = new byte[4];
                    BinaryPrimitives.WriteInt32BigEndian(header, length);
                    await _videoStream.WriteAsync(header, ct);
                    await _videoStream.WriteAsync(data.AsMemory(0, length), ct);
                    await _videoStream.FlushAsync(ct);
                }
                finally
                {
                    _videoWriteLock.Release();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Video send loop error");
        }
    }

    protected static async Task ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), ct);
            if (read == 0)
                throw new IOException("Stream ended");
            totalRead += read;
        }
    }

    #endregion

    public virtual async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _connected = false;

        _cts?.Cancel();
        _videoSendQueue.Writer.TryComplete();

        // Wait for loops to finish
        if (_controlReadTask != null) try { await _controlReadTask; } catch { }
        if (_videoReadTask != null) try { await _videoReadTask; } catch { }
        if (_videoSendTask != null) try { await _videoSendTask; } catch { }

        // Dispose QUIC streams
        if (_controlStream != null) try { await _controlStream.DisposeAsync(); } catch { }
        if (_videoStream != null) try { await _videoStream.DisposeAsync(); } catch { }

        // Close and dispose QUIC connection
        if (_connection != null)
        {
            try { await _connection.CloseAsync(0); } catch { }
            try { await _connection.DisposeAsync(); } catch { }
        }

        _cts?.Dispose();
        _controlWriteLock.Dispose();
        _videoWriteLock.Dispose();

        _logger.LogInformation("StreamTransport disposed");
    }
}
