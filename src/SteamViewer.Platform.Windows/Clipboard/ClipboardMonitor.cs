using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Platform.Windows.Clipboard;

/// <summary>
/// Monitors the Windows clipboard for file copy events (CF_HDROP) on a dedicated STA thread.
/// Uses AddClipboardFormatListener + WM_CLIPBOARDUPDATE for modern clipboard change detection.
/// </summary>
public sealed class ClipboardMonitor : IDisposable
{
    private readonly ILogger _logger;
    private Thread? _thread;
    private IntPtr _hwnd;
    private volatile bool _stopping;
    private volatile bool _isOurClipboard; // Echo prevention

    /// <summary>
    /// Fired when files are detected on the clipboard.
    /// Contains file metadata (for sending to remote) and local paths (for serving chunks).
    /// </summary>
    public event Action<ClipboardFileInfo[], string[]>? ClipboardFilesDetected;

    /// <summary>
    /// Fired when text is detected on the clipboard (and no files are present).
    /// </summary>
    public event Action<string>? ClipboardTextDetected;

    public ClipboardMonitor(ILogger logger)
    {
        _logger = logger;
    }

    public void Start()
    {
        if (_thread != null) return;

        _stopping = false;
        _thread = new Thread(ClipboardThreadProc)
        {
            Name = "ClipboardMonitor-STA",
            IsBackground = true
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        _logger.LogInformation("ClipboardMonitor started");
    }

    public void Stop()
    {
        _stopping = true;
        if (_hwnd != IntPtr.Zero)
        {
            PostMessage(_hwnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
        _thread?.Join(3000);
        _thread = null;
        _logger.LogInformation("ClipboardMonitor stopped");
    }

    /// <summary>
    /// Mark that we just set the clipboard ourselves — prevents echo detection.
    /// Call this before setting clipboard on this machine.
    /// </summary>
    public void SetEchoFlag() => _isOurClipboard = true;

    /// <summary>
    /// Clear the echo flag after our clipboard operation completes.
    /// </summary>
    public void ClearEchoFlag() => _isOurClipboard = false;

    private void ClipboardThreadProc()
    {
        try
        {
            // OleInitialize sets up COM STA + OLE clipboard/drag-drop support
            int hr = OleInitialize(IntPtr.Zero);
            if (hr < 0)
            {
                _logger.LogError("OleInitialize failed: 0x{HR:X8}", hr);
                return;
            }

            try
            {
                _hwnd = CreateClipboardWindow();
                if (_hwnd == IntPtr.Zero)
                {
                    _logger.LogError("Failed to create clipboard monitor window");
                    return;
                }

                if (!AddClipboardFormatListener(_hwnd))
                {
                    _logger.LogError("AddClipboardFormatListener failed: {Error}", Marshal.GetLastWin32Error());
                    DestroyWindow(_hwnd);
                    return;
                }

                _logger.LogDebug("Clipboard monitor window created, listening for WM_CLIPBOARDUPDATE");

                // Message pump — required for OLE clipboard and WM_CLIPBOARDUPDATE
                while (!_stopping && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }

                RemoveClipboardFormatListener(_hwnd);
                DestroyWindow(_hwnd);
            }
            finally
            {
                OleUninitialize();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ClipboardMonitor thread crashed");
        }
    }

    private IntPtr CreateClipboardWindow()
    {
        const string className = "SteamViewer_ClipboardMonitor";

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
            0, className, "SteamViewer Clipboard Monitor",
            0, 0, 0, 0, 0,
            HWND_MESSAGE, // message-only window
            IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    private WndProcDelegate? _wndProc; // prevent GC

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            OnClipboardChanged();
            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void OnClipboardChanged()
    {
        // Echo prevention — if we set the clipboard, ignore this notification
        if (_isOurClipboard)
        {
            _logger.LogDebug("Ignoring clipboard change — echo from our own write");
            return;
        }

        try
        {
            // Check for files first (CF_HDROP) — files take priority over text
            if (IsClipboardFormatAvailable(CF_HDROP))
            {
                var files = ReadFileDropList();
                if (files != null && files.Count > 0)
                {
                    _logger.LogInformation("Clipboard has {Count} file(s)", files.Count);

                    var fileInfos = new ClipboardFileInfo[files.Count];
                    var localPaths = new string[files.Count];

                    for (int i = 0; i < files.Count; i++)
                    {
                        localPaths[i] = files[i];
                        try
                        {
                            var fi = new FileInfo(files[i]);
                            fileInfos[i] = new ClipboardFileInfo(
                                fi.Name,
                                fi.Exists ? fi.Length : 0,
                                (uint)fi.Attributes,
                                fi.Exists ? fi.LastWriteTimeUtc.ToFileTimeUtc() : 0);
                        }
                        catch
                        {
                            // File may not be accessible — send what we can
                            fileInfos[i] = new ClipboardFileInfo(
                                Path.GetFileName(files[i]), 0, 0, 0);
                        }
                    }

                    ClipboardFilesDetected?.Invoke(fileInfos, localPaths);
                    return; // Files found — don't also send text
                }
            }

            // Check for text (CF_UNICODETEXT) — only if no files
            if (IsClipboardFormatAvailable(CF_UNICODETEXT))
            {
                var text = ReadClipboardText();
                if (!string.IsNullOrEmpty(text))
                {
                    _logger.LogDebug("Clipboard has text: {Length} chars", text.Length);
                    ClipboardTextDetected?.Invoke(text);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing clipboard change");
        }
    }

    private static string? ReadClipboardText()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    var hData = GetClipboardData(CF_UNICODETEXT);
                    if (hData == IntPtr.Zero) return null;
                    var pData = GlobalLock(hData);
                    if (pData == IntPtr.Zero) return null;
                    try
                    {
                        return Marshal.PtrToStringUni(pData);
                    }
                    finally { GlobalUnlock(hData); }
                }
                finally
                {
                    CloseClipboard();
                }
            }
            Thread.Sleep(33);
        }
        return null;
    }

    private static List<string>? ReadFileDropList()
    {
        // Retry logic — clipboard may be held by another process
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    var hDrop = GetClipboardData(CF_HDROP);
                    if (hDrop == IntPtr.Zero) return null;

                    uint fileCount = DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
                    if (fileCount == 0) return null;

                    var files = new List<string>((int)fileCount);
                    for (uint i = 0; i < fileCount; i++)
                    {
                        uint charCount = DragQueryFileW(hDrop, i, null, 0);
                        if (charCount == 0) continue;

                        var sb = new StringBuilder((int)charCount + 1);
                        DragQueryFileW(hDrop, i, sb, charCount + 1);
                        files.Add(sb.ToString());
                    }

                    return files;
                }
                finally
                {
                    CloseClipboard();
                }
            }

            // Another process has the clipboard — wait and retry
            Thread.Sleep(33);
        }

        return null;
    }

    public void Dispose()
    {
        Stop();
    }

    #region Win32 Constants

    private const uint CF_UNICODETEXT = 13;
    private const uint CF_HDROP = 15;
    private const uint WM_CLIPBOARDUPDATE = 0x031D;
    private const uint WM_QUIT = 0x0012;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    #endregion

    #region Win32 P/Invoke

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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile,
        [Out] StringBuilder? lpszFile, uint cch);

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

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    #endregion
}
