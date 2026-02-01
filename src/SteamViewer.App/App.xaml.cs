using SteamViewer.App.Services;

namespace SteamViewer.App;

public partial class App : Application
{
    private RemoteViewerService? _viewerService;
    private CollaborationViewerService? _collabViewerService;
    private Window? _viewerWindow;
    private Window? _collabViewerWindow;
    private bool _initialized;

    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var mainWindow = new Window(new MainPage()) { Title = "SteamViewer" };

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
        // Remote viewer service (1:1 mode)
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
    }

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
