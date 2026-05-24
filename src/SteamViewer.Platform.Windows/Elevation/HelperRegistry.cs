using System.Diagnostics;

namespace SteamViewer.Platform.Windows.Elevation;

/// <summary>
/// Tracks running helper processes via small marker files under ProgramData so a freshly-launched
/// admin helper can reap ORPHANED helpers left by a prior session. This is belt-and-suspenders
/// behind the parent-death watchdog (which already self-terminates helpers on host death); it
/// mainly cleans up helpers left by pre-watchdog builds or the rare watchdog fail-open.
/// A marker file is "{ownPid}.marker" containing "{hostPid}|{role}". A helper is an orphan iff its
/// own process is still alive (and still IS our exe) but its host parent PID is dead.
/// See .claude/research/elevated-helper-lifecycle.
/// </summary>
internal static class HelperRegistry
{
    // The process name (no extension) of all our processes - host AND both helper modes share one exe.
    private const string OurProcessName = "SteamViewer.App";

    private static string MarkerDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SteamViewer", "helpers");

    private static string MarkerPathFor(int pid) => Path.Combine(MarkerDir, $"{pid}.marker");

    /// <summary>Record this helper (own PID -> host PID + role) so it can be identified as an orphan later.</summary>
    public static void Register(uint hostPid, string role, Action<string> log)
    {
        try
        {
            Directory.CreateDirectory(MarkerDir);
            File.WriteAllText(MarkerPathFor(Environment.ProcessId), $"{hostPid}|{role}");
            log($"HelperRegistry: registered PID {Environment.ProcessId} (host={hostPid}, role={role}).");
        }
        catch (Exception ex)
        {
            log($"HelperRegistry: register failed: {ex.Message}");
        }
    }

    /// <summary>Remove this helper's marker on clean exit. (Watchdog/kill exits leave it for the reaper to clean.)</summary>
    public static void Deregister(Action<string> log)
    {
        try
        {
            var path = MarkerPathFor(Environment.ProcessId);
            if (File.Exists(path)) File.Delete(path);
            log($"HelperRegistry: deregistered PID {Environment.ProcessId}.");
        }
        catch (Exception ex)
        {
            log($"HelperRegistry: deregister failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Kill helper processes whose host parent is dead (orphans) and clean up stale markers.
    /// Must run with SeDebugPrivilege enabled to terminate SYSTEM-level orphans (the admin helper
    /// enables it via ProcessLauncher). Never touches a helper whose host parent is still alive, so a
    /// concurrent legitimate session is safe. Verifies each live PID is still our exe before killing,
    /// so a reused PID (our helper died, PID recycled by an unrelated process) is never killed.
    /// </summary>
    public static void ReapOrphans(Action<string> log)
    {
        string[] markers;
        try
        {
            if (!Directory.Exists(MarkerDir)) { log("HelperRegistry: no marker dir - nothing to reap."); return; }
            markers = Directory.GetFiles(MarkerDir, "*.marker");
        }
        catch (Exception ex)
        {
            log($"HelperRegistry: reap scan failed: {ex.Message}");
            return;
        }

        var self = Environment.ProcessId;
        int reaped = 0, stale = 0;

        foreach (var marker in markers)
        {
            try
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(marker), out var helperPid) || helperPid == self)
                {
                    if (helperPid != self) TryDelete(marker); // unparseable name
                    continue;
                }

                Process helper;
                try { helper = Process.GetProcessById(helperPid); }
                catch { TryDelete(marker); stale++; continue; } // not running -> stale marker

                using (helper)
                {
                    if (helper.HasExited) { TryDelete(marker); stale++; continue; }

                    // PID-reuse guard: the live process at this PID must still be our exe.
                    if (!helper.ProcessName.Equals(OurProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        log($"HelperRegistry: marker PID {helperPid} is now '{helper.ProcessName}' (PID reused) - dropping stale marker, NOT killing.");
                        TryDelete(marker);
                        stale++;
                        continue;
                    }

                    var parts = File.ReadAllText(marker).Split('|');
                    var role = parts.Length > 1 ? parts[1] : "?";
                    if (!uint.TryParse(parts.Length > 0 ? parts[0] : "", out var hostPid))
                    {
                        log($"HelperRegistry: marker PID {helperPid} unparseable host - dropping marker, NOT killing live helper.");
                        TryDelete(marker);
                        continue;
                    }

                    if (IsAlive((int)hostPid))
                        continue; // host parent alive => live session => leave it

                    // Orphan confirmed: our helper, alive, with a dead host parent.
                    log($"HelperRegistry: ORPHAN - {role} helper PID {helperPid} (dead host {hostPid}); killing.");
                    try { helper.Kill(); reaped++; }
                    catch (Exception ex) { log($"HelperRegistry: failed to kill orphan PID {helperPid}: {ex.Message}"); }
                    TryDelete(marker);
                }
            }
            catch (Exception ex)
            {
                log($"HelperRegistry: error on marker {Path.GetFileName(marker)}: {ex.Message}");
            }
        }

        log($"HelperRegistry: reap complete - {reaped} orphan(s) killed, {stale} stale marker(s) cleaned, {markers.Length} scanned.");
    }

    private static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
