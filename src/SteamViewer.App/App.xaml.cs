using SteamViewer.App.Services;
using System.Collections.Concurrent;
using System.Text.Json;

namespace SteamViewer.App;

public partial class App : Application
{
    private CollaborationViewerService? _collabViewerService;
    private ViewerTabManager? _tabManager;
    private Window? _collabViewerWindow;
    private Window? _mainWindow;
    private readonly ConcurrentDictionary<string, Window> _viewerWindows = new();
    private bool _initialized;

    private static readonly string WindowStateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SteamViewer", "window-state.json");

    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var state = LoadWindowState();

        double width = state?.Width ?? 750;
        double height = state?.Height ?? 460;
        double x, y;

        if (state != null)
        {
            x = state.X;
            y = state.Y;
        }
        else
        {
            var display = DeviceDisplay.MainDisplayInfo;
            var screenW = display.Width / display.Density;
            var screenH = display.Height / display.Density;
            x = (screenW - width) / 2;
            y = (screenH - height) / 2;
        }

        _mainWindow = new Window(new MainPage())
        {
            Title = "SteamViewer",
            Width = width,
            Height = height,
            X = x,
            Y = y
        };

        // Save window state, disconnect sessions, kill child processes, and exit on close
        _mainWindow.Destroying += (s, e) =>
        {
            var debugPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamViewer", "exit-debug.txt");
            try { File.AppendAllText(debugPath, $"[{DateTime.Now:HH:mm:ss.fff}] Destroying fired\n"); } catch { }

            SaveWindowState();
            try
            {
                var sessionManager = MauiProgram.ServiceProvider?.GetService<ViewerSessionManager>();
                File.AppendAllText(debugPath, $"[{DateTime.Now:HH:mm:ss.fff}] SessionManager: {sessionManager != null}\n");
                var signalingClient = MauiProgram.ServiceProvider?.GetService<Client.Core.Network.SignalingClient>();
                File.AppendAllText(debugPath, $"[{DateTime.Now:HH:mm:ss.fff}] SignalingClient: {signalingClient != null}, connected: {signalingClient?.IsConnected}\n");

                var task = Task.Run(async () =>
                {
                    try { File.AppendAllText(debugPath, $"[{DateTime.Now:HH:mm:ss.fff}] Task.Run started\n"); } catch { }
                    if (sessionManager != null) await sessionManager.DisposeAsync();
                    try { File.AppendAllText(debugPath, $"[{DateTime.Now:HH:mm:ss.fff}] SessionManager disposed\n"); } catch { }
                    if (signalingClient != null) await signalingClient.DisposeAsync();
                    try { File.AppendAllText(debugPath, $"[{DateTime.Now:HH:mm:ss.fff}] SignalingClient disposed\n"); } catch { }
                });
                var completed = task.Wait(TimeSpan.FromSeconds(5));
                File.AppendAllText(debugPath, $"[{DateTime.Now:HH:mm:ss.fff}] Wait completed: {completed}\n");
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(debugPath, $"[{DateTime.Now:HH:mm:ss.fff}] ERROR: {ex.Message}\n"); } catch { }
            }

            WinUI.Program.KillChildProcesses();
            Environment.Exit(0);
        };

        // Subscribe to viewer service after services are available
        if (!_initialized)
        {
            _initialized = true;
            MainThread.BeginInvokeOnMainThread(InitializeViewerServices);
        }

        return _mainWindow;
    }

    private static WindowState? LoadWindowState()
    {
        try
        {
            if (!File.Exists(WindowStateFile)) return null;
            var json = File.ReadAllText(WindowStateFile);
            var state = JsonSerializer.Deserialize<WindowState>(json);
            if (state == null) return null;

            // Discard if dimensions are bogus (minimized, corrupted)
            if (state.Width < 200 || state.Height < 200 || state.X < -10000 || state.Y < -10000)
                return null;

            // Discard if window would be entirely off-screen
            var display = DeviceDisplay.MainDisplayInfo;
            var screenW = display.Width / display.Density;
            var screenH = display.Height / display.Density;
            if (state.X > screenW - 50 || state.Y > screenH - 50)
                return null;

            return state;
        }
        catch { return null; }
    }

    private void SaveWindowState()
    {
        try
        {
            if (_mainWindow == null) return;
            var x = _mainWindow.X;
            var y = _mainWindow.Y;
            var w = _mainWindow.Width;
            var h = _mainWindow.Height;

            // Don't save minimized/off-screen garbage (Windows reports -32000,-32000 when minimized)
            if (x < -10000 || y < -10000 || w < 200 || h < 200)
                return;

            var state = new WindowState { X = x, Y = y, Width = w, Height = h };
            Directory.CreateDirectory(Path.GetDirectoryName(WindowStateFile)!);
            File.WriteAllText(WindowStateFile, JsonSerializer.Serialize(state));
        }
        catch { /* best effort */ }
    }

    private record WindowState
    {
        public double X { get; init; }
        public double Y { get; init; }
        public double Width { get; init; }
        public double Height { get; init; }
    }

    private void InitializeViewerServices()
    {
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
    /// Opens a new viewer window at a specific screen position (for new session or tab detach).
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
        // Blazor's DisposeAsync races with Destroying - it wins TryRemove but can't
        // complete the async Disconnect. Send Disconnect for each session directly.
        Task.Run(async () =>
        {
            var sessionManager = MauiProgram.ServiceProvider?.GetService<ViewerSessionManager>();
            if (sessionManager == null) return;
            foreach (var session in sessionManager.Sessions.ToList())
            {
                await sessionManager.RemoveSessionAsync(session.SessionId);
            }
        }).Wait(TimeSpan.FromSeconds(5));
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
