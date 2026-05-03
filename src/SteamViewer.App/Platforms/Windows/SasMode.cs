using System.Runtime.InteropServices;

namespace SteamViewer.App.WinUI;

/// <summary>
/// Lightweight SAS mode: sends Ctrl+Alt+Del, waits for user logon, then launches full app as the user.
/// Runs as SYSTEM via RunOnceEx before the login screen. Never initializes MAUI/WebView2.
/// Called from Program.Main() BEFORE WinUI3 loads — must not reference any MAUI/WinUI types.
/// </summary>
internal static class SasMode
{
    public static void Run()
    {
        try
        {
            SendSAS(false);

            // Wait for user logon, then launch full app as the logged-in user
            if (WaitForLogonAndLaunchApp(timeout: TimeSpan.FromMinutes(10)))
                Environment.Exit(0);
            else
                Environment.Exit(3); // Timeout — no user logged in
        }
        catch
        {
            Environment.Exit(2);
        }
    }

    private static bool WaitForLogonAndLaunchApp(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(2000);

            var sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == 0xFFFFFFFF)
                continue;

            // Try to get the logged-in user's token
            if (!WTSQueryUserToken(sessionId, out var userToken))
                continue;

            try
            {
                // User is logged in — wait briefly for desktop to settle
                Thread.Sleep(3000);
                return LaunchAppAsUser(userToken);
            }
            finally
            {
                CloseHandle(userToken);
            }
        }

        return false;
    }

    private static bool LaunchAppAsUser(IntPtr userToken)
    {
        var appPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(appPath))
            return false;

        IntPtr dupToken = IntPtr.Zero;
        IntPtr envBlock = IntPtr.Zero;

        try
        {
            if (!DuplicateTokenEx(userToken, 0, IntPtr.Zero,
                SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                TOKEN_TYPE.TokenPrimary, out dupToken))
                return false;

            if (!CreateEnvironmentBlock(out envBlock, dupToken, false))
            {
                CloseHandle(dupToken);
                return false;
            }

            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(si);
            si.lpDesktop = "winsta0\\default";

            var cmdLine = $"\"{appPath}\"";
            var result = CreateProcessAsUser(
                dupToken,
                null,
                cmdLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE,
                envBlock,
                Path.GetDirectoryName(appPath),
                ref si,
                out var pi);

            if (result)
            {
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
            }

            return result;
        }
        finally
        {
            if (envBlock != IntPtr.Zero)
                DestroyEnvironmentBlock(envBlock);
            if (dupToken != IntPtr.Zero)
                CloseHandle(dupToken);
        }
    }

    #region Win32 P/Invoke

    [DllImport("sas.dll", SetLastError = true)]
    private static extern void SendSAS(bool asUser);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken,
        uint dwDesiredAccess,
        IntPtr lpTokenAttributes,
        SECURITY_IMPERSONATION_LEVEL impersonationLevel,
        TOKEN_TYPE tokenType,
        out IntPtr phNewToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken,
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, [MarshalAs(UnmanagedType.Bool)] bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private enum SECURITY_IMPERSONATION_LEVEL { SecurityImpersonation = 2 }
    private enum TOKEN_TYPE { TokenPrimary = 1 }

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
}
