using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using SteamViewer.Client.Core.Capture;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Platform.Windows.Input;

public enum CaptureMode { Disabled, LogOnly, Active }

/// <summary>
/// Low-level keyboard hook (WH_KEYBOARD_LL) that intercepts system keys
/// like Win, Alt+Tab, Ctrl+Esc when viewer input is locked. When CaptureMode
/// is LogOnly or Active, processes ALL keystrokes for the native keyboard
/// capture pipeline (Sunshine pattern). In Active mode, resolves Unicode
/// characters via ToUnicodeEx for cross-layout hybrid injection.
/// </summary>
public sealed class WindowsSystemKeyInterceptor : ISystemKeyInterceptor
{
    private readonly ILogger<WindowsSystemKeyInterceptor> _logger;
    private IntPtr _hookId;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private volatile bool _running;
    private HookProc? _hookProc;
    private volatile bool _firstCallback = true;

    private volatile IntPtr _viewerHwnd;
    private uint _viewerPid;
    private volatile CaptureMode _captureMode = CaptureMode.Disabled;

    // Side-specific modifier tracking for accurate ToUnicodeEx keyState.
    private bool _lCtrlDown, _rCtrlDown;
    private bool _lShiftDown, _rShiftDown;
    private bool _lAltDown, _rAltDown;
    private bool _lWinDown, _rWinDown;
    private bool _capsLockOn, _numLockOn;

    // Reusable keyboard state array for ToUnicodeEx (avoid per-callback allocation).
    private readonly byte[] _keyState = new byte[256];
    private readonly char[] _charBuffer = new char[4];

    // Cache keydown Unicode char per VK so keyup sends the same char.
    // Prevents stuck keys from modifier state changing between down/up.
    private readonly Dictionary<ushort, uint> _activeUnicodeKeys = new();

    // Layout change detection: last known HKL from foreground thread.
    private IntPtr _lastKnownHkl;
    private string? _lastKnownKlid;

    public bool IsInstalled => _hookId != IntPtr.Zero;
    public event Action<string, bool, bool>? SystemKeyIntercepted;
    public event Action<ushort, ushort, bool, bool, KeyModifiers, uint>? KeyEventCaptured;
    public event Action<string>? LayoutChanged;

    public bool FullCapture
    {
        get => _captureMode != CaptureMode.Disabled;
        set
        {
            if (!value)
            {
                _captureMode = CaptureMode.Disabled;
                ResetModifierState();
            }
        }
    }

    public CaptureMode Mode
    {
        get => _captureMode;
        set
        {
            _captureMode = value;
            if (value == CaptureMode.Disabled)
                ResetModifierState();
            else if (value == CaptureMode.Active)
                InitializeToggleState();
            _logger.LogInformation("[Hook] CaptureMode set to {Mode}", value);
        }
    }

    public WindowsSystemKeyInterceptor(ILogger<WindowsSystemKeyInterceptor> logger)
    {
        _logger = logger;
    }

    public void SetViewerHwnd(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            _logger.LogWarning("[Hook] SetViewerHwnd called with IntPtr.Zero - ignoring");
            return;
        }
        _viewerHwnd = hwnd;
        GetWindowThreadProcessId(hwnd, out var pid);
        _viewerPid = pid;
        _logger.LogInformation("[Hook] HWND set to 0x{Hwnd:X}, PID={Pid}", hwnd, pid);
    }

    public string? GetCurrentKeyboardLayoutId()
    {
        var sb = new StringBuilder(KL_NAMELENGTH);
        return GetKeyboardLayoutName(sb) ? sb.ToString() : null;
    }

    public void Install()
    {
        if (_hookId != IntPtr.Zero) return;

        _running = true;
        var ready = new ManualResetEventSlim(false);

        _hookThread = new Thread(() =>
        {
            _hookThreadId = GetCurrentThreadId();
            _hookProc = HookCallback;

            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc,
                GetModuleHandle(null), 0);

            if (_hookId == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                _logger.LogError("[SystemKeyHook] SetWindowsHookEx FAILED - Win32 error {Error}", error);
                ready.Set();
                return;
            }

            _logger.LogInformation("[SystemKeyHook] Installed successfully");
            ready.Set();

            while (_running && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        });
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.IsBackground = true;
        _hookThread.Name = "SystemKeyHook";
        _hookThread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
    }

    public void Uninstall()
    {
        if (_hookId == IntPtr.Zero && _hookThread == null) return;

        _logger.LogDebug("[SystemKeyHook] Uninstalling...");
        _running = false;
        _captureMode = CaptureMode.Disabled;
        ResetModifierState();

        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        if (_hookThread != null && _hookThreadId != 0)
        {
            PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _hookThread.Join(TimeSpan.FromSeconds(2));
            _hookThread = null;
            _hookThreadId = 0;
        }

        _hookProc = null;
        _firstCallback = true;
    }

    public void Dispose() => Uninstall();

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var vk = (ushort)kb.vkCode;
            var scanCode = (ushort)kb.scanCode;
            var isExtended = (kb.flags & LLKHF_EXTENDED) != 0;
            var isDown = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
            var isUp = wParam == WM_KEYUP || wParam == WM_SYSKEYUP;

            if (_firstCallback)
            {
                _logger.LogDebug("[SystemKeyHook] First callback - vk=0x{Vk:X2} sc=0x{Sc:X3}", vk, scanCode);
                _firstCallback = false;
            }

            UpdateModState(vk, isDown, isUp);

            var mode = _captureMode;
            var isForeground = IsViewerForeground();

            // --- Capture modes (LogOnly / Active) ---
            if (mode != CaptureMode.Disabled && isForeground && (isDown || isUp))
            {
                if (mode == CaptureMode.LogOnly)
                {
                    _logger.LogDebug("[Hook-Capture] vk=0x{Vk:X2} sc=0x{Sc:X3} ext={Ext} {State}",
                        vk, scanCode, isExtended, isDown ? "DOWN" : "UP");
                }
                else if (mode == CaptureMode.Active)
                {
                    // Drop AltGr phantom LCtrl (sc=0x21D). Host driver synthesizes
                    // its own LCtrl when it receives RAlt.
                    if (scanCode == 0x21D && vk == VK_CONTROL)
                    {
                        _logger.LogTrace("[Hook] Dropped phantom LCtrl sc=0x21D");
                        return (IntPtr)1;
                    }

                    var modSnapshot = SnapshotModifiers();
                    uint unicodeChar = 0;

                    if (isDown)
                    {
                        unicodeChar = ResolveUnicodeChar(vk, scanCode);
                        if (unicodeChar != 0)
                            _activeUnicodeKeys[vk] = unicodeChar;
                        else
                            _activeUnicodeKeys.Remove(vk);
                    }
                    else
                    {
                        // Keyup must use same char as keydown to avoid stuck keys.
                        if (_activeUnicodeKeys.TryGetValue(vk, out var cached))
                        {
                            unicodeChar = cached;
                            _activeUnicodeKeys.Remove(vk);
                        }
                    }

                    _logger.LogDebug("[Hook-Active] vk=0x{Vk:X2} sc=0x{Sc:X3} uc=0x{Uc:X} ({Char}) {State}",
                        vk, scanCode, unicodeChar, unicodeChar >= 0x20 ? (char)unicodeChar : ' ', isDown ? "DOWN" : "UP");

                    // Detect layout change
                    DetectLayoutChange();

                    var capturedSc = scanCode;
                    var capturedVk = vk;
                    var capturedDown = isDown;
                    var capturedExt = isExtended;
                    var capturedUc = unicodeChar;

                    ThreadPool.QueueUserWorkItem(_ =>
                        KeyEventCaptured?.Invoke(capturedSc, capturedVk, capturedDown, capturedExt, modSnapshot, capturedUc));

                    return (IntPtr)1; // Suppress - don't deliver to WebView2
                }
            }

            // --- Legacy system key interception (Win, Alt+Tab, Ctrl+Esc) ---
            var altDown = (kb.flags & LLKHF_ALTDOWN) != 0;
            var ctrlDown = _lCtrlDown || _rCtrlDown;

            bool shouldIntercept = vk switch
            {
                VK_LWIN or VK_RWIN => true,
                VK_TAB when altDown => true,
                VK_ESCAPE when ctrlDown => true,
                _ => false
            };

            if (shouldIntercept && (isDown || isUp))
            {
                var key = VkToKeyString(vk);
                if (key != null)
                {
                    _logger.LogDebug("[SystemKeyHook] Intercepted: {Key} {State}", key, isDown ? "down" : "up");
                    var capturedKey = key;
                    var capturedDown = isDown;
                    var capturedAlt = altDown;
                    ThreadPool.QueueUserWorkItem(_ => SystemKeyIntercepted?.Invoke(capturedKey, capturedDown, capturedAlt));
                    return (IntPtr)1;
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void UpdateModState(ushort vk, bool isDown, bool isUp)
    {
        switch (vk)
        {
            case 0xA2: _lCtrlDown = isDown; break;   // VK_LCONTROL
            case 0xA3: _rCtrlDown = isDown; break;    // VK_RCONTROL
            case 0xA0: _lShiftDown = isDown; break;   // VK_LSHIFT
            case 0xA1: _rShiftDown = isDown; break;   // VK_RSHIFT
            case 0xA4: _lAltDown = isDown; break;     // VK_LMENU
            case 0xA5: _rAltDown = isDown; break;     // VK_RMENU
            case VK_LWIN: _lWinDown = isDown; break;
            case VK_RWIN: _rWinDown = isDown; break;
            // Generic modifiers (some keyboard drivers send these)
            case VK_CONTROL: _lCtrlDown = isDown; break;
            case VK_SHIFT: _lShiftDown = isDown; break;
            case VK_MENU: _lAltDown = isDown; break;
            // Toggle keys: flip on keydown only
            case VK_CAPITAL when isDown: _capsLockOn = !_capsLockOn; break;
            case VK_NUMLOCK when isDown: _numLockOn = !_numLockOn; break;
        }
    }

    private KeyModifiers SnapshotModifiers()
    {
        return new KeyModifiers(
            Ctrl: _lCtrlDown || _rCtrlDown,
            Shift: _lShiftDown || _rShiftDown,
            Alt: _lAltDown || _rAltDown,
            Meta: _lWinDown || _rWinDown
        );
    }

    /// <summary>
    /// Resolve the Unicode character produced by this key press using the viewer's
    /// active keyboard layout. Uses ToUnicodeEx with wFlags=0x4 (non-destructive
    /// peek - does not consume dead key buffer, Win10 1607+).
    /// Returns 0 for modifier keys, function keys, dead keys, and keys that don't
    /// produce printable characters.
    /// </summary>
    private uint ResolveUnicodeChar(ushort vk, ushort scanCode)
    {
        // Skip modifiers, function keys, navigation - they never produce characters.
        if (IsNonTextVk(vk))
            return 0;

        BuildKeyState();

        var fgHwnd = GetForegroundWindow();
        var fgThread = GetWindowThreadProcessId(fgHwnd, out _);
        var hkl = GetKeyboardLayout(fgThread);

        var result = ToUnicodeEx(vk, scanCode, _keyState, _charBuffer, _charBuffer.Length, 0x4, hkl);

        if (result >= 1)
        {
            uint ch = _charBuffer[0];
            // Filter control characters (Ctrl+letter produces ASCII 1-26).
            // Only return printable characters for Unicode injection.
            if (ch >= 0x20)
                return ch;
        }

        // result == -1: dead key (accent waiting for next key) - let scan code handle it
        // result == 0: no translation for this key
        return 0;
    }

    private void BuildKeyState()
    {
        Array.Clear(_keyState, 0, 256);

        if (_lShiftDown) _keyState[0xA0] = 0x80;
        if (_rShiftDown) _keyState[0xA1] = 0x80;
        if (_lShiftDown || _rShiftDown) _keyState[VK_SHIFT] = 0x80;

        if (_lCtrlDown) _keyState[0xA2] = 0x80;
        if (_rCtrlDown) _keyState[0xA3] = 0x80;
        if (_lCtrlDown || _rCtrlDown) _keyState[VK_CONTROL] = 0x80;

        if (_lAltDown) _keyState[0xA4] = 0x80;
        if (_rAltDown) _keyState[0xA5] = 0x80;
        if (_lAltDown || _rAltDown) _keyState[VK_MENU] = 0x80;

        // AltGr = RAlt, but ToUnicodeEx expects LCtrl+RAlt for AltGr chars.
        // The phantom LCtrl (0x21D) is dropped before UpdateModState, so _lCtrlDown
        // won't be set by it. Synthesize LCtrl when RAlt is held.
        if (_rAltDown)
        {
            _keyState[0xA2] = 0x80; // VK_LCONTROL
            _keyState[VK_CONTROL] = 0x80;
        }

        if (_capsLockOn) _keyState[VK_CAPITAL] = 0x01;
        if (_numLockOn) _keyState[VK_NUMLOCK] = 0x01;
    }

    private static bool IsNonTextVk(ushort vk) => vk switch
    {
        // Modifiers
        VK_SHIFT or VK_CONTROL or VK_MENU or VK_LWIN or VK_RWIN => true,
        0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 => true,
        // Function keys
        >= 0x70 and <= 0x87 => true, // F1-F24
        // Navigation / editing
        VK_TAB or VK_ESCAPE or VK_BACK or VK_RETURN => true,
        0x2D or 0x2E or 0x24 or 0x23 or 0x21 or 0x22 => true, // Ins/Del/Home/End/PgUp/PgDn
        0x25 or 0x26 or 0x27 or 0x28 => true, // Arrow keys
        // Lock keys
        VK_CAPITAL or VK_NUMLOCK or 0x91 => true, // Caps/Num/Scroll
        // System keys
        0x2C or 0x13 or 0x03 => true, // PrintScreen/Pause/Cancel
        // Browser/media keys
        >= 0xA6 and <= 0xB7 => true,
        _ => false
    };

    /// <summary>
    /// Check if the foreground thread's keyboard layout changed since last call.
    /// Fires LayoutChanged event with the new KLID string.
    /// Called on every Active mode keystroke (sub-microsecond pointer comparison).
    /// </summary>
    private void DetectLayoutChange()
    {
        var fgHwnd = GetForegroundWindow();
        var fgThread = GetWindowThreadProcessId(fgHwnd, out _);
        var hkl = GetKeyboardLayout(fgThread);

        if (hkl == _lastKnownHkl)
            return;

        _lastKnownHkl = hkl;
        var sb = new StringBuilder(KL_NAMELENGTH);
        if (GetKeyboardLayoutName(sb))
        {
            var klid = sb.ToString();
            if (klid != _lastKnownKlid)
            {
                _lastKnownKlid = klid;
                _logger.LogInformation("[Hook] Keyboard layout changed: {Klid}", klid);
                ThreadPool.QueueUserWorkItem(_ => LayoutChanged?.Invoke(klid));
            }
        }
    }

    private void InitializeToggleState()
    {
        _capsLockOn = (GetKeyState(VK_CAPITAL) & 0x01) != 0;
        _numLockOn = (GetKeyState(VK_NUMLOCK) & 0x01) != 0;
        _logger.LogDebug("[Hook] Toggle state: CapsLock={Caps} NumLock={Num}", _capsLockOn, _numLockOn);
    }

    private void ResetModifierState()
    {
        _lCtrlDown = _rCtrlDown = false;
        _lShiftDown = _rShiftDown = false;
        _lAltDown = _rAltDown = false;
        _lWinDown = _rWinDown = false;
        _capsLockOn = _numLockOn = false;
        _activeUnicodeKeys.Clear();
        _lastKnownHkl = IntPtr.Zero;
        _lastKnownKlid = null;
    }

    private bool IsViewerForeground()
    {
        if (_viewerPid == 0) return false;
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out var fgPid);
        return fgPid == _viewerPid;
    }

    private static string? VkToKeyString(ushort vk) => vk switch
    {
        VK_LWIN or VK_RWIN => "Meta",
        VK_TAB => "Tab",
        VK_ESCAPE => "Escape",
        VK_MENU or 0xA4 or 0xA5 => "Alt",
        _ => null
    };

    #region P/Invoke

    private const int WH_KEYBOARD_LL = 13;
    private const uint LLKHF_ALTDOWN = 0x20;
    private const uint LLKHF_EXTENDED = 0x01;
    private const int KL_NAMELENGTH = 9;

    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;
    private const ushort VK_TAB = 0x09;
    private const ushort VK_BACK = 0x08;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_ESCAPE = 0x1B;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_CAPITAL = 0x14;
    private const ushort VK_NUMLOCK = 0x90;

    private static readonly IntPtr WM_KEYDOWN = (IntPtr)0x0100;
    private static readonly IntPtr WM_KEYUP = (IntPtr)0x0101;
    private static readonly IntPtr WM_SYSKEYDOWN = (IntPtr)0x0104;
    private static readonly IntPtr WM_SYSKEYUP = (IntPtr)0x0105;
    private const uint WM_QUIT = 0x0012;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        [Out, MarshalAs(UnmanagedType.LPArray)] char[] pwszBuff,
        int cchBuff, uint wFlags, IntPtr dwhkl);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardLayoutName([Out] StringBuilder pwszKLID);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    #endregion
}
