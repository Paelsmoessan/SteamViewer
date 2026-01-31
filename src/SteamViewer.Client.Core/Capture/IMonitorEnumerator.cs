using SteamViewer.Common.Protocol;

namespace SteamViewer.Client.Core.Capture;

/// <summary>
/// Platform-agnostic interface for enumerating monitors.
/// </summary>
public interface IMonitorEnumerator
{
    /// <summary>
    /// Get all available monitors.
    /// </summary>
    IReadOnlyList<MonitorInfo> GetMonitors();

    /// <summary>
    /// Get the primary monitor.
    /// </summary>
    MonitorInfo? GetPrimaryMonitor();
}
