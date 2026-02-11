using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SteamViewer.Platform.Windows.Input;

namespace SteamViewer.Platform.Windows.Elevation;

/// <summary>
/// Captures the Secure Desktop (Winlogon) screen via BitBlt when a UAC prompt is active.
/// Runs inside the SYSTEM helper process on a dedicated clean thread (no windows, no COM).
/// Detects desktop switches by polling OpenInputDesktop every 150ms.
/// When Winlogon is active, captures at ~15 FPS and fires OnFrameCaptured with JPEG data.
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

    // The Winlogon desktop handle held by the capture thread (valid while active)
    private IntPtr _winlogonDesktop;

    private static readonly string? DebugPath;

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
    }

    private static void DebugLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [SecureDesktop] {message}";
        Console.WriteLine(line);
        try { if (DebugPath != null) File.AppendAllText(DebugPath, line + "\n"); } catch { }
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

    /// <summary>Raised when a JPEG frame is captured. Parameters: (jpegData, width, height).</summary>
    public event Action<byte[], int, int>? OnFrameCaptured;

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
        _captureThread?.Join(5000);
        _captureThread = null;
        DebugLog("Capture thread stopped");
    }

    /// <summary>
    /// Inject input on the Winlogon desktop. Called from the control pipe thread.
    /// The calling thread must not have any windows (SystemHelperServer's reader thread qualifies).
    /// Switches the calling thread's desktop to Winlogon, injects input via SendInput, then switches back.
    /// </summary>
    public void InjectInputOnWinlogon(string inputJson, int screenWidth, int screenHeight)
    {
        if (!_isSecureDesktopActive) return;

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
    /// and captures at ~15 FPS via BitBlt → JPEG.
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

        // GDI resources — created once, reused across frames, recreated on resolution change
        IntPtr hDesktopDC = IntPtr.Zero;
        IntPtr hMemDC = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr hOldBitmap = IntPtr.Zero;
        int cachedWidth = 0, cachedHeight = 0;

        // JPEG encoder params (quality 65%)
        var jpegEncoder = GetEncoder(ImageFormat.Jpeg);
        var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 65L);

        try
        {
            while (!_stopRequested)
            {
                try
                {
                    var hDesk = OpenInputDesktop(0, false, DESKTOP_READOBJECTS | DESKTOP_SWITCHDESKTOP);
                    if (hDesk == IntPtr.Zero)
                    {
                        // Can't open desktop — might be transitioning, wait and retry
                        Thread.Sleep(150);
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
                            Thread.Sleep(150);
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

                        try { OnSecureDesktopActive?.Invoke(_desktopWidth, _desktopHeight); }
                        catch (Exception ex) { DebugLog($"OnSecureDesktopActive handler error: {ex.Message}"); }
                    }
                    else if (string.Equals(desktopName, "Winlogon", StringComparison.OrdinalIgnoreCase) && wasActive)
                    {
                        // Still on Winlogon — capture a frame
                        CloseDesktop(hDesk); // Close the newly opened handle, we already have _winlogonDesktop

                        // Check for resolution change
                        var currentW = GetSystemMetrics(SM_CXSCREEN);
                        var currentH = GetSystemMetrics(SM_CYSCREEN);
                        if (currentW != _desktopWidth || currentH != _desktopHeight)
                        {
                            DebugLog($"Resolution changed: {_desktopWidth}x{_desktopHeight} → {currentW}x{currentH}");
                            _desktopWidth = currentW;
                            _desktopHeight = currentH;
                            // Force GDI resource recreation
                            CleanupGdiResources(ref hDesktopDC, ref hMemDC, ref hBitmap, ref hOldBitmap);
                            cachedWidth = 0;
                            cachedHeight = 0;
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
                        BitBlt(hMemDC, 0, 0, _desktopWidth, _desktopHeight, hDesktopDC, 0, 0, SRCCOPY);

                        // Encode to JPEG
                        try
                        {
                            using var bitmap = Image.FromHbitmap(hBitmap);
                            using var ms = new MemoryStream();
                            bitmap.Save(ms, jpegEncoder!, encoderParams);
                            var jpegData = ms.ToArray();
                            _frameCount++;
                            if (_frameCount <= 3 || _frameCount % 100 == 0)
                                DebugLog($"Frame #{_frameCount}: {jpegData.Length}b, {_desktopWidth}x{_desktopHeight}, subscribers={OnFrameCaptured?.GetInvocationList().Length ?? 0}");
                            OnFrameCaptured?.Invoke(jpegData, _desktopWidth, _desktopHeight);
                        }
                        catch (Exception ex)
                        {
                            DebugLog($"JPEG encode error: {ex.Message}");
                        }

                        // ~15 FPS
                        Thread.Sleep(66);
                        continue; // Skip the 150ms poll sleep
                    }
                    else if (!string.Equals(desktopName, "Winlogon", StringComparison.OrdinalIgnoreCase) && wasActive)
                    {
                        // Secure Desktop deactivated — switch back to original
                        DebugLog("Secure Desktop INACTIVE (returned to Default desktop)");

                        CleanupGdiResources(ref hDesktopDC, ref hMemDC, ref hBitmap, ref hOldBitmap);
                        cachedWidth = 0;
                        cachedHeight = 0;

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
                        // Not Winlogon and wasn't active — just close and wait
                        CloseDesktop(hDesk);
                    }

                    // 150ms poll when not actively capturing
                    Thread.Sleep(150);
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

            encoderParams.Dispose();
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

    private static ImageCodecInfo? GetEncoder(ImageFormat format)
    {
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
        {
            if (codec.FormatID == format.Guid)
                return codec;
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
    }

    #region Win32 P/Invoke — Desktop API

    private const int UOI_NAME = 2;
    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const uint SRCCOPY = 0x00CC0020;

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

    [DllImport("user32.dll", SetLastError = true)]
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

    #endregion
}
