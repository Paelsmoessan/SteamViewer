using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SteamViewer.Launcher;

static class Program
{
    private static readonly string AppName = "SteamViewer";
    private static readonly string AppExeName = "SteamViewer.App.exe";
    private static readonly string VersionFileName = "launcher-version.txt";
    private static readonly string PayloadResourceName = "SteamViewer.Launcher.payload.SteamViewer.zip";

    static int Main(string[] args)
    {
        try
        {
            var installDir = GetInstallDirectory();
            var appExePath = Path.Combine(installDir, AppExeName);
            var versionFilePath = Path.Combine(installDir, VersionFileName);
            var launcherVersion = GetLauncherVersion();

            var needsExtract = !File.Exists(appExePath) || !IsVersionCurrent(versionFilePath, launcherVersion);

            if (needsExtract)
            {
                using var splash = new SplashWindow();
                splash.Show();

                var isFirstRun = !File.Exists(appExePath);
                splash.SetStatus(isFirstRun ? "Installing SteamViewer..." : "Updating SteamViewer...");

                var success = ExtractPayload(installDir, (extracted, total) =>
                {
                    var percent = (int)((double)extracted / total * 100);
                    splash.SetProgress(percent, $"Extracting files... ({extracted}/{total})");
                });

                if (!success)
                {
                    splash.Close();
                    MessageBoxW(IntPtr.Zero,
                        "Failed to extract application files.\nPlease try downloading again.",
                        "SteamViewer", 0x10); // MB_ICONERROR
                    return 1;
                }

                File.WriteAllText(versionFilePath, launcherVersion);
                splash.SetProgress(100, "Launching...");
                Thread.Sleep(300);
                splash.Close();
            }

            if (!File.Exists(appExePath))
            {
                MessageBoxW(IntPtr.Zero,
                    $"{AppExeName} not found.\nPlease try downloading again.",
                    "SteamViewer", 0x10);
                return 1;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = appExePath,
                WorkingDirectory = installDir,
                UseShellExecute = true
            };

            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);

            Process.Start(startInfo);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBoxW(IntPtr.Zero,
                $"Launch failed:\n{ex.Message}",
                "SteamViewer", 0x10);
            return 1;
        }
    }

    private static string GetInstallDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, AppName);
    }

    private static string GetLauncherVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        return version?.ToString() ?? "0.0.0.0";
    }

    private static bool IsVersionCurrent(string versionFilePath, string launcherVersion)
    {
        if (!File.Exists(versionFilePath))
            return false;

        var installedVersion = File.ReadAllText(versionFilePath).Trim();
        return installedVersion == launcherVersion;
    }

    private static bool ExtractPayload(string installDir, Action<int, int> onProgress)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(PayloadResourceName);

        if (stream == null)
            return false;

        Directory.CreateDirectory(installDir);

        var tempDir = installDir + ".update";
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, true);

        Directory.CreateDirectory(tempDir);

        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var totalEntries = archive.Entries.Count(e => !string.IsNullOrEmpty(e.Name));
            var extracted = 0;

            foreach (var entry in archive.Entries)
            {
                var destPath = Path.Combine(tempDir, entry.FullName);

                var destDir = Path.GetDirectoryName(destPath);
                if (destDir != null)
                    Directory.CreateDirectory(destDir);

                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                entry.ExtractToFile(destPath, true);
                extracted++;

                if (extracted % 20 == 0 || extracted == totalEntries)
                    onProgress(extracted, totalEntries);
            }

            onProgress(totalEntries, totalEntries);

            KillRunningInstances();

            if (Directory.Exists(installDir))
            {
                var oldDir = installDir + ".old";
                if (Directory.Exists(oldDir))
                    Directory.Delete(oldDir, true);

                Directory.Move(installDir, oldDir);
                Directory.Move(tempDir, installDir);
                Directory.Delete(oldDir, true);
            }
            else
            {
                Directory.Move(tempDir, installDir);
            }

            return true;
        }
        catch
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
            throw;
        }
    }

    private static void KillRunningInstances()
    {
        try
        {
            var processes = Process.GetProcessesByName("SteamViewer.App");
            foreach (var proc in processes)
            {
                proc.Kill();
                proc.WaitForExit(5000);
            }
        }
        catch
        {
            // Best effort
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
