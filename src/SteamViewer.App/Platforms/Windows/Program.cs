using System.Runtime.InteropServices;

namespace SteamViewer.App.WinUI;

/// <summary>
/// Custom entry point that intercepts lightweight modes (--sas, --elevated-helper)
/// BEFORE WinUI3/MAUI loads. This prevents Microsoft.InputStateManager.dll from
/// loading in the elevated helper process, which would crash with STATUS_STACK_BUFFER_OVERRUN.
/// </summary>
public static class Program
{
    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [STAThread]
    static void Main(string[] args)
    {
        // Process-level Per-Monitor V2 DPI awareness — set ONCE before anything else.
        // All modes (main app, elevated helper, system helper, boot relay) get physical pixel
        // coordinates on all threads. Replaces fragile per-thread DPI switching.
        // Source: Sunshine (display_base.cpp)
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        // === Intercept lightweight modes BEFORE WinUI3 loads ===
        // At this point NO WinUI3/MAUI DLLs have been loaded.
        // The elevated helper and SAS mode are pure Win32/P/Invoke — no UI needed.

        // --sas: sends Ctrl+Alt+Del, waits for logon, launches app as user (RunOnceEx pre-login)
        if (args.Contains("--sas"))
        {
            SasMode.Run();
            return;
        }

        // --boot-relay [taskName]: SYSTEM-level boot relay — captures login screen, relays via StreamTransport
        var bootIdx = Array.IndexOf(args, "--boot-relay");
        if (bootIdx >= 0)
        {
            var taskName = (bootIdx + 1 < args.Length) ? args[bootIdx + 1] : null;
            var bootDebugPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SteamViewer", "boot-relay-debug.txt");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(bootDebugPath)!);
                File.AppendAllText(bootDebugPath,
                    $"[{DateTime.Now:HH:mm:ss}] Boot relay intercepted. PID: {Environment.ProcessId}\n" +
                    $"[{DateTime.Now:HH:mm:ss}] User: {Environment.UserName}, Task: {taskName ?? "none"}\n");
            }
            catch { }

            try
            {
                SteamViewer.App.Services.BootRelayOrchestrator.Run(taskName);
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(bootDebugPath, $"[{DateTime.Now:HH:mm:ss}] CRASH: {ex}\n"); } catch { }
            }
            return;
        }

        // --system-helper <pipeName> <nonce> <expectedClientPid> <allowedUserSid>: SYSTEM-level pipe server
        var systemIdx = Array.IndexOf(args, "--system-helper");
        if (systemIdx >= 0 && systemIdx + 4 < args.Length)
        {
            var sysPipeName = args[systemIdx + 1];
            var nonce = args[systemIdx + 2];
            if (!uint.TryParse(args[systemIdx + 3], out var sysExpectedClientPid))
            {
                return; // Invalid args - refuse to run
            }
            var allowedUserSid = args[systemIdx + 4];
            // Optional 5th arg: the admin helper's PID, so the SYSTEM helper can watch it too (B3).
            uint sysAdminPid = 0;
            if (systemIdx + 5 < args.Length)
                uint.TryParse(args[systemIdx + 5], out sysAdminPid);
            var sysDebugPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SteamViewer", "system-helper-debug.txt");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sysDebugPath)!);
                File.AppendAllText(sysDebugPath,
                    $"[{DateTime.Now:HH:mm:ss}] System helper intercepted. PID: {Environment.ProcessId}\n" +
                    $"[{DateTime.Now:HH:mm:ss}] PipeName: {sysPipeName}, User: {Environment.UserName}, ExpectedClientPid: {sysExpectedClientPid}, AllowedUserSid: {allowedUserSid}, AdminPid: {sysAdminPid}\n");
            }
            catch { /* best-effort debug log */ }

            // DPI awareness already set at top of Main() - all modes get PMv2.

            try
            {
                SteamViewer.Platform.Windows.Elevation.SystemHelperServer.Run(sysPipeName, nonce, sysExpectedClientPid, allowedUserSid, sysAdminPid);
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(sysDebugPath, $"[{DateTime.Now:HH:mm:ss}] CRASH: {ex}\n"); } catch { }
            }
            return;
        }

        // --elevated-helper <pipeName> <expectedClientPid>: named pipe server for privileged operations
        var helperIdx = Array.IndexOf(args, "--elevated-helper");
        if (helperIdx >= 0 && helperIdx + 2 < args.Length)
        {
            var pipeName = args[helperIdx + 1];
            if (!uint.TryParse(args[helperIdx + 2], out var expectedClientPid))
            {
                return; // Invalid args - refuse to run
            }

            var debugPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SteamViewer", "helper-debug.txt");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(debugPath)!);
                File.AppendAllText(debugPath,
                    $"[{DateTime.Now:HH:mm:ss}] Helper intercepted. Args: {string.Join(" ", args)}\n" +
                    $"[{DateTime.Now:HH:mm:ss}] PipeName: {pipeName}, PID: {Environment.ProcessId}, ExpectedClientPid: {expectedClientPid}\n");
            }
            catch { /* best-effort debug log */ }

            try
            {
                SteamViewer.Platform.Windows.Elevation.ElevatedHelperServer.Run(pipeName, expectedClientPid);
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(debugPath, $"[{DateTime.Now:HH:mm:ss}] CRASH: {ex}\n"); } catch { }
            }
            return;
        }

        // === Normal app mode — NOW load WinUI3 + MAUI ===
        XamlCheckProcessRequirements();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }

    /// <summary>
    /// Kill all child processes (WebView2, etc.) using Toolhelp32 snapshot.
    /// Call before Environment.Exit to prevent orphaned msedgewebview2.exe processes.
    /// </summary>
    public static void KillChildProcesses()
    {
        try
        {
            var parentPid = (uint)Environment.ProcessId;
            var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == INVALID_HANDLE_VALUE) return;

            try
            {
                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                if (!Process32First(snap, ref entry)) return;

                do
                {
                    if (entry.th32ParentProcessID == parentPid)
                    {
                        try
                        {
                            System.Diagnostics.Process.GetProcessById((int)entry.th32ProcessID).Kill(entireProcessTree: true);
                        }
                        catch { }
                    }
                } while (Process32Next(snap, ref entry));
            }
            finally { CloseHandle(snap); }
        }
        catch { /* best effort */ }
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);
}
