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

        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
        builder.Logging.AddConsole();
        // Maximum verbosity for debugging - shows EVERYTHING
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddFilter("SteamViewer", LogLevel.Trace);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning); // Reduce noise from Blazor
#endif

        // Load server URL from appsettings.json
        // Edit appsettings.json to change the signaling server
        var serverUrl = GetSignalingServerUrl();

#if DEBUG
        Console.WriteLine($"Signaling server URL: {serverUrl}");
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

        return builder.Build();
    }

    private static string GetSignalingServerUrl()
    {
        // Try to load from appsettings.json
        try
        {
            var appDir = AppContext.BaseDirectory;
            var configPath = Path.Combine(appDir, "appsettings.json");

            if (File.Exists(configPath))
            {
                var config = new ConfigurationBuilder()
                    .AddJsonFile(configPath, optional: true)
                    .Build();

                var serverUrl = config["SignalingServer"];
                if (!string.IsNullOrEmpty(serverUrl))
                {
                    return serverUrl;
                }
            }
        }
        catch (Exception ex)
        {
#if DEBUG
            Console.WriteLine($"Warning: Could not load appsettings.json: {ex.Message}");
#endif
        }

        // Fallback to environment variable or default
        return Environment.GetEnvironmentVariable("STEAMVIEWER_SERVER")
               ?? "ws://localhost:8080/ws";
    }
}
