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
/// Also connects to a separate video pipe for receiving Secure Desktop JPEG frames.
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
    private bool _disposed;

    // Video pipe for receiving JPEG frames from Secure Desktop capture
    private NamedPipeClientStream? _videoPipe;
    private BinaryReader? _videoReader;
    private Thread? _videoReaderThread;
    private volatile bool _videoStopRequested;
    private int _videoFrameCount;

    // Notify pipe for receiving server-push notifications
    private NamedPipeClientStream? _notifyPipe;
    private StreamReader? _notifyReader;
    private Thread? _notifyReaderThread;
    private volatile bool _notifyStopRequested;

    // Secure Desktop state tracked from notifications
    private volatile bool _isSecureDesktopActive;
    private int _secureDesktopWidth;
    private int _secureDesktopHeight;

    /// <summary>
    /// Whether the SYSTEM helper is connected and authenticated.
    /// </summary>
    public bool IsConnected => _pipeClient?.IsConnected ?? false;

    /// <summary>
    /// Whether the Secure Desktop (Winlogon) is currently active on the host.
    /// </summary>
    public bool IsSecureDesktopActive => _isSecureDesktopActive;

    /// <summary>
    /// Raised when a JPEG frame is received from the Secure Desktop.
    /// Parameters: (jpegData, width, height).
    /// </summary>
    public event Action<byte[], int, int>? OnSecureDesktopFrame;

    /// <summary>
    /// Raised when the Secure Desktop state changes.
    /// Parameter: true = active (UAC visible), false = inactive.
    /// </summary>
    public event Action<bool>? OnSecureDesktopStateChanged;

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

        _logger.LogInformation("Requesting admin helper to launch SYSTEM helper (token duplication)");

        try
        {
            // Ask the admin helper to launch SYSTEM process via token duplication
            var response = await _adminHelper.LaunchSystemHelperAsync(_pipeName, _nonce);
            if (response?.Success != true)
            {
                _logger.LogError("Admin helper failed to launch SYSTEM helper: {Error}", response?.Error ?? "null response");
                return false;
            }

            _logger.LogInformation("SYSTEM helper launched. Waiting for pipe server to start...");

            // Small delay to let the process start and create the pipe
            await Task.Delay(1000);

            // Connect to the SYSTEM helper control pipe
            _logger.LogInformation("Connecting to SYSTEM helper pipe: {PipeName}", _pipeName);
            _pipeClient = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.None);
            await _pipeClient.ConnectAsync(15_000); // 15s timeout (schtask startup can be slow)
            _logger.LogInformation("Control pipe connected. Authenticating...");

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

            // Connect to the video pipe for Secure Desktop frames
            await ConnectVideoPipeAsync();

            // Connect to the notify pipe for server-push notifications
            await ConnectNotifyPipeAsync();

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
    /// Connect to the video pipe ({pipeName}_video) for receiving Secure Desktop JPEG frames.
    /// Non-critical — if it fails, secure desktop capture just won't work.
    /// </summary>
    private async Task ConnectVideoPipeAsync()
    {
        if (_pipeName == null) return;

        var videoPipeName = $"{_pipeName}_video";
        try
        {
            _videoPipe = new NamedPipeClientStream(".", videoPipeName, PipeDirection.In, PipeOptions.None);
            await _videoPipe.ConnectAsync(10_000); // 10s timeout
            _videoReader = new BinaryReader(_videoPipe);
            _logger.LogInformation("Video pipe connected: {PipeName}", videoPipeName);

            // Start background thread to read video frames
            _videoStopRequested = false;
            _videoReaderThread = new Thread(VideoReaderLoop)
            {
                Name = "SecureDesktopVideoReader",
                IsBackground = true
            };
            _videoReaderThread.Start();
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Video pipe connection timed out (10s) — secure desktop capture unavailable");
            CleanupVideoPipe();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect video pipe — secure desktop capture unavailable");
            CleanupVideoPipe();
        }
    }

    /// <summary>
    /// Background thread reading binary JPEG frames from the video pipe.
    /// Protocol: [uint32 length][jpeg bytes] per frame.
    /// </summary>
    private void VideoReaderLoop()
    {
        _logger.LogInformation("Video reader thread started");

        try
        {
            while (!_videoStopRequested && _videoPipe?.IsConnected == true && _videoReader != null)
            {
                try
                {
                    // Read frame: [uint32 length][jpeg bytes]
                    var length = _videoReader.ReadUInt32();
                    if (length == 0 || length > 10_000_000) // Sanity check: max 10MB per frame
                    {
                        _logger.LogWarning("Video frame invalid length: {Length}", length);
                        continue;
                    }

                    var jpegData = _videoReader.ReadBytes((int)length);
                    if (jpegData.Length != (int)length)
                    {
                        _logger.LogWarning("Video frame incomplete: expected {Expected}, got {Actual}", length, jpegData.Length);
                        break; // Pipe likely disconnected
                    }

                    // Use the last known secure desktop dimensions
                    _videoFrameCount++;
                    if (_videoFrameCount <= 3 || _videoFrameCount % 100 == 0)
                        _logger.LogInformation("SD video frame #{Count}: {Bytes}b, dims={W}x{H}, active={Active}",
                            _videoFrameCount, jpegData.Length, _secureDesktopWidth, _secureDesktopHeight, _isSecureDesktopActive);

                    OnSecureDesktopFrame?.Invoke(jpegData, _secureDesktopWidth, _secureDesktopHeight);
                }
                catch (EndOfStreamException)
                {
                    _logger.LogInformation("Video pipe closed (end of stream)");
                    break;
                }
                catch (IOException)
                {
                    _logger.LogInformation("Video pipe disconnected");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Video reader thread error");
        }

        _logger.LogInformation("Video reader thread exited");
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
    /// Set SD capture quality (fps + JPEG quality). Sent to SYSTEM helper which applies to SecureDesktopCapture.
    /// </summary>
    public async Task<bool> SetCaptureQualityAsync(int targetFps, int jpegQuality)
    {
        var response = await SendCommandAsync(new { command = "setCaptureQuality", targetFps, jpegQuality });
        if (response?.Success == true)
        {
            _logger.LogInformation("SetCaptureQuality: fps={Fps}, quality={Quality}", targetFps, jpegQuality);
            return true;
        }
        _logger.LogWarning("SetCaptureQuality failed: {Error}", response?.Error ?? "no response");
        return false;
    }

    /// <summary>
    /// Wake the Secure Desktop capture thread for immediate polling.
    /// Called after LockWorkStation() to reduce SD detection delay.
    /// </summary>
    public async Task WakeCaptureAsync()
    {
        try
        {
            await SendCommandAsync(new { command = "wakeCapture" });
        }
        catch { /* best-effort — detection still works via normal polling */ }
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
    /// Shut down the SYSTEM helper process and clean up pipes.
    /// </summary>
    public async Task ShutdownAsync()
    {
        try
        {
            await SendCommandAsync(new { command = "exit" });
        }
        catch { /* Helper may already be gone */ }

        CleanupVideoPipe();
        CleanupNotifyPipe();
        await CleanupPipeAsync();
    }

    /// <summary>
    /// Connect to the notify pipe ({pipeName}_notify) for receiving server-push notifications.
    /// Non-critical — if it fails, secure desktop state tracking just won't work.
    /// </summary>
    private async Task ConnectNotifyPipeAsync()
    {
        if (_pipeName == null) return;

        var notifyPipeName = $"{_pipeName}_notify";
        try
        {
            _notifyPipe = new NamedPipeClientStream(".", notifyPipeName, PipeDirection.In, PipeOptions.None);
            await _notifyPipe.ConnectAsync(10_000);
            _notifyReader = new StreamReader(_notifyPipe, PipeEncoding);
            _logger.LogInformation("Notify pipe connected: {PipeName}", notifyPipeName);

            // Start background thread to read notifications
            _notifyStopRequested = false;
            _notifyReaderThread = new Thread(NotifyReaderLoop)
            {
                Name = "SystemPipeNotifyReader",
                IsBackground = true
            };
            _notifyReaderThread.Start();
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Notify pipe connection timed out (10s)");
            CleanupNotifyPipe();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect notify pipe");
            CleanupNotifyPipe();
        }
    }

    /// <summary>
    /// Background thread that reads notifications from the dedicated notify pipe.
    /// This pipe only carries notifications — no command responses.
    /// </summary>
    private void NotifyReaderLoop()
    {
        _logger.LogInformation("Notify reader loop starting");

        try
        {
            while (!_notifyStopRequested && _notifyPipe?.IsConnected == true && _notifyReader != null)
            {
                var line = _notifyReader.ReadLine();
                if (line == null)
                {
                    _logger.LogInformation("Notify reader: pipe closed (null read)");
                    break;
                }

                _logger.LogInformation("Notify pipe recv: {Line}", line);
                TryHandleNotification(line);
            }
        }
        catch (IOException)
        {
            _logger.LogInformation("Notify reader: pipe disconnected");
        }
        catch (ObjectDisposedException)
        {
            _logger.LogInformation("Notify reader: pipe disposed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notify reader error");
        }

        _logger.LogInformation("Notify reader loop exited");
    }

    private async Task<ElevatedHelperClient.HelperResponse?> SendCommandAsync(object command)
    {
        if (_writer == null || !IsConnected)
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

            // Direct read — control pipe is command-response only (no notifications)
            var readTask = _reader!.ReadLineAsync();
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

    /// <summary>
    /// Check if a received line is a server-initiated notification (has "notification" property).
    /// If so, process it and return true. Otherwise return false.
    /// </summary>
    private bool TryHandleNotification(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("notification", out var notifProp))
                return false;

            var notification = notifProp.GetString();
            switch (notification)
            {
                case "secureDesktopActive":
                    _secureDesktopWidth = doc.RootElement.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                    _secureDesktopHeight = doc.RootElement.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                    _isSecureDesktopActive = true;
                    _logger.LogInformation("Secure Desktop ACTIVE ({W}x{H})", _secureDesktopWidth, _secureDesktopHeight);
                    OnSecureDesktopStateChanged?.Invoke(true);
                    break;

                case "secureDesktopInactive":
                    _isSecureDesktopActive = false;
                    _logger.LogInformation("Secure Desktop INACTIVE");
                    OnSecureDesktopStateChanged?.Invoke(false);
                    break;

                default:
                    _logger.LogWarning("Unknown notification: {Notification}", notification);
                    break;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void CleanupVideoPipe()
    {
        _videoStopRequested = true;
        try { _videoReader?.Dispose(); } catch { }
        _videoReader = null;
        try { _videoPipe?.Dispose(); } catch { }
        _videoPipe = null;
        _videoReaderThread = null; // IsBackground=true, will die with process
    }

    private void CleanupNotifyPipe()
    {
        _notifyStopRequested = true;
        try { _notifyReader?.Dispose(); } catch { }
        _notifyReader = null;
        try { _notifyPipe?.Dispose(); } catch { }
        _notifyPipe = null;
        _notifyReaderThread = null;
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
        CleanupVideoPipe();
        CleanupNotifyPipe();
        await CleanupPipeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await ShutdownAsync();
    }
}
