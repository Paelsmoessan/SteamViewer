using Microsoft.Extensions.Logging;
using SteamViewer.Client.Core.Capture;
using CoreGraphics;
using Foundation;
using System.Runtime.InteropServices;

namespace SteamViewer.Platform.macOS.ScreenCapture;

/// <summary>
/// macOS screen capture using CoreGraphics.
/// Captures the display as BGRA bitmap data.
/// </summary>
/// <remarks>
/// Note: This requires Screen Recording permission in System Preferences.
/// For production use, consider migrating to ScreenCaptureKit (macOS 12.3+)
/// for better performance and more features.
/// </remarks>
public sealed class MacScreenCapture : IScreenCapture
{
    private readonly ILogger<MacScreenCapture> _logger;
    private bool _disposed;
    private bool _isCapturing;
    private int _width;
    private int _height;
    private uint _displayId;

    public MacScreenCapture(ILogger<MacScreenCapture> logger)
    {
        _logger = logger;
    }

    public (int Width, int Height) Resolution => (_width, _height);

    public bool IsCapturing => _isCapturing;

    public Task InitializeAsync(uint monitorId, CancellationToken cancellationToken = default)
    {
        if (_isCapturing)
        {
            throw new InvalidOperationException("Already capturing");
        }

        _logger.LogInformation("Initializing macOS screen capture for monitor {MonitorId}", monitorId);

        // Get display ID
        var maxDisplays = 16u;
        var displayIds = new uint[maxDisplays];
        uint displayCount = 0;

        var result = CGDisplay.GetActiveDisplayList(maxDisplays, displayIds, out displayCount);

        if (result != CGError.Success)
        {
            throw new InvalidOperationException($"Failed to get display list: {result}");
        }

        if (monitorId >= displayCount)
        {
            throw new ArgumentOutOfRangeException(nameof(monitorId),
                $"Monitor {monitorId} not found. Available: {displayCount}");
        }

        _displayId = displayIds[monitorId];
        var bounds = CGDisplay.GetBounds(_displayId);

        _width = (int)bounds.Width;
        _height = (int)bounds.Height;

        _isCapturing = true;

        _logger.LogInformation("macOS screen capture initialized for display {DisplayId} ({Width}x{Height})",
            _displayId, _width, _height);

        return Task.CompletedTask;
    }

    public Task<CapturedFrame?> CaptureFrameAsync(CancellationToken cancellationToken = default)
    {
        if (!_isCapturing)
        {
            throw new InvalidOperationException("Not capturing");
        }

        try
        {
            // Capture the display using CoreGraphics
            using var image = CGDisplay.CreateImage(_displayId);

            if (image == null)
            {
                _logger.LogWarning("Failed to capture display image - check Screen Recording permission");
                return Task.FromResult<CapturedFrame?>(null);
            }

            // Get image dimensions
            var width = (int)image.Width;
            var height = (int)image.Height;
            var bitsPerPixel = (int)image.BitsPerPixel;
            var bytesPerRow = (int)image.BytesPerRow;

            // Get pixel data
            using var dataProvider = image.DataProvider;
            if (dataProvider == null)
            {
                _logger.LogWarning("Failed to get image data provider");
                return Task.FromResult<CapturedFrame?>(null);
            }

            using var data = dataProvider.CopyData();
            if (data == null)
            {
                _logger.LogWarning("Failed to copy image data");
                return Task.FromResult<CapturedFrame?>(null);
            }

            // Copy to managed array
            var frameData = new byte[data.Length];
            Marshal.Copy(data.Bytes, frameData, 0, (int)data.Length);

            var frame = new CapturedFrame
            {
                Data = frameData,
                Width = width,
                Height = height,
                Stride = bytesPerRow,
                Timestamp = DateTimeOffset.UtcNow
            };

            return Task.FromResult<CapturedFrame?>(frame);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error capturing frame");
            return Task.FromResult<CapturedFrame?>(null);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _isCapturing = false;

        _logger.LogDebug("macOS screen capture disposed");
    }
}
