using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SteamViewer.Common.Protocol;
using SteamViewer.Platform.Windows.Input;

namespace SteamViewer.Platform.Windows.Elevation;

/// <summary>
/// Named pipe server that runs in the SYSTEM-level helper process.
/// Launched via scheduled task: SteamViewer.App.exe --system-helper {pipeName} {nonce}.
/// Handles privileged operations that require SYSTEM: Secure Desktop capture, SendSAS,
/// RunAsSystem (launch processes as SYSTEM in user session), and input injection.
/// Never initializes MAUI — runs as a headless pipe server.
/// </summary>
public static class SystemHelperServer
{
    private static readonly UTF8Encoding PipeEncoding = new(encoderShouldEmitUTF8Identifier: false);

    [DllImport("sas.dll", SetLastError = true)]
    private static extern void SendSAS(bool asUser);

    private static string? _debugPath;
    private static SecureDesktopCapture? _capture;
    private static StreamWriter? _controlWriter;
    private static readonly object _controlWriteLock = new();

    // Video pipe for binary JPEG frames (server → client)
    private static NamedPipeServerStream? _videoPipeServer;
    private static BinaryWriter? _videoWriter;
    private static readonly object _videoWriteLock = new();
    private static volatile bool _videoConnected;

    private static void DebugLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Console.WriteLine($"[SystemHelper] {message}");
        try { if (_debugPath != null) File.AppendAllText(_debugPath, line + "\n"); } catch { }
    }

    /// <summary>
    /// Run the SYSTEM helper pipe server. Blocks until the client disconnects or sends "exit".
    /// First message from client must be authenticate with the correct nonce.
    /// </summary>
    public static void Run(string pipeName, string expectedNonce)
    {
        // Use CommonApplicationData (C:\ProgramData) — SYSTEM user's %LOCALAPPDATA% is different
        _debugPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SteamViewer", "system-helper-debug.txt");
        try { Directory.CreateDirectory(Path.GetDirectoryName(_debugPath)!); } catch { }

        DebugLog($"Starting SYSTEM pipe server: {pipeName} (PID {Environment.ProcessId})");
        DebugLog($"Running as: {Environment.UserName} (SYSTEM expected)");

        try
        {
            // Allow authenticated users to connect (main app runs as regular user)
            var pipeSecurity = new PipeSecurity();
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));

            using var pipeServer = NamedPipeServerStreamAcl.Create(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.None,
                0, 0,
                pipeSecurity);

            DebugLog("Control pipe created (sync mode). Waiting for client connection...");

            // Wait for client with 30s timeout
            var connected = false;
            var connectThread = new Thread(() =>
            {
                try
                {
                    pipeServer.WaitForConnection();
                    connected = true;
                }
                catch (Exception ex)
                {
                    DebugLog($"WaitForConnection error: {ex.Message}");
                }
            });
            connectThread.Start();
            if (!connectThread.Join(TimeSpan.FromSeconds(30)))
            {
                DebugLog("Timeout waiting for client. Exiting.");
                return;
            }

            if (!connected)
            {
                DebugLog("WaitForConnection failed. Exiting.");
                return;
            }

            DebugLog("Client connected. Waiting for authentication...");

            using var reader = new StreamReader(pipeServer, PipeEncoding);
            using var writer = new StreamWriter(pipeServer, PipeEncoding) { AutoFlush = true };
            _controlWriter = writer;

            // First message MUST be authentication with the correct nonce
            if (!Authenticate(reader, writer, expectedNonce))
            {
                DebugLog("Authentication failed. Disconnecting.");
                return;
            }

            DebugLog("Authentication succeeded. Starting video pipe and Secure Desktop capture...");

            // Create video pipe for binary JPEG frames (server → client, outbound only)
            var videoPipeName = $"{pipeName}_video";
            var videoPipeSecurity = new PipeSecurity();
            videoPipeSecurity.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));

            _videoPipeServer = NamedPipeServerStreamAcl.Create(
                videoPipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.None,
                0, 0,
                videoPipeSecurity);

            DebugLog($"Video pipe created: {videoPipeName}. Waiting for client (non-blocking)...");

            // Create Secure Desktop capture and subscribe events (before thread starts)
            _capture = new SecureDesktopCapture();
            _capture.OnSecureDesktopActive += OnCaptureSecureDesktopActive;
            _capture.OnSecureDesktopInactive += OnCaptureSecureDesktopInactive;
            _capture.OnFrameCaptured += OnCaptureFrameCaptured;

            // Wait for video pipe connection on a background thread (non-blocking).
            // The client connects AFTER control pipe auth+ping, so we must not block the main thread
            // or the command loop won't be able to process the ping and the client will never
            // reach ConnectVideoPipeAsync().
            // Capture starts AFTER pipe connects to avoid dropping frames.
            var videoConnectThread = new Thread(() =>
            {
                try
                {
                    _videoPipeServer.WaitForConnection();
                    lock (_videoWriteLock)
                    {
                        _videoWriter = new BinaryWriter(_videoPipeServer);
                        _videoConnected = true;
                    }
                    DebugLog("Video pipe client connected");

                    // Start capture AFTER pipe is connected — frames go directly to pipe
                    _capture.Start();
                    DebugLog("Secure Desktop capture started");
                }
                catch (Exception ex)
                {
                    DebugLog($"Video pipe WaitForConnection error: {ex.Message}");
                }
            })
            {
                Name = "VideoPipeConnect",
                IsBackground = true
            };
            videoConnectThread.Start();

            DebugLog("Processing commands...");

            while (pipeServer.IsConnected)
            {
                string? line;
                try
                {
                    line = reader.ReadLine();
                }
                catch (IOException ex)
                {
                    DebugLog($"Pipe read error (client disconnected?): {ex.Message}");
                    break;
                }
                catch (Exception ex)
                {
                    DebugLog($"Unexpected read error: {ex}");
                    break;
                }

                if (line == null)
                {
                    DebugLog("Client closed connection (null read).");
                    break;
                }

                if (line.Length < 200) // Don't log full input events (noisy)
                    DebugLog($"Received: {line}");

                try
                {
                    var response = HandleCommand(line);
                    if (response != null)
                    {
                        DebugLog($"Sending: {response}");
                        writer.WriteLine(response);
                    }
                }
                catch (IOException ex)
                {
                    DebugLog($"Pipe write error (broken pipe): {ex.Message}");
                    break;
                }
                catch (Exception ex)
                {
                    DebugLog($"Command error: {ex.Message}");
                    var errorResponse = JsonSerializer.Serialize(new HelperResponse(false, ex.Message));
                    try { writer.WriteLine(errorResponse); } catch { break; }
                }
            }

            // Cleanup
            DebugLog("Client disconnected. Cleaning up...");
            CleanupCapture();
            CleanupVideoPipe();
            _controlWriter = null;

            DebugLog("Exiting normally.");
        }
        catch (Exception ex)
        {
            DebugLog($"FATAL: {ex}");
            CleanupCapture();
            CleanupVideoPipe();
        }
    }

    private static void CleanupCapture()
    {
        if (_capture != null)
        {
            _capture.OnSecureDesktopActive -= OnCaptureSecureDesktopActive;
            _capture.OnSecureDesktopInactive -= OnCaptureSecureDesktopInactive;
            _capture.OnFrameCaptured -= OnCaptureFrameCaptured;
            _capture.Dispose();
            _capture = null;
            DebugLog("Secure Desktop capture disposed");
        }
    }

    private static void CleanupVideoPipe()
    {
        _videoConnected = false;
        try { _videoWriter?.Dispose(); } catch { }
        _videoWriter = null;
        try { _videoPipeServer?.Dispose(); } catch { }
        _videoPipeServer = null;
    }

    #region SecureDesktopCapture event handlers

    private static void OnCaptureSecureDesktopActive(int width, int height)
    {
        DebugLog($"Secure Desktop active notification → control pipe ({width}x{height})");
        SendNotification(new { notification = "secureDesktopActive", width, height });
    }

    private static void OnCaptureSecureDesktopInactive()
    {
        DebugLog("Secure Desktop inactive notification → control pipe");
        SendNotification(new { notification = "secureDesktopInactive" });
    }

    private static int _frameCount;

    private static void OnCaptureFrameCaptured(byte[] jpegData, int width, int height)
    {
        if (!_videoConnected || _videoWriter == null) return;

        lock (_videoWriteLock)
        {
            try
            {
                _frameCount++;
                if (_frameCount <= 3 || _frameCount % 100 == 0)
                    DebugLog($"Video frame #{_frameCount}: {jpegData.Length} bytes ({width}x{height})");

                // Binary frame protocol: [uint32 length][jpeg bytes]
                _videoWriter.Write((uint)jpegData.Length);
                _videoWriter.Write(jpegData);
                _videoWriter.Flush();
            }
            catch (IOException)
            {
                // Video pipe disconnected
                DebugLog("Video pipe write failed (disconnected)");
                _videoConnected = false;
            }
            catch (Exception ex)
            {
                DebugLog($"Video frame write error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Send a server-initiated notification over the control pipe.
    /// These are unsolicited messages (not responses to commands).
    /// </summary>
    private static void SendNotification(object notification)
    {
        if (_controlWriter == null) return;

        lock (_controlWriteLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(notification);
                _controlWriter.WriteLine(json);
            }
            catch (Exception ex)
            {
                DebugLog($"Notification write error: {ex.Message}");
            }
        }
    }

    #endregion

    private static bool Authenticate(StreamReader reader, StreamWriter writer, string expectedNonce)
    {
        try
        {
            var line = reader.ReadLine();
            if (line == null)
            {
                DebugLog("Authentication: client disconnected before sending nonce.");
                return false;
            }

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            var command = root.TryGetProperty("command", out var cmdProp) ? cmdProp.GetString() : null;
            var nonce = root.TryGetProperty("nonce", out var nonceProp) ? nonceProp.GetString() : null;

            if (command != "authenticate" || nonce == null)
            {
                DebugLog($"Authentication: expected authenticate command, got: {command}");
                writer.WriteLine(JsonSerializer.Serialize(new HelperResponse(false, "Expected authenticate command")));
                return false;
            }

            // Constant-time comparison to prevent timing attacks
            if (!CryptographicEquals(nonce, expectedNonce))
            {
                DebugLog("Authentication: nonce mismatch");
                writer.WriteLine(JsonSerializer.Serialize(new HelperResponse(false, "Invalid nonce")));
                return false;
            }

            writer.WriteLine(JsonSerializer.Serialize(new HelperResponse(true, null)));
            return true;
        }
        catch (Exception ex)
        {
            DebugLog($"Authentication error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Constant-time string comparison to prevent timing attacks on nonce validation.
    /// </summary>
    private static bool CryptographicEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (int i = 0; i < a.Length; i++)
            diff |= a[i] ^ b[i];
        return diff == 0;
    }

    private static string? HandleCommand(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var command = doc.RootElement.GetProperty("command").GetString();

        if (command != "injectInput")
            DebugLog($"Command: {command}");

        return command switch
        {
            "ping" => JsonSerializer.Serialize(new HelperResponse(true, null)),
            "sendSAS" => HandleSendSAS(),
            "runAsSystem" => HandleRunAsSystem(doc.RootElement),
            "injectInput" => HandleInjectInput(json, doc.RootElement),
            "exit" => HandleExit(),
            _ => JsonSerializer.Serialize(new HelperResponse(false, $"Unknown command: {command}"))
        };
    }

    private static string HandleSendSAS()
    {
        try
        {
            SendSAS(false);
            DebugLog("SendSAS succeeded");
            return JsonSerializer.Serialize(new HelperResponse(true, null));
        }
        catch (Exception ex)
        {
            DebugLog($"SendSAS failed: {ex.Message}");
            return JsonSerializer.Serialize(new HelperResponse(false, ex.Message));
        }
    }

    private static string HandleRunAsSystem(JsonElement root)
    {
        try
        {
            var path = root.TryGetProperty("path", out var p) ? p.GetString() : null;
            var args = root.TryGetProperty("args", out var a) ? a.GetString() : null;

            if (string.IsNullOrEmpty(path))
                return JsonSerializer.Serialize(new HelperResponse(false, "No path specified"));

            if (!File.Exists(path))
                return JsonSerializer.Serialize(new HelperResponse(false, $"File not found: {path}"));

            DebugLog($"RunAsSystem: {path} {args}");

            // Launch process as SYSTEM in the user's desktop session
            if (ProcessLauncher.LaunchInUserSession(path, args, out var pid))
            {
                DebugLog($"RunAsSystem launched PID {pid}");
                return JsonSerializer.Serialize(new HelperResponse(true, null));
            }

            return JsonSerializer.Serialize(new HelperResponse(false, "Failed to launch process in user session"));
        }
        catch (Exception ex)
        {
            DebugLog($"RunAsSystem failed: {ex.Message}");
            return JsonSerializer.Serialize(new HelperResponse(false, ex.Message));
        }
    }

    private static string? HandleInjectInput(string rawJson, JsonElement root)
    {
        try
        {
            var sw = root.TryGetProperty("sw", out var swProp) ? swProp.GetInt32() : 1920;
            var sh = root.TryGetProperty("sh", out var shProp) ? shProp.GetInt32() : 1080;

            // If Secure Desktop is active, route input to the Winlogon desktop
            if (_capture != null && _capture.IsActive)
            {
                // Strip the command/sw/sh wrapper — pass just the input event JSON
                // The rawJson has command + sw + sh + the rest of the input event fields.
                // SecureDesktopCapture.InjectInputOnWinlogon parses type/x/y/etc directly.
                _capture.InjectInputOnWinlogon(rawJson, sw, sh);
                return null; // Fire-and-forget
            }

            // Normal (Default desktop) injection
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "mouse_move":
                    Win32Input.InjectMouseMove(
                        root.GetProperty("x").GetDouble(),
                        root.GetProperty("y").GetDouble(),
                        sw, sh);
                    break;
                case "mouse_down":
                    Win32Input.InjectMouseButton(
                        ParseMouseButton(root.GetProperty("button").GetString()),
                        root.GetProperty("x").GetDouble(),
                        root.GetProperty("y").GetDouble(),
                        sw, sh, isDown: true);
                    break;
                case "mouse_up":
                    Win32Input.InjectMouseButton(
                        ParseMouseButton(root.GetProperty("button").GetString()),
                        root.GetProperty("x").GetDouble(),
                        root.GetProperty("y").GetDouble(),
                        sw, sh, isDown: false);
                    break;
                case "mouse_wheel":
                    Win32Input.InjectMouseWheel(
                        root.GetProperty("delta_x").GetDouble(),
                        root.GetProperty("delta_y").GetDouble());
                    break;
                case "key_down":
                    Win32Input.InjectKey(
                        root.GetProperty("key").GetString()!,
                        ParseModifiers(root),
                        isDown: true);
                    break;
                case "key_up":
                    Win32Input.InjectKey(
                        root.GetProperty("key").GetString()!,
                        ParseModifiers(root),
                        isDown: false);
                    break;
            }
        }
        catch (Exception ex)
        {
            DebugLog($"InjectInput error: {ex.Message}");
        }

        return null; // Fire-and-forget
    }

    private static MouseButton ParseMouseButton(string? button) => button switch
    {
        "Left" => MouseButton.Left,
        "Right" => MouseButton.Right,
        "Middle" => MouseButton.Middle,
        _ => MouseButton.Left
    };

    private static KeyModifiers ParseModifiers(JsonElement root)
    {
        if (!root.TryGetProperty("modifiers", out var mods))
            return KeyModifiers.None;

        return new KeyModifiers(
            mods.TryGetProperty("ctrl", out var c) && c.GetBoolean(),
            mods.TryGetProperty("shift", out var s) && s.GetBoolean(),
            mods.TryGetProperty("alt", out var a) && a.GetBoolean(),
            mods.TryGetProperty("meta", out var m) && m.GetBoolean());
    }

    private static string HandleExit()
    {
        DebugLog("Exit command received");
        return JsonSerializer.Serialize(new HelperResponse(true, null));
    }

    private record HelperResponse(bool Success, string? Error);
}
