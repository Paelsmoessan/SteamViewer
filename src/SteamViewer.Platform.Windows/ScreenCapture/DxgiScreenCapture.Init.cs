using Microsoft.Extensions.Logging;
using SharpGen.Runtime;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using DXGIFactory = Vortice.DXGI.DXGI;

namespace SteamViewer.Platform.Windows.ScreenCapture;

public sealed partial class DxgiScreenCapture
{
    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;
    private const uint DESKTOP_GENERIC_ALL = 0x000F01FF;
    private const int UOI_NAME = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetUserObjectInformationW(IntPtr hObj, int nIndex, byte[] pvInfo, int nLength, out int lpnLengthNeeded);

    public Task InitializeAsync(uint monitorId, CancellationToken cancellationToken = default)
    {
        if (_isCapturing)
        {
            throw new InvalidOperationException("Already capturing");
        }

        _logger.LogInformation("Initializing DXGI screen capture for monitor {MonitorId}", monitorId);

        // Parse monitor ID - format is "adapterN_outputM" but we accept numeric for simple cases
        var (adapterIndex, outputIndex) = ParseMonitorId(monitorId);

        // Create DXGI factory
        using var factory = DXGIFactory.CreateDXGIFactory1<IDXGIFactory1>();

        // Get the specified adapter
        var adapterResult = factory.EnumAdapters1((int)adapterIndex, out var adapter);
        if (adapterResult.Failure || adapter == null)
        {
            throw new InvalidOperationException($"Failed to get DXGI adapter {adapterIndex}");
        }
        using var adapterDisposer = adapter;

        // Get the specified output
        var outputResult = adapter.EnumOutputs((int)outputIndex, out var output);
        if (outputResult.Failure || output == null)
        {
            throw new InvalidOperationException($"Failed to get DXGI output {outputIndex}");
        }
        using var outputDisposer = output;

        // Get output description
        var desc = output.Description;
        var rect = desc.DesktopCoordinates;
        _width = rect.Right - rect.Left;
        _height = rect.Bottom - rect.Top;
        _monitorX = rect.Left;
        _monitorY = rect.Top;
        _monitorId = $"adapter{adapterIndex}_output{outputIndex}";

        _logger.LogInformation("Monitor: {Name}, Resolution: {Width}x{Height}",
            new string(desc.DeviceName).TrimEnd('\0'), _width, _height);

        // Create D3D11 device on this adapter
        var featureLevels = new[] { FeatureLevel.Level_11_0 };
        var createResult = D3D11.D3D11CreateDevice(
            adapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out _device,
            out var featureLevel,
            out _context);

        if (createResult.Failure || _device == null || _context == null)
        {
            throw new InvalidOperationException("Failed to create D3D11 device");
        }

        _logger.LogDebug("Created D3D11 device with feature level {FeatureLevel}", featureLevel);

        // Get IDXGIOutput1 for desktop duplication
        using var output1 = output.QueryInterface<IDXGIOutput1>();

        // Sync thread to current input desktop before DuplicateOutput
        // (matches Microsoft sample + Sunshine pattern - prevents E_ACCESSDENIED after SD)
        SyncThreadDesktop();

        // Create desktop duplication
        try
        {
            _duplication = output1.DuplicateOutput(_device);
        }
        catch (SharpGenException ex)
        {
            throw new InvalidOperationException($"Failed to create desktop duplication: {ex.Message}", ex);
        }

        if (_duplication == null)
        {
            throw new InvalidOperationException("Failed to create desktop duplication: result was null");
        }

        // Create staging texture for CPU access
        var stagingDesc = new Texture2DDescription
        {
            Width = _width,
            Height = _height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        };

        _stagingTexture = _device.CreateTexture2D(stagingDesc);

        _isCapturing = true;
        _logger.LogInformation("DXGI desktop duplication initialized for {MonitorId} ({Width}x{Height})",
            _monitorId, _width, _height);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Release and reinitialize DXGI resources on the same output.
    /// Called on DXGI_ERROR_ACCESS_LOST (display mode change, hot-plug, lock screen).
    /// </summary>
    private void Reinitialize()
    {
        ReleaseResources();
        InitializeAsync(_currentOutputIndex).GetAwaiter().GetResult();
        _logger.LogInformation("DXGI reinitialized on output {Output} ({W}x{H})",
            _currentOutputIndex, _width, _height);
    }

    /// <summary>
    /// Light reinit: only retry DuplicateOutput on existing D3D device.
    /// Much faster than full Reinitialize() which recreates the D3D device.
    /// Syncs thread desktop before attempting DuplicateOutput (Microsoft/Sunshine pattern).
    /// Returns true on success.
    /// </summary>
    private bool TryReinitDuplication()
    {
        if (_device == null)
            return false;

        try
        {
            // Sync thread to current input desktop before DuplicateOutput
            // (Microsoft Desktop Duplication sample + Sunshine pattern)
            if (!SyncThreadDesktop())
                return false;

            _duplication?.Dispose();
            _duplication = null;

            var (adapterIndex, outputIndex) = ParseMonitorId(_currentOutputIndex);
            using var factory = DXGIFactory.CreateDXGIFactory1<IDXGIFactory1>();
            var adapterResult = factory.EnumAdapters1((int)adapterIndex, out var adapter);
            if (adapterResult.Failure || adapter == null)
                return false;

            using (adapter)
            {
                var outputResult = adapter.EnumOutputs((int)outputIndex, out var output);
                if (outputResult.Failure || output == null)
                    return false;

                using (output)
                using (var output1 = output.QueryInterface<IDXGIOutput1>())
                {
                    _duplication = output1.DuplicateOutput(_device);
                    return _duplication != null;
                }
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sync the capture thread to the current input desktop. Must be called before
    /// DuplicateOutput - without this, the thread may be stuck on a stale/Secure
    /// Desktop and DuplicateOutput will fail with E_ACCESSDENIED. Pattern from
    /// Microsoft Desktop Duplication sample and Sunshine.
    /// </summary>
    private bool SyncThreadDesktop()
    {
        var hDesk = OpenInputDesktop(0, false, DESKTOP_GENERIC_ALL);
        if (hDesk == IntPtr.Zero)
            return false;

        var result = SetThreadDesktop(hDesk);
        CloseDesktop(hDesk);
        return result;
    }

    /// <summary>
    /// Check if the current input desktop is the normal Default desktop (not
    /// Winlogon/Secure Desktop). Returns false while Secure Desktop is active -
    /// caller should skip DuplicateOutput attempts.
    /// </summary>
    private bool IsDefaultDesktopAvailable()
    {
        var hDesk = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (hDesk == IntPtr.Zero)
            return false;

        var name = GetDesktopName(hDesk);
        CloseDesktop(hDesk);
        return !string.Equals(name, "Winlogon", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetDesktopName(IntPtr hDesktop)
    {
        var buffer = new byte[256];
        if (GetUserObjectInformationW(hDesktop, UOI_NAME, buffer, buffer.Length, out _))
            return System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        return string.Empty;
    }

    /// <summary>
    /// Release all DXGI/D3D11 resources without disposing the outer object.
    /// Safe to call multiple times (null checks).
    /// </summary>
    private void ReleaseResources()
    {
        _isCapturing = false;

        _stagingTexture?.Dispose();
        _stagingTexture = null;

        _duplication?.Dispose();
        _duplication = null;

        _context?.Dispose();
        _context = null;

        _device?.Dispose();
        _device = null;
    }
}
