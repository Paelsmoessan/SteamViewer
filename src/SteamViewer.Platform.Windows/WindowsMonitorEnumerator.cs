using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SteamViewer.Client.Core.Capture;
using SteamViewer.Common.Protocol;
using Vortice.DXGI;
using DXGIFactory = Vortice.DXGI.DXGI;

namespace SteamViewer.Platform.Windows;

/// <summary>
/// Windows monitor enumeration using DXGI.
/// </summary>
public sealed class WindowsMonitorEnumerator : IMonitorEnumerator
{
    private readonly ILogger<WindowsMonitorEnumerator> _logger;

    public WindowsMonitorEnumerator(ILogger<WindowsMonitorEnumerator> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();

        try
        {
            using var factory = DXGIFactory.CreateDXGIFactory1<IDXGIFactory1>();

            var adapterIndex = 0;
            uint globalOutputIndex = 0;

            // Enumerate adapters
            while (true)
            {
                var adapterResult = factory.EnumAdapters1(adapterIndex, out var adapter);
                if (adapterResult.Failure || adapter == null)
                {
                    break; // No more adapters
                }

                using var adapterDisposer = adapter;
                var adapterDesc = adapter.Description1;
                var adapterName = new string(adapterDesc.Description).TrimEnd('\0');

                _logger.LogDebug("Found adapter {Index}: {Name}", adapterIndex, adapterName);

                var outputIndex = 0;

                // Enumerate outputs for this adapter
                while (true)
                {
                    var outputResult = adapter.EnumOutputs(outputIndex, out var output);
                    if (outputResult.Failure || output == null)
                    {
                        break; // No more outputs
                    }

                    using var outputDisposer = output;
                    var desc = output.Description;

                    var deviceName = new string(desc.DeviceName).TrimEnd('\0');
                    var rect = desc.DesktopCoordinates;

                    var width = (uint)(rect.Right - rect.Left);
                    var height = (uint)(rect.Bottom - rect.Top);
                    var isPrimary = rect.Left == 0 && rect.Top == 0;

                    var monitorInfo = new MonitorInfo(
                        Id: globalOutputIndex,
                        Name: string.IsNullOrEmpty(deviceName) ? $"Display {globalOutputIndex + 1}" : deviceName,
                        Width: width,
                        Height: height,
                        X: rect.Left,
                        Y: rect.Top,
                        IsPrimary: isPrimary
                    );

                    monitors.Add(monitorInfo);

                    _logger.LogDebug("Found monitor {Id}: {Name} ({Width}x{Height}) at ({X},{Y}), Primary: {Primary}",
                        monitorInfo.Id, monitorInfo.Name, monitorInfo.Width, monitorInfo.Height,
                        monitorInfo.X, monitorInfo.Y, monitorInfo.IsPrimary);

                    outputIndex++;
                    globalOutputIndex++;
                }

                adapterIndex++;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate monitors via DXGI");
        }

        if (monitors.Count == 0)
        {
            _logger.LogWarning("No monitors found via DXGI, querying primary via GetSystemMetrics");
            var w = (uint)GetSystemMetrics(0); // SM_CXSCREEN
            var h = (uint)GetSystemMetrics(1); // SM_CYSCREEN
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

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
