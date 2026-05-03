using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SteamViewer.App.WinUI;

/// <summary>
/// Checks for required runtime dependencies on Windows.
/// </summary>
public static class DependencyChecker
{
    public record Dependency(string Name, bool IsInstalled, string DownloadUrl, string? Version = null);

    /// <summary>
    /// Checks all required dependencies and returns their status.
    /// </summary>
    public static List<Dependency> CheckAll()
    {
        var dependencies = new List<Dependency>
        {
            CheckWebView2()
        };

        return dependencies;
    }

    /// <summary>
    /// Returns true if all dependencies are installed.
    /// </summary>
    public static bool AllInstalled()
    {
        return CheckAll().All(d => d.IsInstalled);
    }

    /// <summary>
    /// Checks if Microsoft Edge WebView2 Runtime is installed.
    /// </summary>
    public static Dependency CheckWebView2()
    {
        const string downloadUrl = "https://developer.microsoft.com/en-us/microsoft-edge/webview2/";
        string? version = null;

        // Try to get WebView2 version from registry
        // Check multiple locations as it can be installed per-user or per-machine
        string[] registryPaths = new[]
        {
            @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
            @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
        };

        foreach (var path in registryPaths)
        {
            // Try HKLM first (machine-wide install)
            using var keyLM = Registry.LocalMachine.OpenSubKey(path);
            if (keyLM != null)
            {
                version = keyLM.GetValue("pv") as string;
                if (!string.IsNullOrEmpty(version))
                    return new Dependency("Microsoft Edge WebView2 Runtime", true, downloadUrl, version);
            }

            // Try HKCU (per-user install)
            using var keyCU = Registry.CurrentUser.OpenSubKey(path);
            if (keyCU != null)
            {
                version = keyCU.GetValue("pv") as string;
                if (!string.IsNullOrEmpty(version))
                    return new Dependency("Microsoft Edge WebView2 Runtime", true, downloadUrl, version);
            }
        }

        // Alternative: Try using the WebView2 loader API
        try
        {
            version = GetWebView2Version();
            if (!string.IsNullOrEmpty(version))
                return new Dependency("Microsoft Edge WebView2 Runtime", true, downloadUrl, version);
        }
        catch
        {
            // API not available or failed
        }

        return new Dependency("Microsoft Edge WebView2 Runtime", false, downloadUrl);
    }

    [DllImport("WebView2Loader.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private static extern int GetAvailableCoreWebView2BrowserVersionString(string? browserExecutableFolder, out IntPtr versionInfo);

    private static string? GetWebView2Version()
    {
        try
        {
            int hr = GetAvailableCoreWebView2BrowserVersionString(null, out IntPtr versionPtr);
            if (hr == 0 && versionPtr != IntPtr.Zero)
            {
                string version = Marshal.PtrToStringUni(versionPtr) ?? "";
                Marshal.FreeCoTaskMem(versionPtr);
                return version;
            }
        }
        catch
        {
            // DLL not found or other error
        }
        return null;
    }

    /// <summary>
    /// Opens the download URL in the default browser.
    /// </summary>
    public static void OpenDownloadPage(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // Failed to open browser
        }
    }
}
