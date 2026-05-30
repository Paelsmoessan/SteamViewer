using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using SteamViewer.Common.Logging;
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

    // Set by HandleExit so RunCommandLoop breaks after acknowledging, then the process terminates.
    private static bool _exitRequested;

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
    public static void Run(SystemHelperArgs args)
    {
        var pipeName = args.PipeName;
        var expectedNonce = args.ExpectedNonce;
        var expectedClientPid = args.ExpectedClientPid;
        var allowedUserSid = args.AllowedUserSid;
        var adminPid = args.AdminPid;

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

        // Orphan-proofing: self-terminate if the host dies by any path (crash / taskkill / window-X).
        // expectedClientPid is the host PID (passed through by the admin helper). Armed before the
        // connect-wait. See .claude/research/elevated-helper-lifecycle.
        ParentDeathWatchdog.Arm(expectedClientPid, DebugLog, "host");
        // B3: also watch the admin helper that launched us. If it dies (e.g. the host disposes the
        // elevation service and kills the admin helper) we self-terminate even if the host lives.
        ParentDeathWatchdog.Arm(adminPid, DebugLog, "admin helper");

        // B4: register so the next admin helper can reap us if we ever orphan. (The admin helper does
        // the reaping with SeDebug; the SYSTEM helper only registers/deregisters.)
        HelperRegistry.Register(expectedClientPid, "system", DebugLog);

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

            StartVideoPipe(pipeName, userSid, expectedClientPid);

            StartNotifyPipe(pipeName, userSid, expectedClientPid);

            DebugLog("Processing commands...");
            RunCommandLoop(pipeServer, reader, writer);

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

        // Guarantee process termination after cleanup. The SYSTEM helper holds background threads
        // (input/video/notify) that could otherwise keep the orphaned privileged process alive.
        DebugLog("Run() complete - terminating SYSTEM helper process (Environment.Exit(0)).");
        HelperRegistry.Deregister(DebugLog);
        Environment.Exit(0);
    }

    private static void RunCommandLoop(NamedPipeServerStream pipeServer, StreamReader reader, StreamWriter writer)
    {
        DebugLog($"RunCommandLoop entry: pipeServer.IsConnected={pipeServer.IsConnected}");
        var commandCount = 0;
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

            // Don't log full input events (noisy), and skip the ~60Hz mouse_move injectInput flood
            // (mouse_move is ~110 chars so it slips under the length cap; coordinates are captured in
            // the input-debug log instead). Key/click events and other commands stay visible.
            if (line.Length < 200 && !line.Contains("mouse_move"))
                DebugLog($"Received: {LogSanitizer.MaskJsonSecrets(line)}");
            commandCount++;

            try
            {
                var response = HandleCommand(line);
                if (response != null)
                {
                    DebugLog($"Sending: {LogSanitizer.MaskJsonSecrets(response)}");
                    writer.WriteLine(response);
                }
                if (_exitRequested)
                {
                    DebugLog("Exit acknowledged - breaking command loop to terminate helper.");
                    break;
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
        DebugLog($"RunCommandLoop exit: processed {commandCount} command(s), pipeServer.IsConnected={pipeServer.IsConnected}");
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
        DebugLog("Exit command received - will terminate after sending acknowledgement.");
        _exitRequested = true;
        return JsonSerializer.Serialize(new HelperResponse(true, null));
    }
}

/// <summary>
/// Launch arguments for SystemHelperServer.Run. Bundled into a record to keep the entry-point
/// signature within CS's argument-count budget; the per-field semantics match the cmdline shape
/// `--system-helper {pipeName} {nonce} {expectedClientPid} {allowedUserSid} {adminPid}`.
/// </summary>
public readonly record struct SystemHelperArgs(
    string PipeName,
    string ExpectedNonce,
    uint ExpectedClientPid,
    string AllowedUserSid,
    uint AdminPid = 0);
