using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;

namespace SteamViewer.Launcher;

static class Program
{
    private static readonly string AppName = "SteamViewer";
    private static readonly string AppExeName = "SteamViewer.App.exe";
    private static readonly string VersionFileName = "launcher-version.txt";
    private static readonly string PayloadResourceName = "SteamViewer.Launcher.payload.SteamViewer.zip";

    [STAThread]
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
                ShowConsole();
                Console.WriteLine();
                Console.WriteLine("  SteamViewer Launcher");
                Console.WriteLine("  ====================");
                Console.WriteLine();

                if (!File.Exists(appExePath))
                    Console.WriteLine("  First run — installing SteamViewer...");
                else
                    Console.WriteLine("  Update detected — installing new version...");

                Console.WriteLine();

                if (!ExtractPayload(installDir))
                {
                    Console.WriteLine("  [ERROR] Failed to extract application files.");
                    Console.WriteLine("  Press any key to exit...");
                    Console.ReadKey(true);
                    return 1;
                }

                File.WriteAllText(versionFilePath, launcherVersion);
                Console.WriteLine($"  Installed to: {installDir}");
                Console.WriteLine();
            }

            if (!File.Exists(appExePath))
            {
                ShowConsole();
                Console.WriteLine($"  [ERROR] {AppExeName} not found at: {appExePath}");
                Console.WriteLine("  Press any key to exit...");
                Console.ReadKey(true);
                return 1;
            }

            // Launch the app and exit
            var startInfo = new ProcessStartInfo
            {
                FileName = appExePath,
                WorkingDirectory = installDir,
                UseShellExecute = true
            };

            // Forward any command-line arguments
            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);

            Process.Start(startInfo);
            return 0;
        }
        catch (Exception ex)
        {
            ShowConsole();
            Console.WriteLine($"  [ERROR] {ex.Message}");
            Console.WriteLine();
            Console.WriteLine("  Press any key to exit...");
            Console.ReadKey(true);
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

    private static bool ExtractPayload(string installDir)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(PayloadResourceName);

        if (stream == null)
        {
            Console.WriteLine("  [ERROR] Embedded payload not found in launcher.");
            Console.WriteLine($"  Expected resource: {PayloadResourceName}");
            Console.WriteLine();
            Console.WriteLine("  Available resources:");
            foreach (var name in assembly.GetManifestResourceNames())
                Console.WriteLine($"    - {name}");
            return false;
        }

        Console.Write("  Extracting...");

        // Ensure install directory exists
        Directory.CreateDirectory(installDir);

        // Extract to a temp directory first, then swap
        var tempDir = installDir + ".update";
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, true);

        Directory.CreateDirectory(tempDir);

        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var totalEntries = archive.Entries.Count;
            var extracted = 0;

            foreach (var entry in archive.Entries)
            {
                var destPath = Path.Combine(tempDir, entry.FullName);

                // Create subdirectories
                var destDir = Path.GetDirectoryName(destPath);
                if (destDir != null)
                    Directory.CreateDirectory(destDir);

                // Skip directory entries
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                entry.ExtractToFile(destPath, true);
                extracted++;

                // Progress indicator
                if (extracted % 50 == 0 || extracted == totalEntries)
                    Console.Write($"\r  Extracting... {extracted}/{totalEntries} files");
            }

            Console.WriteLine($"\r  Extracting... {extracted}/{totalEntries} files — done!");

            // Kill any running instance before swapping
            KillRunningInstances();

            // Swap: delete old install, rename temp to install
            if (Directory.Exists(installDir))
            {
                // Move old to .old, move new to install, delete .old
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
            // Clean up temp dir on failure
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
                Console.WriteLine($"  Stopping running instance (PID {proc.Id})...");
                proc.Kill();
                proc.WaitForExit(5000);
            }
        }
        catch
        {
            // Best effort — if we can't kill it, the swap will fail and we'll error out
        }
    }

    private static void ShowConsole()
    {
        // WinExe hides console by default — allocate one for output
        AllocConsole();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AllocConsole();
}
