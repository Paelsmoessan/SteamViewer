using Microsoft.JSInterop;
using SteamViewer.Client.Core.Capture;

namespace SteamViewer.App.Services;

/// <summary>
/// Provides JS interop for rendering decoded video frames to a canvas element.
/// </summary>
public sealed class CanvasRenderingService : IAsyncDisposable
{
    private readonly IJSRuntime _jsRuntime;
    private bool _initialized;
    private string? _canvasId;

    public CanvasRenderingService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    /// <summary>
    /// Initialize the canvas for rendering.
    /// </summary>
    /// <param name="canvasId">The HTML canvas element ID</param>
    public async Task InitializeAsync(string canvasId)
    {
        if (_initialized && _canvasId == canvasId)
        {
            return;
        }

        _canvasId = canvasId;
        _initialized = await _jsRuntime.InvokeAsync<bool>(
            "SteamViewerVideoDecoder.initialize",
            canvasId);

        if (!_initialized)
        {
            throw new InvalidOperationException($"Failed to initialize canvas '{canvasId}'");
        }
    }

    /// <summary>
    /// Render a decoded RGBA frame to the canvas.
    /// </summary>
    /// <param name="frame">The decoded frame containing RGBA data</param>
    public async Task RenderFrameAsync(DecodedFrame frame)
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("Canvas not initialized. Call InitializeAsync first.");
        }

        await _jsRuntime.InvokeVoidAsync(
            "SteamViewerVideoDecoder.renderRGBAFrame",
            frame.Data,
            frame.Width,
            frame.Height);
    }

    /// <summary>
    /// Render raw RGBA data to the canvas.
    /// </summary>
    public async Task RenderRgbaAsync(byte[] data, int width, int height)
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("Canvas not initialized. Call InitializeAsync first.");
        }

        await _jsRuntime.InvokeVoidAsync(
            "SteamViewerVideoDecoder.renderRGBAFrame",
            data,
            width,
            height);
    }

    public async ValueTask DisposeAsync()
    {
        if (_initialized)
        {
            try
            {
                await _jsRuntime.InvokeVoidAsync("SteamViewerVideoDecoder.close");
            }
            catch
            {
                // Ignore errors during disposal
            }

            _initialized = false;
        }
    }
}
