using System.Runtime.InteropServices;

namespace SteamViewer.Platform.Windows.Elevation;

/// <summary>
/// Launches a process as SYSTEM but visible in the active user's desktop session.
/// Native implementation of the ServiceUI.exe technique:
/// DuplicateTokenEx (SYSTEM token) + SetTokenInformation (user's session) + CreateProcessAsUser.
/// Must be called from a SYSTEM-level process.
/// </summary>
internal static class ProcessLauncher
{
    /// <summary>
    /// Launch a process in the active user's session, running as SYSTEM.
    /// The process will appear in the user's taskbar and desktop.
    /// </summary>
    /// <param name="path">Full path to the executable.</param>
    /// <param name="args">Optional command-line arguments.</param>
    /// <param name="processId">PID of the launched process (0 on failure).</param>
    /// <returns>True if the process was launched successfully.</returns>
    public static bool LaunchInUserSession(string path, string? args, out int processId)
    {
        processId = 0;
        IntPtr currentToken = IntPtr.Zero;
        IntPtr dupToken = IntPtr.Zero;
        IntPtr envBlock = IntPtr.Zero;

        try
        {
            // 1. Get the active console session (the user sitting at the keyboard)
            var sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == 0xFFFFFFFF)
                return false; // No active console session

            // 2. Get our current process token (we're running as SYSTEM)
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY, out currentToken))
                return false;

            // 3. Duplicate as a primary token (needed for CreateProcessAsUser)
            if (!DuplicateTokenEx(currentToken, MAXIMUM_ALLOWED, IntPtr.Zero,
                SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                TOKEN_TYPE.TokenPrimary, out dupToken))
                return false;

            // 4. Set the session ID on the duplicated token to the user's session
            //    This is the key step — only SYSTEM can call SetTokenInformation with TokenSessionId
            if (!SetTokenInformation(dupToken, TOKEN_INFORMATION_CLASS.TokenSessionId,
                ref sessionId, sizeof(uint)))
                return false;

            // 5. Create environment variables for the session
            if (!CreateEnvironmentBlock(out envBlock, dupToken, false))
                return false;

            // 6. Launch the process in the user's desktop
            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(si);
            si.lpDesktop = "winsta0\\default"; // User's visible desktop

            var cmdLine = string.IsNullOrEmpty(args)
                ? $"\"{path}\""
                : $"\"{path}\" {args}";

            var result = CreateProcessAsUser(
                dupToken,
                null,
                cmdLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
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

            return result;
        }
        finally
        {
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
            if (dupToken != IntPtr.Zero) CloseHandle(dupToken);
            if (currentToken != IntPtr.Zero) CloseHandle(currentToken);
        }
    }

    #region Win32 P/Invoke

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

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

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, [MarshalAs(UnmanagedType.Bool)] bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;

    private enum SECURITY_IMPERSONATION_LEVEL { SecurityImpersonation = 2 }
    private enum TOKEN_TYPE { TokenPrimary = 1 }
    private enum TOKEN_INFORMATION_CLASS { TokenSessionId = 12 }

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
}
