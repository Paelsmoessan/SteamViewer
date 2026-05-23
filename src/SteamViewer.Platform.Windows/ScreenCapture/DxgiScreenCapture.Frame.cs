using Microsoft.Extensions.Logging;
using SharpGen.Runtime;
using SteamViewer.Client.Core.Capture;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace SteamViewer.Platform.Windows.ScreenCapture;

public sealed partial class DxgiScreenCapture
{
    public Task<CapturedFrame?> CaptureFrameAsync(CancellationToken cancellationToken = default)
    {
        // Thin async wrapper for IScreenCapture interface contract. Actual work is
        // synchronous COM calls - avoid async state machine overhead.
        return Task.FromResult(CaptureFrameCore());
    }

    /// <summary>
    /// Synchronous frame capture. Called directly from CaptureLoopInner (dedicated
    /// thread) and wrapped in Task.FromResult for the async IScreenCapture interface.
    /// Keeping this non-async ensures exceptions propagate directly to the capture
    /// loop's catch blocks (async state machines can interfere with exception
    /// propagation via .GetAwaiter().GetResult()).
    /// </summary>
    private CapturedFrame? CaptureFrameCore()
    {
        if (!_isCapturing || _duplication == null || _device == null || _context == null || _stagingTexture == null)
        {
            throw new DxgiAccessLostException("Not capturing");
        }

        try
        {
            // Try to acquire next frame (0ms timeout for non-blocking)
            var acquireResult = _duplication.AcquireNextFrame(0, out var frameInfo, out var desktopResource);

            if (acquireResult == Result.WaitTimeout || acquireResult == DxgiErrors.ErrorWaitTimeout)
            {
                // No new frame available (desktop unchanged)
                return null;
            }

            if (acquireResult == DxgiErrors.ErrorAccessLost)
            {
                // First fire stays at WARN (boundary marker); subsequent fires during Phase 1
                // SD-wait demote to LogDebug to avoid drowning the log at ~250ms cadence.
                // Counter resets in the success path below when AcquireNextFrame next yields
                // a frame, so a future ACCESS_LOST after recovery starts fresh at WARN.
                var fireCount = ++_consecutiveAccessLostLogs;
                if (fireCount == 1)
                    _logger.LogWarning("Desktop duplication access lost, needs reinitialize");
                else if (fireCount == 2 || fireCount % 50 == 0)
                    _logger.LogDebug("Desktop duplication still ACCESS_LOST (fire #{Count}) - Phase 1 SD-wait", fireCount);
                throw new DxgiAccessLostException("Desktop duplication access lost");
            }

            if (acquireResult.Failure)
            {
                _logger.LogWarning("AcquireNextFrame failed: {Error} (0x{Code:X8}) - needs reinitialize",
                    acquireResult.Description, acquireResult.Code);
                throw new DxgiAccessLostException($"Desktop duplication access lost (0x{acquireResult.Code:X8})");
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

                    // Reuse frame buffer across frames - avoids ~8MB alloc per frame
                    if (_frameBuffer == null || _frameBuffer.Length < dataSize)
                        _frameBuffer = new byte[dataSize];

                    CopyPixelsToFrameBuffer(mappedResource, stride);

                    // Successful capture - reset access-lost log counter so a future ACCESS_LOST
                    // gets fresh WARN at the boundary instead of staying demoted.
                    if (_consecutiveAccessLostLogs > 0)
                    {
                        var prev = _consecutiveAccessLostLogs;
                        _consecutiveAccessLostLogs = 0;
                        _logger.LogInformation("Desktop duplication access restored (was lost for {Count} log fires)", prev);
                    }

                    return new CapturedFrame
                    {
                        Data = _frameBuffer,
                        Width = _width,
                        Height = _height,
                        Stride = stride,
                        Timestamp = DateTimeOffset.UtcNow
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
            var fireCount = ++_consecutiveAccessLostLogs;
            if (fireCount == 1)
                _logger.LogWarning("Desktop duplication access lost (via SharpGenException)");
            else if (fireCount == 2 || fireCount % 50 == 0)
                _logger.LogDebug("Desktop duplication still ACCESS_LOST via SharpGenException (fire #{Count})", fireCount);
            throw new DxgiAccessLostException("Desktop duplication access lost", ex);
        }
    }

    /// <summary>
    /// Copy mapped DXGI pixel data into _frameBuffer. Single memcpy when stride
    /// matches the expected BGRA stride (_width * 4); row-by-row copy when DXGI
    /// hands back a stride with alignment padding. Caller must ensure _frameBuffer
    /// has capacity for _height * stride bytes before calling.
    /// </summary>
    private unsafe void CopyPixelsToFrameBuffer(MappedSubresource mappedResource, int stride)
    {
        var srcPtr = (byte*)mappedResource.DataPointer;
        var expectedStride = _width * 4; // BGRA = 4 bytes per pixel
        var dataSize = _height * stride;
        if (stride == expectedStride)
        {
            // Stride matches width - single memcpy (fastest)
            Marshal.Copy((IntPtr)srcPtr, _frameBuffer!, 0, dataSize);
        }
        else
        {
            for (var y = 0; y < _height; y++)
            {
                Marshal.Copy((IntPtr)(srcPtr + y * stride), _frameBuffer!, y * stride, stride);
            }
        }
    }

    private static (uint AdapterIndex, uint OutputIndex) ParseMonitorId(uint monitorId)
    {
        // For IDs >= 100, decode adapter and output index (from EnumerateMonitors)
        // For IDs < 100, treat as output index on adapter 0
        if (monitorId >= 100)
        {
            return (monitorId / 100, monitorId % 100);
        }
        return (0, monitorId);
    }
}
