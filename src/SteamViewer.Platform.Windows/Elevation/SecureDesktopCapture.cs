using System.Runtime.InteropServices;
using SteamViewer.Platform.Windows.Input;

namespace SteamViewer.Platform.Windows.Elevation;

/// <summary>
/// Captures the Secure Desktop (Winlogon) screen via BitBlt when a UAC prompt is active.
/// Runs inside the SYSTEM helper process on a dedicated clean thread (no windows, no COM).
/// Detects desktop switches by polling OpenInputDesktop every 150ms.
/// Event-driven: captures on input activity, zero bandwidth when idle.
/// Fires OnFrameCaptured with raw BGRA pixel data.
/// Also provides input injection on the Winlogon desktop via SetThreadDesktop + SendInput.
/// </summary>
public sealed class SecureDesktopCapture : IDisposable
{
    private Thread? _captureThread;
    private volatile bool _stopRequested;
    private volatile bool _isSecureDesktopActive;
    private int _desktopWidth;
    private int _desktopHeight;
    private int _frameCount;
    private readonly ManualResetEventSlim _wakeSignal = new(false);

    // Event-driven capture: track last input time for activity-based capture
    private long _lastInputTimeTicks;

    // The Winlogon desktop handle held by the capture thread (valid while active)
    private IntPtr _winlogonDesktop;

    // Reusable BGRA buffer (avoid allocations per frame)
    private byte[]? _bgraBuffer;

    private static readonly string? DebugPath;
    private static readonly string? DebugPathLocal;

    static SecureDesktopCapture()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SteamViewer");
            Directory.CreateDirectory(dir);
            DebugPath = Path.Combine(dir, "secure-desktop-debug.txt");
        }
        catch { }

        // Also log next to exe (readable via network share from Dev PC)
        try
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (exeDir != null)
            {
                var localDir = Path.Combine(exeDir, "logs");
                Directory.CreateDirectory(localDir);
                DebugPathLocal = Path.Combine(localDir, "secure-desktop-debug.txt");
            }
        }
        catch { }
    }

    private static void DebugLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [SecureDesktop] {message}";
        Console.WriteLine(line);
        try { if (DebugPath != null) File.AppendAllText(DebugPath, line + "\n"); } catch { }
        try { if (DebugPathLocal != null) File.AppendAllText(DebugPathLocal, line + "\n"); } catch { }
    }

    /// <summary>Whether the Secure Desktop (Winlogon) is currently active.</summary>
    public bool IsActive => _isSecureDesktopActive;

    /// <summary>Current desktop width (valid when active).</summary>
    public int Width => _desktopWidth;

    /// <summary>Current desktop height (valid when active).</summary>
    public int Height => _desktopHeight;

    /// <summary>Raised when the Secure Desktop becomes active. Parameters: (width, height).</summary>
    public event Action<int, int>? OnSecureDesktopActive;

    /// <summary>Raised when the Secure Desktop becomes inactive (returned to Default desktop).</summary>
    public event Action? OnSecureDesktopInactive;

    /// <summary>Raised when a raw BGRA frame is captured. Parameters: (bgraData, width, height, stride).</summary>
    public event Action<byte[], int, int, int>? OnFrameCaptured;

    /// <summary>
    /// Start the capture thread. Must be called from the SYSTEM helper after authentication.
    /// </summary>
    public void Start()
    {
        if (_captureThread != null) return;

        _stopRequested = false;
        _captureThread = new Thread(CaptureLoop)
        {
            Name = "SecureDesktopCapture",
            IsBackground = true
        };
        _captureThread.Start();
        DebugLog("Capture thread started");
    }

    /// <summary>
    /// Stop the capture thread and wait for it to finish.
    /// </summary>
    public void Stop()
    {
        _stopRequested = true;
        _wakeSignal.Set(); // Wake the thread if sleeping
        _captureThread?.Join(5000);
        _captureThread = null;
        DebugLog("Capture thread stopped");
    }

    /// <summary>
    /// Wake the polling thread immediately instead of waiting for the 150ms cycle.
    /// Called when a lock command is sent to reduce detection delay.
    /// </summary>
    public void WakePolling()
    {
        DebugLog("WakePolling signaled - immediate desktop check");
        _wakeSignal.Set();
    }

    /// <summary>
    /// Notify that input was injected - triggers event-driven capture.
    /// Called from InjectInputOnWinlogon when input is sent to the Winlogon desktop.
    /// </summary>
    public void NotifyInputActivity()
    {
        Interlocked.Exchange(ref _lastInputTimeTicks, Environment.TickCount64);
        _wakeSignal.Set(); // Wake capture loop immediately
    }

    /// <summary>
    /// Inject input on the Winlogon desktop. Called from the control pipe thread.
    /// The calling thread must not have any windows (SystemHelperServer's reader thread qualifies).
    /// Switches the calling thread's desktop to Winlogon, injects input via SendInput, then switches back.
    /// </summary>
    public void InjectInputOnWinlogon(string inputJson, int screenWidth, int screenHeight)
    {
        if (!_isSecureDesktopActive) return;

        // Notify event-driven capture that input happened
        NotifyInputActivity();

        // Open a fresh handle for this thread
        var hDesk = OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP);
        if (hDesk == IntPtr.Zero)
        {
            DebugLog($"InjectInput: OpenInputDesktop failed (error {Marshal.GetLastWin32Error()})");
            return;
        }

        var originalDesktop = GetThreadDesktop(GetCurrentThreadId());

        try
        {
            if (!SetThreadDesktop(hDesk))
            {
                DebugLog($"InjectInput: SetThreadDesktop(winlogon) failed (error {Marshal.GetLastWin32Error()})");
                return;
            }

            // Parse and inject using the existing Win32Input infrastructure
            using var doc = System.Text.Json.JsonDocument.Parse(inputJson);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "mouse_move":
                    Win32Input.InjectMouseMove(
                        root.GetProperty("x").GetDouble(),
                        root.GetProperty("y").GetDouble(),
                        screenWidth, screenHeight);
                    break;
                case "mouse_down":
                    Win32Input.InjectMouseButton(
                        ParseMouseButton(root.GetProperty("button").GetString()),
                        root.GetProperty("x").GetDouble(),
                        root.GetProperty("y").GetDouble(),
                        screenWidth, screenHeight, isDown: true);
                    break;
                case "mouse_up":
                    Win32Input.InjectMouseButton(
                        ParseMouseButton(root.GetProperty("button").GetString()),
                        root.GetProperty("x").GetDouble(),
                        root.GetProperty("y").GetDouble(),
                        screenWidth, screenHeight, isDown: false);
                    break;
                case "mouse_wheel":
                    Win32Input.InjectMouseWheel(
                        root.GetProperty("delta_x").GetDouble(),
                        root.GetProperty("delta_y").GetDouble());
                    break;
                case "key_down":
                    Win32Input.InjectKey(
                        root.GetProperty("key").GetString()!,
                        ParseModifiers(root),
                        isDown: true);
                    break;
                case "key_up":
                    Win32Input.InjectKey(
                        root.GetProperty("key").GetString()!,
                        ParseModifiers(root),
                        isDown: false);
                    break;
            }
        }
        catch (Exception ex)
        {
            DebugLog($"InjectInput error: {ex.Message}");
        }
        finally
        {
            // Switch back to original desktop
            SetThreadDesktop(originalDesktop);
            CloseDesktop(hDesk);
        }
    }

    /// <summary>
    /// Main capture loop running on a dedicated clean thread.
    /// Polls OpenInputDesktop every 150ms. When "Winlogon" is detected, switches desktop
    /// and captures event-driven (on input activity) via BitBlt -> raw BGRA.
    /// </summary>
    private void CaptureLoop()
    {
        try
        {
            CaptureLoopInner();
        }
        catch (Exception ex)
        {
            DebugLog($"FATAL: CaptureLoop crashed: {ex}");
        }
    }

    private void CaptureLoopInner()
    {
        DebugLog("Capture loop starting");

        var originalDesktop = GetThreadDesktop(GetCurrentThreadId());
        var wasActive = false;
        string? lastLoggedDesktopName = null;
        var pollCount = 0;

        // GDI resources - created once, reused across frames, recreated on resolution change
        IntPtr hDesktopDC = IntPtr.Zero;
        IntPtr hMemDC = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr hOldBitmap = IntPtr.Zero;
        int cachedWidth = 0, cachedHeight = 0;

        try
        {
            while (!_stopRequested)
            {
                try
                {
                    var hDesk = OpenInputDesktop(0, false, DESKTOP_READOBJECTS | DESKTOP_SWITCHDESKTOP);
                    if (hDesk == IntPtr.Zero)
                    {
                        // Can't open desktop - might be transitioning, wait and retry
                        _wakeSignal.Wait(150);
                        _wakeSignal.Reset();
                        continue;
                    }

                    var desktopName = GetDesktopName(hDesk);
                    pollCount++;

                    // Log desktop name on first poll, on change, and every ~5s (33 polls at 150ms)
                    if (desktopName != lastLoggedDesktopName || pollCount % 33 == 1)
                    {
                        DebugLog($"Desktop: \"{desktopName}\" (poll #{pollCount}, hDesk=0x{hDesk:X})");
                        lastLoggedDesktopName = desktopName;
                    }

                    if (string.Equals(desktopName, "Winlogon", StringComparison.OrdinalIgnoreCase) && !wasActive)
                    {
                        // Secure Desktop just activated
                        DebugLog("Secure Desktop ACTIVE (Winlogon detected)");

                        if (!SetThreadDesktop(hDesk))
                        {
                            DebugLog($"SetThreadDesktop(winlogon) failed: {Marshal.GetLastWin32Error()}");
                            CloseDesktop(hDesk);
                            _wakeSignal.Wait(150);
                            _wakeSignal.Reset();
                            continue;
                        }

                        DebugLog("SetThreadDesktop(winlogon) succeeded");
                        _winlogonDesktop = hDesk;
                        _frameCount = 0;

                        // Query resolution AFTER switching desktop (per research findings)
                        _desktopWidth = GetSystemMetrics(SM_CXSCREEN);
                        _desktopHeight = GetSystemMetrics(SM_CYSCREEN);
                        DebugLog($"Winlogon resolution: {_desktopWidth}x{_desktopHeight}");

                        wasActive = true;
                        _isSecureDesktopActive = true;
                        Interlocked.Exchange(ref _lastInputTimeTicks, Environment.TickCount64); // Treat activation as input (burst initial frames)

                        try { OnSecureDesktopActive?.Invoke(_desktopWidth, _desktopHeight); }
                        catch (Exception ex) { DebugLog($"OnSecureDesktopActive handler error: {ex.Message}"); }
                    }
                    else if (string.Equals(desktopName, "Winlogon", StringComparison.OrdinalIgnoreCase) && wasActive)
                    {
                        // Still on Winlogon - event-driven capture
                        CloseDesktop(hDesk); // Close the newly opened handle, we already have _winlogonDesktop

                        // Check for resolution change
                        var currentW = GetSystemMetrics(SM_CXSCREEN);
                        var currentH = GetSystemMetrics(SM_CYSCREEN);
                        if (currentW != _desktopWidth || currentH != _desktopHeight)
                        {
                            DebugLog($"Resolution changed: {_desktopWidth}x{_desktopHeight} -> {currentW}x{currentH}");
                            _desktopWidth = currentW;
                            _desktopHeight = currentH;
                            // Force GDI resource recreation
                            CleanupGdiResources(ref hDesktopDC, ref hMemDC, ref hBitmap, ref hOldBitmap);
                            cachedWidth = 0;
                            cachedHeight = 0;
                            _bgraBuffer = null; // Force buffer reallocation
                        }

                        // Event-driven capture decision
                        var msSinceInput = Environment.TickCount64 - Interlocked.Read(ref _lastInputTimeTicks);
                        var shouldCapture = _frameCount < 3 || msSinceInput < 500;

                        if (!shouldCapture)
                        {
                            // Idle - no input activity, just poll desktop state
                            // Log periodically so we know capture thread is alive
                            if (pollCount % 33 == 0)
                                DebugLog($"SD idle: waiting for input (frames sent={_frameCount}, msSinceInput={msSinceInput})");
                            _wakeSignal.Wait(150);
                            _wakeSignal.Reset();
                            continue;
                        }

                        // Create/reuse GDI resources
                        if (cachedWidth != _desktopWidth || cachedHeight != _desktopHeight)
                        {
                            CleanupGdiResources(ref hDesktopDC, ref hMemDC, ref hBitmap, ref hOldBitmap);

                            var hDesktopWnd = GetDesktopWindow();
                            hDesktopDC = GetWindowDC(hDesktopWnd);
                            hMemDC = CreateCompatibleDC(hDesktopDC);
                            // CRITICAL: CreateCompatibleBitmap must use the desktop DC, not the memory DC
                            hBitmap = CreateCompatibleBitmap(hDesktopDC, _desktopWidth, _desktopHeight);
                            hOldBitmap = SelectObject(hMemDC, hBitmap);
                            cachedWidth = _desktopWidth;
                            cachedHeight = _desktopHeight;

                            DebugLog($"GDI resources created: {cachedWidth}x{cachedHeight}");
                        }

                        // BitBlt capture
                        var bitBltOk = BitBlt(hMemDC, 0, 0, _desktopWidth, _desktopHeight, hDesktopDC, 0, 0, SRCCOPY);
                        if (!bitBltOk && _frameCount < 3)
                            DebugLog($"BitBlt failed (error {Marshal.GetLastWin32Error()})");

                        // Read raw BGRA pixels via GetDIBits
                        // CRITICAL: Deselect bitmap from DC before GetDIBits (Phase 4 lesson - API contract)
                        // GetDIBits requires the bitmap NOT selected into any DC.
                        var deselected = SelectObject(hMemDC, hOldBitmap);
                        if (_frameCount < 3)
                            DebugLog($"GetDIBits prep: deselected=0x{deselected:X}, hBitmap=0x{hBitmap:X}, hDesktopDC=0x{hDesktopDC:X}, hMemDC=0x{hMemDC:X}");

                        try
                        {
                            var stride = _desktopWidth * 4;
                            var bufferSize = stride * _desktopHeight;

                            // Reuse buffer if same size
                            if (_bgraBuffer == null || _bgraBuffer.Length != bufferSize)
                            {
                                _bgraBuffer = new byte[bufferSize];
                                DebugLog($"Allocated BGRA buffer: {bufferSize}b ({_desktopWidth}x{_desktopHeight}x4)");
                            }

                            var bmi = new BITMAPINFO
                            {
                                bmiHeader = new BITMAPINFOHEADER
                                {
                                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                                    biWidth = _desktopWidth,
                                    biHeight = -_desktopHeight, // Negative = top-down (matches DXGI BGRA layout)
                                    biPlanes = 1,
                                    biBitCount = 32,
                                    biCompression = BI_RGB
                                }
                            };

                            // Use hDesktopDC for color format reference (Phase 4 lesson)
                            if (_frameCount < 3)
                                DebugLog($"Calling GetDIBits: hdc=0x{hDesktopDC:X}, hbmp=0x{hBitmap:X}, scanlines={_desktopHeight}, bufSize={bufferSize}");

                            var scanlines = GetDIBits(hDesktopDC, hBitmap, 0, (uint)_desktopHeight,
                                _bgraBuffer, ref bmi, DIB_RGB_COLORS);

                            if (_frameCount < 3)
                                DebugLog($"GetDIBits returned {scanlines} scanlines (expected {_desktopHeight})");

                            if (scanlines > 0)
                            {
                                _frameCount++;
                                if (_frameCount <= 3 || _frameCount % 100 == 0)
                                    DebugLog($"Frame #{_frameCount}: {bufferSize}b BGRA, {_desktopWidth}x{_desktopHeight}, msSinceInput={msSinceInput}, subscribers={OnFrameCaptured?.GetInvocationList().Length ?? 0}");

                                // Verify first pixel isn't all zeros (sanity check for valid capture)
                                if (_frameCount <= 3)
                                {
                                    var b = _bgraBuffer[0]; var g = _bgraBuffer[1]; var r = _bgraBuffer[2]; var a = _bgraBuffer[3];
                                    DebugLog($"First pixel BGRA: ({b},{g},{r},{a}) - should be non-zero for valid capture");
                                }

                                OnFrameCaptured?.Invoke(_bgraBuffer, _desktopWidth, _desktopHeight, stride);

                                if (_frameCount <= 3)
                                    DebugLog($"Frame #{_frameCount} delivered to {OnFrameCaptured?.GetInvocationList().Length ?? 0} subscribers");
                            }
                            else
                            {
                                DebugLog($"GetDIBits returned 0 scanlines (error {Marshal.GetLastWin32Error()})");
                            }
                        }
                        catch (Exception ex)
                        {
                            DebugLog($"BGRA read error: {ex.GetType().Name}: {ex.Message}");
                        }

                        // Re-select bitmap into DC for next BitBlt
                        hOldBitmap = SelectObject(hMemDC, hBitmap);
                        if (_frameCount <= 3)
                            DebugLog($"Re-selected bitmap into DC: hOldBitmap=0x{hOldBitmap:X}");

                        // FPS during active capture: ~15fps for responsive hover feedback
                        var sleepMs = _frameCount <= 3 ? 33 : 66; // Burst first 3 frames faster (~30fps), then ~15fps
                        Thread.Sleep(sleepMs);
                        continue; // Skip the 150ms poll sleep
                    }
                    else if (!string.Equals(desktopName, "Winlogon", StringComparison.OrdinalIgnoreCase) && wasActive)
                    {
                        // Secure Desktop deactivated - switch back to original
                        DebugLog("Secure Desktop INACTIVE (returned to Default desktop)");

                        CleanupGdiResources(ref hDesktopDC, ref hMemDC, ref hBitmap, ref hOldBitmap);
                        cachedWidth = 0;
                        cachedHeight = 0;
                        _bgraBuffer = null;

                        SetThreadDesktop(originalDesktop);
                        CloseDesktop(_winlogonDesktop);
                        _winlogonDesktop = IntPtr.Zero;

                        wasActive = false;
                        _isSecureDesktopActive = false;

                        CloseDesktop(hDesk);

                        try { OnSecureDesktopInactive?.Invoke(); }
                        catch (Exception ex) { DebugLog($"OnSecureDesktopInactive handler error: {ex.Message}"); }
                    }
                    else
                    {
                        // Not Winlogon and wasn't active - just close and wait
                        CloseDesktop(hDesk);
                    }

                    // 150ms poll when not actively capturing (wake signal can shorten this)
                    _wakeSignal.Wait(150);
                    _wakeSignal.Reset();
                }
                catch (Exception ex)
                {
                    DebugLog($"Capture loop iteration error: {ex.Message}");
                    Thread.Sleep(500); // Longer sleep on error to avoid spinning
                }
            }
        }
        finally
        {
            // Cleanup
            CleanupGdiResources(ref hDesktopDC, ref hMemDC, ref hBitmap, ref hOldBitmap);

            if (wasActive)
            {
                SetThreadDesktop(originalDesktop);
                if (_winlogonDesktop != IntPtr.Zero)
                {
                    CloseDesktop(_winlogonDesktop);
                    _winlogonDesktop = IntPtr.Zero;
                }
                _isSecureDesktopActive = false;
            }

            DebugLog("Capture loop exited");
        }
    }

    private static void CleanupGdiResources(ref IntPtr hDesktopDC, ref IntPtr hMemDC,
        ref IntPtr hBitmap, ref IntPtr hOldBitmap)
    {
        if (hOldBitmap != IntPtr.Zero && hMemDC != IntPtr.Zero)
        {
            SelectObject(hMemDC, hOldBitmap);
            hOldBitmap = IntPtr.Zero;
        }

        if (hBitmap != IntPtr.Zero)
        {
            DeleteObject(hBitmap);
            hBitmap = IntPtr.Zero;
        }

        if (hMemDC != IntPtr.Zero)
        {
            DeleteDC(hMemDC);
            hMemDC = IntPtr.Zero;
        }

        if (hDesktopDC != IntPtr.Zero)
        {
            var hDesktopWnd = GetDesktopWindow();
            ReleaseDC(hDesktopWnd, hDesktopDC);
            hDesktopDC = IntPtr.Zero;
        }
    }

    private static string? GetDesktopName(IntPtr hDesktop)
    {
        var buffer = new byte[256];
        if (GetUserObjectInformation(hDesktop, UOI_NAME, buffer, buffer.Length, out int lengthNeeded))
        {
            // lengthNeeded includes the null terminator (2 bytes for Unicode)
            var charCount = Math.Max(0, lengthNeeded / 2 - 1);
            return System.Text.Encoding.Unicode.GetString(buffer, 0, charCount * 2);
        }
        return null;
    }

    private static Common.Protocol.MouseButton ParseMouseButton(string? button) => button switch
    {
        "Left" => Common.Protocol.MouseButton.Left,
        "Right" => Common.Protocol.MouseButton.Right,
        "Middle" => Common.Protocol.MouseButton.Middle,
        _ => Common.Protocol.MouseButton.Left
    };

    private static Common.Protocol.KeyModifiers ParseModifiers(System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("modifiers", out var mods))
            return Common.Protocol.KeyModifiers.None;

        return new Common.Protocol.KeyModifiers(
            mods.TryGetProperty("ctrl", out var c) && c.GetBoolean(),
            mods.TryGetProperty("shift", out var s) && s.GetBoolean(),
            mods.TryGetProperty("alt", out var a) && a.GetBoolean(),
            mods.TryGetProperty("meta", out var m) && m.GetBoolean());
    }

    public void Dispose()
    {
        Stop();
        _wakeSignal.Dispose();
    }

    #region Win32 P/Invoke - Desktop API + GetDIBits

    private const int UOI_NAME = 2;
    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetThreadDesktop(uint dwThreadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex,
        [Out] byte[] pvInfo, int nLength, out int lpnLengthNeeded);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest,
        int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines,
        [Out] byte[] lpvBits, ref BITMAPINFO lpbi, uint uUsage);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        // bmiColors omitted - not needed for BI_RGB 32bpp
    }

    #endregion
}
