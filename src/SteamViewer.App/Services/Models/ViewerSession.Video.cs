using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SteamViewer.Client.Core.Video;
using System.Text.Json;

namespace SteamViewer.App.Services.Models;

// Video concerns for ViewerSession: FFmpeg decoder lifecycle, video-data + lossless
// frame handling, resolution negotiation, cursor + display commands.
public sealed partial class ViewerSession
{
    private FFmpegDecoder? _decoder;
    private int _decodeErrorCount;
    private int _lastDesiredWidth;
    private int _lastDesiredHeight;

#if WINDOWS
    private Services.NativeFrameBridge? _frameBridge;
#endif

    /// <summary>
    /// Raised when the first video frame is rendered via direct rendering.
    /// Used to dismiss the "Waiting for host screen" overlay.
    /// </summary>
    public event Action? OnVideoStarted;

    /// <summary>
    /// Raised when host sends capture dimensions (on first frame + capture change).
    /// Viewer should constrain canvas to this AR for 1:1 pixel mapping.
    /// </summary>
    public event Action<int, int>? OnCaptureInfoReceived;

    /// <summary>
    /// Host capture dimensions (for AR-aware canvas sizing).
    /// </summary>
    public int CaptureWidth { get; private set; }
    public int CaptureHeight { get; private set; }

    /// <summary>
    /// Toggle host cursor visibility.
    /// </summary>
    public Task SendToggleCursorAsync()
        => SendAsync(new { type = "toggleCursor" }, "toggleCursor");

    /// <summary>
    /// Request the host to switch which display is being captured.
    /// </summary>
    public Task SendSwitchDisplayAsync(int monitorId)
        => SendAsync(new { type = "switchDisplay", monitorId }, "switch display request");

    /// <summary>
    /// Send desired encode resolution to host. Host will downscale using Lanczos
    /// before encoding, so viewer receives frames at exact display size (zero scaling blur).
    /// Call on connect and on window resize (debounced).
    /// </summary>
    public async Task SendDesiredResolutionAsync(int width, int height)
    {
        if (_transport == null || !_transport.IsConnected) return;
        if (width <= 0 || height <= 0) return;
        _lastDesiredWidth = width;
        _lastDesiredHeight = height;

        try
        {
            var json = JsonSerializer.Serialize(new { type = "setResolution", width, height });
            await _transport.SendControlAsync(json);
            _logger.LogInformation("Session {SessionId}: Sent desired resolution {W}x{H}", SessionId, width, height);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session {SessionId}: Failed to send resolution", SessionId);
        }
    }

    /// <summary>
    /// Enable direct rendering to a visible DOM canvas element.
    /// Sets the render target in JS for SharedBuffer frames.
    /// </summary>
    public async Task<bool> TryEnableDirectRenderingAsync(string canvasId, IJSRuntime viewerJsRuntime)
    {
        try
        {
            // Initialize video session in JS
            await viewerJsRuntime.InvokeVoidAsync("SteamViewerVideo.initialize", SessionId);

            var result = await viewerJsRuntime.InvokeAsync<bool>(
                "SteamViewerVideo.setRenderTarget", SessionId, canvasId);

            if (result)
            {
                // Set DotNetRef for OnVideoStartedCallback
                _dotNetRef ??= DotNetObjectReference.Create(this);
                await viewerJsRuntime.InvokeVoidAsync("SteamViewerVideo.setDotNetRef", SessionId, _dotNetRef);

                _logger.LogInformation("Session {SessionId}: Direct rendering enabled → '{CanvasId}'", SessionId, canvasId);
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session {SessionId}: Failed to enable direct rendering", SessionId);
            return false;
        }
    }

    [JSInvokable]
    public void OnVideoStartedCallback()
    {
        _logger.LogInformation("Session {SessionId}: First video frame rendered", SessionId);
        OnVideoStarted?.Invoke();
    }

    private void HandleVideoData(byte[] data, int length)
    {
        if (_decoder == null) return;

        try
        {
            var result = _decoder.DecodeFrame(data, length);
            if (result is var (bgraData, width, height, stride))
            {
#if WINDOWS
                // Push decoded BGRA frame to JS canvas via SharedBuffer
                if (_frameBridge?.IsInitialized == true)
                {
                    _frameBridge.PushRawFrame(bgraData, width, height, stride, SessionId);
                }
#endif

                // Check if we should request a lossless frame (input idle)
                if (ShouldRequestLosslessFrame(
                        _losslessActive, _losslessRequestPending, IsSecureDesktopActive,
                        (long)(DateTime.UtcNow - _lastInputTime).TotalMilliseconds))
                {
                    RequestLosslessFrame();
                }
            }
        }
        catch (Exception ex)
        {
            if (_decodeErrorCount++ % 300 == 0)
                _logger.LogWarning(ex, "Session {SessionId}: Decode error (sample)", SessionId);
        }
    }

    // Gate: viewer requests a lossless QOI snapshot only when no input has happened
    // recently AND a lossless frame isn't already active or in-flight AND the host
    // isn't on Secure Desktop (which has its own delivery path). Pure-function shape
    // so it is unit-testable without a ViewerSession instance.
    internal static bool ShouldRequestLosslessFrame(
        bool losslessActive, bool requestPending, bool secureDeskActive, long elapsedMsSinceLastInput)
        => !losslessActive && !requestPending && !secureDeskActive && elapsedMsSinceLastInput > 150;

    private async void RequestLosslessFrame()
    {
        if (_transport == null || !_transport.IsConnected) return;

        // Use decoder dimensions — matches H.264 encode resolution (8-aligned)
        // This ensures lossless and H.264 frames are identical size → no canvas resize
        var w = _decoder?.Width ?? 0;
        var h = _decoder?.Height ?? 0;
        if (w <= 0 || h <= 0) return;

        _losslessRequestPending = true;
        try
        {
            var json = JsonSerializer.Serialize(new { type = "requestLosslessFrame", width = w, height = h });
            await _transport.SendControlAsync(json);
        }
        catch (Exception ex)
        {
            _losslessRequestPending = false;
            _logger.LogWarning(ex, "Session {SessionId}: Failed to request lossless frame", SessionId);
        }
    }

    private void HandleLosslessFrame(byte[] qoiData, int length)
    {
        _losslessRequestPending = false;

        // Discard if input resumed while frame was in-flight (race: encode takes 50-100ms)
        if ((DateTime.UtcNow - _lastInputTime).TotalMilliseconds < 150)
            return;

        _losslessActive = true;

        try
        {
            // Decode QOI to BGRA
            var actualData = qoiData;
            if (length < qoiData.Length)
            {
                actualData = new byte[length];
                Buffer.BlockCopy(qoiData, 0, actualData, 0, length);
            }

            var bgra = QoiCodec.Decode(actualData, out int w, out int h);

#if WINDOWS
            // Push lossless BGRA to JS canvas via SharedBuffer with lossless flag
            if (_frameBridge?.IsInitialized == true)
            {
                _frameBridge.PushLosslessFrame(bgra, w, h, w * 4, SessionId);
            }
#endif

            _logger.LogInformation("Session {SessionId}: Lossless frame rendered: {W}x{H}, QOI={Size}KB",
                SessionId, w, h, length / 1024);
        }
        catch (Exception ex)
        {
            _losslessActive = false;
            _logger.LogWarning(ex, "Session {SessionId}: Failed to decode/render lossless frame", SessionId);
        }
    }
}
