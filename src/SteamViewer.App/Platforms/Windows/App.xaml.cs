using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

namespace SteamViewer.App.WinUI;

public partial class App : MauiWinUIApplication
{
#if DEBUG
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
#endif

    public App()
    {
#if DEBUG
        // Allocate a console window for debug output
        AllocConsole();
        Console.WriteLine("=== SteamViewer.App Debug Console ===");
#endif
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
