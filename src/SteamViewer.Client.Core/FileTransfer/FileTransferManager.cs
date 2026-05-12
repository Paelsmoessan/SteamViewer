using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Client.Core.FileTransfer;

/// <summary>
/// Manages file transfers over WebRTC data channel.
/// </summary>
public sealed class FileTransferManager : IAsyncDisposable
{
    private readonly ILogger<FileTransferManager> _logger;
    private readonly ConcurrentDictionary<Guid, FileTransferState> _transfers = new();
    private readonly Func<byte[], Task> _sendDataAsync;
    private bool _disposed;

    #region Events

    /// <summary>Fired when a transfer starts.</summary>
    public event EventHandler<FileTransferState>? TransferStarted;

    /// <summary>Fired when transfer progress updates.</summary>
    public event EventHandler<FileTransferState>? TransferProgress;

    /// <summary>Fired when a transfer completes.</summary>
    public event EventHandler<FileTransferState>? TransferCompleted;

    /// <summary>Fired when a transfer fails.</summary>
    public event EventHandler<FileTransferState>? TransferFailed;

    /// <summary>Fired when an incoming transfer request is received.</summary>
    public event EventHandler<FileTransferState>? IncomingTransferRequest;

    #endregion

    public FileTransferManager(ILogger<FileTransferManager> logger, Func<byte[], Task> sendDataAsync)
    {
        _logger = logger;
        _sendDataAsync = sendDataAsync;
    }

    /// <summary>
    /// Get all active transfers.
    /// </summary>
    public IEnumerable<FileTransferState> GetTransfers() => _transfers.Values;

    /// <summary>
    /// Send a file to the peer.
    /// </summary>
    public async Task SendFileAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("File not found", filePath);

        var fileInfo = new FileInfo(filePath);
        var transferId = Guid.NewGuid();

        var state = new FileTransferState(
            transferId,
            fileInfo.Name,
            (ulong)fileInfo.Length,
            FileTransferDirection.Sending)
        {
            LocalPath = filePath,
            Status = FileTransferStatus.Pending
        };

        _transfers[transferId] = state;

        _logger.LogInformation("Requesting file transfer: {Filename} ({Size} bytes)",
            state.Filename, state.FileSize);

        // Send transfer request
        var request = new FileTransferMessage.Request(transferId, state.Filename, state.FileSize);
        await SendMessageAsync(request, ct);

        TransferStarted?.Invoke(this, state);
    }

    /// <summary>
    /// Accept an incoming transfer request.
    /// </summary>
    public async Task AcceptTransferAsync(Guid transferId, string savePath, CancellationToken ct = default)
    {
        if (!_transfers.TryGetValue(transferId, out var state))
            throw new InvalidOperationException($"Transfer {transferId} not found");

        state.LocalPath = savePath;
        state.Status = FileTransferStatus.InProgress;
        state.StartTime = DateTimeOffset.UtcNow;

        // Create directory if needed
        var dir = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Open file for writing
        state.FileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None);

        _logger.LogInformation("Accepted transfer {TransferId}, saving to {Path}", transferId, savePath);

        // Send accept message
        var accept = new FileTransferMessage.Accept(transferId);
        await SendMessageAsync(accept, ct);

        TransferStarted?.Invoke(this, state);
    }

    /// <summary>
    /// Reject an incoming transfer request.
    /// </summary>
    public async Task RejectTransferAsync(Guid transferId, string reason = "Rejected by user", CancellationToken ct = default)
    {
        if (_transfers.TryRemove(transferId, out var state))
        {
            state.Status = FileTransferStatus.Rejected;
            state.ErrorMessage = reason;
            state.Cleanup();
        }

        _logger.LogInformation("Rejected transfer {TransferId}: {Reason}", transferId, reason);

        var reject = new FileTransferMessage.Reject(transferId, reason);
        await SendMessageAsync(reject, ct);
    }

    /// <summary>
    /// Cancel an active transfer.
    /// </summary>
    public async Task CancelTransferAsync(Guid transferId, CancellationToken ct = default)
    {
        if (_transfers.TryRemove(transferId, out var state))
        {
            state.Status = FileTransferStatus.Cancelled;
            state.Cleanup();

            var error = new FileTransferMessage.FileError(transferId, "Cancelled");
            await SendMessageAsync(error, ct);

            TransferFailed?.Invoke(this, state);
        }
    }

    /// <summary>
    /// Handle incoming file transfer message.
    /// </summary>
    public async Task HandleMessageAsync(FileTransferMessage message, CancellationToken ct = default)
    {
        switch (message)
        {
            case FileTransferMessage.Request request:
                await HandleRequestAsync(request, ct);
                break;
            case FileTransferMessage.Accept accept:
                await HandleAcceptAsync(accept, ct);
                break;
            case FileTransferMessage.Reject reject:
                HandleReject(reject);
                break;
            case FileTransferMessage.Chunk chunk:
                await HandleChunkAsync(chunk, ct);
                break;
            case FileTransferMessage.Complete complete:
                HandleComplete(complete);
                break;
            case FileTransferMessage.FileError error:
                HandleError(error);
                break;
            case FileTransferMessage.Progress progress:
                HandleProgress(progress);
                break;
        }
    }

    private async Task HandleRequestAsync(FileTransferMessage.Request request, CancellationToken ct)
    {
        await Task.CompletedTask; // currently sync body; reserve async sugar for future awaits
        _logger.LogInformation("Incoming file transfer request: {Filename} ({Size} bytes)",
            request.Filename, request.FileSize);

        var state = new FileTransferState(
            request.TransferId,
            request.Filename,
            request.FileSize,
            FileTransferDirection.Receiving)
        {
            Status = FileTransferStatus.Pending
        };

        _transfers[request.TransferId] = state;
        IncomingTransferRequest?.Invoke(this, state);
    }

    private async Task HandleAcceptAsync(FileTransferMessage.Accept accept, CancellationToken ct)
    {
        if (!_transfers.TryGetValue(accept.TransferId, out var state))
        {
            _logger.LogWarning("Accept for unknown transfer {TransferId}", accept.TransferId);
            return;
        }

        _logger.LogInformation("Transfer {TransferId} accepted, starting send", accept.TransferId);

        state.Status = FileTransferStatus.InProgress;
        state.StartTime = DateTimeOffset.UtcNow;
        state.FileStream = new FileStream(state.LocalPath!, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Start sending chunks
        await SendChunksAsync(state, ct);
    }

    private void HandleReject(FileTransferMessage.Reject reject)
    {
        if (_transfers.TryRemove(reject.TransferId, out var state))
        {
            state.Status = FileTransferStatus.Rejected;
            state.ErrorMessage = reject.Reason;
            state.Cleanup();

            _logger.LogInformation("Transfer {TransferId} rejected: {Reason}", reject.TransferId, reject.Reason);
            TransferFailed?.Invoke(this, state);
        }
    }

    private async Task HandleChunkAsync(FileTransferMessage.Chunk chunk, CancellationToken ct)
    {
        if (!_transfers.TryGetValue(chunk.TransferId, out var state))
        {
            _logger.LogWarning("Chunk for unknown transfer {TransferId}", chunk.TransferId);
            return;
        }

        if (state.FileStream == null)
        {
            _logger.LogWarning("No file stream for transfer {TransferId}", chunk.TransferId);
            return;
        }

        // Write chunk to file
        await state.FileStream.WriteAsync(chunk.Data, ct);
        state.BytesTransferred += (ulong)chunk.Data.Length;

        TransferProgress?.Invoke(this, state);

        // Send progress update
        var progress = new FileTransferMessage.Progress(
            chunk.TransferId,
            state.BytesTransferred,
            state.FileSize);
        await SendMessageAsync(progress, ct);
    }

    private void HandleComplete(FileTransferMessage.Complete complete)
    {
        if (_transfers.TryRemove(complete.TransferId, out var state))
        {
            state.Status = FileTransferStatus.Completed;
            state.Cleanup();

            _logger.LogInformation("Transfer {TransferId} completed", complete.TransferId);
            TransferCompleted?.Invoke(this, state);
        }
    }

    private void HandleError(FileTransferMessage.FileError error)
    {
        if (_transfers.TryRemove(error.TransferId, out var state))
        {
            state.Status = FileTransferStatus.Failed;
            state.ErrorMessage = error.Message;
            state.Cleanup();

            _logger.LogError("Transfer {TransferId} failed: {Message}", error.TransferId, error.Message);
            TransferFailed?.Invoke(this, state);
        }
    }

    private void HandleProgress(FileTransferMessage.Progress progress)
    {
        if (_transfers.TryGetValue(progress.TransferId, out var state))
        {
            // Update progress for sending transfers
            if (state.Direction == FileTransferDirection.Sending)
            {
                state.BytesTransferred = progress.BytesTransferred;
                TransferProgress?.Invoke(this, state);
            }
        }
    }

    private async Task SendChunksAsync(FileTransferState state, CancellationToken ct)
    {
        var buffer = new byte[FileTransferState.ChunkSize];
        ulong chunkIndex = 0;

        try
        {
            while (state.BytesTransferred < state.FileSize && !ct.IsCancellationRequested)
            {
                var bytesRead = await state.FileStream!.ReadAsync(buffer, ct);
                if (bytesRead == 0) break;

                var chunkData = bytesRead == buffer.Length ? buffer : buffer[..bytesRead];
                var chunk = new FileTransferMessage.Chunk(state.TransferId, chunkIndex, chunkData);

                await SendMessageAsync(chunk, ct);

                state.BytesTransferred += (ulong)bytesRead;
                chunkIndex++;

                TransferProgress?.Invoke(this, state);

                // Small delay to avoid overwhelming the data channel
                await Task.Delay(1, ct);
            }

            // Send complete message
            var complete = new FileTransferMessage.Complete(state.TransferId);
            await SendMessageAsync(complete, ct);

            state.Status = FileTransferStatus.Completed;
            state.Cleanup();
            _transfers.TryRemove(state.TransferId, out _);

            _logger.LogInformation("Finished sending {Filename}", state.Filename);
            TransferCompleted?.Invoke(this, state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending file {Filename}", state.Filename);

            state.Status = FileTransferStatus.Failed;
            state.ErrorMessage = ex.Message;
            state.Cleanup();
            _transfers.TryRemove(state.TransferId, out _);

            var error = new FileTransferMessage.FileError(state.TransferId, ex.Message);
            await SendMessageAsync(error, ct);

            TransferFailed?.Invoke(this, state);
        }
    }

    private async Task SendMessageAsync(FileTransferMessage message, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message);
        await _sendDataAsync(json);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.CompletedTask; // currently sync body; reserve async sugar for future awaits
        if (_disposed) return;
        _disposed = true;

        foreach (var state in _transfers.Values)
        {
            state.Cleanup();
        }
        _transfers.Clear();
    }
}
