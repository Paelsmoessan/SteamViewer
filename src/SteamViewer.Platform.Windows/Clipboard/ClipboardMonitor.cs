using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

    // Echo prevention via content-hash. When a clipboard write originates locally
    // (RecordSelfWriteText / RecordSelfWriteFiles), CheckClipboard compares the
    // current clipboard's content hash against this field; on match it suppresses
    // the event ONCE (then clears the hash so the next non-echo change fires
    // normally). Replaces the previous SetEchoFlag/Thread.Sleep(100)/ClearEchoFlag
    // ceremony, which had a timing race + 100ms blocking sleep on every write.
    private volatile string? _lastSelfWriteContentHash;

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
    /// Record that we just wrote the given text to the clipboard ourselves.
    /// The next CheckClipboard call whose content hashes identically will suppress
    /// its event (and clear the recording). Call this BEFORE the write operation
    /// so the recording is in place by the time WM_CLIPBOARDUPDATE arrives.
    /// </summary>
    public void RecordSelfWriteText(string text)
    {
        var hash = "text:" + ComputeContentHash(text);
        _lastSelfWriteContentHash = hash;
        _logger.LogDebug("RecordSelfWriteText: {Length} chars (hash={HashPrefix})", text.Length, hash[..Math.Min(20, hash.Length)]);
    }

    /// <summary>
    /// Record that we just wrote the given file list to the clipboard ourselves.
    /// Used by ClipboardFileWriter when our virtual-file IDataObject is set;
    /// covers the case where a real CF_HDROP also ends up advertised (defensive
    /// safety belt — VirtualFileDataObject typically exposes CFSTR_FILECONTENTS
    /// only, but we want the same suppression contract regardless).
    /// </summary>
    public void RecordSelfWriteFiles(IReadOnlyList<string> fileIdentifiers)
    {
        var hash = "files:" + ComputeContentHash(string.Join("|", fileIdentifiers));
        _lastSelfWriteContentHash = hash;
        _logger.LogDebug("RecordSelfWriteFiles: {Count} files (hash={HashPrefix})", fileIdentifiers.Count, hash[..Math.Min(20, hash.Length)]);
    }

    private static string ComputeContentHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

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

                _logger.LogDebug("Clipboard monitor window created (class={ClassName}), listening for WM_CLIPBOARDUPDATE", _registeredClassName);

                // Message pump — required for OLE clipboard and WM_CLIPBOARDUPDATE
                while (!_stopping && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }

                RemoveClipboardFormatListener(_hwnd);
                DestroyWindow(_hwnd);

                // Unregister the per-instance window class to prevent accumulation across
                // reconnects. Each session uses a fresh GUID-suffixed class name so
                // RegisterClassEx always succeeds and each window's WndProc is bound to
                // its own instance (see CreateClipboardWindow comment).
                if (_registeredClassName != null)
                {
                    var hInstance = GetModuleHandle(null);
                    if (UnregisterClass(_registeredClassName, hInstance))
                        _logger.LogDebug("UnregisterClass succeeded for {ClassName}", _registeredClassName);
                    else
                        _logger.LogWarning("UnregisterClass failed for {ClassName} (error={Error})", _registeredClassName, Marshal.GetLastWin32Error());
                }
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
        // Per-instance class name. The lpfnWndProc in WNDCLASSEX is class-scoped, so a
        // shared/const class name would route every future window's WM_* messages
        // through the FIRST monitor's WndProc (subsequent RegisterClassEx calls fail
        // silently with ERROR_CLASS_ALREADY_EXISTS=1410). That bug caused post-reconnect
        // clipboard events to fire on the OLD disposed session, never on the new one
        // (see .claude/plans/clipboardmonitor-classname-fix-and-logging-policy.md).
        var className = $"SteamViewer_ClipboardMonitor_{Guid.NewGuid():N}";

        _wndProc = WndProc; // prevent GC of delegate
        _wndProcHandle = GCHandle.Alloc(_wndProc); // explicit root - GC cannot collect this

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = className
        };

        var classAtom = RegisterClassEx(ref wc);
        if (classAtom == 0)
        {
            _logger.LogError("RegisterClassEx failed for {ClassName} (error={Error})", className, Marshal.GetLastWin32Error());
            return IntPtr.Zero;
        }
        _registeredClassName = className;
        _logger.LogDebug("RegisterClassEx succeeded for {ClassName} (atom={Atom})", className, classAtom);

        return CreateWindowEx(
            0, className, "SteamViewer Clipboard Monitor",
            0, 0, 0, 0, 0,
            HWND_MESSAGE, // message-only window
            IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    private WndProcDelegate? _wndProc; // prevent GC
    private GCHandle _wndProcHandle; // explicit GC root - prevents native callback crash
    private string? _registeredClassName; // set after RegisterClassEx so dispose can unregister

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
        // Snapshot the self-write hash atomically — RecordSelfWriteText/Files writers
        // may execute on any thread; the volatile read ensures we compare against a
        // consistent value within this dispatch.
        var lastSelfHash = _lastSelfWriteContentHash;

        try
        {
            // Check for files first (CF_HDROP) — files take priority over text
            if (IsClipboardFormatAvailable(CF_HDROP))
            {
                var files = ReadFileDropList();
                if (files != null && files.Count > 0)
                {
                    // Echo suppression for file paths — see RecordSelfWriteFiles XML doc
                    if (lastSelfHash != null)
                    {
                        var currentFilesHash = "files:" + ComputeContentHash(string.Join("|", files));
                        if (currentFilesHash == lastSelfHash)
                        {
                            _lastSelfWriteContentHash = null; // single-fire suppression
                            _logger.LogDebug("Suppressed file-clipboard echo: {Count} files, hash matches last self-write", files.Count);
                            return;
                        }
                    }

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
                    // Echo suppression for text — see RecordSelfWriteText XML doc
                    if (lastSelfHash != null)
                    {
                        var currentTextHash = "text:" + ComputeContentHash(text);
                        if (currentTextHash == lastSelfHash)
                        {
                            _lastSelfWriteContentHash = null; // single-fire suppression
                            _logger.LogDebug("Suppressed text-clipboard echo: {Length} chars, hash matches last self-write", text.Length);
                            return;
                        }
                    }

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
        // Do NOT free _wndProcHandle here — the clipboard thread may still be
        // processing a final DispatchMessage after Stop() returns (3s timeout).
        // Freeing the handle allows GC to collect the WndProc delegate, which
        // crashes the next time Windows calls it. One handle per session is not
        // a meaningful leak. The handle is freed when the process exits.
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

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
