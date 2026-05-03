namespace SteamViewer.Client.Core.Capture;

/// <summary>
/// Represents a captured frame from the screen.
/// </summary>
public sealed class CapturedFrame : IDisposable
{
    /// <summary>Raw pixel data in BGRA format</summary>
    public required byte[] Data { get; init; }

    /// <summary>Frame width in pixels</summary>
    public required int Width { get; init; }

    /// <summary>Frame height in pixels</summary>
    public required int Height { get; init; }

    /// <summary>Bytes per row (stride)</summary>
    public required int Stride { get; init; }

    /// <summary>Timestamp when frame was captured</summary>
    public required DateTimeOffset Timestamp { get; init; }

    public void Dispose()
    {
        // Frame data can be pooled in the future
    }
}

/// <summary>
/// Platform-agnostic interface for screen capture.
/// </summary>
public interface IScreenCapture : IDisposable
{
    /// <summary>
    /// Initialize the screen capture for a specific monitor.
    /// </summary>
    /// <param name="monitorId">The monitor ID to capture</param>
    Task InitializeAsync(uint monitorId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Capture the next frame.
    /// </summary>
    /// <returns>The captured frame, or null if no new frame is available</returns>
    Task<CapturedFrame?> CaptureFrameAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the current capture resolution.
    /// </summary>
    (int Width, int Height) Resolution { get; }

    /// <summary>
    /// Whether capture is currently active.
    /// </summary>
    bool IsCapturing { get; }
}
