using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Platform.Windows.Clipboard;

/// <summary>
/// COM IStream implementation that fetches file data on-demand from the remote machine.
/// Supports two modes:
/// 1. Push mode (fast): On first Read(), sends StartStreaming. Sender pushes chunks into
///    our prefetch buffer. Read() dequeues from buffer — no per-chunk round-trip.
///    All received data is cached to a temp file so Explorer's Seek(0) + re-read is instant.
/// 2. Pull mode (fallback): If prefetch buffer is empty and push hasn't started yet,
///    sends a FileContentsRequest and blocks for the response (original behavior).
/// </summary>
public sealed class RemoteFileStream : IStream, IDisposable
{
    private readonly int _fileIndex;
    private readonly string _fileName;
    private readonly long _fileSize;
    private readonly Func<ClipboardFileMessage.FileContentsRequest, Task> _sendRequest;
    private readonly Func<ClipboardFileMessage.StartStreaming, Task>? _sendStartStreaming;
    private readonly Func<ClipboardFileMessage.StopStreaming, Task>? _sendStopStreaming;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<byte[]?>> _pendingRequests;
    private long _position;
    private static int _nextStreamId;
    private volatile bool _streamingRequested;
    private volatile bool _streamingEofReceived;

    // Prefetch buffer — push chunks land here, Read() dequeues
    private readonly ConcurrentQueue<byte[]> _prefetchBuffer = new();
    private readonly ManualResetEventSlim _dataAvailable = new(false);
    private long _prefetchBytesBuffered;
    private byte[]? _partialChunk; // leftover from a chunk that was larger than cb
    private int _partialOffset;

    // Local cache — all received data written here so Seek+re-read is instant
    private FileStream? _cacheFile;
    private readonly string _cachePath;
    private long _cachedBytes;
    private readonly object _cacheLock = new();

    private const int ResponseTimeoutMs = 30_000;
    private const int PrefetchWaitMs = 5_000; // Wait up to 5s for push data before falling back to pull

    public RemoteFileStream(
        int fileIndex,
        string fileName,
        long fileSize,
        Func<ClipboardFileMessage.FileContentsRequest, Task> sendRequest,
        ConcurrentDictionary<int, TaskCompletionSource<byte[]?>> pendingRequests,
        Func<ClipboardFileMessage.StartStreaming, Task>? sendStartStreaming = null,
        Func<ClipboardFileMessage.StopStreaming, Task>? sendStopStreaming = null)
    {
        _fileIndex = fileIndex;
        _fileName = fileName;
        _fileSize = fileSize;
        _sendRequest = sendRequest;
        _pendingRequests = pendingRequests;
        _sendStartStreaming = sendStartStreaming;
        _sendStopStreaming = sendStopStreaming;

        // Create temp file for caching received data
        _cachePath = Path.Combine(Path.GetTempPath(), $"sv_filetransfer_{fileIndex}_{Guid.NewGuid():N}.tmp");
        _cacheFile = new FileStream(_cachePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 262144);
    }

    /// <summary>
    /// Accept a push chunk from the sender's streaming loop.
    /// Called by ClipboardFileWriter when it receives a FlagPushChunk binary message.
    /// Data is written to cache AND enqueued for immediate consumption.
    /// </summary>
    public void AcceptPushChunk(byte[] data)
    {
        // Write to cache file (append at end)
        WriteToCache(data);

        _prefetchBuffer.Enqueue(data);
        Interlocked.Add(ref _prefetchBytesBuffered, data.Length);
        _dataAvailable.Set();
    }

    /// <summary>
    /// Signal that the push stream has reached EOF.
    /// </summary>
    public void AcceptPushEof()
    {
        _streamingEofReceived = true;
        _dataAvailable.Set(); // Wake up any waiting Read()
    }

    /// <summary>
    /// Called by Explorer when it needs file data (paste operation).
    /// Tries cache first (for re-reads after Seek), then prefetch buffer, then pull fallback.
    /// </summary>
    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        int bytesRead = 0;

        try
        {
            if (_position >= _fileSize)
            {
                if (pcbRead != IntPtr.Zero)
                    Marshal.WriteIntPtr(pcbRead, IntPtr.Zero);
                return;
            }

            // Clamp to remaining
            long remaining = _fileSize - _position;
            if (cb > remaining)
                cb = (int)remaining;

            // On first Read(), trigger push streaming
            if (!_streamingRequested && _sendStartStreaming != null)
            {
                _streamingRequested = true;
                var msg = new ClipboardFileMessage.StartStreaming(_fileIndex);
                _sendStartStreaming(msg).ConfigureAwait(false);
            }

            // If position is within cached data, read from cache (handles Seek+re-read)
            if (_position < Interlocked.Read(ref _cachedBytes))
            {
                bytesRead = ReadFromCache(pv, cb);
            }

            // If cache didn't satisfy the read, try prefetch buffer (push mode)
            if (bytesRead == 0)
            {
                bytesRead = ReadFromPrefetchBuffer(pv, cb);
            }

            // If no data from prefetch, fall back to pull mode
            if (bytesRead == 0)
            {
                bytesRead = ReadViaPull(pv, cb);
            }

            _position += bytesRead;
        }
        catch (Exception)
        {
            bytesRead = 0;
        }

        if (pcbRead != IntPtr.Zero)
            Marshal.WriteIntPtr(pcbRead, (IntPtr)bytesRead);
    }

    /// <summary>
    /// Read from the local temp file cache. Used when Explorer Seek()s back to already-received data.
    /// </summary>
    private int ReadFromCache(byte[] pv, int cb)
    {
        lock (_cacheLock)
        {
            if (_cacheFile == null) return 0;

            long cached = Interlocked.Read(ref _cachedBytes);
            if (_position >= cached) return 0;

            // Clamp to what's cached
            long available = cached - _position;
            int toRead = (int)Math.Min(cb, available);

            _cacheFile.Position = _position;
            int totalRead = 0;
            while (totalRead < toRead)
            {
                int read = _cacheFile.Read(pv, totalRead, toRead - totalRead);
                if (read == 0) break;
                totalRead += read;
            }
            return totalRead;
        }
    }

    /// <summary>
    /// Write data to the cache file (append sequentially).
    /// </summary>
    private void WriteToCache(byte[] data)
    {
        lock (_cacheLock)
        {
            if (_cacheFile == null) return;

            long cached = Interlocked.Read(ref _cachedBytes);
            _cacheFile.Position = cached;
            _cacheFile.Write(data, 0, data.Length);
            Interlocked.Add(ref _cachedBytes, data.Length);
        }
    }

    /// <summary>
    /// Try to read from the prefetch buffer. Returns 0 if no data available within timeout.
    /// </summary>
    private int ReadFromPrefetchBuffer(byte[] pv, int cb)
    {
        int totalCopied = 0;

        // First, drain any partial chunk from a previous Read
        if (_partialChunk != null)
        {
            int available = _partialChunk.Length - _partialOffset;
            int toCopy = Math.Min(available, cb);
            Array.Copy(_partialChunk, _partialOffset, pv, 0, toCopy);
            totalCopied += toCopy;

            if (toCopy == available)
            {
                _partialChunk = null;
                _partialOffset = 0;
            }
            else
            {
                _partialOffset += toCopy;
            }

            if (totalCopied >= cb)
                return totalCopied;
        }

        // Try to dequeue chunks from the prefetch buffer
        while (totalCopied < cb)
        {
            if (_prefetchBuffer.TryDequeue(out var chunk))
            {
                Interlocked.Add(ref _prefetchBytesBuffered, -chunk.Length);

                int space = cb - totalCopied;
                int toCopy = Math.Min(chunk.Length, space);
                Array.Copy(chunk, 0, pv, totalCopied, toCopy);
                totalCopied += toCopy;

                // Save leftover if chunk is larger than needed
                if (toCopy < chunk.Length)
                {
                    _partialChunk = chunk;
                    _partialOffset = toCopy;
                }

                continue;
            }

            // Buffer empty — wait for more data if streaming is active
            if (_streamingRequested && !_streamingEofReceived)
            {
                _dataAvailable.Reset();
                if (_dataAvailable.Wait(PrefetchWaitMs))
                    continue; // New data arrived, try again
            }

            break; // No more data available
        }

        return totalCopied;
    }

    /// <summary>
    /// Original pull-mode: send FileContentsRequest, block for response.
    /// Used as fallback when prefetch buffer is empty.
    /// Also caches received data to temp file.
    /// </summary>
    private int ReadViaPull(byte[] pv, int cb)
    {
        int streamId = Interlocked.Increment(ref _nextStreamId);
        var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[streamId] = tcs;

        try
        {
            var request = new ClipboardFileMessage.FileContentsRequest(
                streamId, _fileIndex, _position, cb);

            _sendRequest(request).ConfigureAwait(false);

            if (!tcs.Task.Wait(ResponseTimeoutMs))
            {
                throw new TimeoutException(
                    $"File contents response timed out after {ResponseTimeoutMs}ms " +
                    $"for file {_fileName} at position {_position}");
            }

            var data = tcs.Task.Result;
            if (data != null && data.Length > 0)
            {
                // Cache pull data too (for potential re-reads)
                WriteToCache(data);

                int toCopy = Math.Min(data.Length, pv.Length);
                Array.Copy(data, 0, pv, 0, toCopy);
                return toCopy;
            }
        }
        finally
        {
            _pendingRequests.TryRemove(streamId, out _);
        }

        return 0;
    }

    public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
    {
        switch (dwOrigin)
        {
            case 0: // STREAM_SEEK_SET
                _position = dlibMove;
                break;
            case 1: // STREAM_SEEK_CUR
                _position += dlibMove;
                break;
            case 2: // STREAM_SEEK_END
                _position = _fileSize + dlibMove;
                break;
        }

        _position = Math.Max(0, Math.Min(_position, _fileSize));

        if (plibNewPosition != IntPtr.Zero)
            Marshal.WriteInt64(plibNewPosition, _position);
    }

    public void Stat(out STATSTG pstatstg, int grfStatFlag)
    {
        pstatstg = new STATSTG
        {
            type = 2, // STGTY_STREAM
            cbSize = _fileSize,
            pwcsName = (grfStatFlag & 1) == 0 ? _fileName : null! // STATFLAG_NONAME = 1
        };
    }

    public void Dispose()
    {
        lock (_cacheLock)
        {
            _cacheFile?.Dispose();
            _cacheFile = null;
        }

        try { if (File.Exists(_cachePath)) File.Delete(_cachePath); }
        catch { /* best effort cleanup */ }

        _dataAvailable.Dispose();
    }

    public void SetSize(long libNewSize) =>
        Marshal.ThrowExceptionForHR(unchecked((int)0x80004001)); // E_NOTIMPL

    public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) =>
        Marshal.ThrowExceptionForHR(unchecked((int)0x80004001));

    public void Commit(int grfCommitFlags) =>
        Marshal.ThrowExceptionForHR(unchecked((int)0x80004001));

    public void Revert() =>
        Marshal.ThrowExceptionForHR(unchecked((int)0x80004001));

    public void LockRegion(long libOffset, long cb, int dwLockType) =>
        Marshal.ThrowExceptionForHR(unchecked((int)0x80004001));

    public void UnlockRegion(long libOffset, long cb, int dwLockType) =>
        Marshal.ThrowExceptionForHR(unchecked((int)0x80004001));

    public void Write(byte[] pv, int cb, IntPtr pcbWritten) =>
        Marshal.ThrowExceptionForHR(unchecked((int)0x80070005)); // E_ACCESSDENIED

    public void Clone(out IStream ppstm) =>
        throw new NotImplementedException();
}
