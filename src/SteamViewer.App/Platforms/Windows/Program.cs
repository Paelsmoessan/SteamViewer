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

        // --boot-relay: SYSTEM-level boot relay — captures login screen, relays via SIPSorcery WebRTC
        if (args.Contains("--boot-relay"))
        {
            var bootDebugPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SteamViewer", "boot-relay-debug.txt");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(bootDebugPath)!);
                File.AppendAllText(bootDebugPath,
                    $"[{DateTime.Now:HH:mm:ss}] Boot relay intercepted. PID: {Environment.ProcessId}\n" +
                    $"[{DateTime.Now:HH:mm:ss}] User: {Environment.UserName}\n");
            }
            catch { }

            try
            {
                SteamViewer.Platform.Windows.Elevation.BootRelayService.Run();
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(bootDebugPath, $"[{DateTime.Now:HH:mm:ss}] CRASH: {ex}\n"); } catch { }
            }
            return;
        }

        // --system-helper <pipeName> <nonce>: SYSTEM-level pipe server (launched via scheduled task as SYSTEM)
        var systemIdx = Array.IndexOf(args, "--system-helper");
        if (systemIdx >= 0 && systemIdx + 2 < args.Length)
        {
            var sysPipeName = args[systemIdx + 1];
            var nonce = args[systemIdx + 2];
            var sysDebugPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SteamViewer", "system-helper-debug.txt");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sysDebugPath)!);
                File.AppendAllText(sysDebugPath,
                    $"[{DateTime.Now:HH:mm:ss}] System helper intercepted. PID: {Environment.ProcessId}\n" +
                    $"[{DateTime.Now:HH:mm:ss}] PipeName: {sysPipeName}, User: {Environment.UserName}\n");
            }
            catch { /* best-effort debug log */ }

            // DPI awareness already set at top of Main() — all modes get PMv2.

            try
            {
                SteamViewer.Platform.Windows.Elevation.SystemHelperServer.Run(sysPipeName, nonce);
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(sysDebugPath, $"[{DateTime.Now:HH:mm:ss}] CRASH: {ex}\n"); } catch { }
            }
            return;
        }

        // --elevated-helper <pipeName>: named pipe server for privileged operations
        var helperIdx = Array.IndexOf(args, "--elevated-helper");
        if (helperIdx >= 0 && helperIdx + 1 < args.Length)
        {
            var pipeName = args[helperIdx + 1];
            var debugPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SteamViewer", "helper-debug.txt");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(debugPath)!);
                File.AppendAllText(debugPath,
                    $"[{DateTime.Now:HH:mm:ss}] Helper intercepted. Args: {string.Join(" ", args)}\n" +
                    $"[{DateTime.Now:HH:mm:ss}] PipeName: {pipeName}, PID: {Environment.ProcessId}\n");
            }
            catch { /* best-effort debug log */ }

            try
            {
                SteamViewer.Platform.Windows.Elevation.ElevatedHelperServer.Run(pipeName);
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);
}
