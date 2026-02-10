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
        // === Intercept lightweight modes BEFORE WinUI3 loads ===
        // At this point NO WinUI3/MAUI DLLs have been loaded.
        // The elevated helper and SAS mode are pure Win32/P/Invoke — no UI needed.

        // --sas: sends Ctrl+Alt+Del, waits for logon, launches app as user (RunOnceEx pre-login)
        if (args.Contains("--sas"))
        {
            SasMode.Run();
            return;
        }

        // --elevated-helper <pipeName>: named pipe server for privileged operations
        var helperIdx = Array.IndexOf(args, "--elevated-helper");
        if (helperIdx >= 0 && helperIdx + 1 < args.Length)
        {
            SteamViewer.Platform.Windows.Elevation.ElevatedHelperServer.Run(args[helperIdx + 1]);
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
}
