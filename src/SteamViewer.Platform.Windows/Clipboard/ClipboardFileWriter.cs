using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Extensions.Logging;
using SteamViewer.Common.Protocol;
using IComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace SteamViewer.Platform.Windows.Clipboard;

/// <summary>
/// Places a VirtualFileDataObject on the clipboard via OleSetClipboard.
/// Runs clipboard operations on a dedicated STA thread (required by OLE).
/// Handles echo prevention via ClipboardMonitor coordination.
/// </summary>
public sealed class ClipboardFileWriter : IDisposable
{
    private readonly ILogger _logger;
    private readonly Func<ClipboardFileMessage.FileContentsRequest, Task> _sendRequest;
    private readonly ClipboardMonitor? _clipboardMonitor;

    /// <summary>
    /// Pending file content responses — shared with RemoteFileStream instances.
    /// Key: StreamId, Value: TCS that the stream blocks on.
    /// </summary>
    public ConcurrentDictionary<int, TaskCompletionSource<byte[]?>> PendingRequests { get; } = new();

    private VirtualFileDataObject? _currentDataObject;

    public ClipboardFileWriter(
        ILogger logger,
        Func<ClipboardFileMessage.FileContentsRequest, Task> sendRequest,
        ClipboardMonitor? clipboardMonitor = null)
    {
        _logger = logger;
        _sendRequest = sendRequest;
        _clipboardMonitor = clipboardMonitor;
    }

    /// <summary>
    /// Present remote files as pasteable clipboard items.
    /// Called when we receive a FormatList from the remote machine.
    /// </summary>
    public void SetClipboard(ClipboardFileInfo[] files)
    {
        _logger.LogInformation("Setting clipboard with {Count} virtual file(s)", files.Length);

        // Run on STA thread — OleSetClipboard requires it
        var thread = new Thread(() =>
        {
            try
            {
                int hr = OleInitialize(IntPtr.Zero);
                if (hr < 0)
                {
                    _logger.LogError("OleInitialize failed: 0x{HR:X8}", hr);
                    return;
                }

                try
                {
                    _currentDataObject = new VirtualFileDataObject(files, _sendRequest, PendingRequests);

                    // Tell our own clipboard monitor to ignore this change
                    _clipboardMonitor?.SetEchoFlag();

                    hr = OleSetClipboard(_currentDataObject);
                    if (hr < 0)
                    {
                        _logger.LogError("OleSetClipboard failed: 0x{HR:X8}", hr);
                        _clipboardMonitor?.ClearEchoFlag();
                        return;
                    }

                    _logger.LogDebug("OleSetClipboard succeeded — {Count} virtual files available", files.Length);

                    // Clear echo flag after a brief delay to let clipboard notification propagate
                    Thread.Sleep(100);
                    _clipboardMonitor?.ClearEchoFlag();
                }
                finally
                {
                    OleUninitialize();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set virtual file clipboard");
                _clipboardMonitor?.ClearEchoFlag();
            }
        })
        {
            Name = "ClipboardFileWriter-STA",
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(5000); // Wait for clipboard to be set
    }

    /// <summary>
    /// Resolve a pending FileContentsResponse — unblocks the RemoteFileStream::Read.
    /// </summary>
    public void HandleFileContentsResponse(ClipboardFileMessage.FileContentsResponse response)
    {
        if (PendingRequests.TryGetValue(response.StreamId, out var tcs))
        {
            if (response.IsError)
            {
                _logger.LogWarning("File contents error for stream {StreamId}: {Error}",
                    response.StreamId, response.ErrorMessage);
                tcs.TrySetResult(null);
            }
            else
            {
                tcs.TrySetResult(response.Data);
            }
        }
        else
        {
            _logger.LogWarning("No pending request for stream {StreamId}", response.StreamId);
        }
    }

    /// <summary>
    /// Clean up — flush clipboard so data persists if our process exits.
    /// </summary>
    public void Dispose()
    {
        // Cancel all pending requests
        foreach (var kvp in PendingRequests)
        {
            kvp.Value.TrySetCanceled();
        }
        PendingRequests.Clear();

        _currentDataObject = null;
    }

    #region P/Invoke

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();

    [DllImport("ole32.dll")]
    private static extern int OleSetClipboard(
        [MarshalAs(UnmanagedType.Interface)] IComDataObject? pDataObj);

    [DllImport("ole32.dll")]
    private static extern int OleFlushClipboard();

    #endregion
}
