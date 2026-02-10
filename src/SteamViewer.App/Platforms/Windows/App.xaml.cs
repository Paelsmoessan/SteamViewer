using Microsoft.UI.Xaml;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SteamViewer.App.WinUI;

public partial class App : MauiWinUIApplication
{
#if DEBUG
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
#endif

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0x00000000;
    private const uint MB_OKCANCEL = 0x00000001;
    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint IDOK = 1;

    public App()
    {
        // Lightweight mode: --sas sends Ctrl+Alt+Del and exits immediately (used by RunOnceEx pre-login)
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--sas"))
        {
            HandleSasMode();
            return;
        }

#if DEBUG
        // Allocate a console window for debug output
        AllocConsole();
        Console.WriteLine("=== SteamViewer.App Debug Console ===");
        Console.WriteLine("Checking dependencies...");
#endif

        // Configure WebView2 to use local user data folder
        // This is critical when running from network shares
        ConfigureWebView2UserDataFolder();

        // Check dependencies before initializing
        if (!CheckDependencies())
        {
            // Exit if critical dependencies are missing and user chose not to continue
            Environment.Exit(1);
        }

        InitializeComponent();
    }

    private void ConfigureWebView2UserDataFolder()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamViewer", "WebView2");

        try
        {
            Directory.CreateDirectory(userDataFolder);
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);
#if DEBUG
            Console.WriteLine($"WebView2 user data folder: {userDataFolder}");
#endif
        }
        catch (Exception ex)
        {
#if DEBUG
            Console.WriteLine($"Warning: Could not create WebView2 user data folder: {ex.Message}");
#endif
        }
    }

    private bool CheckDependencies()
    {
        var dependencies = DependencyChecker.CheckAll();
        var missing = dependencies.Where(d => !d.IsInstalled).ToList();

        if (missing.Count == 0)
        {
#if DEBUG
            Console.WriteLine("All dependencies OK:");
            foreach (var dep in dependencies)
            {
                Console.WriteLine($"  - {dep.Name}: {dep.Version ?? "installed"}");
            }
#endif
            return true;
        }

        // Build error message
        var sb = new StringBuilder();
        sb.AppendLine("The following required components are missing:\n");

        foreach (var dep in missing)
        {
            sb.AppendLine($"  - {dep.Name}");
            sb.AppendLine($"    Download: {dep.DownloadUrl}");
            sb.AppendLine();
        }

        sb.AppendLine("Click OK to open the download page(s), or Cancel to exit.");

#if DEBUG
        Console.WriteLine("Missing dependencies:");
        foreach (var dep in missing)
        {
            Console.WriteLine($"  - {dep.Name}: {dep.DownloadUrl}");
        }
#endif

        // Show message box
        var result = MessageBox(
            IntPtr.Zero,
            sb.ToString(),
            "SteamViewer - Missing Dependencies",
            MB_OKCANCEL | MB_ICONWARNING);

        if (result == IDOK)
        {
            // Open download pages
            foreach (var dep in missing)
            {
                DependencyChecker.OpenDownloadPage(dep.DownloadUrl);
            }
        }

        return false; // Don't continue without dependencies
    }

    /// <summary>
    /// Lightweight SAS mode: sends Ctrl+Alt+Del, waits for user logon, then launches full app as the user.
    /// Runs as SYSTEM via RunOnceEx before the login screen. Never initializes MAUI/WebView2.
    /// </summary>
    private static void HandleSasMode()
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

    /// <summary>
    /// Polls WTS APIs until a user logs in, then launches the full app in their session.
    /// </summary>
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

    /// <summary>
    /// Launches the full app (no --sas flag) in the user's session via CreateProcessAsUser.
    /// </summary>
    private static bool LaunchAppAsUser(IntPtr userToken)
    {
        var appPath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(appPath))
            return false;

        IntPtr dupToken = IntPtr.Zero;
        IntPtr envBlock = IntPtr.Zero;

        try
        {
            // Duplicate the token as a primary token for CreateProcessAsUser
            if (!DuplicateTokenEx(userToken, 0, IntPtr.Zero,
                SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                TOKEN_TYPE.TokenPrimary, out dupToken))
                return false;

            // Create environment block for the user
            if (!CreateEnvironmentBlock(out envBlock, dupToken, false))
            {
                CloseHandle(dupToken);
                return false;
            }

            // Set up startup info
            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(si);
            si.lpDesktop = "winsta0\\default";

            // Launch the app without --sas (normal mode)
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

    #region Win32 Interop — SAS + WTS

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

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
