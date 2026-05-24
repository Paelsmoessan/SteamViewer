using System.Diagnostics;

namespace SteamViewer.Platform.Windows.Elevation;

/// <summary>
/// Self-termination watchdog for the headless helper processes (admin + SYSTEM).
/// A helper holds an open privileged named pipe; if its parent host process dies without a clean
/// "exit"/pipe-close (crash, taskkill, window-X with slow dispose), the helper would otherwise
/// linger as an orphaned privileged endpoint — a local privilege-escalation surface.
/// This arms a background thread that waits on the parent process handle and force-exits the helper
/// when the parent dies, orphan-proofing every host-death path at the root.
/// Keystone of the helper-lifecycle fix (see .claude/research/elevated-helper-lifecycle).
/// </summary>
internal static class ParentDeathWatchdog
{
    /// <summary>
    /// Arm the watchdog. <paramref name="parentPid"/> is a process to watch; when it exits, this
    /// (helper) process calls Environment.Exit(<paramref name="exitCode"/>). <paramref name="parentLabel"/>
    /// names the watched process in the logs (e.g. "host", "admin helper"). May be called more than
    /// once with different parents (the SYSTEM helper watches both the host and the admin helper);
    /// each arm is an independent thread and the first death to fire wins.
    /// A handle to the specific process instance is captured at arm time, so PID reuse after the
    /// parent dies cannot keep the helper alive (the handle tracks the original instance).
    /// Fails OPEN: if the parent can't be opened/waited, it logs and disarms rather than risk
    /// killing a live helper - the pipe-close and exit-command paths remain as fallbacks.
    /// </summary>
    public static void Arm(uint parentPid, Action<string> log, string parentLabel = "host", int exitCode = 0)
    {
        if (parentPid == 0)
        {
            log($"Watchdog NOT armed for {parentLabel}: pid=0 (no parent to watch).");
            return;
        }

        Process parent;
        try
        {
            parent = Process.GetProcessById((int)parentPid);
        }
        catch (ArgumentException)
        {
            // Parent already gone before we could arm - nothing to be orphaned to; exit now.
            log($"Watchdog: {parentLabel} PID {parentPid} not running at arm time - terminating helper immediately (Environment.Exit({exitCode})).");
            Environment.Exit(exitCode);
            return;
        }
        catch (Exception ex)
        {
            log($"Watchdog NOT armed for {parentLabel} PID {parentPid}: could not open process: {ex.Message}. Relying on pipe-close/exit-command fallback.");
            return;
        }

        var thread = new Thread(() =>
        {
            try
            {
                parent.WaitForExit();
            }
            catch (Exception ex)
            {
                // Lost the ability to wait (e.g. access revoked) - do NOT kill a possibly-live helper.
                log($"Watchdog disarmed: WaitForExit on {parentLabel} PID {parentPid} failed: {ex.Message}. Relying on pipe-close/exit-command fallback.");
                return;
            }
            log($"Watchdog FIRED: {parentLabel} PID {parentPid} exited - terminating helper now (Environment.Exit({exitCode})).");
            Environment.Exit(exitCode);
        })
        {
            IsBackground = true,
            Name = $"ParentDeathWatchdog-{parentLabel}"
        };
        thread.Start();
        log($"Watchdog armed: watching {parentLabel} PID {parentPid}; helper self-terminates when it exits.");
    }
}
