using SteamViewer.App.Services;
using System.Collections.Concurrent;

namespace SteamViewer.App;

public partial class App : Application
{
    private RemoteViewerService? _viewerService;
    private CollaborationViewerService? _collabViewerService;
    private ViewerTabManager? _tabManager;
    private Window? _viewerWindow;
    private Window? _collabViewerWindow;
    private readonly ConcurrentDictionary<string, Window> _viewerWindows = new();
    private bool _initialized;

    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var mainWindow = new Window(new MainPage())
        {
            Title = "SteamViewer",
            Width = 640,
            Height = 720
        };

        // Subscribe to viewer service after services are available
        if (!_initialized)
        {
            _initialized = true;
            MainThread.BeginInvokeOnMainThread(InitializeViewerServices);
        }

        return mainWindow;
    }

    private void InitializeViewerServices()
    {
        // Remote viewer service (1:1 mode - legacy)
        _viewerService = MauiProgram.ServiceProvider?.GetService<RemoteViewerService>();
        if (_viewerService != null)
        {
            _viewerService.OnViewerOpenRequested += OpenViewerWindow;
        }

        // Collaboration viewer service (multi-user mode)
        _collabViewerService = MauiProgram.ServiceProvider?.GetService<CollaborationViewerService>();
        if (_collabViewerService != null)
        {
            _collabViewerService.OnViewerOpenRequested += OpenCollabViewerWindow;
        }

        // Tab manager for multi-tab viewer windows
        _tabManager = MauiProgram.ServiceProvider?.GetService<ViewerTabManager>();
        if (_tabManager != null)
        {
            _tabManager.OnWindowRequested += OpenViewerWindowAtPosition;
            _tabManager.OnWindowCloseRequested += CloseViewerWindowById;
        }
    }

    /// <summary>
    /// Opens the legacy viewer window (for 1:1 connections).
    /// </summary>
    private void OpenViewerWindow()
    {
        if (_viewerWindow != null)
        {
            // Window already open
            return;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            _viewerWindow = new Window(new ViewerPage())
            {
                Title = $"Remote Viewer - {_viewerService?.PeerId ?? "Unknown"}",
                Width = 1280,
                Height = 800
            };

            _viewerWindow.Destroying += OnViewerWindowDestroying;
            _viewerService?.RegisterViewerWindow(_viewerWindow);

            Application.Current?.OpenWindow(_viewerWindow);
        });
    }

    private void OnViewerWindowDestroying(object? sender, EventArgs e)
    {
        _viewerWindow = null;
        _viewerService?.NotifyViewerClosed();
    }

    /// <summary>
    /// Opens a new viewer window at a specific screen position (for tab detach).
    /// </summary>
    private void OpenViewerWindowAtPosition(string windowId, int screenX, int screenY)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var window = new Window(new ViewerPage())
            {
                Title = "Remote Viewer",
                Width = 1280,
                Height = 800,
                X = screenX,
                Y = screenY
            };

            window.Destroying += (s, e) => OnMultiViewerWindowDestroying(windowId);

            _viewerWindows[windowId] = window;
            _tabManager?.RegisterWindow(windowId, window);

            Application.Current?.OpenWindow(window);
        });
    }

    /// <summary>
    /// Closes a viewer window by its ID.
    /// </summary>
    private void CloseViewerWindowById(string windowId)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_viewerWindows.TryRemove(windowId, out var window))
            {
                Application.Current?.CloseWindow(window);
            }
        });
    }

    private void OnMultiViewerWindowDestroying(string windowId)
    {
        _viewerWindows.TryRemove(windowId, out _);
        // TabManager will handle cleanup via RemoveWindowAsync
    }

    private void OpenCollabViewerWindow(string peerId)
    {
        if (_collabViewerWindow != null)
        {
            // Window already open - just switch tab
            _collabViewerService?.SwitchTab(peerId);
            return;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            _collabViewerWindow = new Window(new CollaborationViewerPage())
            {
                Title = "Collaboration Viewer",
                Width = 1280,
                Height = 800
            };

            _collabViewerWindow.Destroying += OnCollabViewerWindowDestroying;
            _collabViewerService?.RegisterViewerWindow(_collabViewerWindow);

            Application.Current?.OpenWindow(_collabViewerWindow);
        });
    }

    private void OnCollabViewerWindowDestroying(object? sender, EventArgs e)
    {
        _collabViewerWindow = null;
        _collabViewerService?.NotifyViewerClosed();
    }
}
