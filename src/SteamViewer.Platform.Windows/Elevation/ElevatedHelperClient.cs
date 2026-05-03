using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SteamViewer.Platform.Windows.Elevation;

/// <summary>
/// Named pipe client that runs in the main (non-elevated) app process.
/// Launches the elevated helper via UAC and communicates over named pipe.
/// </summary>
public sealed class ElevatedHelperClient : IAsyncDisposable
{
    private static readonly UTF8Encoding PipeEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NamedPipeClientStream? _pipeClient;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Process? _helperProcess;
    private string? _pipeName;
    private bool _disposed;

    /// <summary>
    /// Whether the elevated helper is connected and ready.
    /// </summary>
    public bool IsConnected => _pipeClient?.IsConnected ?? false;

    public ElevatedHelperClient(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Launch the elevated helper process (triggers UAC) and connect via named pipe.
    /// Returns true if helper is running and pipe is connected.
    /// </summary>
    public async Task<bool> LaunchAndConnectAsync()
    {
        if (IsConnected) return true;

        _pipeName = $"SteamViewer-Elevated-{Environment.ProcessId}";

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            _logger.LogError("Cannot launch helper: process path unknown");
            return false;
        }

        _logger.LogInformation("Launching elevated helper: {ExePath} --elevated-helper {PipeName}", exePath, _pipeName);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--elevated-helper {_pipeName}",
                UseShellExecute = true,
                Verb = "runas"
            };

            _helperProcess = Process.Start(psi);
            if (_helperProcess == null)
            {
                _logger.LogError("Failed to start elevated helper process");
                return false;
            }

            _logger.LogInformation("Elevated helper launched (PID: {PID}), waiting for pipe...", _helperProcess.Id);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _logger.LogWarning("UAC prompt denied by user");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch elevated helper");
            return false;
        }

        // Small delay to let helper start and create the pipe
        await Task.Delay(500);

        // Connect to the pipe (helper is the server)
        try
        {
            _logger.LogInformation("Connecting to pipe: {PipeName}", _pipeName);
            _pipeClient = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.None);
            await _pipeClient.ConnectAsync(10_000); // 10s timeout
            _logger.LogInformation("Pipe connected successfully");

            _reader = new StreamReader(_pipeClient, PipeEncoding);
            _writer = new StreamWriter(_pipeClient, PipeEncoding) { AutoFlush = true };
            _logger.LogInformation("Reader/writer created, sending ping...");

            // Verify connection with ping
            var response = await SendCommandAsync(new { command = "ping" });
            if (response?.Success != true)
            {
                _logger.LogError("Elevated helper ping failed: {Error}", response?.Error ?? "null response");
                await CleanupAsync();
                return false;
            }

            _logger.LogInformation("Elevated helper connected and verified (ping OK)");
            return true;
        }
        catch (TimeoutException)
        {
            _logger.LogError("Timeout connecting to elevated helper pipe (10s)");
            await CleanupAsync();
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to elevated helper pipe");
            await CleanupAsync();
            return false;
        }
    }

    /// <summary>
    /// Send Ctrl+Alt+Del (SAS) via the elevated helper.
    /// </summary>
    public async Task<bool> SendSASAsync()
    {
        var response = await SendCommandAsync(new { command = "sendSAS" });
        if (response?.Success == true)
        {
            _logger.LogInformation("SendSAS succeeded via elevated helper");
            return true;
        }

        _logger.LogWarning("SendSAS failed: {Error}", response?.Error ?? "no response");
        return false;
    }

    /// <summary>
    /// Run a process elevated via the helper (no additional UAC prompt).
    /// </summary>
    public async Task<bool> RunElevatedAsync(string path, string? args = null)
    {
        var response = await SendCommandAsync(new { command = "runElevated", path, args });
        if (response?.Success == true)
        {
            _logger.LogInformation("RunElevated succeeded: {Path}", path);
            return true;
        }

        _logger.LogWarning("RunElevated failed: {Error}", response?.Error ?? "no response");
        return false;
    }

    /// <summary>
    /// Reboot with auto-restart via the elevated helper (writes RunOnceEx + shutdown).
    /// </summary>
    public async Task<bool> RebootAsync(string? clientId = null, string? passwordHash = null, string? viewerPeerId = null,
        string? serverUrl = null, string[]? stunUrls = null,
        string[]? turnUrls = null, string? turnUsername = null, string? turnCredential = null)
    {
        var response = await SendCommandAsync(new
        {
            command = "reboot",
            clientId,
            passwordHash,
            viewerPeerId,
            serverUrl,
            stunUrls,
            turnUrls,
            turnUsername,
            turnCredential
        });

        if (response?.Success == true)
        {
            _logger.LogInformation("Reboot initiated via elevated helper");
            return true;
        }

        _logger.LogWarning("Reboot failed: {Error}", response?.Error ?? "no response");
        return false;
    }

    /// <summary>
    /// Send an input event to the elevated helper for injection (fire-and-forget, no response).
    /// Used for high-frequency input (60+ Hz mouse events) where round-trip latency matters.
    /// </summary>
    private int _inputWriteCount;
    private int _inputDropCount;

    public async Task SendInputEventAsync(string inputJson, int screenWidth, int screenHeight)
    {
        if (_writer == null || !IsConnected)
        {
            _inputDropCount++;
            if (_inputDropCount <= 3 || _inputDropCount % 100 == 0)
                _logger.LogWarning("Admin pipe input dropped #{Count}: writer={Writer}, connected={Connected}",
                    _inputDropCount, _writer != null, IsConnected);
            return;
        }

        var commandJson = string.Concat(
            "{\"command\":\"injectInput\",\"sw\":", screenWidth.ToString(),
            ",\"sh\":", screenHeight.ToString(), ",",
            inputJson.Substring(1));

        _inputWriteCount++;
        await _writeLock.WaitAsync();
        try
        {
            await _writer.WriteLineAsync(commandJson);
            if (_inputWriteCount <= 3 || _inputWriteCount % 500 == 0)
                _logger.LogInformation("Admin pipe input #{Count}: {Len}b written", _inputWriteCount, commandJson.Length);
        }
        catch (Exception ex)
        {
            _inputDropCount++;
            if (_inputDropCount <= 3 || _inputDropCount % 100 == 0)
                _logger.LogWarning(ex, "Admin pipe write error #{Count}", _inputDropCount);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Shut down the elevated helper process.
    /// </summary>
    public async Task ShutdownHelperAsync()
    {
        try
        {
            await SendCommandAsync(new { command = "exit" });
        }
        catch
        {
            // Ignore — helper may already be gone
        }

        await CleanupAsync();
    }

    private async Task<HelperResponse?> SendCommandAsync(object command)
    {
        if (_writer == null || _reader == null || !IsConnected)
        {
            _logger.LogWarning("Cannot send command: pipe not connected");
            return null;
        }

        await _writeLock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(command);
            _logger.LogInformation("Pipe send: {Json}", json);
            await _writer.WriteLineAsync(json);
            await _writer.FlushAsync();

            // Read response with 10s timeout (avoid CancellationToken — unreliable on named pipes)
            var readTask = _reader.ReadLineAsync();
            var completed = await Task.WhenAny(readTask, Task.Delay(10_000));
            if (completed != readTask)
            {
                _logger.LogWarning("Pipe read timeout (10s) — helper may be stuck");
                return null;
            }

            var responseLine = await readTask;
            _logger.LogInformation("Pipe recv: {Response}", responseLine ?? "(null)");

            if (responseLine == null) return null;

            return JsonSerializer.Deserialize<HelperResponse>(responseLine,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pipe communication error");
            return null;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task CleanupAsync()
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

        if (_helperProcess != null && !_helperProcess.HasExited)
        {
            try { _helperProcess.Kill(); } catch { }
        }
        _helperProcess?.Dispose();
        _helperProcess = null;
    }

    /// <summary>
    /// Request the admin helper to launch a SYSTEM-level helper via token duplication.
    /// </summary>
    internal async Task<HelperResponse?> LaunchSystemHelperAsync(string pipeName, string nonce)
    {
        return await SendCommandAsync(new
        {
            command = "launchSystemHelper",
            pipeName,
            nonce
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await ShutdownHelperAsync();
    }

    internal record HelperResponse(bool Success, string? Error);
}
