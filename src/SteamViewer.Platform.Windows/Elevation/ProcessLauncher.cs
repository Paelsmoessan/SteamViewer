using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SteamViewer.Platform.Windows.Elevation;

/// <summary>
/// Launches processes with elevated token manipulation.
/// Two modes:
/// 1. LaunchInUserSession — called from SYSTEM process, launches SYSTEM in user's desktop session
/// 2. LaunchAsSystemFromAdmin — called from admin process, steals SYSTEM token from winlogon.exe
/// </summary>
internal static class ProcessLauncher
{
    /// <summary>
    /// Launch a process in the active user's session, running as SYSTEM.
    /// The process will appear in the user's taskbar and desktop.
    /// Must be called from a SYSTEM-level process.
    /// </summary>
    public static bool LaunchInUserSession(string path, string? args, out int processId)
    {
        processId = 0;
        IntPtr currentToken = IntPtr.Zero;
        IntPtr dupToken = IntPtr.Zero;
        IntPtr envBlock = IntPtr.Zero;

        try
        {
            var sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == 0xFFFFFFFF)
                return false;

            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY, out currentToken))
                return false;

            if (!DuplicateTokenEx(currentToken, MAXIMUM_ALLOWED, IntPtr.Zero,
                SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                TOKEN_TYPE.TokenPrimary, out dupToken))
                return false;

            if (!SetTokenInformation(dupToken, TOKEN_INFORMATION_CLASS.TokenSessionId,
                ref sessionId, sizeof(uint)))
                return false;

            if (!CreateEnvironmentBlock(out envBlock, dupToken, false))
                return false;

            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(si);
            si.lpDesktop = "winsta0\\default";

            var cmdLine = string.IsNullOrEmpty(args)
                ? $"\"{path}\""
                : $"\"{path}\" {args}";

            var result = CreateProcessAsUser(
                dupToken, null, cmdLine,
                IntPtr.Zero, IntPtr.Zero, false,
                CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE,
                envBlock, Path.GetDirectoryName(path),
                ref si, out var pi);

            if (result)
            {
                processId = pi.dwProcessId;
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
            }

            return result;
        }
        finally
        {
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
            if (dupToken != IntPtr.Zero) CloseHandle(dupToken);
            if (currentToken != IntPtr.Zero) CloseHandle(currentToken);
        }
    }

    /// <summary>
    /// Launch a process as SYSTEM in the current session.
    /// Called from an admin-elevated process. Gets SYSTEM token from winlogon.exe.
    /// Uses the PsExec -s -i technique: OpenProcess(winlogon) → DuplicateToken → CreateProcessWithTokenW.
    /// </summary>
    /// <param name="path">Full path to the executable.</param>
    /// <param name="args">Optional command-line arguments.</param>
    /// <param name="processId">PID of the launched process (0 on failure).</param>
    /// <param name="error">Error details if the operation failed.</param>
    /// <returns>True if the process was launched successfully.</returns>
    public static bool LaunchAsSystemFromAdmin(string path, string? args, out int processId, out string? error)
    {
        processId = 0;
        error = null;
        IntPtr processHandle = IntPtr.Zero;
        IntPtr tokenHandle = IntPtr.Zero;
        IntPtr dupToken = IntPtr.Zero;
        IntPtr envBlock = IntPtr.Zero;

        try
        {
            // 1. Enable SeDebugPrivilege (admin has it but it's disabled by default)
            if (!EnableDebugPrivilege())
            {
                error = $"Failed to enable SeDebugPrivilege (Win32: {Marshal.GetLastWin32Error()})";
                return false;
            }

            // 2. Find winlogon.exe in our session (not WTSGetActiveConsoleSessionId — that's wrong for RDP)
            var sessionId = Process.GetCurrentProcess().SessionId;
            var winlogon = FindWinlogonInSession(sessionId);
            if (winlogon == null)
            {
                error = $"No winlogon.exe found in session {sessionId}";
                return false;
            }

            // 3. Open winlogon's process to access its SYSTEM token
            processHandle = OpenProcess(PROCESS_QUERY_INFORMATION, false, winlogon.Id);
            if (processHandle == IntPtr.Zero)
            {
                error = $"OpenProcess(winlogon PID {winlogon.Id}) failed (Win32: {Marshal.GetLastWin32Error()})";
                return false;
            }

            // 4. Get winlogon's token
            if (!OpenProcessToken(processHandle, TOKEN_DUPLICATE, out tokenHandle))
            {
                error = $"OpenProcessToken(winlogon) failed (Win32: {Marshal.GetLastWin32Error()})";
                return false;
            }

            // 5. Duplicate as primary token (needed for CreateProcessWithTokenW)
            if (!DuplicateTokenEx(tokenHandle, MAXIMUM_ALLOWED, IntPtr.Zero,
                SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                TOKEN_TYPE.TokenPrimary, out dupToken))
            {
                error = $"DuplicateTokenEx failed (Win32: {Marshal.GetLastWin32Error()})";
                return false;
            }

            // 6. Create environment block for the SYSTEM token
            if (!CreateEnvironmentBlock(out envBlock, dupToken, false))
            {
                error = $"CreateEnvironmentBlock failed (Win32: {Marshal.GetLastWin32Error()})";
                return false;
            }

            // 7. Launch the process on the user's interactive desktop
            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(si);
            si.lpDesktop = "winsta0\\default";

            var cmdLine = string.IsNullOrEmpty(args)
                ? $"\"{path}\""
                : $"\"{path}\" {args}";

            var result = CreateProcessWithTokenW(
                dupToken,
                0, // No logon flags
                null,
                cmdLine,
                CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE,
                envBlock,
                Path.GetDirectoryName(path),
                ref si,
                out var pi);

            if (result)
            {
                processId = pi.dwProcessId;
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
            }
            else
            {
                error = $"CreateProcessWithTokenW failed (Win32: {Marshal.GetLastWin32Error()})";
            }

            return result;
        }
        catch (Exception ex)
        {
            error = $"Exception: {ex.Message}";
            return false;
        }
        finally
        {
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
            if (dupToken != IntPtr.Zero) CloseHandle(dupToken);
            if (tokenHandle != IntPtr.Zero) CloseHandle(tokenHandle);
            if (processHandle != IntPtr.Zero) CloseHandle(processHandle);
        }
    }

    /// <summary>
    /// Find winlogon.exe running in the specified session.
    /// </summary>
    private static Process? FindWinlogonInSession(int sessionId)
    {
        try
        {
            var processes = Process.GetProcessesByName("winlogon");
            foreach (var p in processes)
            {
                if (p.SessionId == sessionId)
                    return p;
                p.Dispose();
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Enable SeDebugPrivilege on the current process token.
    /// Required to open winlogon.exe's process handle, and to terminate SYSTEM-level orphan helpers
    /// during startup reap (HelperRegistry.ReapOrphans). Admins hold it but it's disabled by default.
    /// </summary>
    internal static bool EnableDebugPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            return false;

        try
        {
            if (!LookupPrivilegeValue(null, SE_DEBUG_NAME, out var luid))
                return false;

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES
                {
                    Luid = luid,
                    Attributes = SE_PRIVILEGE_ENABLED
                }
            };

            return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)
                   && Marshal.GetLastWin32Error() == 0; // ERROR_SUCCESS — not ERROR_NOT_ALL_ASSIGNED
        }
        finally
        {
            CloseHandle(token);
        }
    }

    #region Win32 P/Invoke

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes,
        SECURITY_IMPERSONATION_LEVEL impersonationLevel, TOKEN_TYPE tokenType,
        out IntPtr phNewToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetTokenInformation(
        IntPtr tokenHandle, TOKEN_INFORMATION_CLASS tokenInformationClass,
        ref uint tokenInformation, int tokenInformationLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken, string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessWithTokenW(
        IntPtr hToken, uint dwLogonFlags, string? lpApplicationName, string lpCommandLine,
        uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, [MarshalAs(UnmanagedType.Bool)] bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    // Constants
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    private const string SE_DEBUG_NAME = "SeDebugPrivilege";

    // Enums
    private enum SECURITY_IMPERSONATION_LEVEL { SecurityImpersonation = 2 }
    private enum TOKEN_TYPE { TokenPrimary = 1 }
    private enum TOKEN_INFORMATION_CLASS { TokenSessionId = 12 }

    // Structs
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

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;
    }

    #endregion
}
