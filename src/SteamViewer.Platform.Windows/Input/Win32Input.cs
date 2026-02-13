using System.Runtime.InteropServices;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Platform.Windows.Input;

/// <summary>
/// Shared static Win32 input injection logic.
/// Used by both WindowsInputInjector (non-elevated) and ElevatedHelperServer (elevated).
/// </summary>
internal static class Win32Input
{
    // Lazy-init virtual screen dimensions
    private static int _vsLeft, _vsTop, _vsWidth, _vsHeight;
    private static bool _initialized;
    private static readonly object InitLock = new();


    private static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (InitLock)
        {
            if (_initialized) return;
            _vsLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
            _vsTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
            _vsWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            _vsHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            _initialized = true;
        }
    }

    /// <summary>
    /// Get virtual screen dimensions. Call after EnsureInitialized.
    /// </summary>
    public static (int Left, int Top, int Width, int Height) GetVirtualScreen()
    {
        EnsureInitialized();
        return (_vsLeft, _vsTop, _vsWidth, _vsHeight);
    }

    /// <summary>
    /// Inject an input event using Win32 SendInput.
    /// </summary>
    public static void InjectInputEvent(InputEvent inputEvent, int screenWidth, int screenHeight)
    {
        EnsureInitialized();

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
    /// </summary>
    public static (int AbsX, int AbsY) ConvertToAbsoluteCoordinates(double x, double y, int screenWidth, int screenHeight)
    {
        EnsureInitialized();

        var localX = x * _vsWidth / screenWidth + _vsLeft;
        var localY = y * _vsHeight / screenHeight + _vsTop;

        var absX = (int)Math.Round((localX - _vsLeft) * 65535.0 / _vsWidth);
        var absY = (int)Math.Round((localY - _vsTop) * 65535.0 / _vsHeight);

        return (Math.Clamp(absX, 0, 65535), Math.Clamp(absY, 0, 65535));
    }

    /// <summary>
    /// Convert coordinates directly to absolute (0-65535) without virtual screen mapping.
    /// Used for Winlogon/Secure Desktop where virtual screen dims don't apply.
    /// </summary>
    internal static (int AbsX, int AbsY) ConvertToAbsoluteDirect(double x, double y, int screenWidth, int screenHeight)
    {
        var absX = (int)Math.Round(x * 65535.0 / screenWidth);
        var absY = (int)Math.Round(y * 65535.0 / screenHeight);
        return (Math.Clamp(absX, 0, 65535), Math.Clamp(absY, 0, 65535));
    }

    /// <summary>
    /// Inject mouse move with pre-computed absolute coordinates. No virtual screen mapping.
    /// </summary>
    internal static void InjectMouseMoveRaw(int absX, int absY)
    {
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
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Inject mouse button with pre-computed absolute coordinates. No virtual screen mapping.
    /// </summary>
    internal static void InjectMouseButtonRaw(MouseButton button, int absX, int absY, bool isDown)
    {
        uint flags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE;

        switch (button)
        {
            case MouseButton.Left:
                flags |= isDown ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP;
                break;
            case MouseButton.Right:
                flags |= isDown ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP;
                break;
            case MouseButton.Middle:
                flags |= isDown ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP;
                break;
        }

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
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
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

        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    internal static void InjectMouseButton(MouseButton button, double x, double y, int screenWidth, int screenHeight, bool isDown)
    {
        var (absX, absY) = ConvertToAbsoluteCoordinates(x, y, screenWidth, screenHeight);

        uint flags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;

        switch (button)
        {
            case MouseButton.Left:
                flags |= isDown ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP;
                break;
            case MouseButton.Right:
                flags |= isDown ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP;
                break;
            case MouseButton.Middle:
                flags |= isDown ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP;
                break;
        }

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
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    internal static void InjectMouseWheel(double deltaX, double deltaY)
    {
        var inputs = new List<INPUT>();

        if (Math.Abs(deltaY) > 0.001)
        {
            var wheelDelta = (int)(-deltaY * WHEEL_DELTA / 100.0);
            inputs.Add(new INPUT
            {
                type = INPUT_MOUSE,
                union = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dx = 0, dy = 0,
                        mouseData = (uint)wheelDelta,
                        dwFlags = MOUSEEVENTF_WHEEL,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            });
        }

        if (Math.Abs(deltaX) > 0.001)
        {
            var wheelDelta = (int)(deltaX * WHEEL_DELTA / 100.0);
            inputs.Add(new INPUT
            {
                type = INPUT_MOUSE,
                union = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dx = 0, dy = 0,
                        mouseData = (uint)wheelDelta,
                        dwFlags = MOUSEEVENTF_HWHEEL,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            });
        }

        if (inputs.Count > 0)
        {
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        }
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
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
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
    internal const uint MOUSEEVENTF_WHEEL = 0x0800;
    internal const uint MOUSEEVENTF_HWHEEL = 0x1000;
    internal const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    internal const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    internal const uint KEYEVENTF_KEYUP = 0x0002;

    internal const int WHEEL_DELTA = 120;

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
