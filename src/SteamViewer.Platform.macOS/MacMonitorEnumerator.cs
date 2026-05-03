using Microsoft.Extensions.Logging;
using SteamViewer.Client.Core.Capture;
using SteamViewer.Common.Protocol;
using CoreGraphics;
using ObjCRuntime;

namespace SteamViewer.Platform.macOS;

/// <summary>
/// macOS monitor enumeration using CoreGraphics display APIs.
/// </summary>
public sealed class MacMonitorEnumerator : IMonitorEnumerator
{
    private readonly ILogger<MacMonitorEnumerator> _logger;

    public MacMonitorEnumerator(ILogger<MacMonitorEnumerator> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();

        try
        {
            // Get all active displays using CoreGraphics
            var maxDisplays = 16u;
            var displayIds = new uint[maxDisplays];
            uint displayCount = 0;

            var result = CGDisplay.GetActiveDisplayList(maxDisplays, displayIds, out displayCount);

            if (result != CGError.Success)
            {
                _logger.LogError("Failed to get display list: {Error}", result);
            }
            else
            {
                var mainDisplayId = CGDisplay.MainDisplayID;

                for (uint i = 0; i < displayCount; i++)
                {
                    var displayId = displayIds[i];
                    var bounds = CGDisplay.GetBounds(displayId);

                    var monitorInfo = new MonitorInfo(
                        Id: i,
                        Name: $"Display {i + 1}",
                        Width: (uint)bounds.Width,
                        Height: (uint)bounds.Height,
                        X: (int)bounds.X,
                        Y: (int)bounds.Y,
                        IsPrimary: displayId == mainDisplayId
                    );

                    monitors.Add(monitorInfo);

                    _logger.LogDebug("Found monitor {Id}: {Name} ({Width}x{Height}) at ({X},{Y}), Primary: {Primary}",
                        monitorInfo.Id, monitorInfo.Name, monitorInfo.Width, monitorInfo.Height,
                        monitorInfo.X, monitorInfo.Y, monitorInfo.IsPrimary);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate monitors via CoreGraphics");
        }

        if (monitors.Count == 0)
        {
            // Last resort: query main display bounds directly
            var mainBounds = CGDisplay.GetBounds(CGDisplay.MainDisplayID);
            var w = (uint)mainBounds.Width;
            var h = (uint)mainBounds.Height;
            _logger.LogWarning("No monitors from display list, main display bounds: {W}x{H}", w, h);
            monitors.Add(new MonitorInfo(
                Id: 0,
                Name: "Primary Display",
                Width: w > 0 ? w : 1920,
                Height: h > 0 ? h : 1080,
                X: 0,
                Y: 0,
                IsPrimary: true
            ));
        }

        _logger.LogInformation("Found {Count} monitors", monitors.Count);
        return monitors;
    }

    public MonitorInfo? GetPrimaryMonitor()
    {
        return GetMonitors().FirstOrDefault(m => m.IsPrimary);
    }
}
