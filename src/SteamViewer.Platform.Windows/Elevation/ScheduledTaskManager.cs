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
    /// <param name="error">Error details if the operation failed.</param>
    /// <returns>True if both create and run succeeded.</returns>
    public static bool CreateAndRun(string taskName, string exePath, string arguments, out string? error)
    {
        // Create the task: one-time with past trigger (won't auto-run), runs as SYSTEM, force overwrite
        var createArgs = $"/create /tn \"{taskName}\" /tr \"\\\"{exePath}\\\" {arguments}\" /sc once /st 00:00 /ru SYSTEM /it /f";
        if (!RunSchtasks(createArgs, out error))
            return false;

        // Run it immediately
        return RunSchtasks($"/run /tn \"{taskName}\"", out error);
    }

    /// <summary>
    /// Delete a scheduled task.
    /// </summary>
    /// <param name="taskName">The task name to delete.</param>
    /// <returns>True if deletion succeeded (or task didn't exist).</returns>
    public static bool Delete(string taskName)
    {
        return RunSchtasks($"/delete /tn \"{taskName}\" /f", out _);
    }

    private static bool RunSchtasks(string arguments, out string? error)
    {
        error = null;
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
            if (process == null)
            {
                error = "Failed to start schtasks.exe";
                return false;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(10_000); // 10s timeout

            if (process.ExitCode != 0)
            {
                error = $"schtasks exit {process.ExitCode} | args: {arguments} | stdout: {stdout.Trim()} | stderr: {stderr.Trim()}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"schtasks exception: {ex.Message} | args: {arguments}";
            return false;
        }
    }
}
