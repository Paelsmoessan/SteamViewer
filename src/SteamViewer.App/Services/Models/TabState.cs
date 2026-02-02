namespace SteamViewer.App.Services.Models;

/// <summary>
/// Represents the UI state of a tab in a viewer window.
/// </summary>
public sealed class TabState
{
    /// <summary>
    /// The session ID this tab represents.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Display title for the tab.
    /// </summary>
    public string Title { get; set; } = "Remote";

    /// <summary>
    /// Icon to display on the tab (emoji or icon identifier).
    /// </summary>
    public string Icon { get; set; } = "monitor";

    /// <summary>
    /// Whether this tab is currently active (visible) in its window.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Whether the session is currently connected.
    /// </summary>
    public bool IsConnected { get; set; }

    /// <summary>
    /// Whether the remote peer is sharing their screen.
    /// </summary>
    public bool IsPeerSharing { get; set; }

    /// <summary>
    /// Last frame thumbnail for preview when tab is inactive.
    /// Base64-encoded JPEG, may be null if no frame received yet.
    /// </summary>
    public string? ThumbnailBase64 { get; set; }

    /// <summary>
    /// Timestamp of last frame received (for staleness detection).
    /// </summary>
    public DateTime? LastFrameTime { get; set; }
}

/// <summary>
/// Represents the state of a viewer window with its tabs.
/// </summary>
public sealed class WindowState
{
    /// <summary>
    /// Unique identifier for this window.
    /// </summary>
    public required string WindowId { get; init; }

    /// <summary>
    /// List of tab session IDs in this window, in display order.
    /// </summary>
    public List<string> TabIds { get; } = new();

    /// <summary>
    /// The currently active tab's session ID, or null if no tabs.
    /// </summary>
    public string? ActiveTabId { get; set; }

    /// <summary>
    /// Reference to the MAUI Window object.
    /// </summary>
    public Window? Window { get; set; }
}
