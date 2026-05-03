using System.Runtime.InteropServices;
using SteamViewer.Client.Core.Session;

namespace SteamViewer.Platform.Windows.Elevation;

/// <summary>
/// Win32 helpers for boot relay mode: WinSta0 attachment, user logon detection,
/// process launching. Video/input/transport is handled by HostSession via BootRelayOrchestrator.
/// </summary>
public static class BootRelayService
{
    private static string? _debugPath;
    private static string? _debugPathLocal;

    #region P/Invoke

    private const uint WINSTA_ALL_ACCESS = 0x37F;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenWindowStation(string lpszWinSta, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessWindowStation(IntPtr hWinSta);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess,
        IntPtr lpTokenAttributes, int impersonationLevel, int tokenType, out IntPtr phNewToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(IntPtr hToken, string? lpApplicationName,
        string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken,
        [MarshalAs(UnmanagedType.Bool)] bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    #endregion

    #region Debug Logging

    public static void InitDebugLog()
    {
        _debugPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SteamViewer", "boot-relay-debug.txt");
        try { Directory.CreateDirectory(Path.GetDirectoryName(_debugPath)!); } catch { }

        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (exeDir != null)
        {
            _debugPathLocal = Path.Combine(exeDir, "logs", "boot-relay-debug.txt");
            try { Directory.CreateDirectory(Path.GetDirectoryName(_debugPathLocal)!); } catch { }
        }
    }

    public static void DebugLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [BootRelay] {message}";
        Console.WriteLine(line);
        try { if (_debugPath != null) File.AppendAllText(_debugPath, line + "\n"); } catch { }
        try { if (_debugPathLocal != null) File.AppendAllText(_debugPathLocal, line + "\n"); } catch { }
    }

    #endregion

    #region WinSta0 Attachment

    /// <summary>Attach process to the interactive window station (required for input injection).</summary>
    public static void AttachWinSta0()
    {
        InitDebugLog();

        var hWinSta = OpenWindowStation("WinSta0", false, WINSTA_ALL_ACCESS);
        if (hWinSta != IntPtr.Zero)
        {
            if (SetProcessWindowStation(hWinSta))
                DebugLog("Attached to WinSta0");
            else
                DebugLog($"SetProcessWindowStation failed (error {Marshal.GetLastWin32Error()})");
        }
        else
        {
            DebugLog($"OpenWindowStation('WinSta0') failed (error {Marshal.GetLastWin32Error()})");
        }
    }

    #endregion

    #region User Logon Monitoring

    /// <summary>
    /// Monitors for user logon. When detected, launches the main app as the user.
    /// Call on a background thread. Invokes onStopping when boot relay should exit.
    /// </summary>
    public static void MonitorUserLogon(
        ReconnectCredentials.ReconnectResult creds, CancellationToken ct, Action onStopping)
    {
        DebugLog("Logon monitor started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                Thread.Sleep(2000);

                var sessionId = WTSGetActiveConsoleSessionId();
                if (sessionId == 0xFFFFFFFF)
                    continue;

                if (!WTSQueryUserToken(sessionId, out var userToken))
                    continue;

                try
                {
                    DebugLog($"User logged in (session {sessionId}). Waiting for desktop to settle...");
                    Thread.Sleep(5000);

                    var appPath = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(appPath))
                    {
                        DebugLog($"Launching main app via ProcessLauncher: {appPath}");
                        if (ProcessLauncher.LaunchInUserSession(appPath, null, out var pid))
                        {
                            DebugLog($"Main app launched in user session: PID {pid}");
                            Thread.Sleep(30_000);
                        }
                        else
                        {
                            DebugLog($"ProcessLauncher failed (error {Marshal.GetLastWin32Error()})");
                            if (LaunchAppAsUser(userToken, appPath))
                                DebugLog("Fallback: launched main app as user");
                            else
                                DebugLog("Failed to launch main app via any method");
                        }
                    }

                    onStopping();
                    return;
                }
                finally
                {
                    CloseHandle(userToken);
                }
            }
            catch (Exception ex)
            {
                DebugLog($"Logon monitor error: {ex.Message}");
            }
        }

        DebugLog("Logon monitor stopped");
    }

    #endregion

    #region LaunchAppAsUser

    private static bool LaunchAppAsUser(IntPtr userToken, string appPath)
    {
        IntPtr dupToken = IntPtr.Zero;
        IntPtr envBlock = IntPtr.Zero;

        try
        {
            if (!DuplicateTokenEx(userToken, 0, IntPtr.Zero, 2, 1, out dupToken))
            {
                DebugLog($"DuplicateTokenEx failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            if (!CreateEnvironmentBlock(out envBlock, dupToken, false))
            {
                DebugLog($"CreateEnvironmentBlock failed: {Marshal.GetLastWin32Error()}");
                CloseHandle(dupToken);
                return false;
            }

            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(si);
            si.lpDesktop = "winsta0\\default";

            var cmdLine = $"\"{appPath}\"";
            var result = CreateProcessAsUser(dupToken, null, cmdLine,
                IntPtr.Zero, IntPtr.Zero, false,
                CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE,
                envBlock, Path.GetDirectoryName(appPath),
                ref si, out var pi);

            if (result)
            {
                DebugLog($"Main app launched as user: PID {pi.dwProcessId}");
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
            }
            else
            {
                DebugLog($"CreateProcessAsUser failed: {Marshal.GetLastWin32Error()}");
            }

            return result;
        }
        finally
        {
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
            if (dupToken != IntPtr.Zero) CloseHandle(dupToken);
        }
    }

    #endregion
}
