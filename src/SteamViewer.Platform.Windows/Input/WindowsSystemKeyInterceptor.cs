using System.Runtime.InteropServices;
using SteamViewer.Client.Core.Capture;

namespace SteamViewer.Platform.Windows.Input;

/// <summary>
/// Low-level keyboard hook (WH_KEYBOARD_LL) that intercepts system keys
/// like Win, Alt+Tab, Ctrl+Esc when viewer input is locked.
/// </summary>
public sealed class WindowsSystemKeyInterceptor : ISystemKeyInterceptor
{
    private IntPtr _hookId;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private volatile bool _running;
    private HookProc? _hookProc; // prevent GC collection of delegate
    private volatile bool _firstCallback = true;

    public bool IsInstalled => _hookId != IntPtr.Zero;
    public event Action<string, bool, bool>? SystemKeyIntercepted;

    public void Install()
    {
        if (_hookId != IntPtr.Zero) return;

        _running = true;
        var ready = new ManualResetEventSlim(false);

        _hookThread = new Thread(() =>
        {
            _hookThreadId = GetCurrentThreadId();
            _hookProc = HookCallback;

            // GetModuleHandle(null) returns the main EXE handle — works reliably in .NET 8 MAUI.
            // The previous approach (Process.MainModule.ModuleName) can fail in .NET Core hosts.
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc,
                GetModuleHandle(null), 0);

            if (_hookId == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                Console.WriteLine($"[SystemKeyHook] SetWindowsHookEx FAILED — Win32 error {error}");
                ready.Set();
                return;
            }

            Console.WriteLine("[SystemKeyHook] Installed successfully");
            ready.Set();

            // Message pump required for WH_KEYBOARD_LL
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

        Console.WriteLine("[SystemKeyHook] Uninstalling...");
        _running = false;

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

            if (_firstCallback)
            {
                Console.WriteLine($"[SystemKeyHook] First callback — vk=0x{vk:X2}, wParam=0x{wParam:X}");
                _firstCallback = false;
            }
            var isDown = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
            var isUp = wParam == WM_KEYUP || wParam == WM_SYSKEYUP;
            var altDown = (kb.flags & LLKHF_ALTDOWN) != 0;
            var ctrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

            // Determine if this key should be intercepted
            bool shouldIntercept = vk switch
            {
                VK_LWIN or VK_RWIN => true,
                VK_TAB when altDown => true,    // Alt+Tab
                VK_ESCAPE when ctrlDown => true, // Ctrl+Esc (Start menu)
                _ => false
            };

            if (shouldIntercept && (isDown || isUp))
            {
                var key = VkToKeyString(vk);
                if (key != null)
                {
                    Console.WriteLine($"[SystemKeyHook] Intercepted: {key} {(isDown ? "down" : "up")}");
                    // Dispatch off hook thread immediately — LL hooks have a ~200ms Windows timeout.
                    // Blocking here (sync event → async SendInput → thread marshal) causes 3s+ delays.
                    var capturedKey = key;
                    var capturedDown = isDown;
                    var capturedAlt = altDown;
                    ThreadPool.QueueUserWorkItem(_ => SystemKeyIntercepted?.Invoke(capturedKey, capturedDown, capturedAlt));
                    return (IntPtr)1; // Suppress the key
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static string? VkToKeyString(ushort vk) => vk switch
    {
        VK_LWIN or VK_RWIN => "Meta",
        VK_TAB => "Tab",
        VK_ESCAPE => "Escape",
        VK_MENU or VK_LMENU or VK_RMENU => "Alt",
        _ => null
    };

    #region P/Invoke

    private const int WH_KEYBOARD_LL = 13;
    private const uint LLKHF_ALTDOWN = 0x20;

    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;
    private const ushort VK_TAB = 0x09;
    private const ushort VK_ESCAPE = 0x1B;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_LMENU = 0xA4;
    private const ushort VK_RMENU = 0xA5;

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
    private static extern short GetAsyncKeyState(int vKey);

    #endregion
}
