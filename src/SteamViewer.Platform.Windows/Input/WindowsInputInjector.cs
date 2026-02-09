using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SteamViewer.Client.Core.Capture;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Platform.Windows.Input;

/// <summary>
/// Windows input injection using SendInput API.
/// </summary>
public sealed class WindowsInputInjector : IInputInjector
{
    private readonly ILogger<WindowsInputInjector> _logger;
    private bool _disposed;

    // Virtual screen dimensions for coordinate conversion
    private readonly int _virtualScreenLeft;
    private readonly int _virtualScreenTop;
    private readonly int _virtualScreenWidth;
    private readonly int _virtualScreenHeight;

    // Debug log file for cursor diagnostics (in logs/ folder alongside client/server logs)
    private static readonly string DebugLogPath = Path.Combine(
        FindLogsDirectory(),
        $"input-{Environment.MachineName}.log");
    private static readonly object LogLock = new();
    private int _logCount;
    private const int MaxLogEntries = 100; // Limit to avoid huge log files

    public WindowsInputInjector(ILogger<WindowsInputInjector> logger)
    {
        _logger = logger;

        // Get virtual screen dimensions (all monitors combined)
        _virtualScreenLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
        _virtualScreenTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
        _virtualScreenWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        _virtualScreenHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        _logger.LogInformation("Virtual screen: ({Left},{Top}) {Width}x{Height}",
            _virtualScreenLeft, _virtualScreenTop, _virtualScreenWidth, _virtualScreenHeight);

        // Initialize debug log file
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DebugLogPath)!);
            File.WriteAllText(DebugLogPath,
                $"=== SteamViewer Input Debug Log - {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n" +
                $"Virtual Screen: left={_virtualScreenLeft}, top={_virtualScreenTop}, " +
                $"width={_virtualScreenWidth}, height={_virtualScreenHeight}\n" +
                $"Log file: {DebugLogPath}\n\n");
            _logger.LogInformation("Debug log file created at: {Path}", DebugLogPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create debug log file at {Path}", DebugLogPath);
        }
    }

    public bool IsAvailable => !_disposed;

    /// <summary>Whether the current process is running elevated (admin).</summary>
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public bool SendSecureAttentionSequence()
    {
        var helperPath = Path.Combine(AppContext.BaseDirectory, "SteamViewer.SasHelper.exe");
        if (!File.Exists(helperPath))
        {
            _logger.LogWarning("SAS helper not found at {Path}", helperPath);
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = helperPath,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                _logger.LogWarning("Failed to start SAS helper");
                return false;
            }

            process.WaitForExit(5000);
            var success = process.ExitCode == 0;
            _logger.LogInformation("SAS helper exited with code {Code}", process.ExitCode);
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch SAS helper");
            return false;
        }
    }

    public bool RebootWithAutoRestart()
    {
        try
        {
            // If elevated, register auto-restart via registry
            if (IsElevated)
            {
                var sasHelperPath = Path.Combine(AppContext.BaseDirectory, "SteamViewer.SasHelper.exe");
                var appPath = Process.GetCurrentProcess().MainModule?.FileName;

                // RunOnceEx — SAS helper runs pre-login to show Ctrl+Alt+Del screen
                if (File.Exists(sasHelperPath))
                {
                    try
                    {
                        using var runOnceExKey = Registry.LocalMachine.CreateSubKey(
                            @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnceEx\SteamViewer");
                        runOnceExKey?.SetValue("", sasHelperPath);
                        _logger.LogInformation("Registered SAS helper in RunOnceEx");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to write RunOnceEx key (SAS helper won't run pre-login)");
                    }
                }

                // RunOnce — main app restarts after login
                if (!string.IsNullOrEmpty(appPath))
                {
                    try
                    {
                        using var runOnceKey = Registry.LocalMachine.OpenSubKey(
                            @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", writable: true);
                        runOnceKey?.SetValue("SteamViewerRestart", appPath);
                        _logger.LogInformation("Registered app in RunOnce for auto-restart");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to write RunOnce key (app won't auto-restart)");
                    }
                }
            }
            else
            {
                _logger.LogInformation("Not elevated — rebooting without auto-restart");
            }

            // Reboot (works for standard users)
            _logger.LogInformation("Initiating system reboot");
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown",
                Arguments = "/r /t 0",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initiate reboot");
            return false;
        }
    }

    public void InjectInput(InputEvent inputEvent, int screenWidth, int screenHeight)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsInputInjector));
        }

        switch (inputEvent)
        {
            case InputEvent.MouseMove move:
                InjectMouseMove(move, screenWidth, screenHeight);
                break;
            case InputEvent.MouseDown down:
                InjectMouseButton(down.Button, down.X, down.Y, screenWidth, screenHeight, isDown: true);
                break;
            case InputEvent.MouseUp up:
                InjectMouseButton(up.Button, up.X, up.Y, screenWidth, screenHeight, isDown: false);
                break;
            case InputEvent.MouseWheel wheel:
                InjectMouseWheel(wheel);
                break;
            case InputEvent.KeyDown keyDown:
                InjectKey(keyDown.Key, keyDown.Modifiers, isDown: true);
                break;
            case InputEvent.KeyUp keyUp:
                InjectKey(keyUp.Key, keyUp.Modifiers, isDown: false);
                break;
            default:
                _logger.LogWarning("Unknown input event type: {Type}", inputEvent.GetType().Name);
                break;
        }
    }

    private void InjectMouseMove(InputEvent.MouseMove move, int screenWidth, int screenHeight)
    {
        // Convert coordinates from remote screen to absolute screen coordinates
        var (absX, absY) = ConvertToAbsoluteCoordinates(move.X, move.Y, screenWidth, screenHeight);

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

    private void InjectMouseButton(MouseButton button, double x, double y, int screenWidth, int screenHeight, bool isDown)
    {
        var (absX, absY) = ConvertToAbsoluteCoordinates(x, y, screenWidth, screenHeight);

        uint flags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
        uint mouseData = 0;

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
                    mouseData = mouseData,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private void InjectMouseWheel(InputEvent.MouseWheel wheel)
    {
        var inputs = new List<INPUT>();

        // Vertical scroll
        if (Math.Abs(wheel.DeltaY) > 0.001)
        {
            var wheelDelta = (int)(-wheel.DeltaY * WHEEL_DELTA / 100.0);
            inputs.Add(new INPUT
            {
                type = INPUT_MOUSE,
                union = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dx = 0,
                        dy = 0,
                        mouseData = (uint)wheelDelta,
                        dwFlags = MOUSEEVENTF_WHEEL,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            });
        }

        // Horizontal scroll
        if (Math.Abs(wheel.DeltaX) > 0.001)
        {
            var wheelDelta = (int)(wheel.DeltaX * WHEEL_DELTA / 100.0);
            inputs.Add(new INPUT
            {
                type = INPUT_MOUSE,
                union = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dx = 0,
                        dy = 0,
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

    private void InjectKey(string key, KeyModifiers modifiers, bool isDown)
    {
        var inputs = new List<INPUT>();

        // Handle modifier keys
        if (isDown)
        {
            if (modifiers.Ctrl) AddKeyInput(inputs, VK_CONTROL, isDown: true);
            if (modifiers.Alt) AddKeyInput(inputs, VK_MENU, isDown: true);
            if (modifiers.Shift) AddKeyInput(inputs, VK_SHIFT, isDown: true);
            if (modifiers.Meta) AddKeyInput(inputs, VK_LWIN, isDown: true);
        }

        // Handle the main key
        var vk = KeyToVirtualKey(key);
        if (vk != 0)
        {
            AddKeyInput(inputs, vk, isDown);
        }
        else
        {
            _logger.LogWarning("Unknown key: {Key}", key);
        }

        // Release modifiers in reverse order
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

    private (int AbsX, int AbsY) ConvertToAbsoluteCoordinates(double x, double y, int screenWidth, int screenHeight)
    {
        // Scale coordinates from remote screen (capture size) to local screen (virtual screen)
        // x, y are in capture pixel space (0 to screenWidth/screenHeight)
        // We need to map them to the local virtual screen coordinates
        var localX = x * _virtualScreenWidth / screenWidth + _virtualScreenLeft;
        var localY = y * _virtualScreenHeight / screenHeight + _virtualScreenTop;

        // Convert to absolute coordinates (0-65535 range)
        var absX = (int)((localX - _virtualScreenLeft) * 65535 / _virtualScreenWidth);
        var absY = (int)((localY - _virtualScreenTop) * 65535 / _virtualScreenHeight);

        // Debug logging to file
        if (_logCount < MaxLogEntries)
        {
            try
            {
                lock (LogLock)
                {
                    if (_logCount < MaxLogEntries)
                    {
                        var logLine = $"[{DateTime.Now:HH:mm:ss.fff}] " +
                            $"INPUT: x={x:F1}, y={y:F1}, captureSize={screenWidth}x{screenHeight} | " +
                            $"VIRTUAL: {_virtualScreenWidth}x{_virtualScreenHeight} | " +
                            $"LOCAL: x={localX:F1}, y={localY:F1} | " +
                            $"ABS: x={absX}, y={absY} (0-65535 range)\n";
                        File.AppendAllText(DebugLogPath, logLine);
                        _logCount++;

                        if (_logCount == MaxLogEntries)
                        {
                            File.AppendAllText(DebugLogPath,
                                $"\n=== Max log entries ({MaxLogEntries}) reached, logging stopped ===\n");
                        }
                    }
                }
            }
            catch
            {
                // Ignore logging errors to not disrupt input
            }
        }

        return (Math.Clamp(absX, 0, 65535), Math.Clamp(absY, 0, 65535));
    }

    private static ushort KeyToVirtualKey(string key)
    {
        // Map JavaScript key names to Windows virtual key codes
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

            // Modifiers (in case they come as regular keys)
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

    /// <summary>
    /// Finds the logs directory by walking up from base directory looking for solution root markers.
    /// Same algorithm as SharedFileLogger.FindLogsDirectory().
    /// </summary>
    private static string FindLogsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0 || dir.GetFiles("CLAUDE.md").Length > 0)
            {
                return Path.Combine(dir.FullName, "logs");
            }
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "logs");
    }

    public void Dispose()
    {
        _disposed = true;
    }

    #region Win32 Interop

    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const int WHEEL_DELTA = 120;

    // System metrics
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    // Virtual key codes
    private const ushort VK_BACK = 0x08;
    private const ushort VK_TAB = 0x09;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12; // Alt
    private const ushort VK_CAPITAL = 0x14;
    private const ushort VK_ESCAPE = 0x1B;
    private const ushort VK_SPACE = 0x20;
    private const ushort VK_PRIOR = 0x21; // Page Up
    private const ushort VK_NEXT = 0x22;  // Page Down
    private const ushort VK_END = 0x23;
    private const ushort VK_HOME = 0x24;
    private const ushort VK_LEFT = 0x25;
    private const ushort VK_UP = 0x26;
    private const ushort VK_RIGHT = 0x27;
    private const ushort VK_DOWN = 0x28;
    private const ushort VK_INSERT = 0x2D;
    private const ushort VK_DELETE = 0x2E;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_NUMLOCK = 0x90;
    private const ushort VK_SCROLL = 0x91;
    private const ushort VK_F1 = 0x70;
    private const ushort VK_F2 = 0x71;
    private const ushort VK_F3 = 0x72;
    private const ushort VK_F4 = 0x73;
    private const ushort VK_F5 = 0x74;
    private const ushort VK_F6 = 0x75;
    private const ushort VK_F7 = 0x76;
    private const ushort VK_F8 = 0x77;
    private const ushort VK_F9 = 0x78;
    private const ushort VK_F10 = 0x79;
    private const ushort VK_F11 = 0x7A;
    private const ushort VK_F12 = 0x7B;
    private const ushort VK_OEM_1 = 0xBA;      // ;:
    private const ushort VK_OEM_PLUS = 0xBB;   // =+
    private const ushort VK_OEM_COMMA = 0xBC;  // ,<
    private const ushort VK_OEM_MINUS = 0xBD;  // -_
    private const ushort VK_OEM_PERIOD = 0xBE; // .>
    private const ushort VK_OEM_2 = 0xBF;      // /?
    private const ushort VK_OEM_3 = 0xC0;      // `~
    private const ushort VK_OEM_4 = 0xDB;      // [{
    private const ushort VK_OEM_5 = 0xDC;      // \|
    private const ushort VK_OEM_6 = 0xDD;      // ]}
    private const ushort VK_OEM_7 = 0xDE;      // '"

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    #endregion
}
