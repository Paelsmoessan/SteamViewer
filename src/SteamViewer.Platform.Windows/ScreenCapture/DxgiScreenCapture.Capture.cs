using Microsoft.Extensions.Logging;
using SteamViewer.Client.Core.Capture;
using System.Drawing;
using System.Drawing.Imaging;

namespace SteamViewer.Platform.Windows.ScreenCapture;

public sealed partial class DxgiScreenCapture
{
    /// <summary>
    /// DXGI access-lost recovery state machine.
    /// Phase 1: while Secure Desktop is active, just poll until Default desktop is
    /// available (don't waste time on DuplicateOutput).
    /// Phase 2: Default desktop available - reset reinit clock, apply progressive
    /// backoff (250ms x20 / 2s x60 / 5s indefinitely), then attempt light reinit
    /// (DuplicateOutput only) falling back to full reinit (recreate D3D device).
    /// Never gives up (Microsoft Desktop Duplication sample pattern).
    /// State (consecutiveErrors / reinitStartTicks) is owned by the capture loop
    /// and passed by ref so the helper can mutate it across iterations.
    /// </summary>
    private void HandleAccessLost(Exception ex, ref int consecutiveErrors, ref long reinitStartTicks)
    {
        // DXGI_ERROR_ACCESS_LOST or resources released after failed reinitialize
        // Triggers: desktop switch (UAC Secure Desktop), hot-plug, lock screen, display mode change
        consecutiveErrors++;

        if (consecutiveErrors == 1)
            _logger.LogWarning("DXGI access lost: {Message}", ex.Message);

        // Phase 1: While Secure Desktop is active, don't waste time on DuplicateOutput.
        // Just poll until Default desktop is available (Microsoft/Sunshine pattern).
        if (!IsDefaultDesktopAvailable())
        {
            if (consecutiveErrors == 1 || consecutiveErrors % 20 == 0)
                _logger.LogInformation("DXGI waiting for Secure Desktop to exit (attempt {Count})", consecutiveErrors);
            // Wait up to 500ms, but wake immediately if NotifyDesktopAvailable() is called
            _desktopAvailableSignal.Wait(500);
            _desktopAvailableSignal.Reset();
            return; // Don't attempt reinit, don't count against timeout (was `continue` inline)
        }

        // Phase 2: Default desktop is back - reset clock and attempt reinit.
        // Start/reset the reinit clock on first attempt after desktop available.
        if (reinitStartTicks == 0L)
        {
            reinitStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            _logger.LogInformation("Default desktop available - starting DXGI reinit");
        }

        var reinitElapsedSec = (System.Diagnostics.Stopwatch.GetTimestamp() - reinitStartTicks)
            / (double)System.Diagnostics.Stopwatch.Frequency;

        // Progressive backoff: 250ms x20 (5s), then 2s x60 (2min), then 5s indefinitely.
        // Never give up (Microsoft Desktop Duplication sample pattern).
        int sleepMs;
        if (consecutiveErrors <= 20) sleepMs = 250;
        else if (consecutiveErrors <= 80) sleepMs = 2000;
        else sleepMs = 5000;

        if (consecutiveErrors > 1)
            Thread.Sleep(sleepMs);

        try
        {
            // Try light reinit first (just DuplicateOutput, keep D3D device).
            // Falls back to full reinit if light reinit fails.
            bool success = TryReinitDuplication();
            if (!success)
            {
                _logger.LogDebug("Light DXGI reinit failed, trying full reinitialize");
                Reinitialize();
            }
            // Success - reset for next desktop switch event
            consecutiveErrors = 0;
            reinitStartTicks = 0L;
            _logger.LogInformation("DXGI reinitialize succeeded after {Elapsed:F1}s ({Method})",
                reinitElapsedSec, success ? "light" : "full");
        }
        catch (Exception reinitEx)
        {
            if (consecutiveErrors <= 5 || consecutiveErrors % 20 == 0)
                _logger.LogWarning(reinitEx, "DXGI reinitialize failed (attempt {Count}, {Elapsed:F1}s)", consecutiveErrors, reinitElapsedSec);
        }
    }

    /// <summary>
    /// JPEG-encode a captured BGRA frame and fire the OnFrameCaptured event.
    /// Reuses the MemoryStream across frames to reduce GC pressure at 30 FPS.
    /// </summary>
    private void EncodeAndFireFrame(CapturedFrame frame, ImageCodecInfo jpegEncoder,
        EncoderParameters encoderParams, MemoryStream jpegStream, ref int frameCount)
    {
        unsafe
        {
            fixed (byte* ptr = frame.Data)
            {
                // BGRA from DXGI maps directly to Format32bppArgb (same byte layout on little-endian)
                using var bitmap = new Bitmap(frame.Width, frame.Height, frame.Stride,
                    PixelFormat.Format32bppArgb, (IntPtr)ptr);

                // Composite mouse cursor onto frame before encoding
                if (ShowCursor)
                    DrawCursorOnBitmap(bitmap);

                frameCount++;

                // Raw BGRA path - skip JPEG encode entirely (saves 15-30ms per frame).
                // Cursor is composited via DrawIconEx which writes directly into frame.Data.
                if (OnRawFrameCaptured != null)
                {
                    if (frameCount <= 3 || frameCount % 300 == 0)
                    {
                        _logger.LogDebug("DXGI raw frame #{Count}: {Size}b, {W}x{H}, stride={Stride}",
                            frameCount, frame.Data.Length, frame.Width, frame.Height, frame.Stride);
                    }
                    OnRawFrameCaptured.Invoke(frame.Data, frame.Width, frame.Height, frame.Stride);
                    return;
                }

                // Fallback: JPEG encode for base64 path
                jpegStream.SetLength(0);
                bitmap.Save(jpegStream, jpegEncoder, encoderParams);
                var jpegData = jpegStream.ToArray();

                if (frameCount <= 3 || frameCount % 300 == 0)
                {
                    _logger.LogDebug("DXGI frame #{Count}: {Size}b, {W}x{H}",
                        frameCount, jpegData.Length, frame.Width, frame.Height);
                }

                OnFrameCaptured?.Invoke(jpegData, frame.Width, frame.Height);
            }
        }
    }

    private static ImageCodecInfo? GetJpegEncoder()
    {
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
        {
            if (codec.FormatID == ImageFormat.Jpeg.Guid)
                return codec;
        }
        return null;
    }
}

/// <summary>
/// Typed signal for DXGI states that require reinit (ACCESS_LOST, null duplication
/// state, AcquireNextFrame failure). Caught by CaptureLoopInner and dispatched to
/// HandleAccessLost. Subclasses InvalidOperationException so any pre-typed catch
/// sites still work.
/// </summary>
internal sealed class DxgiAccessLostException : InvalidOperationException
{
    public DxgiAccessLostException(string message) : base(message) { }
    public DxgiAccessLostException(string message, Exception innerException) : base(message, innerException) { }
}
