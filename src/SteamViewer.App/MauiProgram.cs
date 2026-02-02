using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SteamViewer.Client.Core.Capture;
using SteamViewer.Client.Core.Network;
using SteamViewer.Client.Core.Session;
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
        // Maximum verbosity for debugging - shows EVERYTHING
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddFilter("SteamViewer", LogLevel.Trace);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning); // Reduce noise from Blazor
#endif

        // Load server URL from configuration (required)
        var serverUrl = configuration["SignalingServer"]
                        ?? Environment.GetEnvironmentVariable("STEAMVIEWER_SERVER")
                        ?? throw new InvalidOperationException("SignalingServer not configured in appsettings.json. Run 'git pull' and rebuild.");

#if DEBUG
        Console.WriteLine($"Signaling server URL: {serverUrl}");
        Console.WriteLine($"TURN enabled: {configuration.GetValue<bool>("TurnServer:Enabled")}");
#endif

        // Register platform-agnostic services
        builder.Services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<SignalingClient>>();
            return new SignalingClient(serverUrl, logger);
        });

        // Register SessionManager
        builder.Services.AddSingleton(sp =>
        {
            var config = new SessionConfig
            {
                SignalingServerUrl = serverUrl,
                TargetFps = 30,
                VideoBitrate = 4_000_000
            };
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            return new SessionManager(config, loggerFactory);
        });

        // Register CanvasRenderingService (scoped for Blazor component lifetime)
        builder.Services.AddScoped<CanvasRenderingService>();

        // Register RemoteViewerService for multi-window support
        builder.Services.AddSingleton<RemoteViewerService>();

        // Register CollaborationViewerService for multi-user viewer window
        builder.Services.AddSingleton<CollaborationViewerService>();

        // Register ViewerSessionManager for multi-tab viewer sessions
        builder.Services.AddSingleton<ViewerSessionManager>();

        // Register ViewerTabManager for multi-tab/multi-window coordination
        builder.Services.AddSingleton<ViewerTabManager>();

        // Register platform-specific services
#if WINDOWS
        builder.Services.AddSingleton<IMonitorEnumerator, SteamViewer.Platform.Windows.WindowsMonitorEnumerator>();
        builder.Services.AddSingleton<IScreenCapture, SteamViewer.Platform.Windows.ScreenCapture.DxgiScreenCapture>();
        builder.Services.AddSingleton<IInputInjector, SteamViewer.Platform.Windows.Input.WindowsInputInjector>();
#elif MACCATALYST
        builder.Services.AddSingleton<IMonitorEnumerator, SteamViewer.Platform.macOS.MacMonitorEnumerator>();
        builder.Services.AddSingleton<IScreenCapture, SteamViewer.Platform.macOS.ScreenCapture.MacScreenCapture>();
        builder.Services.AddSingleton<IInputInjector, SteamViewer.Platform.macOS.Input.MacInputInjector>();
#endif

        var app = builder.Build();

        // Store service provider for App to access
        ServiceProvider = app.Services;

        return app;
    }

    /// <summary>
    /// Global service provider for accessing services from non-DI contexts.
    /// </summary>
    public static IServiceProvider? ServiceProvider { get; private set; }
}
