using SteamViewer.Server.Handlers;
using SteamViewer.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// Configure to listen on port 8080
builder.WebHost.UseUrls("http://0.0.0.0:8080");

// Add services
builder.Services.AddSingleton<ClientRegistry>();
builder.Services.AddSingleton<SignalingHandler>();

// Configure logging
builder.Logging.AddConsole();

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
var urls = builder.Configuration["ASPNETCORE_URLS"] ?? "http://0.0.0.0:8080";
logger.LogInformation("SteamViewer Signaling Server starting on {Urls}", urls);

app.Run();
