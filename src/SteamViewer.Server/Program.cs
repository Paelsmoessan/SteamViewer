using SteamViewer.Common.Logging;
using SteamViewer.Server.Handlers;
using SteamViewer.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// Configure port - use PORT env var (for Render/Railway) or default to 8080
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// Add services
builder.Services.AddSingleton<ClientRegistry>();
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<SignalingHandler>();

// Configure file logging for multi-dev debugging
var fileLogger = new SharedFileLogger("server");
builder.Services.AddSingleton(fileLogger);

// Configure logging
builder.Logging.AddConsole();
builder.Logging.AddProvider(new SharedFileLoggerProvider(fileLogger));

var app = builder.Build();

// Enable WebSockets
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

// Health check endpoint
app.MapGet("/health", () => "OK");

// WebSocket endpoint
app.Map("/ws", async (HttpContext context, SignalingHandler handler) =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        await handler.HandleConnectionAsync(webSocket, context.RequestAborted);
    }
    else
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
    }
});

// Log startup
var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("SteamViewer Signaling Server starting on port {Port}", port);

app.Run();
