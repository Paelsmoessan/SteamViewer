using System.Diagnostics;

namespace SteamViewer.Platform.Windows.Elevation;

/// <summary>
/// Manages Windows scheduled tasks for launching the SYSTEM-level helper process.
/// Uses schtasks.exe to create, run, and delete on-demand tasks running as SYSTEM.
/// </summary>
internal static class ScheduledTaskManager
{
    /// <summary>
    /// Create an on-demand scheduled task running as SYSTEM and immediately run it.
    /// </summary>
    /// <param name="taskName">Unique task name (e.g., "SteamViewer-System-{guid}").</param>
    /// <param name="exePath">Full path to the executable.</param>
    /// <param name="arguments">Command-line arguments for the executable.</param>
    /// <returns>True if both create and run succeeded.</returns>
    public static bool CreateAndRun(string taskName, string exePath, string arguments)
    {
        // Create the task: on-demand trigger, runs as SYSTEM, force overwrite if exists
        var createResult = RunSchtasks(
            $"/create /tn \"{taskName}\" /tr \"\\\"{exePath}\\\" {arguments}\" /sc ondemand /ru SYSTEM /f");

        if (!createResult)
            return false;

        // Run it immediately
        return RunSchtasks($"/run /tn \"{taskName}\"");
    }

    /// <summary>
    /// Delete a scheduled task.
    /// </summary>
    /// <param name="taskName">The task name to delete.</param>
    /// <returns>True if deletion succeeded (or task didn't exist).</returns>
    public static bool Delete(string taskName)
    {
        return RunSchtasks($"/delete /tn \"{taskName}\" /f");
    }

    private static bool RunSchtasks(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            process.WaitForExit(10_000); // 10s timeout
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
