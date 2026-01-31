using SteamViewer.App.Services;

namespace SteamViewer.App;

public partial class App : Application
{
    private RemoteViewerService? _viewerService;
    private Window? _viewerWindow;
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
            MainThread.BeginInvokeOnMainThread(InitializeViewerService);
        }

        return mainWindow;
    }

    private void InitializeViewerService()
    {
        _viewerService = MauiProgram.ServiceProvider?.GetService<RemoteViewerService>();
        if (_viewerService != null)
        {
            _viewerService.OnViewerOpenRequested += OpenViewerWindow;
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
}
