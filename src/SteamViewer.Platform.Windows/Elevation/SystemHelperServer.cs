using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Collections.Concurrent;
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
public static partial class SystemHelperServer
{
    private static readonly UTF8Encoding PipeEncoding = new(encoderShouldEmitUTF8Identifier: false);

    // Window station — SYSTEM process may need to attach to WinSta0 (interactive)
    // (Run is the only consumer; Input partial owns its own desktop-switching P/Invokes.)
    private const uint WINSTA_ALL_ACCESS = 0x37F;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenWindowStation(string lpszWinSta, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessWindowStation(IntPtr hWinSta);

    private static string? _debugPath;
    private static string? _debugPathLocal;

    private static void DebugLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Console.WriteLine($"[SystemHelper] {message}");
        try { if (_debugPath != null) File.AppendAllText(_debugPath, line + "\n"); } catch { }
        try { if (_debugPathLocal != null) File.AppendAllText(_debugPathLocal, line + "\n"); } catch { }
    }

    /// <summary>
    /// Run the SYSTEM helper pipe server. Blocks until the client disconnects or sends "exit".
    /// First message from client must be authenticate with the correct nonce.
    /// expectedClientPid + allowedUserSid restrict pipe access to one specific process running
    /// as the launching user; without this, any local user could connect (LPE).
    /// </summary>
    public static void Run(string pipeName, string expectedNonce, uint expectedClientPid, string allowedUserSid)
    {
        // Use CommonApplicationData (C:\ProgramData) — SYSTEM user's %LOCALAPPDATA% is different
        _debugPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SteamViewer", "system-helper-debug.txt");
        try { Directory.CreateDirectory(Path.GetDirectoryName(_debugPath)!); } catch { }

        // Also log next to exe (readable via network share from Dev PC)
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (exeDir != null)
        {
            _debugPathLocal = Path.Combine(exeDir, "logs", "system-helper-debug.txt");
            try { Directory.CreateDirectory(Path.GetDirectoryName(_debugPathLocal)!); } catch { }
        }

        DebugLog($"Starting SYSTEM pipe server: {pipeName} (PID {Environment.ProcessId})");
        DebugLog($"Running as: {Environment.UserName} (SYSTEM expected)");

        // Attach to interactive window station — SYSTEM process may be in Service-0x0-3e7$
        var hWinSta = OpenWindowStation("WinSta0", false, WINSTA_ALL_ACCESS);
        if (hWinSta != IntPtr.Zero)
        {
            if (SetProcessWindowStation(hWinSta))
                DebugLog("Attached to WinSta0 (interactive window station)");
            else
                DebugLog($"SetProcessWindowStation failed (error {Marshal.GetLastWin32Error()})");
            // Don't close — needed for process lifetime
        }
        else
        {
            DebugLog($"OpenWindowStation('WinSta0') failed (error {Marshal.GetLastWin32Error()})");
        }

        // Enable SeTcbPrivilege — required for SendSAS(false) to actually trigger the lock screen
        EnableTcbPrivilege();

        // Enable software-generated SAS — required for SendSAS(false) to work
        EnsureSoftwareSASEnabled();

        // Spawn dedicated input thread — SetThreadDesktop must be the very first user32 call
        _inputQueue = new BlockingCollection<(string json, int sw, int sh)>();
        _inputThread = new Thread(() => InputThreadProc())
        {
            IsBackground = true,
            Name = "SystemHelper-Input"
        };
        _inputThread.SetApartmentState(ApartmentState.MTA);
        _inputThread.Start();

        // Resolve the allowed user SID once - reused for all three pipes.
        SecurityIdentifier userSid;
        try
        {
            userSid = new SecurityIdentifier(allowedUserSid);
        }
        catch (Exception ex)
        {
            DebugLog($"Invalid allowedUserSid '{allowedUserSid}': {ex.Message}");
            return;
        }

        try
        {
            // ACL: only the launching user. Replaces AuthenticatedUserSid which allowed any local user (LPE).
            var pipeSecurity = PipeAcl.ForUserSid(userSid);

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

            // Defense in depth: verify the connected client is the expected admin helper PID.
            if (!PipeAuth.TryGetClientProcessId(pipeServer, out var actualClientPid))
            {
                DebugLog("Could not determine client PID. Refusing connection.");
                return;
            }
            if (actualClientPid != expectedClientPid)
            {
                DebugLog($"Client PID mismatch: expected {expectedClientPid}, got {actualClientPid}. Refusing connection.");
                return;
            }
            DebugLog($"Client PID {actualClientPid} matches expected. Waiting for authentication...");

            using var reader = new StreamReader(pipeServer, PipeEncoding);
            using var writer = new StreamWriter(pipeServer, PipeEncoding) { AutoFlush = true };

            // First message MUST be authentication with the correct nonce
            if (!Authenticate(reader, writer, expectedNonce))
            {
                DebugLog("Authentication failed. Disconnecting.");
                return;
            }

            DebugLog("Authentication succeeded. Starting video pipe and Secure Desktop capture...");

            // Create video pipe for binary BGRA frames (server -> client, outbound only)
            var videoPipeName = $"{pipeName}_video";
            var videoPipeSecurity = PipeAcl.ForUserSid(userSid);

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

                    // Verify client PID matches the expected admin helper.
                    if (!PipeAuth.TryGetClientProcessId(_videoPipeServer, out var videoClientPid)
                        || videoClientPid != expectedClientPid)
                    {
                        DebugLog($"Video pipe: client PID {videoClientPid} != expected {expectedClientPid}. Refusing.");
                        try { _videoPipeServer.Disconnect(); } catch { }
                        return;
                    }

                    lock (_videoWriteLock)
                    {
                        _videoWriter = new BinaryWriter(_videoPipeServer);
                        _videoConnected = true;
                    }
                    DebugLog($"Video pipe client connected (PID {videoClientPid})");

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

            // Create notify pipe for server-push notifications (server → client, outbound only)
            var notifyPipeName = $"{pipeName}_notify";
            var notifyPipeSecurity = PipeAcl.ForUserSid(userSid);

            _notifyPipeServer = NamedPipeServerStreamAcl.Create(
                notifyPipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.None,
                0, 0,
                notifyPipeSecurity);

            DebugLog($"Notify pipe created: {notifyPipeName}. Waiting for client (non-blocking)...");

            var notifyConnectThread = new Thread(() =>
            {
                try
                {
                    _notifyPipeServer.WaitForConnection();

                    // Verify client PID matches the expected admin helper.
                    if (!PipeAuth.TryGetClientProcessId(_notifyPipeServer, out var notifyClientPid)
                        || notifyClientPid != expectedClientPid)
                    {
                        DebugLog($"Notify pipe: client PID {notifyClientPid} != expected {expectedClientPid}. Refusing.");
                        try { _notifyPipeServer.Disconnect(); } catch { }
                        return;
                    }

                    lock (_notifyWriteLock)
                    {
                        _notifyWriter = new StreamWriter(_notifyPipeServer, PipeEncoding) { AutoFlush = true };
                        _notifyConnected = true;
                    }
                    DebugLog($"Notify pipe client connected (PID {notifyClientPid})");
                }
                catch (Exception ex)
                {
                    DebugLog($"Notify pipe WaitForConnection error: {ex.Message}");
                }
            })
            {
                Name = "NotifyPipeConnect",
                IsBackground = true
            };
            notifyConnectThread.Start();

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

            // Signal input thread to exit (unblocks GetConsumingEnumerable)
            try { _inputQueue?.CompleteAdding(); } catch { }
            _inputThread?.Join(2000);

            CleanupCapture();
            CleanupVideoPipe();
            CleanupNotifyPipe();

            DebugLog("Exiting normally.");
        }
        catch (Exception ex)
        {
            DebugLog($"FATAL: {ex}");
            try { _inputQueue?.CompleteAdding(); } catch { }
            CleanupCapture();
            CleanupVideoPipe();
            CleanupNotifyPipe();
        }
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
            "wakeCapture" => HandleWakeCapture(),
            "exit" => HandleExit(),
            _ => JsonSerializer.Serialize(new HelperResponse(false, $"Unknown command: {command}"))
        };
    }

    private static string HandleWakeCapture()
    {
        _capture?.WakePolling();
        return JsonSerializer.Serialize(new HelperResponse(true, null));
    }

    private static string HandleExit()
    {
        DebugLog("Exit command received");
        return JsonSerializer.Serialize(new HelperResponse(true, null));
    }

    private record HelperResponse(bool Success, string? Error);
}
