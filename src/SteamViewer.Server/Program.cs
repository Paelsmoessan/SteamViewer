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

// Configure logging (in code, so appsettings.json not required for Release)
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddProvider(new SharedFileLoggerProvider(fileLogger));
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("SteamViewer.Server", LogLevel.Debug);

var app = builder.Build();

// Enable WebSockets
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

// Health check endpoint
app.MapGet("/health", () => "OK");

// ==================== Remote Management API (DEBUG ONLY) ====================
#if DEBUG

// Whitelist of allowed machine names (prevents command injection)
var allowedMachines = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "MEDIASERVER",
    Environment.MachineName
};

// Validate machine name - must be alphanumeric/hyphen only and in whitelist
bool IsValidMachine(string? machine, out string machineName)
{
    machineName = machine ?? "MEDIASERVER";

    // Must match pattern: alphanumeric, hyphens, underscores only
    if (!System.Text.RegularExpressions.Regex.IsMatch(machineName, @"^[a-zA-Z0-9_-]+$"))
        return false;

    // Must be in whitelist
    return allowedMachines.Contains(machineName);
}

// Log reading endpoints
app.MapGet("/api/logs/{type}/{machine?}", (string type, string? machine, int? lines) =>
{
    var lineCount = Math.Clamp(lines ?? 100, 1, 1000); // Limit lines to prevent memory issues

    if (!IsValidMachine(machine, out var machineName))
        return Results.BadRequest($"Invalid or unauthorized machine name: {machine}");

    string? path = type.ToLowerInvariant() switch
    {
        "server" => Path.Combine(AppContext.BaseDirectory, "logs", $"server-{Environment.MachineName}.log"),
        "client" => $@"\\{machineName}\SteamViewer\logs\client-{machineName}.log",
        "input" => $@"\\{machineName}\SteamViewer\SteamViewer_InputDebug.log",
        _ => null
    };

    // For server logs, also try the development path
    if (type.ToLowerInvariant() == "server" && !File.Exists(path))
    {
        path = $@"C:\_Development\SteamViewer.NET\logs\server-{Environment.MachineName}.log";
    }

    if (path == null)
        return Results.BadRequest($"Unknown log type: {type}. Valid types: server, client, input");

    if (!File.Exists(path))
        return Results.NotFound($"Log file not found: {path}");

    try
    {
        var allLines = File.ReadAllLines(path);
        var lastLines = allLines.TakeLast(lineCount);
        return Results.Text(string.Join("\n", lastLines), "text/plain");
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error reading log: {ex.Message}");
    }
});

// Remote app status check
app.MapGet("/api/remote/status/{machine?}", (string? machine) =>
{
    if (!IsValidMachine(machine, out var machineName))
        return Results.BadRequest($"Invalid or unauthorized machine name: {machine}");

    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "tasklist",
            Arguments = $"/s {machineName} /fi \"imagename eq SteamViewer.App.exe\" /fo csv /nh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(psi);
        var output = process?.StandardOutput.ReadToEnd() ?? "";
        var error = process?.StandardError.ReadToEnd() ?? "";
        process?.WaitForExit();

        var isRunning = output.Contains("SteamViewer.App.exe", StringComparison.OrdinalIgnoreCase);

        return Results.Json(new
        {
            machine = machineName,
            running = isRunning,
            details = isRunning ? output.Trim() : null,
            error = string.IsNullOrEmpty(error) ? null : error
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error checking status: {ex.Message}");
    }
});

// Remote app start (uses schtasks - requires pre-created task on remote machine)
app.MapPost("/api/remote/start/{machine?}", (string? machine) =>
{
    if (!IsValidMachine(machine, out var machineName))
        return Results.BadRequest($"Invalid or unauthorized machine name: {machine}");

    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "schtasks",
            Arguments = $"/run /s {machineName} /tn SteamViewerApp",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(psi);
        var output = process?.StandardOutput.ReadToEnd() ?? "";
        var error = process?.StandardError.ReadToEnd() ?? "";
        process?.WaitForExit();

        var exitCode = process?.ExitCode ?? -1;
        var success = exitCode == 0;

        return Results.Json(new
        {
            machine = machineName,
            success,
            message = success ? "Start command sent" : "Failed to start",
            output = output.Trim(),
            error = string.IsNullOrEmpty(error) ? null : error.Trim()
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error starting app: {ex.Message}");
    }
});

// Remote app stop
app.MapPost("/api/remote/stop/{machine?}", (string? machine) =>
{
    if (!IsValidMachine(machine, out var machineName))
        return Results.BadRequest($"Invalid or unauthorized machine name: {machine}");

    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "taskkill",
            Arguments = $"/s {machineName} /im SteamViewer.App.exe /f",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(psi);
        var output = process?.StandardOutput.ReadToEnd() ?? "";
        var error = process?.StandardError.ReadToEnd() ?? "";
        process?.WaitForExit();

        var exitCode = process?.ExitCode ?? -1;
        // Exit code 0 = success, 128 = process not found (also OK)
        var success = exitCode == 0 || exitCode == 128;

        return Results.Json(new
        {
            machine = machineName,
            success,
            message = success ? "Stop command sent" : "Failed to stop",
            output = output.Trim(),
            error = string.IsNullOrEmpty(error) ? null : error.Trim()
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error stopping app: {ex.Message}");
    }
});

#endif
// ==================== End Remote Management API ====================

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
