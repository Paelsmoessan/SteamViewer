using System.Runtime.InteropServices;

namespace SteamViewer.Platform.Windows.Input;

// Win32 P/Invoke layer for Win32Input. All consts, DllImports, structs, and
// delegates used by the input injection pipeline live here. Keeps the entry-
// point and per-cluster partials free of interop noise.
public static partial class Win32Input
{
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

    internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal const uint KEYEVENTF_UNICODE = 0x0004;
    internal const uint KEYEVENTF_SCANCODE = 0x0008;

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
    internal const ushort VK_OEM_102 = 0xE2; // IntlBackslash (key between left Shift and Z on ISO keyboards)

    private const uint MAPVK_VK_TO_VSC = 0;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszBuff,
        int cchBuff, uint wFlags, IntPtr dwhkl);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    private const int MONITORINFOF_PRIMARY = 1;

    private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW lpmi);

    private const uint KLF_ACTIVATE = 0x00000001;
    private const uint KLF_SUBSTITUTE_OK = 0x00000002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint Flags);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "ActivateKeyboardLayout")]
    private static extern IntPtr ActivateKeyboardLayoutApi(IntPtr hkl, uint Flags);

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
}
