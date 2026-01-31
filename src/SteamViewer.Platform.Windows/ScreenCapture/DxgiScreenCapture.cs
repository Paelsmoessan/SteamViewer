using Microsoft.Extensions.Logging;
using SteamViewer.Client.Core.Capture;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using System.Runtime.InteropServices;
using DXGIFactory = Vortice.DXGI.DXGI;

namespace SteamViewer.Platform.Windows.ScreenCapture;

/// <summary>
/// Windows screen capture using DXGI Desktop Duplication API.
/// GPU-accelerated for high performance.
/// </summary>
public sealed class DxgiScreenCapture : IScreenCapture
{
    private readonly ILogger<DxgiScreenCapture> _logger;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingTexture;
    private bool _disposed;
    private bool _isCapturing;
    private int _width;
    private int _height;
    private string? _monitorId;

    public DxgiScreenCapture(ILogger<DxgiScreenCapture> logger)
    {
        _logger = logger;
    }

    public (int Width, int Height) Resolution => (_width, _height);

    public bool IsCapturing => _isCapturing;

    public async Task InitializeAsync(uint monitorId, CancellationToken cancellationToken = default)
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

        await Task.CompletedTask;
    }

    public async Task<CapturedFrame?> CaptureFrameAsync(CancellationToken cancellationToken = default)
    {
        if (!_isCapturing || _duplication == null || _device == null || _context == null || _stagingTexture == null)
        {
            throw new InvalidOperationException("Not capturing");
        }

        try
        {
            // Try to acquire next frame (0ms timeout for non-blocking)
            var acquireResult = _duplication.AcquireNextFrame(0, out var frameInfo, out var desktopResource);

            if (acquireResult == Result.WaitTimeout)
            {
                // No new frame available (desktop unchanged)
                return null;
            }

            if (acquireResult == DxgiErrors.ErrorAccessLost)
            {
                _logger.LogWarning("Desktop duplication access lost, needs reinitialize");
                throw new InvalidOperationException("Desktop duplication access lost");
            }

            if (acquireResult.Failure)
            {
                _logger.LogWarning("Failed to acquire next frame: {Error}", acquireResult.Description);
                return null;
            }

            try
            {
                // Query for ID3D11Texture2D
                using var texture = desktopResource!.QueryInterface<ID3D11Texture2D>();

                // Copy to staging texture
                _context.CopyResource(_stagingTexture, texture);

                // Map staging texture to read pixels
                var mappedResource = _context.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

                try
                {
                    var stride = mappedResource.RowPitch;
                    var dataSize = _height * stride;
                    var frameData = new byte[dataSize];

                    // Copy pixel data row by row (handles different strides)
                    unsafe
                    {
                        var srcPtr = (byte*)mappedResource.DataPointer;
                        for (var y = 0; y < _height; y++)
                        {
                            Marshal.Copy((IntPtr)(srcPtr + y * stride), frameData, y * stride, stride);
                        }
                    }

                    var timestamp = DateTimeOffset.UtcNow;

                    return new CapturedFrame
                    {
                        Data = frameData,
                        Width = _width,
                        Height = _height,
                        Stride = stride,
                        Timestamp = timestamp
                    };
                }
                finally
                {
                    _context.Unmap(_stagingTexture, 0);
                }
            }
            finally
            {
                desktopResource?.Dispose();
                _duplication.ReleaseFrame();
            }
        }
        catch (SharpGen.Runtime.SharpGenException ex) when (ex.HResult == DxgiErrors.ErrorAccessLost.Code)
        {
            _logger.LogWarning("Desktop duplication access lost");
            throw new InvalidOperationException("Desktop duplication access lost", ex);
        }
    }

    private static (uint AdapterIndex, uint OutputIndex) ParseMonitorId(uint monitorId)
    {
        // For simplicity, treat the ID as output index on adapter 0
        // In a full implementation, you'd parse "adapterN_outputM" format
        return (0, monitorId);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _isCapturing = false;

        _stagingTexture?.Dispose();
        _stagingTexture = null;

        _duplication?.Dispose();
        _duplication = null;

        _context?.Dispose();
        _context = null;

        _device?.Dispose();
        _device = null;

        _logger.LogDebug("DXGI screen capture disposed");
    }
}

/// <summary>
/// DXGI error codes
/// </summary>
internal static class DxgiErrors
{
    public static readonly Result ErrorAccessLost = new Result(unchecked((int)0x887A0026));
}
