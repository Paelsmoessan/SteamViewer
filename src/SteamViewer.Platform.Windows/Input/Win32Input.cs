using System.Runtime.InteropServices;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Platform.Windows.Input;

/// <summary>
/// Shared static Win32 input injection logic.
/// Used by both WindowsInputInjector (non-elevated) and ElevatedHelperServer (elevated).
/// </summary>
internal static class Win32Input
{
    // Virtual screen dimensions — refreshed on every call (sub-microsecond GetSystemMetrics)
    private static int _vsLeft, _vsTop, _vsWidth, _vsHeight;

    // Multi-monitor support: cached monitor rectangles, re-enumerated when virtual screen changes
    private record struct MonitorRect(int X, int Y, int Width, int Height, bool IsPrimary, string DeviceName);
    private static MonitorRect[]? _monitors;

    // Cached captured monitor bounds — set once at capture start, used as fast-path in ConvertToAbsoluteCoordinates.
    // Source: Sunshine — match once at capture start, reuse stored bounds. (.claude/research/mouse-input/research.md)
    private static int _cachedCaptureW, _cachedCaptureH;
    private static MonitorRect? _cachedTarget;

    private static void RefreshDisplayState()
    {
        // Process-level PMv2 DPI awareness is set in Program.Main() — all threads
        // get physical pixel dimensions from GetSystemMetrics/EnumDisplayMonitors.
        var left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        // Re-enumerate monitors only when virtual screen dimensions change (hot-plug, settings change)
        if (width != _vsWidth || height != _vsHeight || left != _vsLeft || top != _vsTop || _monitors == null)
        {
            EnumerateMonitors();
        }

        _vsLeft = left;
        _vsTop = top;
        _vsWidth = width;
        _vsHeight = height;
    }

    // Desktop sync retry: last-known desktop handle per thread (value only, for comparison).
    // When SendInput fails (returns 0), the thread's desktop may be stale.
    // Re-open the input desktop and retry. Source: Sunshine send_input() + syncThreadDesktop().
    [ThreadStatic]
    private static IntPtr _lastKnownDesktop;

    /// <summary>
    /// SendInput with desktop sync retry. If SendInput returns 0, re-opens the
    /// current input desktop and retries once. Handles desktop transitions
    /// (UAC return, fast user switch) without requiring the SYSTEM helper.
    /// </summary>
    private static uint SendInputWithRetry(uint nInputs, INPUT[] pInputs, int cbSize)
    {
        var sent = SendInput(nInputs, pInputs, cbSize);
        if (sent == nInputs)
            return sent;

        // SendInput failed — try to re-attach to the current input desktop
        var hDesk = OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP);
        if (hDesk == IntPtr.Zero)
            return sent; // Can't open desktop — give up

        if (hDesk != _lastKnownDesktop)
        {
            SetThreadDesktop(hDesk);
            _lastKnownDesktop = hDesk;
            sent = SendInput(nInputs, pInputs, cbSize); // retry
        }

        // Close our handle — SetThreadDesktop already gave the thread its own reference.
        // We keep _lastKnownDesktop as a stale sentinel for change detection (Sunshine pattern).
        CloseDesktop(hDesk);
        return sent;
    }

    private static List<MonitorRect>? _monitorCollector;

    private static void EnumerateMonitors()
    {
        _monitorCollector = new List<MonitorRect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, MonitorEnumCallback, IntPtr.Zero);
        _monitors = _monitorCollector.ToArray();
        _monitorCollector = null;
    }

    private static bool MonitorEnumCallback(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData)
    {
        var mi = new MONITORINFOEXW { cbSize = Marshal.SizeOf<MONITORINFOEXW>() };
        if (GetMonitorInfoW(hMonitor, ref mi))
        {
            _monitorCollector?.Add(new MonitorRect(
                mi.rcMonitor.left,
                mi.rcMonitor.top,
                mi.rcMonitor.right - mi.rcMonitor.left,
                mi.rcMonitor.bottom - mi.rcMonitor.top,
                (mi.dwFlags & MONITORINFOF_PRIMARY) != 0,
                mi.szDevice ?? ""));
        }
        return true;
    }

    /// <summary>
    /// Get virtual screen dimensions. Call after EnsureInitialized.
    /// </summary>
    public static (int Left, int Top, int Width, int Height) GetVirtualScreen()
    {
        RefreshDisplayState();
        return (_vsLeft, _vsTop, _vsWidth, _vsHeight);
    }

    /// <summary>
    /// Get the primary monitor's physical pixel dimensions.
    /// Uses SM_CXSCREEN/SM_CYSCREEN (always returns primary monitor size).
    /// Use this instead of hardcoding 1920x1080.
    /// </summary>
    public static (int Width, int Height) GetPrimaryMonitorSize()
    {
        return (GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
    }

    /// <summary>
    /// Get cached monitor list with physical pixel dimensions.
    /// </summary>
    public static IReadOnlyList<(int X, int Y, int Width, int Height, bool IsPrimary, string DeviceName)> GetMonitors()
    {
        RefreshDisplayState();
        if (_monitors == null) return Array.Empty<(int, int, int, int, bool, string)>();
        return _monitors.Select(m => (m.X, m.Y, m.Width, m.Height, m.IsPrimary, m.DeviceName)).ToArray();
    }

    /// <summary>
    /// Cache the target monitor bounds for the current capture session.
    /// Called once when screen sharing starts. Matches capture dimensions to a monitor.
    /// If capture matches full virtual desktop, clears the cache (no offset needed).
    /// Source: Sunshine — match once, use stored bounds.
    /// </summary>
    public static void SetCapturedMonitor(int captureWidth, int captureHeight)
    {
        RefreshDisplayState();
        _cachedCaptureW = captureWidth;
        _cachedCaptureH = captureHeight;

        if (captureWidth == _vsWidth && captureHeight == _vsHeight)
        {
            _cachedTarget = null; // Full virtual desktop — no offset needed
        }
        else
        {
            _cachedTarget = FindMonitorByResolution(captureWidth, captureHeight)
                           ?? _monitors?.FirstOrDefault(m => m.IsPrimary);
        }
    }

    /// <summary>
    /// Explicitly set the target monitor bounds (when monitor identity is known, e.g., from picker UI).
    /// Bypasses resolution matching entirely — no ambiguity possible.
    /// </summary>
    public static void SetCapturedMonitorExplicit(int x, int y, int w, int h)
    {
        _cachedTarget = new MonitorRect(x, y, w, h, false, "");
        _cachedCaptureW = w;
        _cachedCaptureH = h;
    }

    /// <summary>
    /// Clear the cached target monitor (call when capture stops).
    /// </summary>
    public static void ClearCapturedMonitor()
    {
        _cachedTarget = null;
        _cachedCaptureW = 0;
        _cachedCaptureH = 0;
    }

    /// <summary>
    /// Inject an input event using Win32 SendInput.
    /// </summary>
    public static void InjectInputEvent(InputEvent inputEvent, int screenWidth, int screenHeight)
    {
        RefreshDisplayState();

        switch (inputEvent)
        {
            case InputEvent.MouseMove move:
                InjectMouseMove(move.X, move.Y, screenWidth, screenHeight);
                break;
            case InputEvent.MouseDown down:
                InjectMouseButton(down.Button, down.X, down.Y, screenWidth, screenHeight, isDown: true);
                break;
            case InputEvent.MouseUp up:
                InjectMouseButton(up.Button, up.X, up.Y, screenWidth, screenHeight, isDown: false);
                break;
            case InputEvent.MouseWheel wheel:
                InjectMouseWheel(wheel.DeltaX, wheel.DeltaY);
                break;
            case InputEvent.KeyDown keyDown:
                InjectKey(keyDown.Key, keyDown.Modifiers, isDown: true);
                break;
            case InputEvent.KeyUp keyUp:
                InjectKey(keyUp.Key, keyUp.Modifiers, isDown: false);
                break;
        }
    }

    /// <summary>
    /// Convert remote capture coordinates to Win32 absolute coordinates (0-65535).
    /// When capturing a single monitor on a multi-monitor setup, maps coords to that
    /// monitor's area rather than stretching across the full virtual desktop.
    /// Uses cached monitor bounds (set via SetCapturedMonitor) as fast path.
    /// </summary>
    public static (int AbsX, int AbsY) ConvertToAbsoluteCoordinates(double x, double y, int screenWidth, int screenHeight)
    {
        RefreshDisplayState();

        // Determine which area of the virtual desktop the capture represents
        int targetX = _vsLeft, targetY = _vsTop, targetW = _vsWidth, targetH = _vsHeight;

        // Fast path: use cached target if capture dimensions match what was cached
        if (_cachedTarget != null && screenWidth == _cachedCaptureW && screenHeight == _cachedCaptureH)
        {
            targetX = _cachedTarget.Value.X;
            targetY = _cachedTarget.Value.Y;
            targetW = _cachedTarget.Value.Width;
            targetH = _cachedTarget.Value.Height;
        }
        // Slow path: capture dims changed (rare) or cache not set — re-match
        else if (screenWidth != _vsWidth || screenHeight != _vsHeight)
        {
            var match = FindMonitorByResolution(screenWidth, screenHeight)
                        ?? _monitors?.FirstOrDefault(m => m.IsPrimary);
            if (match != null)
            {
                targetX = match.Value.X;
                targetY = match.Value.Y;
                targetW = match.Value.Width;
                targetH = match.Value.Height;
            }
        }

        // Map capture pixel coords to virtual screen position
        var localX = targetX + x * targetW / screenWidth;
        var localY = targetY + y * targetH / screenHeight;

        // Convert to absolute 0-65535 range across full virtual desktop
        var absX = (int)Math.Round((localX - _vsLeft) * 65535.0 / _vsWidth);
        var absY = (int)Math.Round((localY - _vsTop) * 65535.0 / _vsHeight);

        return (Math.Clamp(absX, 0, 65535), Math.Clamp(absY, 0, 65535));
    }

    /// <summary>
    /// Match capture dimensions to a specific monitor.
    /// Returns null if capture matches full virtual desktop or no monitor matches.
    /// </summary>
    private static MonitorRect? FindMonitorByResolution(int width, int height)
    {
        if (_monitors == null || _monitors.Length <= 1) return null;

        MonitorRect? firstMatch = null;
        var matchCount = 0;

        foreach (var mon in _monitors)
        {
            if (mon.Width == width && mon.Height == height)
            {
                firstMatch ??= mon;
                matchCount++;
            }
        }

        if (matchCount == 1) return firstMatch;

        // Multiple monitors with same resolution — prefer primary
        if (matchCount > 1)
        {
            foreach (var mon in _monitors)
            {
                if (mon.Width == width && mon.Height == height && mon.IsPrimary)
                    return mon;
            }
            return firstMatch; // None is primary, use first match
        }

        return null; // No match — fall back to full virtual desktop
    }

    internal static void InjectMouseMove(double x, double y, int screenWidth, int screenHeight)
    {
        var (absX, absY) = ConvertToAbsoluteCoordinates(x, y, screenWidth, screenHeight);

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            union = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = absX,
                    dy = absY,
                    mouseData = 0,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInputWithRetry(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    internal static void InjectMouseButton(MouseButton button, double x, double y, int screenWidth, int screenHeight, bool isDown)
    {
        var (absX, absY) = ConvertToAbsoluteCoordinates(x, y, screenWidth, screenHeight);

        // Split move + click into separate SendInput calls.
        // Windows may not process the position before the click if combined.
        // Source: FreeRDP, Sunshine
        var moveInput = new INPUT
        {
            type = INPUT_MOUSE,
            union = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = absX,
                    dy = absY,
                    mouseData = 0,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInputWithRetry(1, new[] { moveInput }, Marshal.SizeOf<INPUT>());

        // Button event — no MOVE flag, no position. Cursor is already at the right spot.
        // Source: Sunshine button_mouse() (research.md lines 1183-1195)
        uint buttonFlags = 0;
        uint mouseData = 0;
        switch (button)
        {
            case MouseButton.Left:
                buttonFlags = isDown ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP;
                break;
            case MouseButton.Right:
                buttonFlags = isDown ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP;
                break;
            case MouseButton.Middle:
                buttonFlags = isDown ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP;
                break;
            case MouseButton.XButton1:
                buttonFlags = isDown ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP;
                mouseData = XBUTTON1;
                break;
            case MouseButton.XButton2:
                buttonFlags = isDown ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP;
                mouseData = XBUTTON2;
                break;
        }

        var buttonInput = new INPUT
        {
            type = INPUT_MOUSE,
            union = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = mouseData,
                    dwFlags = buttonFlags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInputWithRetry(1, new[] { buttonInput }, Marshal.SizeOf<INPUT>());
    }

    // Scroll accumulation — accumulates sub-tick deltas, only sends full WHEEL_DELTA (120) multiples.
    // Prevents phantom scroll events from high-precision trackpads.
    // Source: Sunshine high-resolution scroll accumulation (research.md lines 1124-1139)
    private static int _accumulatedVScroll;
    private static int _accumulatedHScroll;

    internal static void InjectMouseWheel(double deltaX, double deltaY)
    {
        _accumulatedVScroll += (int)(-deltaY * WHEEL_DELTA / 100.0);
        _accumulatedHScroll += (int)(deltaX * WHEEL_DELTA / 100.0);

        var inputs = new List<INPUT>();

        var vTicks = _accumulatedVScroll / WHEEL_DELTA;
        if (vTicks != 0)
        {
            inputs.Add(MakeWheelInput(MOUSEEVENTF_WHEEL, vTicks * WHEEL_DELTA));
            _accumulatedVScroll -= vTicks * WHEEL_DELTA;
        }

        var hTicks = _accumulatedHScroll / WHEEL_DELTA;
        if (hTicks != 0)
        {
            inputs.Add(MakeWheelInput(MOUSEEVENTF_HWHEEL, hTicks * WHEEL_DELTA));
            _accumulatedHScroll -= hTicks * WHEEL_DELTA;
        }

        if (inputs.Count > 0)
        {
            SendInputWithRetry((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        }
    }

    private static INPUT MakeWheelInput(uint flags, int wheelDelta)
    {
        return new INPUT
        {
            type = INPUT_MOUSE,
            union = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0, dy = 0,
                    mouseData = (uint)wheelDelta,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    internal static void InjectKey(string key, KeyModifiers modifiers, bool isDown)
    {
        var inputs = new List<INPUT>();

        if (isDown)
        {
            if (modifiers.Ctrl) AddKeyInput(inputs, VK_CONTROL, isDown: true);
            if (modifiers.Alt) AddKeyInput(inputs, VK_MENU, isDown: true);
            if (modifiers.Shift) AddKeyInput(inputs, VK_SHIFT, isDown: true);
            if (modifiers.Meta) AddKeyInput(inputs, VK_LWIN, isDown: true);
        }

        var vk = KeyToVirtualKey(key);
        if (vk != 0)
        {
            AddKeyInput(inputs, vk, isDown);
        }

        if (!isDown)
        {
            if (modifiers.Meta) AddKeyInput(inputs, VK_LWIN, isDown: false);
            if (modifiers.Shift) AddKeyInput(inputs, VK_SHIFT, isDown: false);
            if (modifiers.Alt) AddKeyInput(inputs, VK_MENU, isDown: false);
            if (modifiers.Ctrl) AddKeyInput(inputs, VK_CONTROL, isDown: false);
        }

        if (inputs.Count > 0)
        {
            SendInputWithRetry((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        }
    }

    private static void AddKeyInput(List<INPUT> inputs, ushort vk, bool isDown)
    {
        inputs.Add(new INPUT
        {
            type = INPUT_KEYBOARD,
            union = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = isDown ? 0u : KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        });
    }

    public static ushort KeyToVirtualKey(string key)
    {
        return key.ToLowerInvariant() switch
        {
            // Letters
            "a" => 0x41, "b" => 0x42, "c" => 0x43, "d" => 0x44, "e" => 0x45,
            "f" => 0x46, "g" => 0x47, "h" => 0x48, "i" => 0x49, "j" => 0x4A,
            "k" => 0x4B, "l" => 0x4C, "m" => 0x4D, "n" => 0x4E, "o" => 0x4F,
            "p" => 0x50, "q" => 0x51, "r" => 0x52, "s" => 0x53, "t" => 0x54,
            "u" => 0x55, "v" => 0x56, "w" => 0x57, "x" => 0x58, "y" => 0x59,
            "z" => 0x5A,

            // Numbers
            "0" or ")" => 0x30, "1" or "!" => 0x31, "2" or "@" => 0x32,
            "3" or "#" => 0x33, "4" or "$" => 0x34, "5" or "%" => 0x35,
            "6" or "^" => 0x36, "7" or "&" => 0x37, "8" or "*" => 0x38,
            "9" or "(" => 0x39,

            // Function keys
            "f1" => VK_F1, "f2" => VK_F2, "f3" => VK_F3, "f4" => VK_F4,
            "f5" => VK_F5, "f6" => VK_F6, "f7" => VK_F7, "f8" => VK_F8,
            "f9" => VK_F9, "f10" => VK_F10, "f11" => VK_F11, "f12" => VK_F12,

            // Special keys
            "enter" => VK_RETURN,
            "escape" => VK_ESCAPE,
            "backspace" => VK_BACK,
            "tab" => VK_TAB,
            " " or "space" => VK_SPACE,
            "delete" => VK_DELETE,
            "insert" => VK_INSERT,
            "home" => VK_HOME,
            "end" => VK_END,
            "pageup" => VK_PRIOR,
            "pagedown" => VK_NEXT,

            // Arrow keys
            "arrowup" => VK_UP,
            "arrowdown" => VK_DOWN,
            "arrowleft" => VK_LEFT,
            "arrowright" => VK_RIGHT,

            // Modifiers
            "shift" => VK_SHIFT,
            "control" => VK_CONTROL,
            "alt" => VK_MENU,
            "meta" => VK_LWIN,
            "capslock" => VK_CAPITAL,
            "numlock" => VK_NUMLOCK,
            "scrolllock" => VK_SCROLL,

            // Punctuation
            ";" or ":" => VK_OEM_1,
            "=" or "+" => VK_OEM_PLUS,
            "," or "<" => VK_OEM_COMMA,
            "-" or "_" => VK_OEM_MINUS,
            "." or ">" => VK_OEM_PERIOD,
            "/" or "?" => VK_OEM_2,
            "`" or "~" => VK_OEM_3,
            "[" or "{" => VK_OEM_4,
            "\\" or "|" => VK_OEM_5,
            "]" or "}" => VK_OEM_6,
            "'" or "\"" => VK_OEM_7,

            _ => 0
        };
    }

    #region Win32 Interop

    internal const int INPUT_MOUSE = 0;
    internal const int INPUT_KEYBOARD = 1;

    internal const uint MOUSEEVENTF_MOVE = 0x0001;
    internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
    internal const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    internal const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    internal const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    internal const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    internal const uint MOUSEEVENTF_XDOWN = 0x0080;
    internal const uint MOUSEEVENTF_XUP = 0x0100;
    internal const uint MOUSEEVENTF_WHEEL = 0x0800;
    internal const uint MOUSEEVENTF_HWHEEL = 0x1000;
    internal const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    internal const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    internal const uint KEYEVENTF_KEYUP = 0x0002;

    internal const int WHEEL_DELTA = 120;
    internal const uint XBUTTON1 = 0x0001;
    internal const uint XBUTTON2 = 0x0002;

    internal const int SM_CXSCREEN = 0;
    internal const int SM_CYSCREEN = 1;
    internal const int SM_XVIRTUALSCREEN = 76;
    internal const int SM_YVIRTUALSCREEN = 77;
    internal const int SM_CXVIRTUALSCREEN = 78;
    internal const int SM_CYVIRTUALSCREEN = 79;

    internal const ushort VK_BACK = 0x08;
    internal const ushort VK_TAB = 0x09;
    internal const ushort VK_RETURN = 0x0D;
    internal const ushort VK_SHIFT = 0x10;
    internal const ushort VK_CONTROL = 0x11;
    internal const ushort VK_MENU = 0x12;
    internal const ushort VK_CAPITAL = 0x14;
    internal const ushort VK_ESCAPE = 0x1B;
    internal const ushort VK_SPACE = 0x20;
    internal const ushort VK_PRIOR = 0x21;
    internal const ushort VK_NEXT = 0x22;
    internal const ushort VK_END = 0x23;
    internal const ushort VK_HOME = 0x24;
    internal const ushort VK_LEFT = 0x25;
    internal const ushort VK_UP = 0x26;
    internal const ushort VK_RIGHT = 0x27;
    internal const ushort VK_DOWN = 0x28;
    internal const ushort VK_INSERT = 0x2D;
    internal const ushort VK_DELETE = 0x2E;
    internal const ushort VK_LWIN = 0x5B;
    internal const ushort VK_NUMLOCK = 0x90;
    internal const ushort VK_SCROLL = 0x91;
    internal const ushort VK_F1 = 0x70;
    internal const ushort VK_F2 = 0x71;
    internal const ushort VK_F3 = 0x72;
    internal const ushort VK_F4 = 0x73;
    internal const ushort VK_F5 = 0x74;
    internal const ushort VK_F6 = 0x75;
    internal const ushort VK_F7 = 0x76;
    internal const ushort VK_F8 = 0x77;
    internal const ushort VK_F9 = 0x78;
    internal const ushort VK_F10 = 0x79;
    internal const ushort VK_F11 = 0x7A;
    internal const ushort VK_F12 = 0x7B;
    internal const ushort VK_OEM_1 = 0xBA;
    internal const ushort VK_OEM_PLUS = 0xBB;
    internal const ushort VK_OEM_COMMA = 0xBC;
    internal const ushort VK_OEM_MINUS = 0xBD;
    internal const ushort VK_OEM_PERIOD = 0xBE;
    internal const ushort VK_OEM_2 = 0xBF;
    internal const ushort VK_OEM_3 = 0xC0;
    internal const ushort VK_OEM_4 = 0xDB;
    internal const ushort VK_OEM_5 = 0xDC;
    internal const ushort VK_OEM_6 = 0xDD;
    internal const ushort VK_OEM_7 = 0xDE;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    // Multi-monitor enumeration
    private const int MONITORINFOF_PRIMARY = 1;

    private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW lpmi);

    // Desktop sync retry P/Invoke
    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEXW
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public int type;
        public InputUnion union;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    #endregion
}
