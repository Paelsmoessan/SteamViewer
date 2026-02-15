using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SteamViewer.Client.Core.Capture;
using SteamViewer.Client.Core.Session;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Platform.Windows.Input;

/// <summary>
/// Windows input injection using SendInput API.
/// Delegates core injection to Win32Input; adds debug logging and IInputInjector interface.
/// </summary>
public sealed class WindowsInputInjector : IInputInjector
{
    private readonly ILogger<WindowsInputInjector> _logger;
    private bool _disposed;

    // Debug log file for cursor diagnostics (in logs/ folder alongside client/server logs)
    private static readonly string DebugLogPath = Path.Combine(
        FindLogsDirectory(),
        $"input-{Environment.MachineName}.log");
    private static readonly object LogLock = new();
    private int _logCount;
    private const int MaxLogEntries = 100;

    public WindowsInputInjector(ILogger<WindowsInputInjector> logger)
    {
        _logger = logger;

        var (left, top, width, height) = Win32Input.GetVirtualScreen();

        _logger.LogInformation("Virtual screen: ({Left},{Top}) {Width}x{Height} (DPI-aware physical pixels)",
            left, top, width, height);

        var monitors = Win32Input.GetMonitors();
        foreach (var mon in monitors)
        {
            _logger.LogInformation("Monitor: {Device} ({W}x{H}) at ({X},{Y}) Primary={Primary}",
                mon.DeviceName, mon.Width, mon.Height, mon.X, mon.Y, mon.IsPrimary);
        }

        // Initialize debug log file
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DebugLogPath)!);
            var monitorInfo = string.Join("\n", monitors.Select(m =>
                $"  {m.DeviceName}: {m.Width}x{m.Height} at ({m.X},{m.Y}) Primary={m.IsPrimary}"));
            File.WriteAllText(DebugLogPath,
                $"=== SteamViewer Input Debug Log - {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n" +
                $"Virtual Screen: left={left}, top={top}, " +
                $"width={width}, height={height} (DPI-aware physical pixels)\n" +
                $"Monitors:\n{monitorInfo}\n" +
                $"Log file: {DebugLogPath}\n\n");
            _logger.LogInformation("Debug log file created at: {Path}", DebugLogPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create debug log file at {Path}", DebugLogPath);
        }
    }

    public bool IsAvailable => !_disposed;

    /// <summary>Whether the current process is running elevated (admin).</summary>
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public bool SendSecureAttentionSequence()
    {
        if (!IsElevated)
        {
            _logger.LogWarning("SendSAS requires elevation");
            return false;
        }

        try
        {
            SendSAS(false);
            _logger.LogInformation("SendSAS succeeded");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendSAS failed");
            return false;
        }
    }

    public bool RebootWithAutoRestart(string? clientId = null, string? passwordHash = null, string? viewerPeerId = null)
    {
        try
        {
            // If elevated, register auto-restart via registry
            if (IsElevated)
            {
                var appPath = Environment.ProcessPath;

                if (!string.IsNullOrEmpty(appPath))
                {
                    // Save encrypted reconnect credentials (if provided)
                    if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(passwordHash) && !string.IsNullOrEmpty(viewerPeerId))
                    {
                        try
                        {
                            ReconnectCredentials.Save(clientId, passwordHash, viewerPeerId);
                            _logger.LogInformation("Saved encrypted reconnect credentials for post-reboot auto-reconnect");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to save reconnect credentials (app won't auto-reconnect)");
                        }
                    }

                    // RunOnceEx — main app with --sas runs pre-login to show Ctrl+Alt+Del screen,
                    // waits for logon, then launches full app as the logged-in user
                    try
                    {
                        using var runOnceExKey = Registry.LocalMachine.CreateSubKey(
                            @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnceEx\SteamViewer");
                        runOnceExKey?.SetValue("", $"\"{appPath}\" --sas");
                        _logger.LogInformation("Registered app --sas in RunOnceEx");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to write RunOnceEx key (SAS won't run pre-login)");
                    }
                }
            }
            else
            {
                _logger.LogInformation("Not elevated — rebooting without auto-restart");
            }

            // Reboot (works for standard users)
            _logger.LogInformation("Initiating system reboot");
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown",
                Arguments = "/r /t 0",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initiate reboot");
            return false;
        }
    }

    public void SetCapturedMonitor(int captureWidth, int captureHeight)
    {
        Win32Input.SetCapturedMonitor(captureWidth, captureHeight);
        _logger.LogInformation("Cached target monitor for capture {W}x{H}", captureWidth, captureHeight);
    }

    public void ClearCapturedMonitor()
    {
        Win32Input.ClearCapturedMonitor();
        _logger.LogInformation("Cleared cached target monitor");
    }

    public void InjectInput(InputEvent inputEvent, int screenWidth, int screenHeight)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsInputInjector));
        }

        // Debug logging for mouse coordinate conversion
        if (inputEvent is InputEvent.MouseMove move && _logCount < MaxLogEntries)
        {
            LogCoordinateConversion(move.X, move.Y, screenWidth, screenHeight);
        }

        Win32Input.InjectInputEvent(inputEvent, screenWidth, screenHeight);
    }

    private void LogCoordinateConversion(double x, double y, int screenWidth, int screenHeight)
    {
        try
        {
            var (absX, absY) = Win32Input.ConvertToAbsoluteCoordinates(x, y, screenWidth, screenHeight);
            var (_, _, vsWidth, vsHeight) = Win32Input.GetVirtualScreen();

            lock (LogLock)
            {
                if (_logCount < MaxLogEntries)
                {
                    var logLine = $"[{DateTime.Now:HH:mm:ss.fff}] " +
                        $"INPUT: x={x:F1}, y={y:F1}, captureSize={screenWidth}x{screenHeight} | " +
                        $"VIRTUAL: {vsWidth}x{vsHeight} | " +
                        $"ABS: x={absX}, y={absY} (0-65535 range)\n";
                    File.AppendAllText(DebugLogPath, logLine);
                    _logCount++;

                    if (_logCount == MaxLogEntries)
                    {
                        File.AppendAllText(DebugLogPath,
                            $"\n=== Max log entries ({MaxLogEntries}) reached, logging stopped ===\n");
                    }
                }
            }
        }
        catch
        {
            // Ignore logging errors to not disrupt input
        }
    }

    /// <summary>
    /// Finds the logs directory by walking up from base directory looking for solution root markers.
    /// </summary>
    private static string FindLogsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0 || dir.GetFiles("CLAUDE.md").Length > 0)
            {
                return Path.Combine(dir.FullName, "logs");
            }
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "logs");
    }

    public void Dispose()
    {
        _disposed = true;
    }

    [DllImport("sas.dll", SetLastError = true)]
    private static extern void SendSAS(bool asUser);
}
