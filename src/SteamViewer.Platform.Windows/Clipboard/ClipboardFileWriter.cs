using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Extensions.Logging;
using SteamViewer.Common.Protocol;
using IComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace SteamViewer.Platform.Windows.Clipboard;

/// <summary>
/// Places a VirtualFileDataObject on the clipboard via OleSetClipboard.
/// Runs on a persistent STA thread with a message pump so the COM apartment
/// stays alive — Explorer needs it to call IDataObject.GetData / IStream.Read
/// when the user pastes.
///
/// Source attribution: STA + message pump pattern from RustDesk/FreeRDP (Apache-2.0).
/// </summary>
public sealed class ClipboardFileWriter : IDisposable
{
    private readonly ILogger _logger;
    private readonly Func<ClipboardFileMessage.FileContentsRequest, Task> _sendRequest;
    private readonly Func<byte[], Task<bool>>? _sendBinaryAsync;
    private readonly Func<ClipboardFileMessage.StartStreaming, Task>? _sendStartStreaming;
    private readonly Func<ClipboardFileMessage.StopStreaming, Task>? _sendStopStreaming;
    private readonly ClipboardMonitor? _clipboardMonitor;

    /// <summary>
    /// Pending file content responses — shared with RemoteFileStream instances.
    /// Key: StreamId, Value: TCS that the stream blocks on.
    /// </summary>
    public ConcurrentDictionary<int, TaskCompletionSource<byte[]?>> PendingRequests { get; } = new();

    private VirtualFileDataObject? _currentDataObject;
    private ClipboardFileInfo[]? _currentFiles;
    private Thread? _staThread;
    private IntPtr _hwnd;
    private volatile bool _stopping;
    private readonly ConcurrentQueue<ClipboardFileInfo[]> _pendingClipboardSets = new();
    private WndProcDelegate? _wndProc; // prevent GC of delegate

    // Transfer tracking (receiver side)
    private long _receiveStartTick;
    private long _receiveBytesTotal;
    private long _lastReceiveReportTick;
    private const int ReceiveProgressIntervalMs = 500;

    /// <summary>
    /// Fired periodically during file receive with (fileIndex, bytesReceived, totalBytes, speedMBps).
    /// </summary>
    public event Action<int, long, long, double>? OnTransferProgress;

    private const uint WM_SET_CLIPBOARD = 0x0400 + 1; // WM_USER + 1
    private const uint WM_QUIT = 0x0012;

    public ClipboardFileWriter(
        ILogger logger,
        Func<ClipboardFileMessage.FileContentsRequest, Task> sendRequest,
        ClipboardMonitor? clipboardMonitor = null,
        Func<ClipboardFileMessage.StartStreaming, Task>? sendStartStreaming = null,
        Func<ClipboardFileMessage.StopStreaming, Task>? sendStopStreaming = null,
        Func<byte[], Task<bool>>? sendBinaryAsync = null)
    {
        _logger = logger;
        _sendRequest = sendRequest;
        _clipboardMonitor = clipboardMonitor;
        _sendStartStreaming = sendStartStreaming;
        _sendStopStreaming = sendStopStreaming;
        _sendBinaryAsync = sendBinaryAsync;
    }

    /// <summary>
    /// Start the persistent STA thread with message pump.
    /// Must be called before SetClipboard.
    /// </summary>
    public void Start()
    {
        if (_staThread != null) return;

        _stopping = false;
        _staThread = new Thread(StaThreadProc)
        {
            Name = "ClipboardFileWriter-STA",
            IsBackground = true
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();

        _logger.LogInformation("ClipboardFileWriter STA thread started");
    }

    /// <summary>
    /// Stop the STA thread and clean up.
    /// </summary>
    public void Stop()
    {
        _stopping = true;
        if (_hwnd != IntPtr.Zero)
        {
            PostMessage(_hwnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
        _staThread?.Join(3000);
        _staThread = null;
        _hwnd = IntPtr.Zero;
        _logger.LogInformation("ClipboardFileWriter STA thread stopped");
    }

    /// <summary>
    /// Present remote files as pasteable clipboard items.
    /// Called when we receive a FormatList from the remote machine.
    /// Posts to the STA thread — OleSetClipboard runs there.
    /// </summary>
    public void SetClipboard(ClipboardFileInfo[] files)
    {
        _logger.LogInformation("Setting clipboard with {Count} virtual file(s)", files.Length);

        if (_hwnd == IntPtr.Zero)
        {
            _logger.LogWarning("ClipboardFileWriter not started — cannot set clipboard");
            return;
        }

        _currentFiles = files;
        // Reset receive tracking for new transfer
        Interlocked.Exchange(ref _receiveBytesTotal, 0);
        Interlocked.Exchange(ref _receiveStartTick, 0);

        _pendingClipboardSets.Enqueue(files);
        PostMessage(_hwnd, WM_SET_CLIPBOARD, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Resolve a pending binary FileContentsResponse from the file-data channel.
    /// Handles both pull-mode responses and push-mode streaming chunks.
    /// Format: [4 bytes id BE] [4 bytes flags BE] [N bytes data]
    ///   Pull: id=streamId, flags=0x00/0x01/0x02
    ///   Push: id=fileIndex, flags=0x10/0x12
    /// </summary>
    public void HandleBinaryFileContentsResponse(byte[] raw)
    {
        if (raw.Length < 8)
        {
            _logger.LogWarning("Binary file response too short: {Length} bytes", raw.Length);
            return;
        }

        int id = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(0, 4));
        int flags = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(4, 4));

        // Push-mode: route to RemoteFileStream prefetch buffer
        if (flags == ClipboardFileServer.FlagPushChunk)
        {
            var data = raw.Length > 8 ? raw[8..] : Array.Empty<byte>();
            var stream = _currentDataObject?.GetStream(id);
            if (stream != null)
            {
                stream.AcceptPushChunk(data);
                TrackReceiveProgress(data.Length);
            }
            // Send ACK back to sender for flow control — only when data was stored
            if (stream != null)
                SendPushAck(id);
            return;
        }

        if (flags == ClipboardFileServer.FlagPushEof)
        {
            var stream = _currentDataObject?.GetStream(id);
            stream?.AcceptPushEof();
            return;
        }

        // Pull-mode: resolve pending TCS
        if (!PendingRequests.TryGetValue(id, out var tcs))
        {
            _logger.LogWarning("No pending request for stream {StreamId}", id);
            return;
        }

        switch (flags)
        {
            case ClipboardFileServer.FlagSuccess:
                var successData = raw.Length > 8 ? raw[8..] : Array.Empty<byte>();
                tcs.TrySetResult(successData);
                TrackReceiveProgress(successData.Length);
                break;

            case ClipboardFileServer.FlagError:
                var errorMsg = System.Text.Encoding.UTF8.GetString(raw.AsSpan(8));
                _logger.LogWarning("File contents error for stream {StreamId}: {Error}", id, errorMsg);
                tcs.TrySetResult(null);
                break;

            case ClipboardFileServer.FlagEof:
                tcs.TrySetResult(Array.Empty<byte>());
                break;

            default:
                _logger.LogWarning("Unknown binary response flag {Flags} for stream {StreamId}", flags, id);
                tcs.TrySetResult(null);
                break;
        }
    }

    private void SendPushAck(int fileIndex)
    {
        if (_sendBinaryAsync == null) return;
        var ack = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(ack.AsSpan(0, 4), fileIndex);
        BinaryPrimitives.WriteInt32BigEndian(ack.AsSpan(4, 4), ClipboardFileServer.FlagPushAck);
        _ = _sendBinaryAsync(ack);
    }

    private void TrackReceiveProgress(int bytesReceived)
    {
        if (bytesReceived <= 0) return;

        // Initialize on first chunk
        if (Interlocked.Read(ref _receiveBytesTotal) == 0)
            Interlocked.Exchange(ref _receiveStartTick, Environment.TickCount64);

        Interlocked.Add(ref _receiveBytesTotal, bytesReceived);

        long totalReceived = Interlocked.Read(ref _receiveBytesTotal);
        long elapsed = Environment.TickCount64 - Interlocked.Read(ref _receiveStartTick);
        double elapsedSec = elapsed / 1000.0;
        double speedMBps = elapsedSec > 0 ? (totalReceived / 1_048_576.0) / elapsedSec : 0;

        // Determine total file size from current files
        long totalSize = 0;
        string fileName = "unknown";
        if (_currentFiles is { Length: > 0 })
        {
            totalSize = _currentFiles.Sum(f => f.FileSize);
            fileName = _currentFiles[0].FileName;
            if (_currentFiles.Length > 1) fileName += $" (+{_currentFiles.Length - 1} more)";
        }

        // Report periodically
        long now = Environment.TickCount64;
        long lastReport = Interlocked.Read(ref _lastReceiveReportTick);
        if (now - lastReport >= ReceiveProgressIntervalMs)
        {
            Interlocked.Exchange(ref _lastReceiveReportTick, now);

            _logger.LogInformation("File transfer [receiving]: {FileName} — {Transferred}/{Total} ({Speed:F1} MB/s)",
                fileName, FormatBytes(totalReceived), FormatBytes(totalSize), speedMBps);

            OnTransferProgress?.Invoke(0, totalReceived, totalSize, Math.Round(speedMBps, 2));
        }

        // Log completion
        if (totalSize > 0 && totalReceived >= totalSize)
        {
            _logger.LogInformation("File transfer [receiving]: {FileName} COMPLETE — {Total} in {Elapsed:F1}s ({Speed:F1} MB/s)",
                fileName, FormatBytes(totalSize), elapsedSec, speedMBps);

            // Reset for next transfer
            Interlocked.Exchange(ref _receiveBytesTotal, 0);
            Interlocked.Exchange(ref _receiveStartTick, 0);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    public void Dispose()
    {
        Stop();

        // Cancel all pending requests
        foreach (var kvp in PendingRequests)
        {
            kvp.Value.TrySetCanceled();
        }
        PendingRequests.Clear();

        _currentDataObject?.Dispose();
        _currentDataObject = null;
    }

    #region STA Thread

    private void StaThreadProc()
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
                _hwnd = CreateWriterWindow();
                if (_hwnd == IntPtr.Zero)
                {
                    _logger.LogError("Failed to create ClipboardFileWriter window");
                    return;
                }

                _logger.LogDebug("ClipboardFileWriter STA thread running with message pump");

                // Message pump — keeps COM apartment alive for Explorer's IDataObject/IStream calls
                while (!_stopping && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }

                DestroyWindow(_hwnd);
            }
            finally
            {
                OleUninitialize();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ClipboardFileWriter STA thread crashed");
        }
    }

    private IntPtr CreateWriterWindow()
    {
        const string className = "SteamViewer_ClipboardFileWriter";

        _wndProc = WndProc; // prevent GC of delegate

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = className
        };

        RegisterClassEx(ref wc);

        return CreateWindowEx(
            0, className, "SteamViewer Clipboard Writer",
            0, 0, 0, 0, 0,
            HWND_MESSAGE, // message-only window
            IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_SET_CLIPBOARD)
        {
            ProcessPendingClipboardSets();
            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ProcessPendingClipboardSets()
    {
        while (_pendingClipboardSets.TryDequeue(out var files))
        {
            try
            {
                // Dispose old data object (cleans up RemoteFileStream temp files)
                _currentDataObject?.Dispose();

                _currentDataObject = new VirtualFileDataObject(
                    files, _sendRequest, PendingRequests,
                    _sendStartStreaming, _sendStopStreaming);

                // Tell our own clipboard monitor to ignore this change
                _clipboardMonitor?.SetEchoFlag();

                int hr = OleSetClipboard(_currentDataObject);
                if (hr < 0)
                {
                    _logger.LogError("OleSetClipboard failed: 0x{HR:X8}", hr);
                    _clipboardMonitor?.ClearEchoFlag();
                    continue;
                }

                _logger.LogDebug("OleSetClipboard succeeded — {Count} virtual files available", files.Length);

                // Clear echo flag after a brief delay to let clipboard notification propagate
                Thread.Sleep(100);
                _clipboardMonitor?.ClearEchoFlag();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set virtual file clipboard");
                _clipboardMonitor?.ClearEchoFlag();
            }
        }
    }

    #endregion

    #region Win32

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();

    [DllImport("ole32.dll")]
    private static extern int OleSetClipboard(
        [MarshalAs(UnmanagedType.Interface)] IComDataObject? pDataObj);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    #endregion
}
