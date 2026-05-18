using Microsoft.Extensions.Logging;
using SteamViewer.Common.Protocol;
using SteamViewer.Platform.Windows.Clipboard;
using System.Text.Json;

namespace SteamViewer.App.Services.Models;

// Clipboard concerns for ViewerSession: text + file clipboard monitoring,
// host/viewer round-trip, format-list send, file-data binary routing.
public sealed partial class ViewerSession
{
    // Clipboard file transfer — viewer monitors clipboard and receives remote files
    private ClipboardMonitor? _clipboardMonitor;
    private ClipboardFileServer? _clipboardFileServer;
    private ClipboardFileWriter? _clipboardFileWriter;

    /// <summary>
    /// Raised when clipboard data is received from the host.
    /// </summary>
    public event Action<string, string>? OnClipboardReceived;

    /// <summary>
    /// Request the host's clipboard contents.
    /// </summary>
    public Task RequestClipboardAsync()
        => SendAsync<ClipboardMessage>(new ClipboardMessage.Request(), "clipboard request");

    /// <summary>
    /// Send clipboard data to the host.
    /// </summary>
    public Task SendClipboardAsync(string format, string data)
        => SendAsync<ClipboardMessage>(new ClipboardMessage.Set(format, data), "clipboard");

    /// <summary>
    /// Send clipboard data to the host and trigger paste.
    /// </summary>
    public Task SendClipboardPasteAsync(string format, string data)
        => SendAsync<ClipboardMessage>(new ClipboardMessage.Paste(format, data), "clipboard paste");

    /// <summary>
    /// Record that the viewer just wrote text to its own local clipboard
    /// (typically from a host->viewer clipboard_data sync). Forwards to the
    /// monitor so its next WM_CLIPBOARDUPDATE is suppressed by hash match
    /// instead of bouncing back to the host as a clipboard_set echo.
    /// Public surface lets RemoteViewer.razor call it before TrySetClipboardNative.
    /// </summary>
    public void RecordSelfWriteText(string text) => _clipboardMonitor?.RecordSelfWriteText(text);

    private void StartClipboardFileTransfer()
    {
        if (!OperatingSystem.IsWindows() || _transport == null) return;

        try
        {
            _clipboardFileServer = new ClipboardFileServer(
                _loggerFactory.CreateLogger<ClipboardFileServer>(),
                async (data) => { return await _transport!.SendFileDataAsync(data); },
                async (json) => await _transport!.SendFileSignalingAsync(json));

            _clipboardMonitor = new ClipboardMonitor(_loggerFactory.CreateLogger<ClipboardMonitor>());
            _clipboardMonitor.ClipboardFilesDetected += OnClipboardFilesDetected;
            _clipboardMonitor.ClipboardTextDetected += OnClipboardTextDetected;
            _clipboardMonitor.Start();

            _clipboardFileWriter = new ClipboardFileWriter(
                _loggerFactory.CreateLogger<ClipboardFileWriter>(),
                async (request) =>
                {
                    var json = JsonSerializer.Serialize<ClipboardFileMessage>(request);
                    await _transport.SendFileSignalingAsync(json);
                },
                _clipboardMonitor,
                async (startMsg) =>
                {
                    var json = JsonSerializer.Serialize<ClipboardFileMessage>(startMsg);
                    await _transport.SendFileSignalingAsync(json);
                },
                async (stopMsg) =>
                {
                    var json = JsonSerializer.Serialize<ClipboardFileMessage>(stopMsg);
                    await _transport.SendFileSignalingAsync(json);
                },
                async (data) => await _transport!.SendFileDataAsync(data));
            _clipboardFileWriter.Start();

            _logger.LogInformation("Session {SessionId}: Clipboard file transfer initialized", SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session {SessionId}: Failed to initialize clipboard file transfer", SessionId);
        }
    }

    private void OnClipboardFilesDetected(ClipboardFileInfo[] files, string[] localPaths)
    {
        _logger.LogDebug("Session {SessionId}: OnClipboardFilesDetected entry: files={Count} transport={Transport} connected={Connected}",
            SessionId, files.Length,
            _transport != null ? "set" : "null",
            _transport?.IsConnected);
        if (_transport == null || !_transport.IsConnected)
        {
            _logger.LogWarning("Session {SessionId}: OnClipboardFilesDetected: dropping {Count} file(s) — transport not ready (transport={Transport}, connected={Connected})",
                SessionId, files.Length, _transport != null ? "set" : "null", _transport?.IsConnected);
            return;
        }

        try
        {
            _clipboardFileServer?.SetFilePaths(localPaths);

            var formatList = new ClipboardFileMessage.FormatList(files);
            var json = JsonSerializer.Serialize<ClipboardFileMessage>(formatList);

            _ = Task.Run(() => SendClipboardFormatListAsync(json, files.Length));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session {SessionId}: Error handling clipboard files detected", SessionId);
        }
    }

    /// <summary>
    /// Send a clipboard file-format-list JSON to the host 3x with 500 ms gaps for UDP
    /// reliability (idempotent on the receiver). Extracted from OnClipboardFilesDetected
    /// as a named, testable unit. Mechanical-only: same body, hoisted out of the inline
    /// Task.Run lambda so the outer method stays focused on transport-gating.
    /// </summary>
    private async Task SendClipboardFormatListAsync(string json, int fileCount)
    {
        try
        {
            // Send 3x with 500ms gaps for UDP reliability (idempotent on receiver)
            for (int i = 0; i < 3; i++)
            {
                if (_transport == null || !_transport.IsConnected)
                {
                    _logger.LogWarning("Session {SessionId}: Clipboard format list send loop break at i={Iteration}: transport={Transport} connected={Connected}",
                        SessionId, i, _transport != null ? "set" : "null", _transport?.IsConnected);
                    break;
                }
                var sent = await _transport.SendFileSignalingAsync(json);
                if (i == 0) _logger.LogInformation("Session {SessionId}: Sent clipboard file format list: {Count} files (sent={Sent}, attempt={Attempt})", SessionId, fileCount, sent, i);
                else _logger.LogDebug("Session {SessionId}: Re-sent clipboard file format list (sent={Sent}, attempt={Attempt})", SessionId, sent, i);
                if (i < 2) await Task.Delay(500);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session {SessionId}: Failed to send clipboard file format list", SessionId);
        }
    }

    /// <summary>
    /// Auto-push viewer's local clipboard text to host on each WM_CLIPBOARDUPDATE
    /// the monitor flags as text. Mirrors the host-side OnClipboardTextDetected
    /// pattern but sends `clipboard_set` (which host's HandleClipboardSetAsync
    /// already handles) instead of `clipboard_data`. Echo loop is prevented by
    /// the monitor's hash-based suppression on both sides — viewer's own
    /// HandleClipboardReceived calls RecordSelfWriteText before writing.
    /// </summary>
    private void OnClipboardTextDetected(string text)
    {
        _logger.LogDebug("Session {SessionId}: OnClipboardTextDetected entry: len={Length} transport={Transport} connected={Connected}",
            SessionId, text.Length,
            _transport != null ? "set" : "null",
            _transport?.IsConnected);
        if (_transport == null || !_transport.IsConnected)
        {
            _logger.LogWarning("Session {SessionId}: OnClipboardTextDetected: dropping {Length}-char text — transport not ready (transport={Transport}, connected={Connected})",
                SessionId, text.Length, _transport != null ? "set" : "null", _transport?.IsConnected);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var msg = JsonSerializer.Serialize<ClipboardMessage>(new ClipboardMessage.Set("text", text));
                var sent = await _transport.SendControlAsync(msg);
                _logger.LogInformation("Session {SessionId}: Sent viewer clipboard text to host: {Length} chars (sent={Sent})",
                    SessionId, text.Length, sent);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session {SessionId}: Failed to send viewer clipboard text to host", SessionId);
            }
        });
    }

    private async Task HandleFileChannelMessage(string json)
    {
        try
        {
            var message = JsonSerializer.Deserialize<ClipboardFileMessage>(json);
            if (message == null) return;

            switch (message)
            {
                case ClipboardFileMessage.FormatList formatList:
                    _clipboardFileWriter?.SetClipboard(formatList.Files);
                    break;
                case ClipboardFileMessage.FileContentsRequest request:
                    if (_clipboardFileServer != null)
                        await _clipboardFileServer.HandleRequestAsync(request);
                    break;
                case ClipboardFileMessage.StartStreaming startStreaming:
                    _clipboardFileServer?.HandleStartStreaming(startStreaming.FileIndex);
                    break;
                case ClipboardFileMessage.StopStreaming stopStreaming:
                    _clipboardFileServer?.HandleStopStreaming(stopStreaming.FileIndex);
                    break;
                case ClipboardFileMessage.TransferProgress progress:
                    _logger.LogInformation("Session {SessionId}: Remote transfer progress: {FileName} — {Transferred}/{Total} ({Speed} MB/s)",
                        SessionId, progress.FileName, FormatBytes(progress.BytesTransferred), FormatBytes(progress.TotalBytes), progress.SpeedMBps);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session {SessionId}: Failed to handle file channel message", SessionId);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    private Task HandleFileDataBinary(byte[] data)
    {
        // Route ACKs to file server (sender), everything else to file writer (receiver)
        if (data.Length >= 8)
        {
            int flags = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4, 4));
            if (flags == ClipboardFileServer.FlagPushAck)
            {
                int fileIndex = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0, 4));
                long bytesAcked = data.Length >= 16
                    ? System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(8, 8))
                    : 0;
                _clipboardFileServer?.HandlePushAck(fileIndex, bytesAcked);
                return Task.CompletedTask;
            }
        }
        _clipboardFileWriter?.HandleBinaryFileContentsResponse(data);
        return Task.CompletedTask;
    }

    private void StopClipboardFileTransfer()
    {
        _clipboardMonitor?.Dispose();
        _clipboardMonitor = null;
        _clipboardFileServer?.Dispose();
        _clipboardFileServer = null;
        _clipboardFileWriter?.Dispose();
        _clipboardFileWriter = null;
    }
}
