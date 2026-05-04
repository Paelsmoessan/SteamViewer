using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SteamViewer.Client.Core.Capture;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Platform.Windows.Input;

public enum CaptureMode { Disabled, LogOnly, Active }

/// <summary>
/// Low-level keyboard hook (WH_KEYBOARD_LL) that intercepts system keys
/// like Win, Alt+Tab, Ctrl+Esc when viewer input is locked. When CaptureMode
/// is LogOnly or Active, processes ALL keystrokes for the native keyboard
/// capture pipeline (Sunshine pattern).
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

    // Modifier bitfield maintained synchronously in the hook callback.
    // Avoids GetAsyncKeyState races with rapid keystroke sequences.
    private byte _modBits;
    private const byte MOD_CTRL = 0x01;
    private const byte MOD_SHIFT = 0x02;
    private const byte MOD_ALT = 0x04;
    private const byte MOD_META = 0x08;

    public bool IsInstalled => _hookId != IntPtr.Zero;
    public event Action<string, bool, bool>? SystemKeyIntercepted;
    public event Action<ushort, ushort, bool, bool, KeyModifiers>? KeyEventCaptured;

    public bool FullCapture
    {
        get => _captureMode != CaptureMode.Disabled;
        set
        {
            if (!value)
            {
                _captureMode = CaptureMode.Disabled;
                _modBits = 0;
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
                _modBits = 0;
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
        _modBits = 0;

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

            // Update modifier bitfield synchronously (race-free)
            UpdateModBits(vk, isDown);

            var mode = _captureMode;
            var isForeground = IsViewerForeground();

            // --- Capture modes (LogOnly / Active) ---
            if (mode != CaptureMode.Disabled && isForeground && (isDown || isUp))
            {
                if (mode == CaptureMode.LogOnly)
                {
                    _logger.LogDebug("[Hook-Capture] vk=0x{Vk:X2} sc=0x{Sc:X3} ext={Ext} {State}",
                        vk, scanCode, isExtended, isDown ? "DOWN" : "UP");
                    // LogOnly: always CallNextHookEx, never suppress, never fire event
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
                    var capturedSc = scanCode;
                    var capturedVk = vk;
                    var capturedDown = isDown;
                    var capturedExt = isExtended;

                    ThreadPool.QueueUserWorkItem(_ =>
                        KeyEventCaptured?.Invoke(capturedSc, capturedVk, capturedDown, capturedExt, modSnapshot));

                    return (IntPtr)1; // Suppress - don't deliver to WebView2
                }
            }

            // --- Legacy system key interception (Win, Alt+Tab, Ctrl+Esc) ---
            var altDown = (kb.flags & LLKHF_ALTDOWN) != 0;
            var ctrlDown = (_modBits & MOD_CTRL) != 0;

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

    private void UpdateModBits(ushort vk, bool isDown)
    {
        byte bit = vk switch
        {
            VK_CONTROL or 0xA2 or 0xA3 => MOD_CTRL,
            VK_SHIFT or 0xA0 or 0xA1 => MOD_SHIFT,
            VK_MENU or 0xA4 or 0xA5 => MOD_ALT,
            VK_LWIN or VK_RWIN => MOD_META,
            _ => 0
        };

        if (bit == 0) return;

        if (isDown)
            _modBits |= bit;
        else
            _modBits &= (byte)~bit;
    }

    private KeyModifiers SnapshotModifiers()
    {
        var bits = _modBits;
        return new KeyModifiers(
            Ctrl: (bits & MOD_CTRL) != 0,
            Shift: (bits & MOD_SHIFT) != 0,
            Alt: (bits & MOD_ALT) != 0,
            Meta: (bits & MOD_META) != 0
        );
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

    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;
    private const ushort VK_TAB = 0x09;
    private const ushort VK_ESCAPE = 0x1B;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_MENU = 0x12;

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

    #endregion
}
