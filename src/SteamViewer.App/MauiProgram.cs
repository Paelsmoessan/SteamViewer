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
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
#endif

        // Get signaling server URL from environment variable or use default
        // Set STEAMVIEWER_SERVER env var to override (e.g., "wss://steamviewer-signaling.onrender.com/ws")
        var serverUrl = Environment.GetEnvironmentVariable("STEAMVIEWER_SERVER")
                        ?? "ws://localhost:8080/ws";

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
}
