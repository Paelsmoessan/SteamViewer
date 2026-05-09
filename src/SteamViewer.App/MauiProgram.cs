using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using SteamViewer.Client.Core.Capture;
using SteamViewer.Client.Core.Elevation;
using SteamViewer.Client.Core.Network;
using SteamViewer.App.Services;

namespace SteamViewer.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        // Load configuration from appsettings.json
        var appDir = AppContext.BaseDirectory;
        var configPath = Path.Combine(appDir, "appsettings.json");

        var configBuilder = new ConfigurationBuilder();
        if (File.Exists(configPath))
        {
            configBuilder.AddJsonFile(configPath, optional: false, reloadOnChange: false);
        }
        var configuration = configBuilder.Build();
        builder.Services.AddSingleton<IConfiguration>(configuration);

        builder.Services.AddMauiBlazorWebView();

        // File logger - writes all logs to %TEMP%\SteamViewer\debug.log
        var fileLogger = new FileLoggerService();
        builder.Services.AddSingleton(fileLogger);
        builder.Logging.AddProvider(new FileLoggerProvider(fileLogger));

        Console.WriteLine($"=== Log file: {fileLogger.LogFilePath} ===");

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
        builder.Logging.AddConsole();
        // File logger captures everything (Debug+). Console only shows Info+ to keep CMD clean.
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddFilter("SteamViewer", LogLevel.Debug);
        builder.Logging.AddFilter<ConsoleLoggerProvider>(null, LogLevel.Information);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning); // Reduce noise from Blazor
#endif

        // Load server URL from configuration (required)
        var serverUrl = configuration["SignalingServer"]
                        ?? Environment.GetEnvironmentVariable("STEAMVIEWER_SERVER")
                        ?? throw new InvalidOperationException("SignalingServer not configured in appsettings.json. Run 'git pull' and rebuild.");

#if DEBUG
        Console.WriteLine($"Signaling server URL: {serverUrl}");
#endif

        // TURN config fetched from server at runtime (not shipped in binary)
        builder.Services.AddSingleton<TurnConfigService>();

        // Register platform-agnostic services
        builder.Services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<SignalingClient>>();
            return new SignalingClient(serverUrl, logger);
        });

        // Register CanvasRenderingService (scoped for Blazor component lifetime)
        builder.Services.AddScoped<CanvasRenderingService>();

        // Register CollaborationViewerService for multi-user viewer window
        builder.Services.AddSingleton<CollaborationViewerService>();

        // Register session managers
        builder.Services.AddSingleton<ViewerSessionManager>();
        builder.Services.AddSingleton<HostSessionManager>();

        // Register ViewerTabManager for multi-tab/multi-window coordination
        builder.Services.AddSingleton<ViewerTabManager>();

        // Register platform-specific services
#if WINDOWS
        builder.Services.AddSingleton<Services.NativeFrameBridge>();
        builder.Services.AddSingleton<Services.InputMessageRouter>();
        builder.Services.AddSingleton<IMonitorEnumerator, SteamViewer.Platform.Windows.WindowsMonitorEnumerator>();
        builder.Services.AddSingleton<IScreenCapture, SteamViewer.Platform.Windows.ScreenCapture.DxgiScreenCapture>();
        builder.Services.AddSingleton<IInputInjector, SteamViewer.Platform.Windows.Input.WindowsInputInjector>();
        builder.Services.AddTransient<IElevationService, SteamViewer.Platform.Windows.Elevation.WindowsElevationService>();
        builder.Services.AddSingleton<ISystemKeyInterceptor, SteamViewer.Platform.Windows.Input.WindowsSystemKeyInterceptor>();
#elif MACCATALYST
        builder.Services.AddSingleton<IMonitorEnumerator, SteamViewer.Platform.macOS.MacMonitorEnumerator>();
        builder.Services.AddSingleton<IScreenCapture, SteamViewer.Platform.macOS.ScreenCapture.MacScreenCapture>();
        builder.Services.AddSingleton<IInputInjector, SteamViewer.Platform.macOS.Input.MacInputInjector>();
        builder.Services.AddTransient<IElevationService, SteamViewer.Platform.macOS.Elevation.MacElevationService>();
        builder.Services.AddSingleton<ISystemKeyInterceptor, SteamViewer.Platform.macOS.Input.MacSystemKeyInterceptor>();
#endif

        var app = builder.Build();

        // Store service provider for App to access
        ServiceProvider = app.Services;

        // Ensure signaling disconnect on app exit (window close kills process before Blazor DisposeAsync completes)
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                var sessionManager = ServiceProvider?.GetService<ViewerSessionManager>();
                sessionManager?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
                var signalingClient = ServiceProvider?.GetService<SignalingClient>();
                signalingClient?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            }
            catch { }
        };

        return app;
    }

    /// <summary>
    /// Global service provider for accessing services from non-DI contexts.
    /// </summary>
    public static IServiceProvider? ServiceProvider { get; private set; }
}
