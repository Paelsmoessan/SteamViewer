using Microsoft.Extensions.Logging;
using SteamViewer.Client.Core.Capture;

namespace SteamViewer.App.Services;

/// <summary>
/// Service that relays video frames and input events between windows.
/// Allows the remote viewer to run in a separate window while WebRTC stays in the main window.
/// </summary>
public sealed class RemoteViewerService
{
    private readonly ILogger<RemoteViewerService> _logger;
    private Window? _viewerWindow;

    /// <summary>
    /// Raised when a video frame is received (for viewer window to render).
    /// </summary>
    public event Action<DecodedFrame>? OnVideoFrame;

    /// <summary>
    /// Raised when a JPEG frame is received (for viewer window to render).
    /// </summary>
    public event Action<JpegFrame>? OnJpegFrame;

    /// <summary>
    /// Raised when an input event is received from the viewer window.
    /// </summary>
    public event Action<InputEventData>? OnInputEvent;

    /// <summary>
    /// Raised when the viewer window is closed.
    /// </summary>
    public event Action? OnViewerClosed;

    /// <summary>
    /// Raised when the viewer window requests to open.
    /// </summary>
    public event Action? OnViewerOpenRequested;

    /// <summary>
    /// Raised when the viewer window requests stats relay start/stop.
    /// Home.razor handles this since the WebRTC session lives in its JS context.
    /// </summary>
    public event Action<bool>? OnStatsToggleRequested;

    /// <summary>
    /// Raised when stats data is available from the host's JS context (relayed to viewer overlay).
    /// </summary>
    public event Action<string>? OnStatsUpdate;

    /// <summary>
    /// Whether the viewer window is currently open.
    /// </summary>
    public bool IsViewerOpen => _viewerWindow != null;

    /// <summary>
    /// The peer ID being viewed.
    /// </summary>
    public string? PeerId { get; private set; }

    /// <summary>
    /// Canvas dimensions for proper input scaling.
    /// </summary>
    public (int Width, int Height) CanvasDimensions { get; set; } = (1920, 1080);

    public RemoteViewerService(ILogger<RemoteViewerService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Open the viewer in a new window.
    /// </summary>
    public void OpenViewer(string peerId)
    {
        if (_viewerWindow != null)
        {
            _logger.LogWarning("Viewer window already open");
            return;
        }

        PeerId = peerId;
        OnViewerOpenRequested?.Invoke();
        _logger.LogInformation("Viewer window open requested for peer {PeerId}", peerId);
    }

    /// <summary>
    /// Register the viewer window (called by App.xaml.cs).
    /// </summary>
    public void RegisterViewerWindow(Window window)
    {
        _viewerWindow = window;
        _logger.LogInformation("Viewer window registered");
    }

    /// <summary>
    /// Close the viewer window.
    /// </summary>
    public void CloseViewer()
    {
        if (_viewerWindow != null)
        {
            Application.Current?.CloseWindow(_viewerWindow);
            _viewerWindow = null;
            PeerId = null;
            OnViewerClosed?.Invoke();
            _logger.LogInformation("Viewer window closed");
        }
    }

    /// <summary>
    /// Called when the viewer window is destroyed externally.
    /// </summary>
    public void NotifyViewerClosed()
    {
        _viewerWindow = null;
        PeerId = null;
        OnViewerClosed?.Invoke();
        _logger.LogInformation("Viewer window closed notification received");
    }

    /// <summary>
    /// Send a video frame to the viewer window.
    /// </summary>
    public void SendVideoFrame(DecodedFrame frame)
    {
        OnVideoFrame?.Invoke(frame);
    }

    /// <summary>
    /// Send a JPEG frame to the viewer window.
    /// </summary>
    public void SendJpegFrame(string base64Data, int width, int height)
    {
        OnJpegFrame?.Invoke(new JpegFrame(base64Data, width, height));
    }

    /// <summary>
    /// Send an input event from the viewer window to the main window.
    /// </summary>
    public void SendInputEvent(InputEventData inputEvent)
    {
        OnInputEvent?.Invoke(inputEvent);
    }

    /// <summary>
    /// Request stats relay start/stop (routed to Home.razor's JS context).
    /// </summary>
    public void RequestStatsToggle(bool enable)
    {
        OnStatsToggleRequested?.Invoke(enable);
    }

    /// <summary>
    /// Send stats update data from Home.razor to viewer window.
    /// </summary>
    public void SendStatsUpdate(string json)
    {
        OnStatsUpdate?.Invoke(json);
    }
}

/// <summary>
/// Input event data for cross-window relay.
/// </summary>
public record InputEventData
{
    public InputEventType Type { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public int Button { get; init; }
    public double DeltaX { get; init; }
    public double DeltaY { get; init; }
    public string? Key { get; init; }
    public bool Ctrl { get; init; }
    public bool Shift { get; init; }
    public bool Alt { get; init; }
    /// <summary>
    /// Capture width in pixels (canvas dimensions for coordinate scaling).
    /// </summary>
    public int CaptureWidth { get; init; }
    /// <summary>
    /// Capture height in pixels (canvas dimensions for coordinate scaling).
    /// </summary>
    public int CaptureHeight { get; init; }
}

public enum InputEventType
{
    MouseMove,
    MouseDown,
    MouseUp,
    Wheel,
    KeyDown,
    KeyUp
}

/// <summary>
/// JPEG frame data for cross-window video relay.
/// </summary>
public record JpegFrame(string Base64Data, int Width, int Height);
