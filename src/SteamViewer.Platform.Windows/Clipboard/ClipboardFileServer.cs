using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Platform.Windows.Clipboard;

/// <summary>
/// Serves file chunks on demand when the remote side's IStream requests data.
/// Maintains open file handles for the current clipboard file set with idle timeout.
/// </summary>
public sealed class ClipboardFileServer : IDisposable
{
    private readonly ILogger _logger;
    private readonly Func<byte[], Task> _sendAsync;
    private string[] _currentPaths = Array.Empty<string>();
    private readonly ConcurrentDictionary<int, CachedFileHandle> _fileHandles = new();
    private readonly Timer _idleTimer;

    private const int IdleTimeoutMs = 30_000; // Close idle file handles after 30s

    public ClipboardFileServer(ILogger logger, Func<byte[], Task> sendAsync)
    {
        _logger = logger;
        _sendAsync = sendAsync;
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
        _currentPaths = paths;
        _logger.LogDebug("File server updated with {Count} files", paths.Length);
    }

    /// <summary>
    /// Handle an incoming FileContentsRequest — read the requested chunk and send response.
    /// </summary>
    public async Task HandleRequestAsync(ClipboardFileMessage.FileContentsRequest request)
    {
        try
        {
            if (request.FileIndex < 0 || request.FileIndex >= _currentPaths.Length)
            {
                await SendErrorResponse(request.StreamId, $"Invalid file index: {request.FileIndex}");
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
            var bytesToRead = request.BytesRequested;

            // Clamp to remaining bytes
            long remaining = stream.Length - request.Position;
            if (remaining <= 0)
            {
                // EOF — send empty response
                var eofResponse = new ClipboardFileMessage.FileContentsResponse(
                    request.StreamId, Array.Empty<byte>());
                await SendResponse(eofResponse);
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

            var response = new ClipboardFileMessage.FileContentsResponse(request.StreamId, data);
            await SendResponse(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error serving file contents for index {Index}", request.FileIndex);
            await SendErrorResponse(request.StreamId, ex.Message);
        }
    }

    private async Task SendResponse(ClipboardFileMessage.FileContentsResponse response)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes<ClipboardFileMessage>(response);
        await _sendAsync(json);
    }

    private async Task SendErrorResponse(int streamId, string message)
    {
        var response = new ClipboardFileMessage.FileContentsResponse(
            streamId, null, IsError: true, ErrorMessage: message);
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes<ClipboardFileMessage>(response);
        await _sendAsync(json);
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
}
