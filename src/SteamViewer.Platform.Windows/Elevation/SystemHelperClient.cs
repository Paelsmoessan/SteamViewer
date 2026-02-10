using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SteamViewer.Platform.Windows.Elevation;

/// <summary>
/// Named pipe client for the SYSTEM-level helper process.
/// Delegates task creation to the admin helper (which has permission to create SYSTEM tasks).
/// Connects to the SYSTEM helper pipe and authenticates with a nonce.
/// </summary>
public sealed class SystemHelperClient : IAsyncDisposable
{
    private static readonly UTF8Encoding PipeEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ILogger _logger;
    private readonly ElevatedHelperClient _adminHelper;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NamedPipeClientStream? _pipeClient;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private string? _pipeName;
    private string? _nonce;
    private string? _taskName;
    private bool _disposed;

    /// <summary>
    /// Whether the SYSTEM helper is connected and authenticated.
    /// </summary>
    public bool IsConnected => _pipeClient?.IsConnected ?? false;

    /// <summary>
    /// The scheduled task name (for cleanup).
    /// </summary>
    public string? TaskName => _taskName;

    public SystemHelperClient(ILogger logger, ElevatedHelperClient adminHelper)
    {
        _logger = logger;
        _adminHelper = adminHelper;
    }

    /// <summary>
    /// Launch the SYSTEM helper via the admin helper (schtask) and connect via named pipe.
    /// Returns true if the SYSTEM helper is running and authenticated.
    /// </summary>
    public async Task<bool> LaunchAndConnectAsync()
    {
        if (IsConnected) return true;

        if (!_adminHelper.IsConnected)
        {
            _logger.LogError("Cannot launch SYSTEM helper: admin helper not connected");
            return false;
        }

        // Generate unique identifiers for this session
        _pipeName = $"SteamViewer-System-{Guid.NewGuid():N}";
        _nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _taskName = $"SteamViewer-System-{Guid.NewGuid().ToString()[..8]}";

        _logger.LogInformation("Requesting admin helper to create SYSTEM task: {TaskName}", _taskName);

        try
        {
            // Ask the admin helper to create and run the scheduled task
            var response = await _adminHelper.LaunchSystemHelperAsync(_pipeName, _nonce, _taskName);
            if (response?.Success != true)
            {
                _logger.LogError("Admin helper failed to create SYSTEM task: {Error}", response?.Error ?? "null response");
                return false;
            }

            _logger.LogInformation("SYSTEM task created. Waiting for helper to start...");

            // Small delay to let the scheduled task start and create the pipe
            await Task.Delay(1000);

            // Connect to the SYSTEM helper pipe
            _logger.LogInformation("Connecting to SYSTEM helper pipe: {PipeName}", _pipeName);
            _pipeClient = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.None);
            await _pipeClient.ConnectAsync(15_000); // 15s timeout (schtask startup can be slow)
            _logger.LogInformation("Pipe connected. Authenticating...");

            _reader = new StreamReader(_pipeClient, PipeEncoding);
            _writer = new StreamWriter(_pipeClient, PipeEncoding) { AutoFlush = true };

            // Authenticate with nonce
            var authResponse = await SendCommandAsync(new { command = "authenticate", nonce = _nonce });
            if (authResponse?.Success != true)
            {
                _logger.LogError("SYSTEM helper authentication failed: {Error}", authResponse?.Error ?? "null response");
                await CleanupAsync();
                return false;
            }

            // Verify with ping
            var pingResponse = await SendCommandAsync(new { command = "ping" });
            if (pingResponse?.Success != true)
            {
                _logger.LogError("SYSTEM helper ping failed after auth");
                await CleanupAsync();
                return false;
            }

            _logger.LogInformation("SYSTEM helper connected, authenticated, and verified");
            return true;
        }
        catch (TimeoutException)
        {
            _logger.LogError("Timeout connecting to SYSTEM helper pipe (15s)");
            await CleanupAsync();
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to SYSTEM helper");
            await CleanupAsync();
            return false;
        }
    }

    /// <summary>
    /// Run a process as SYSTEM in the user's desktop session.
    /// </summary>
    public async Task<bool> RunAsSystemAsync(string path, string? args = null)
    {
        var response = await SendCommandAsync(new { command = "runAsSystem", path, args });
        if (response?.Success == true)
        {
            _logger.LogInformation("RunAsSystem succeeded: {Path}", path);
            return true;
        }

        _logger.LogWarning("RunAsSystem failed: {Error}", response?.Error ?? "no response");
        return false;
    }

    /// <summary>
    /// Send Ctrl+Alt+Del (SAS) via the SYSTEM helper.
    /// </summary>
    public async Task<bool> SendSASAsync()
    {
        var response = await SendCommandAsync(new { command = "sendSAS" });
        return response?.Success == true;
    }

    /// <summary>
    /// Send an input event to the SYSTEM helper for injection (fire-and-forget).
    /// </summary>
    public async Task SendInputEventAsync(string inputJson, int screenWidth, int screenHeight)
    {
        if (_writer == null || !IsConnected) return;

        var commandJson = string.Concat(
            "{\"command\":\"injectInput\",\"sw\":", screenWidth.ToString(),
            ",\"sh\":", screenHeight.ToString(), ",",
            inputJson.Substring(1));

        await _writeLock.WaitAsync();
        try
        {
            await _writer.WriteLineAsync(commandJson);
        }
        catch { /* Ignore write errors on fire-and-forget input */ }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Clean up the scheduled task via the admin helper.
    /// </summary>
    public async Task CleanupScheduledTaskAsync()
    {
        if (_adminHelper.IsConnected && !string.IsNullOrEmpty(_taskName))
        {
            try
            {
                await _adminHelper.DeleteSystemTaskAsync(_taskName);
                _logger.LogInformation("Deleted SYSTEM scheduled task: {TaskName}", _taskName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete SYSTEM task: {TaskName}", _taskName);
            }
        }
    }

    /// <summary>
    /// Shut down the SYSTEM helper process and clean up the scheduled task.
    /// </summary>
    public async Task ShutdownAsync()
    {
        try
        {
            await SendCommandAsync(new { command = "exit" });
        }
        catch { /* Helper may already be gone */ }

        await CleanupScheduledTaskAsync();
        await CleanupPipeAsync();
    }

    private async Task<ElevatedHelperClient.HelperResponse?> SendCommandAsync(object command)
    {
        if (_writer == null || _reader == null || !IsConnected)
        {
            _logger.LogWarning("Cannot send command: SYSTEM pipe not connected");
            return null;
        }

        await _writeLock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(command);
            _logger.LogInformation("System pipe send: {Json}", json);
            await _writer.WriteLineAsync(json);
            await _writer.FlushAsync();

            // Read response with 10s timeout
            var readTask = _reader.ReadLineAsync();
            var completed = await Task.WhenAny(readTask, Task.Delay(10_000));
            if (completed != readTask)
            {
                _logger.LogWarning("System pipe read timeout (10s)");
                return null;
            }

            var responseLine = await readTask;
            _logger.LogInformation("System pipe recv: {Response}", responseLine ?? "(null)");

            if (responseLine == null) return null;

            return JsonSerializer.Deserialize<ElevatedHelperClient.HelperResponse>(responseLine,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "System pipe communication error");
            return null;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task CleanupPipeAsync()
    {
        _reader?.Dispose();
        _reader = null;

        _writer?.Dispose();
        _writer = null;

        if (_pipeClient != null)
        {
            await _pipeClient.DisposeAsync();
            _pipeClient = null;
        }
    }

    private async Task CleanupAsync()
    {
        await CleanupPipeAsync();
        await CleanupScheduledTaskAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await ShutdownAsync();
    }
}
