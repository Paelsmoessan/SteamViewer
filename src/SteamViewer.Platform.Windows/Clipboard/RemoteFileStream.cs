using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Platform.Windows.Clipboard;

/// <summary>
/// COM IStream implementation that fetches file data on-demand from the remote machine.
/// When Explorer calls Read(), this sends a FileContentsRequest over the data channel
/// and blocks until the response arrives.
/// </summary>
public sealed class RemoteFileStream : IStream
{
    private readonly int _fileIndex;
    private readonly string _fileName;
    private readonly long _fileSize;
    private readonly Func<ClipboardFileMessage.FileContentsRequest, Task> _sendRequest;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<byte[]?>> _pendingRequests;
    private long _position;
    private static int _nextStreamId;

    private const int ResponseTimeoutMs = 30_000;

    public RemoteFileStream(
        int fileIndex,
        string fileName,
        long fileSize,
        Func<ClipboardFileMessage.FileContentsRequest, Task> sendRequest,
        ConcurrentDictionary<int, TaskCompletionSource<byte[]?>> pendingRequests)
    {
        _fileIndex = fileIndex;
        _fileName = fileName;
        _fileSize = fileSize;
        _sendRequest = sendRequest;
        _pendingRequests = pendingRequests;
    }

    /// <summary>
    /// Called by Explorer when it needs file data (paste operation).
    /// Sends a FileContentsRequest to the remote and blocks for the response.
    /// </summary>
    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        int bytesRead = 0;

        try
        {
            if (_position >= _fileSize)
            {
                // EOF
                if (pcbRead != IntPtr.Zero)
                    Marshal.WriteIntPtr(pcbRead, IntPtr.Zero);
                return;
            }

            // Clamp to remaining
            long remaining = _fileSize - _position;
            if (cb > remaining)
                cb = (int)remaining;

            int streamId = Interlocked.Increment(ref _nextStreamId);
            var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests[streamId] = tcs;

            try
            {
                // Send request to remote
                var request = new ClipboardFileMessage.FileContentsRequest(
                    streamId, _fileIndex, _position, cb);

                // Fire-and-forget the send — we'll wait on the TCS
                _sendRequest(request).ConfigureAwait(false);

                // Block waiting for response (Explorer's thread expects synchronous IStream)
                if (!tcs.Task.Wait(ResponseTimeoutMs))
                {
                    throw new TimeoutException(
                        $"File contents response timed out after {ResponseTimeoutMs}ms " +
                        $"for file {_fileName} at position {_position}");
                }

                var data = tcs.Task.Result;
                if (data != null && data.Length > 0)
                {
                    int toCopy = Math.Min(data.Length, pv.Length);
                    Array.Copy(data, 0, pv, 0, toCopy);
                    bytesRead = toCopy;
                    _position += toCopy;
                }
            }
            finally
            {
                _pendingRequests.TryRemove(streamId, out _);
            }
        }
        catch (Exception)
        {
            // Return S_FALSE / 0 bytes on error — Explorer will treat as EOF
            bytesRead = 0;
        }

        if (pcbRead != IntPtr.Zero)
            Marshal.WriteIntPtr(pcbRead, (IntPtr)bytesRead);
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
