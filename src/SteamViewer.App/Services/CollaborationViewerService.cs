using Microsoft.Extensions.Logging;
using SteamViewer.Common.Protocol;

namespace SteamViewer.App.Services;

/// <summary>
/// Service that manages the collaboration viewer window.
/// Opens screens in a separate window with tabs for multiple participants.
/// </summary>
public sealed class CollaborationViewerService
{
    private readonly ILogger<CollaborationViewerService> _logger;
    private Window? _viewerWindow;

    /// <summary>Raised when viewer window should open.</summary>
    public event Action<string>? OnViewerOpenRequested;

    /// <summary>Raised when the viewer window is closed.</summary>
    public event Action? OnViewerClosed;

    /// <summary>Raised when active tab changes.</summary>
    public event Action<string>? OnActiveTabChanged;

    /// <summary>Whether the viewer window is open.</summary>
    public bool IsViewerOpen => _viewerWindow != null;

    /// <summary>The currently active peer ID being viewed.</summary>
    public string? ActivePeerId { get; private set; }

    /// <summary>Initial peer ID to show when window opens.</summary>
    public string? InitialPeerId { get; private set; }

    /// <summary>All sharing participants (set by Collaboration.razor).</summary>
    public IEnumerable<ParticipantInfo> SharingParticipants { get; set; } = Enumerable.Empty<ParticipantInfo>();

    /// <summary>Our client ID.</summary>
    public string? MyClientId { get; set; }

    /// <summary>Function to render peer video to canvas.</summary>
    public Func<string, string, Task<bool>>? RenderPeerToCanvas { get; set; }

    public CollaborationViewerService(ILogger<CollaborationViewerService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Open the viewer window for a specific peer.
    /// </summary>
    public void OpenViewer(string peerId)
    {
        InitialPeerId = peerId;
        ActivePeerId = peerId;
        OnViewerOpenRequested?.Invoke(peerId);
        _logger.LogInformation("Collaboration viewer open requested for peer {PeerId}", peerId);
    }

    /// <summary>
    /// Register the viewer window (called by App.xaml.cs).
    /// </summary>
    public void RegisterViewerWindow(Window window)
    {
        _viewerWindow = window;
        _logger.LogInformation("Collaboration viewer window registered");
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
            ActivePeerId = null;
            InitialPeerId = null;
            OnViewerClosed?.Invoke();
            _logger.LogInformation("Collaboration viewer window closed");
        }
    }

    /// <summary>
    /// Called when the viewer window is destroyed externally.
    /// </summary>
    public void NotifyViewerClosed()
    {
        _viewerWindow = null;
        ActivePeerId = null;
        InitialPeerId = null;
        OnViewerClosed?.Invoke();
        _logger.LogInformation("Collaboration viewer window closed notification");
    }

    /// <summary>
    /// Switch to a different tab/peer.
    /// </summary>
    public void SwitchTab(string peerId)
    {
        ActivePeerId = peerId;
        OnActiveTabChanged?.Invoke(peerId);
    }
}
