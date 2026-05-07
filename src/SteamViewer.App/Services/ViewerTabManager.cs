using Microsoft.Extensions.Logging;
using SteamViewer.App.Services.Models;
using System.Collections.Concurrent;

namespace SteamViewer.App.Services;

/// <summary>
/// Manages tab state across multiple viewer windows.
/// Coordinates which sessions are displayed in which windows.
/// </summary>
public sealed class ViewerTabManager
{
    private readonly ILogger<ViewerTabManager> _logger;
    private readonly ViewerSessionManager _sessionManager;

    private readonly ConcurrentDictionary<string, WindowState> _windows = new();
    private readonly ConcurrentDictionary<string, TabState> _tabs = new();
    private readonly ConcurrentQueue<string> _pendingWindowIds = new();
    private int _windowCounter;

    /// <summary>
    /// Raised when tabs change in any window.
    /// </summary>
    public event Action<string>? OnTabsChanged;

    /// <summary>
    /// Raised when the active tab changes in a window.
    /// </summary>
    public event Action<string, string?>? OnActiveTabChanged;

    /// <summary>
    /// Raised when a new window should be created (for tab detach).
    /// Parameters: windowId, x, y position.
    /// </summary>
    public event Action<string, int, int>? OnWindowRequested;

    /// <summary>
    /// Raised when a window should be closed (last tab removed).
    /// </summary>
    public event Action<string>? OnWindowCloseRequested;

    public ViewerTabManager(
        ILogger<ViewerTabManager> logger,
        ViewerSessionManager sessionManager)
    {
        _logger = logger;
        _sessionManager = sessionManager;

        // Subscribe to session events
        _sessionManager.OnSessionCreated += HandleSessionCreated;
        _sessionManager.OnSessionRemoved += HandleSessionRemoved;
        _sessionManager.OnSessionStateChanged += HandleSessionStateChanged;
    }

    /// <summary>
    /// Create a new window and return its ID.
    /// </summary>
    public string CreateWindow(Window? mauiWindow = null)
    {
        var windowId = $"viewer-{Interlocked.Increment(ref _windowCounter)}";
        var windowState = new WindowState
        {
            WindowId = windowId,
            Window = mauiWindow
        };

        _windows[windowId] = windowState;
        _logger.LogInformation("Created window {WindowId}", windowId);

        return windowId;
    }

    /// <summary>
    /// Register a MAUI Window with a window ID.
    /// </summary>
    public void RegisterWindow(string windowId, Window mauiWindow)
    {
        if (_windows.TryGetValue(windowId, out var state))
        {
            state.Window = mauiWindow;
            _logger.LogDebug("Registered MAUI window for {WindowId}", windowId);
        }
    }

    /// <summary>
    /// Remove a window (called when window is closed).
    /// </summary>
    public async Task RemoveWindowAsync(string windowId)
    {
        if (_windows.TryRemove(windowId, out var state))
        {
            // Remove all tabs in this window (disconnect sessions)
            foreach (var tabId in state.TabIds.ToList())
            {
                await RemoveTabAsync(windowId, tabId);
            }

            _logger.LogInformation("Removed window {WindowId}", windowId);
        }
    }

    /// <summary>
    /// Get all windows.
    /// </summary>
    public IReadOnlyCollection<WindowState> GetWindows() => _windows.Values.ToList();

    /// <summary>
    /// Get a specific window state.
    /// </summary>
    public WindowState? GetWindow(string windowId)
    {
        return _windows.TryGetValue(windowId, out var state) ? state : null;
    }

    /// <summary>
    /// Get all tabs for a specific window.
    /// </summary>
    public IReadOnlyList<TabState> GetTabs(string windowId)
    {
        if (!_windows.TryGetValue(windowId, out var window))
        {
            return Array.Empty<TabState>();
        }

        return window.TabIds
            .Select(id => _tabs.TryGetValue(id, out var tab) ? tab : null)
            .Where(t => t != null)
            .Cast<TabState>()
            .ToList();
    }

    /// <summary>
    /// Get the active tab for a window.
    /// </summary>
    public TabState? GetActiveTab(string windowId)
    {
        if (!_windows.TryGetValue(windowId, out var window))
        {
            return null;
        }

        if (window.ActiveTabId != null && _tabs.TryGetValue(window.ActiveTabId, out var tab))
        {
            return tab;
        }

        return null;
    }

    /// <summary>
    /// Add a tab to a window for an existing session.
    /// </summary>
    public void AddTab(string windowId, string sessionId, string? title = null)
    {
        if (!_windows.TryGetValue(windowId, out var window))
        {
            _logger.LogWarning("Cannot add tab: window {WindowId} not found", windowId);
            return;
        }

        if (window.TabIds.Contains(sessionId))
        {
            _logger.LogDebug("Tab {SessionId} already in window {WindowId}", sessionId, windowId);
            return;
        }

        // Create tab state if it doesn't exist
        if (!_tabs.ContainsKey(sessionId))
        {
            var session = _sessionManager.GetSession(sessionId);
            _tabs[sessionId] = new TabState
            {
                SessionId = sessionId,
                Title = title ?? session?.Title ?? sessionId,
                IsConnected = session?.State == ViewerSessionState.Connected || session == null,
                IsPeerSharing = session?.IsPeerSharing ?? true
            };
        }
        else if (title != null)
        {
            _tabs[sessionId].Title = title;
        }

        window.TabIds.Add(sessionId);

        // If this is the first tab or no active tab, make it active
        if (window.ActiveTabId == null)
        {
            SetActiveTab(windowId, sessionId);
        }

        _logger.LogInformation("Added tab {SessionId} to window {WindowId}", sessionId, windowId);
        OnTabsChanged?.Invoke(windowId);
    }

    /// <summary>
    /// Remove a tab from a window.
    /// </summary>
    public async Task RemoveTabAsync(string windowId, string sessionId)
    {
        if (!_windows.TryGetValue(windowId, out var window))
        {
            return;
        }

        if (!window.TabIds.Remove(sessionId))
        {
            return;
        }

        // Update active tab if we removed the active one
        if (window.ActiveTabId == sessionId)
        {
            window.ActiveTabId = window.TabIds.FirstOrDefault();
            if (window.ActiveTabId != null && _tabs.TryGetValue(window.ActiveTabId, out var newActiveTab))
            {
                newActiveTab.IsActive = true;
            }
            OnActiveTabChanged?.Invoke(windowId, window.ActiveTabId);
        }

        // Remove tab state if not in any window
        var inOtherWindow = _windows.Values.Any(w => w.TabIds.Contains(sessionId));
        if (!inOtherWindow)
        {
            _tabs.TryRemove(sessionId, out _);
            // Disconnect the session
            await _sessionManager.RemoveSessionAsync(sessionId);
        }

        _logger.LogInformation("Removed tab {SessionId} from window {WindowId}", sessionId, windowId);
        OnTabsChanged?.Invoke(windowId);

        // If window has no tabs, request close
        if (window.TabIds.Count == 0)
        {
            OnWindowCloseRequested?.Invoke(windowId);
        }
    }

    /// <summary>
    /// Move a tab from one window to another.
    /// </summary>
    public void MoveTab(string fromWindowId, string toWindowId, string sessionId)
    {
        if (!_windows.TryGetValue(fromWindowId, out var fromWindow) ||
            !_windows.TryGetValue(toWindowId, out var toWindow))
        {
            return;
        }

        if (!fromWindow.TabIds.Remove(sessionId))
        {
            return;
        }

        toWindow.TabIds.Add(sessionId);

        // Update active tabs
        if (fromWindow.ActiveTabId == sessionId)
        {
            fromWindow.ActiveTabId = fromWindow.TabIds.FirstOrDefault();
            OnActiveTabChanged?.Invoke(fromWindowId, fromWindow.ActiveTabId);
        }

        // Make moved tab active in target window
        SetActiveTab(toWindowId, sessionId);

        _logger.LogInformation("Moved tab {SessionId} from {From} to {To}", sessionId, fromWindowId, toWindowId);
        OnTabsChanged?.Invoke(fromWindowId);
        OnTabsChanged?.Invoke(toWindowId);

        // If source window has no tabs, request close
        if (fromWindow.TabIds.Count == 0)
        {
            OnWindowCloseRequested?.Invoke(fromWindowId);
        }
    }

    /// <summary>
    /// Set the active tab in a window.
    /// </summary>
    public void SetActiveTab(string windowId, string sessionId)
    {
        if (!_windows.TryGetValue(windowId, out var window))
        {
            return;
        }

        if (!window.TabIds.Contains(sessionId))
        {
            return;
        }

        // Deactivate previous active tab
        if (window.ActiveTabId != null && _tabs.TryGetValue(window.ActiveTabId, out var prevTab))
        {
            prevTab.IsActive = false;
        }

        // Activate new tab
        window.ActiveTabId = sessionId;
        if (_tabs.TryGetValue(sessionId, out var newTab))
        {
            newTab.IsActive = true;
        }

        _logger.LogDebug("Set active tab to {SessionId} in window {WindowId}", sessionId, windowId);
        OnActiveTabChanged?.Invoke(windowId, sessionId);
    }

    /// <summary>
    /// Opens a new viewer window for a session and adds it as the first tab.
    /// If the session already has a window, activates that window instead.
    /// </summary>
    public void OpenViewerForSession(string sessionId, string title)
    {
        // If session already has a window, just activate it
        var existingWindow = FindWindowForSession(sessionId);
        if (existingWindow != null)
        {
            SetActiveTab(existingWindow, sessionId);
            return;
        }

        var windowId = CreateWindow();
        AddTab(windowId, sessionId, title);
        _pendingWindowIds.Enqueue(windowId);
        // OnWindowRequested fires → App.xaml.cs opens the window
        OnWindowRequested?.Invoke(windowId, 0, 0);
    }

    /// <summary>
    /// Detach a tab from a window into a new window at the specified position.
    /// </summary>
    public string DetachTab(string fromWindowId, string sessionId, int screenX, int screenY)
    {
        // Create new window
        var newWindowId = CreateWindow();
        _pendingWindowIds.Enqueue(newWindowId);

        // Request the window to be created at the position
        OnWindowRequested?.Invoke(newWindowId, screenX, screenY);

        // Move the tab
        MoveTab(fromWindowId, newWindowId, sessionId);

        return newWindowId;
    }

    /// <summary>
    /// Update the thumbnail for a tab.
    /// </summary>
    public void UpdateTabThumbnail(string sessionId, string base64Data)
    {
        if (_tabs.TryGetValue(sessionId, out var tab))
        {
            tab.ThumbnailBase64 = base64Data;
            tab.LastFrameTime = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Find which window contains a session.
    /// </summary>
    public string? FindWindowForSession(string sessionId)
    {
        foreach (var (windowId, state) in _windows)
        {
            if (state.TabIds.Contains(sessionId))
            {
                return windowId;
            }
        }
        return null;
    }

    /// <summary>
    /// Claim a pre-created window ID (from OpenViewerForSession/DetachTab) or create a new one.
    /// Used by RemoteViewer.OnInitialized to avoid creating a duplicate empty window.
    /// Skips dequeued IDs that no longer exist in _windows (e.g. failed-connect window that
    /// was opened-then-closed before its RemoteViewer initialized — those IDs would otherwise
    /// pollute the queue and cause the NEXT RemoteViewer to bind to a dead window).
    /// </summary>
    public string ClaimOrCreateWindow()
    {
        while (_pendingWindowIds.TryDequeue(out var windowId))
        {
            if (_windows.ContainsKey(windowId))
            {
                return windowId;
            }
            _logger.LogDebug("ClaimOrCreateWindow: skipping stale windowId {WindowId} (window already closed)", windowId);
        }
        return CreateWindow();
    }

    private void HandleSessionCreated(ViewerSession session)
    {
        // Session created but not yet added to a window
        // The caller (e.g., ConnectionDialog) will call AddTab
        _logger.LogDebug("Session {SessionId} created, waiting to be added to window", session.SessionId);
    }

    private void HandleSessionRemoved(string sessionId)
    {
        // Snapshot windowIds we need to drop AFTER iterating so we don't mutate _windows
        // mid-iteration. Each entry is the windowId of a window whose last tab was just removed.
        var emptiedWindows = new List<string>();

        foreach (var (windowId, window) in _windows)
        {
            if (window.TabIds.Contains(sessionId))
            {
                window.TabIds.Remove(sessionId);

                if (window.ActiveTabId == sessionId)
                {
                    window.ActiveTabId = window.TabIds.FirstOrDefault();
                    OnActiveTabChanged?.Invoke(windowId, window.ActiveTabId);
                }

                OnTabsChanged?.Invoke(windowId);

                if (window.TabIds.Count == 0)
                {
                    emptiedWindows.Add(windowId);
                }
            }
        }

        // Now drop the dead windows from _windows so ClaimOrCreateWindow can skip them
        // when validating dequeued IDs. Without this, _pendingWindowIds keeps polluted IDs
        // and the next RemoteViewer binds to a window that no longer has tabs (leading
        // to "No active sessions" UI on a freshly-opened MAUI window).
        foreach (var windowId in emptiedWindows)
        {
            _windows.TryRemove(windowId, out _);
            OnWindowCloseRequested?.Invoke(windowId);
        }

        _tabs.TryRemove(sessionId, out _);
    }

    private void HandleSessionStateChanged(string sessionId, ViewerSessionState state)
    {
        if (_tabs.TryGetValue(sessionId, out var tab))
        {
            tab.IsConnected = state == ViewerSessionState.Connected;

            // Notify windows containing this tab
            foreach (var (windowId, window) in _windows)
            {
                if (window.TabIds.Contains(sessionId))
                {
                    OnTabsChanged?.Invoke(windowId);
                }
            }
        }
    }
}
