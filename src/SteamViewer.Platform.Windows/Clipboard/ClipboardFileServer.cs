using System.Buffers.Binary;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Platform.Windows.Clipboard;

/// <summary>
/// Serves file chunks on demand when the remote side's IStream requests data.
/// Maintains open file handles for the current clipboard file set with idle timeout.
/// Responses are sent as raw binary on a dedicated file-data channel (no JSON/base64).
/// </summary>
public sealed class ClipboardFileServer : IDisposable
{
    private readonly ILogger _logger;
    private readonly Func<byte[], Task> _sendBinaryAsync;
    private readonly Func<string, Task>? _sendProgressAsync;
    private string[] _currentPaths = Array.Empty<string>();
    private readonly ConcurrentDictionary<int, CachedFileHandle> _fileHandles = new();
    private readonly ConcurrentDictionary<int, TransferTracker> _transferTrackers = new();
    private readonly Timer _idleTimer;

    private const int IdleTimeoutMs = 30_000; // Close idle file handles after 30s
    private const int MaxChunkSize = 65_536;   // 64KB — well under SCTP 256KB limit
    private const int ProgressIntervalMs = 500; // Send progress every 500ms

    // Binary response flags (in 4-byte flags field)
    internal const int FlagSuccess = 0x00;
    internal const int FlagError = 0x01;
    internal const int FlagEof = 0x02;

    public ClipboardFileServer(ILogger logger, Func<byte[], Task> sendBinaryAsync, Func<string, Task>? sendProgressAsync = null)
    {
        _logger = logger;
        _sendBinaryAsync = sendBinaryAsync;
        _sendProgressAsync = sendProgressAsync;
        _idleTimer = new Timer(CleanupIdleHandles, null, IdleTimeoutMs, IdleTimeoutMs);
    }

    /// <summary>
    /// Update the file paths for the current clipboard content.
    /// Called when ClipboardMonitor detects new CF_HDROP.
    /// </summary>
    public void SetFilePaths(string[] paths)
    {
        // Close any existing handles — clipboard changed
        CloseAllHandles();
        _transferTrackers.Clear();
        _currentPaths = paths;
        _logger.LogDebug("File server updated with {Count} files", paths.Length);
    }

    /// <summary>
    /// Handle an incoming FileContentsRequest — read the requested chunk and send binary response.
    /// </summary>
    public async Task HandleRequestAsync(ClipboardFileMessage.FileContentsRequest request)
    {
        try
        {
            if (request.FileIndex < 0 || request.FileIndex >= _currentPaths.Length)
            {
                await SendBinaryError(request.StreamId, $"Invalid file index: {request.FileIndex}");
                return;
            }

            var path = _currentPaths[request.FileIndex];

            // Get or open cached file handle
            var handle = _fileHandles.GetOrAdd(request.FileIndex, idx =>
            {
                _logger.LogDebug("Opening file for serving: {Path}", path);
                return new CachedFileHandle(
                    new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
            });

            handle.Touch(); // Reset idle timeout

            var stream = handle.Stream;
            var bytesToRead = Math.Min(request.BytesRequested, MaxChunkSize);

            // Clamp to remaining bytes
            long remaining = stream.Length - request.Position;
            if (remaining <= 0)
            {
                // EOF
                await SendBinaryResponse(request.StreamId, FlagEof, Array.Empty<byte>());
                return;
            }

            if (bytesToRead > remaining)
                bytesToRead = (int)remaining;

            // Seek and read
            stream.Position = request.Position;
            var buffer = new byte[bytesToRead];
            int totalRead = 0;
            while (totalRead < bytesToRead)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(totalRead, bytesToRead - totalRead));
                if (read == 0) break;
                totalRead += read;
            }

            // Trim buffer if we read less than requested
            var data = totalRead == buffer.Length ? buffer : buffer[..totalRead];

            await SendBinaryResponse(request.StreamId, FlagSuccess, data);

            // Track transfer progress
            await TrackProgressAsync(request.FileIndex, data.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error serving file contents for index {Index}", request.FileIndex);
            await SendBinaryError(request.StreamId, ex.Message);
        }
    }

    private async Task TrackProgressAsync(int fileIndex, int bytesServed)
    {
        if (fileIndex < 0 || fileIndex >= _currentPaths.Length) return;

        var path = _currentPaths[fileIndex];
        var handle = _fileHandles.GetValueOrDefault(fileIndex);
        long totalSize = handle?.Stream.Length ?? 0;

        var tracker = _transferTrackers.GetOrAdd(fileIndex, _ => new TransferTracker());
        tracker.AddBytes(bytesServed);

        long totalServed = tracker.TotalBytes;
        double elapsedSec = tracker.ElapsedSeconds;
        double speedMBps = elapsedSec > 0 ? (totalServed / 1_048_576.0) / elapsedSec : 0;

        // Log + send progress periodically
        if (tracker.ShouldReport(ProgressIntervalMs))
        {
            string fileName = Path.GetFileName(path);
            _logger.LogInformation("File transfer [sending]: {FileName} — {Transferred}/{Total} ({Speed:F1} MB/s)",
                fileName, FormatBytes(totalServed), FormatBytes(totalSize), speedMBps);

            if (_sendProgressAsync != null)
            {
                var progress = new ClipboardFileMessage.TransferProgress(
                    fileIndex, fileName, totalServed, totalSize, Math.Round(speedMBps, 2));
                var json = System.Text.Json.JsonSerializer.Serialize<ClipboardFileMessage>(progress);
                await _sendProgressAsync(json);
            }
        }

        // Log completion
        if (totalSize > 0 && totalServed >= totalSize)
        {
            string fileName = Path.GetFileName(path);
            _logger.LogInformation("File transfer [sending]: {FileName} COMPLETE — {Total} in {Elapsed:F1}s ({Speed:F1} MB/s)",
                fileName, FormatBytes(totalSize), elapsedSec, speedMBps);
            _transferTrackers.TryRemove(fileIndex, out _);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    /// <summary>
    /// Send binary response: [4 bytes streamId BE] [4 bytes flags BE] [N bytes data]
    /// </summary>
    private async Task SendBinaryResponse(int streamId, int flags, byte[] data)
    {
        var message = new byte[8 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(0, 4), streamId);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(4, 4), flags);
        data.CopyTo(message.AsSpan(8));
        await _sendBinaryAsync(message);
    }

    private async Task SendBinaryError(int streamId, string errorMessage)
    {
        var errorBytes = System.Text.Encoding.UTF8.GetBytes(errorMessage);
        await SendBinaryResponse(streamId, FlagError, errorBytes);
    }

    private void CleanupIdleHandles(object? state)
    {
        var now = Environment.TickCount64;
        foreach (var kvp in _fileHandles)
        {
            if (now - kvp.Value.LastAccessTick > IdleTimeoutMs)
            {
                if (_fileHandles.TryRemove(kvp.Key, out var handle))
                {
                    _logger.LogDebug("Closing idle file handle for index {Index}", kvp.Key);
                    handle.Dispose();
                }
            }
        }
    }

    private void CloseAllHandles()
    {
        foreach (var kvp in _fileHandles)
        {
            if (_fileHandles.TryRemove(kvp.Key, out var handle))
                handle.Dispose();
        }
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        CloseAllHandles();
    }

    private sealed class CachedFileHandle : IDisposable
    {
        public FileStream Stream { get; }
        public long LastAccessTick { get; private set; }

        public CachedFileHandle(FileStream stream)
        {
            Stream = stream;
            Touch();
        }

        public void Touch() => LastAccessTick = Environment.TickCount64;

        public void Dispose() => Stream.Dispose();
    }

    private sealed class TransferTracker
    {
        private readonly long _startTick = Environment.TickCount64;
        private long _totalBytes;
        private long _lastReportTick;

        public long TotalBytes => _totalBytes;
        public double ElapsedSeconds => (Environment.TickCount64 - _startTick) / 1000.0;

        public void AddBytes(int bytes)
        {
            Interlocked.Add(ref _totalBytes, bytes);
        }

        public bool ShouldReport(int intervalMs)
        {
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref _lastReportTick);
            if (now - last >= intervalMs)
            {
                Interlocked.Exchange(ref _lastReportTick, now);
                return true;
            }
            return false;
        }
    }
}
