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

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
