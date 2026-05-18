using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using SteamViewer.Platform.Windows.Input;

namespace SteamViewer.Platform.Windows.Elevation;

// Input-injection + RunAsSystem concerns for SystemHelperServer.
// Owns the dedicated input thread (must have zero prior user32 calls before
// SetThreadDesktop), desktop-switching P/Invokes for per-event SD attachment,
// and the stateless RunAsSystem process launcher. The RunAsSystem launcher
// folds in here as the semantic neighbor (both are "user-session execution").
public static partial class SystemHelperServer
{
    // Dedicated input thread — SetThreadDesktop requires a thread with zero prior user32 calls
    private static BlockingCollection<(string json, int sw, int sh)>? _inputQueue;
    private static Thread? _inputThread;

    private static int _sdInputLogCount;
    private static int _sdInputFailCount;

    // Desktop switching for per-event Secure Desktop attachment
    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;
    private const uint GENERIC_ALL = 0x10000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    // Desktop access by name — needed for explicit Winlogon desktop access
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

    private static string HandleRunAsSystem(JsonElement root)
    {
        try
        {
            var path = root.TryGetProperty("path", out var p) ? p.GetString() : null;
            var args = root.TryGetProperty("args", out var a) ? a.GetString() : null;

            if (string.IsNullOrEmpty(path))
                return JsonSerializer.Serialize(new HelperResponse(false, "No path specified"));

            if (!File.Exists(path))
                return JsonSerializer.Serialize(new HelperResponse(false, $"File not found: {path}"));

            DebugLog($"RunAsSystem: {path} {args}");

            // Launch process as SYSTEM in the user's desktop session
            if (ProcessLauncher.LaunchInUserSession(path, args, out var pid))
            {
                DebugLog($"RunAsSystem launched PID {pid}");
                return JsonSerializer.Serialize(new HelperResponse(true, null));
            }

            return JsonSerializer.Serialize(new HelperResponse(false, "Failed to launch process in user session"));
        }
        catch (Exception ex)
        {
            DebugLog($"RunAsSystem failed: {ex.Message}");
            return JsonSerializer.Serialize(new HelperResponse(false, ex.Message));
        }
    }

    private static string? HandleInjectInput(string rawJson, JsonElement root)
    {
        try
        {
            var (defaultW, defaultH) = Win32Input.GetPrimaryMonitorSize();
            var sw = root.TryGetProperty("sw", out var swProp) ? swProp.GetInt32() : defaultW;
            var sh = root.TryGetProperty("sh", out var shProp) ? shProp.GetInt32() : defaultH;

            // Always enqueue — the input thread handles both Default and Secure Desktop
            // by switching desktops dynamically (clean thread, no prior user32 calls)
            _inputQueue?.TryAdd((rawJson, sw, sh));

            // Notify capture thread of input activity (event-driven capture)
            _capture?.NotifyInputActivity();
        }
        catch (Exception ex)
        {
            DebugLog($"InjectInput error: {ex.Message}");
        }

        return null; // Fire-and-forget
    }

    /// <summary>
    /// Dedicated input thread — attaches to Default desktop as its very first user32 call,
    /// then processes input from queue. Handles both Default and Secure Desktop input by
    /// dynamically switching desktops when _capture.IsActive changes.
    /// SetThreadDesktop works because this thread never creates windows.
    /// </summary>
    private static void InputThreadProc()
    {
        try
        {
            // First user32 call on this thread — attach to Default desktop
            // Keep the handle open so we can switch back from Winlogon later
            var hDefaultDesk = OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP);
            if (hDefaultDesk != IntPtr.Zero)
            {
                var ok = SetThreadDesktop(hDefaultDesk);
                DebugLog($"Input thread SetThreadDesktop(Default): {ok} (error {Marshal.GetLastWin32Error()})");
            }
            else
            {
                DebugLog($"Input thread OpenInputDesktop failed (error {Marshal.GetLastWin32Error()})");
            }

            // Transition flag — used to detect SD→normal transitions so we re-acquire the
            // Default desktop handle (preserves load-bearing piece of 210eaf3). The Winlogon
            // handle is NOT cached across events: per-event open/switch/inject/close avoids
            // the leak (pre-210eaf3 bug) and the stale-handle race (post-210eaf3 bug). See
            // .claude/plans/fix-sd-input-stale-handle.md for the full reasoning.
            bool wasOnSecureDesktop = false;

            // Process input commands from queue
            foreach (var (json, sw, sh) in _inputQueue!.GetConsumingEnumerable())
            {
                IntPtr hCurrent = IntPtr.Zero;
                try
                {
                    var onSecureDesktop = _capture != null && _capture.IsActive;

                    if (onSecureDesktop)
                    {
                        // Per-event: open current input desktop, switch, inject, close.
                        // OpenInputDesktop returns whichever desktop has user input focus right
                        // now — robust to mid-flow desktop swaps (e.g. lock-screen Phase 1↔2).
                        hCurrent = OpenInputDesktop(0, false, GENERIC_ALL);
                        if (hCurrent == IntPtr.Zero)
                        {
                            // Fallback to explicit name (matches pre-existing fallback)
                            hCurrent = OpenDesktop("Winlogon", 0, false, GENERIC_ALL);
                        }
                        if (hCurrent == IntPtr.Zero)
                        {
                            _sdInputFailCount++;
                            if (_sdInputFailCount <= 10 || _sdInputFailCount % 100 == 0)
                                DebugLog($"SD input: can't open input desktop (fail #{_sdInputFailCount}, err {Marshal.GetLastWin32Error()})");
                            continue;
                        }

                        if (!SetThreadDesktop(hCurrent))
                        {
                            DebugLog($"SD input: SetThreadDesktop failed (err {Marshal.GetLastWin32Error()})");
                            CloseDesktop(hCurrent);
                            hCurrent = IntPtr.Zero;
                            continue;
                        }

                        if (!wasOnSecureDesktop)
                        {
                            _sdInputLogCount = 0;
                            _sdInputFailCount = 0;
                            DebugLog("SD input: entered SD (per-event re-acquire pattern)");
                            wasOnSecureDesktop = true;
                        }

                        _sdInputLogCount++;
                        if (_sdInputLogCount <= 3 || _sdInputLogCount % 200 == 0)
                            DebugLog($"SD input #{_sdInputLogCount}: injecting on current input desktop");
                    }
                    else if (wasOnSecureDesktop)
                    {
                        // Transition: SD → normal. Re-acquire Default desktop handle —
                        // the startup handle may be stale after SD round-trip (210eaf3 fix).
                        var hNewDefault = OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP);
                        if (hNewDefault != IntPtr.Zero)
                        {
                            if (hDefaultDesk != IntPtr.Zero)
                                CloseDesktop(hDefaultDesk);
                            hDefaultDesk = hNewDefault;
                            DebugLog("SD input: re-acquired Default desktop handle on SD→normal transition");
                        }
                        else
                        {
                            DebugLog($"SD input: WARNING — failed to re-acquire Default desktop (err {Marshal.GetLastWin32Error()}), using old handle");
                        }

                        var switchedBack = SetThreadDesktop(hDefaultDesk);
                        DebugLog($"SD input: leaving SD, SetThreadDesktop(Default)={switchedBack}, total SD events={_sdInputLogCount}, failures={_sdInputFailCount}");
                        wasOnSecureDesktop = false;
                    }

                    // Parse and inject input via canonical dispatcher
                    using var doc = JsonDocument.Parse(json);
                    Win32Input.InjectInputFromJson(doc.RootElement, sw, sh,
                        msg => DebugLog($"Input thread: {msg}"));
                }
                catch (Exception ex)
                {
                    DebugLog($"Input thread error: {ex.Message}");
                }
                finally
                {
                    // Always close the per-event SD handle — never cache across events.
                    if (hCurrent != IntPtr.Zero)
                        CloseDesktop(hCurrent);
                }
            }

            // Cleanup
            if (hDefaultDesk != IntPtr.Zero)
                CloseDesktop(hDefaultDesk);
        }
        catch (Exception ex)
        {
            DebugLog($"Input thread fatal: {ex.Message}");
        }
    }
}
