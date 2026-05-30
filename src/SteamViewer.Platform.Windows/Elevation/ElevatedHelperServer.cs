using Microsoft.Win32;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SteamViewer.Client.Core.Session;
using SteamViewer.Common.Logging;
using SteamViewer.Common.Protocol;
using SteamViewer.Platform.Windows.Input;

namespace SteamViewer.Platform.Windows.Elevation;

/// <summary>
/// Named pipe server that runs in the elevated (admin) process.
/// Launched via SteamViewer.App.exe --elevated-helper {pipeName}.
/// Handles privileged operations: SendSAS, reboot with RunOnceEx, run processes elevated.
/// Never initializes MAUI — runs as a headless pipe server.
/// </summary>
public static class ElevatedHelperServer
{
    private static readonly UTF8Encoding PipeEncoding = new(encoderShouldEmitUTF8Identifier: false);

    [DllImport("sas.dll", SetLastError = true)]
    private static extern void SendSAS(bool asUser);

    private static string? _debugPath;
    private static string? _debugPathLocal;

    // Host app PID — the SYSTEM helper's pipe gate must check this, not Environment.ProcessId.
    private static uint _hostClientPid;

    // Set by HandleExit so the command loop breaks after acknowledging, then the process terminates.
    private static bool _exitRequested;

    private static void DebugLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Console.WriteLine($"[ElevatedHelper] {message}");
        try { if (_debugPath != null) File.AppendAllText(_debugPath, line + "\n"); } catch { }
        try { if (_debugPathLocal != null) File.AppendAllText(_debugPathLocal, line + "\n"); } catch { }
    }

    /// <summary>
    /// Run the elevated helper pipe server. Blocks until the client disconnects or sends "exit".
    /// expectedClientPid is the parent SteamViewer process that launched us via UAC; only that
    /// PID is allowed to connect (defense in depth on top of the same-user ACL).
    /// </summary>
    public static void Run(string pipeName, uint expectedClientPid)
    {
        _debugPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamViewer", "helper-debug.txt");
        try { Directory.CreateDirectory(Path.GetDirectoryName(_debugPath)!); } catch { }

        // Also log next to exe (readable via network share from Dev PC)
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (exeDir != null)
        {
            _debugPathLocal = Path.Combine(exeDir, "logs", "helper-debug.txt");
            try { Directory.CreateDirectory(Path.GetDirectoryName(_debugPathLocal)!); } catch { }
        }

        DebugLog($"Starting pipe server: {pipeName} (PID {Environment.ProcessId}, expecting client PID {expectedClientPid})");
        _hostClientPid = expectedClientPid;

        InitializeHelperLifecycle(expectedClientPid);

        try
        {
            // ACL: restrict to the user SID that launched us. UAC elevation does not change user identity,
            // so the current user SID matches the parent SteamViewer's user SID.
            // Replaces the previous AuthenticatedUserSid ACL which allowed any local user to connect — LPE.
            var pipeSecurity = PipeAcl.CurrentUserOnly();

            // PipeOptions.None (synchronous) — sync ReadLine() hangs on async pipes
            using var pipeServer = NamedPipeServerStreamAcl.Create(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.None,
                0, 0,
                pipeSecurity);

            DebugLog("Pipe created (sync mode). Waiting for client connection...");

            // Wait for client with 30s timeout (sync pipe, use thread for timeout)
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

            // Defense in depth: verify the connected client is the expected parent PID.
            // Without this, any process running as the same user (e.g. malware) could connect.
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
            DebugLog($"Client PID {actualClientPid} matches expected. Processing commands...");

            using var reader = new StreamReader(pipeServer, PipeEncoding);
            using var writer = new StreamWriter(pipeServer, PipeEncoding) { AutoFlush = true };

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

                // Skip the ~60Hz mouse_move injectInput flood (coordinates are captured in the
                // input-debug log); everything else stays visible.
                if (!line.Contains("mouse_move"))
                    DebugLog($"Received: {LogSanitizer.MaskJsonSecrets(line)}");

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

            DebugLog("Client disconnected. Exiting normally.");
        }
        catch (Exception ex)
        {
            DebugLog($"FATAL: {ex}");
        }

        // Guarantee process termination on every Run() exit (normal disconnect, exit-command, FATAL).
        // Without this the privileged pipe endpoint could linger if any non-background thread survives.
        DebugLog("Run() complete - terminating helper process (Environment.Exit(0)).");
        HelperRegistry.Deregister(DebugLog);
        Environment.Exit(0);
    }

    private static string? HandleCommand(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var command = doc.RootElement.GetProperty("command").GetString();

        // Don't log injectInput (too frequent — 60+ Hz mouse events)
        if (command != "injectInput")
            DebugLog($"Command: {command}");

        return command switch
        {
            "ping" => JsonSerializer.Serialize(new HelperResponse(true, null)),
            "sendSAS" => HandleSendSAS(),
            "reboot" => HandleReboot(doc.RootElement),
            "runElevated" => HandleRunElevated(doc.RootElement),
            "launchSystemHelper" => HandleLaunchSystemHelper(doc.RootElement),
            "injectInput" => HandleInjectInput(doc.RootElement),
            "exit" => HandleExit(),
            _ => JsonSerializer.Serialize(new HelperResponse(false, $"Unknown command: {command}"))
        };
    }

    private static string HandleSendSAS()
    {
        try
        {
            SendSAS(false);
            DebugLog("SendSAS called (note: may be silently ignored from admin context — SYSTEM required for reliable SAS)");
            return JsonSerializer.Serialize(new HelperResponse(true, null));
        }
        catch (Exception ex)
        {
            DebugLog($"SendSAS failed: {ex.Message}");
            return JsonSerializer.Serialize(new HelperResponse(false, ex.Message));
        }
    }

    private static string HandleRunElevated(JsonElement root)
    {
        try
        {
            var path = root.TryGetProperty("path", out var p) ? p.GetString() : null;
            var args = root.TryGetProperty("args", out var a) ? a.GetString() : null;

            if (string.IsNullOrEmpty(path))
                return JsonSerializer.Serialize(new HelperResponse(false, "No path specified"));

            // Validate the path exists (security: only allow launching real executables)
            if (!File.Exists(path))
                return JsonSerializer.Serialize(new HelperResponse(false, $"File not found: {path}"));

            DebugLog($"RunElevated: {path} {args}");

            var psi = new ProcessStartInfo
            {
                FileName = path,
                Arguments = args ?? "",
                UseShellExecute = false
            };

            var proc = Process.Start(psi);
            if (proc == null)
                return JsonSerializer.Serialize(new HelperResponse(false, "Failed to start process"));

            DebugLog($"RunElevated launched PID {proc.Id}");
            return JsonSerializer.Serialize(new HelperResponse(true, null));
        }
        catch (Exception ex)
        {
            DebugLog($"RunElevated failed: {ex.Message}");
            return JsonSerializer.Serialize(new HelperResponse(false, ex.Message));
        }
    }

    private static string HandleReboot(JsonElement root)
    {
        try
        {
            var appPath = Environment.ProcessPath;

            SaveRebootReconnectCredentials(root);
            DisablePressCtrlAltDelAtLogin();
            if (!string.IsNullOrEmpty(appPath))
            {
                CreateBootRelaySchtask(appPath);
                RegisterRunOnceExSasEntry(appPath);
            }
            InitiateSystemReboot();

            return JsonSerializer.Serialize(new HelperResponse(true, null));
        }
        catch (Exception ex)
        {
            DebugLog($"Reboot failed: {ex.Message}");
            return JsonSerializer.Serialize(new HelperResponse(false, ex.Message));
        }
    }

    /// <summary>
    /// Persist reconnect credentials so the boot relay + post-login SAS path can reconstitute the
    /// session after the reboot. No-op if the viewer didn't pass the credential fields (e.g. legacy
    /// caller). Saves serverUrl + STUN/TURN config so the SYSTEM-context boot relay can reach the
    /// signaling server before any user logs in.
    /// </summary>
    private static void SaveRebootReconnectCredentials(JsonElement root)
    {
        var creds = ParseRebootRequest(root);
        if (string.IsNullOrEmpty(creds.ClientId) || string.IsNullOrEmpty(creds.PasswordHash) || string.IsNullOrEmpty(creds.ViewerPeerId))
            return;

        try
        {
            ReconnectCredentials.Save(creds.ClientId, creds.PasswordHash, creds.ViewerPeerId,
                creds.ServerUrl, creds.StunUrls, creds.TurnUrls, creds.TurnUsername, creds.TurnCredential);
            DebugLog($"Saved reconnect credentials (serverUrl={creds.ServerUrl}, stunUrls={creds.StunUrls?.Length ?? 0}, turnUrls={creds.TurnUrls?.Length ?? 0})");
        }
        catch (Exception ex)
        {
            DebugLog($"Failed to save reconnect credentials: {ex.Message}");
        }
    }

    private readonly record struct RebootRequestPayload(
        string? ClientId, string? PasswordHash, string? ViewerPeerId, string? ServerUrl,
        string? TurnUsername, string? TurnCredential, string[]? StunUrls, string[]? TurnUrls);

    private static RebootRequestPayload ParseRebootRequest(JsonElement root) => new(
        ClientId: GetString(root, "clientId"),
        PasswordHash: GetString(root, "passwordHash"),
        ViewerPeerId: GetString(root, "viewerPeerId"),
        ServerUrl: GetString(root, "serverUrl"),
        TurnUsername: GetString(root, "turnUsername"),
        TurnCredential: GetString(root, "turnCredential"),
        StunUrls: GetStringArray(root, "stunUrls"),
        TurnUrls: GetStringArray(root, "turnUrls"));

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) ? v.GetString() : null;

    private static string[]? GetStringArray(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : null;

    /// <summary>
    /// Set HKLM DisableCAD=1 so the login screen shows the password field directly. Persists
    /// across the reboot. Domain GPO may overwrite, but survives at least one reboot.
    /// </summary>
    private static void DisablePressCtrlAltDelAtLogin()
    {
        try
        {
            using var policyKey = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", writable: true);
            policyKey?.SetValue("DisableCAD", 1, RegistryValueKind.DWord);
            DebugLog("Set DisableCAD=1 (skip Ctrl+Alt+Del at login)");
        }
        catch (Exception ex)
        {
            DebugLog($"Failed to set DisableCAD: {ex.Message}");
        }
    }

    /// <summary>
    /// Create the SYSTEM-context boot relay schtask that runs at boot BEFORE any user logs in.
    /// Streams the login screen so the viewer can type a password remotely.
    /// </summary>
    private static void CreateBootRelaySchtask(string appPath)
    {
        try
        {
            var schtaskResult = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks",
                Arguments = $"/create /tn \"SteamViewerBootRelay\" /tr \"\\\"{appPath}\\\" --boot-relay\" /sc onstart /ru SYSTEM /f",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            schtaskResult?.WaitForExit(5000);
            var schtaskOut = schtaskResult?.StandardOutput.ReadToEnd();
            var schtaskErr = schtaskResult?.StandardError.ReadToEnd();
            DebugLog($"Boot relay schtask created (exit={schtaskResult?.ExitCode}, out={schtaskOut?.Trim()}, err={schtaskErr?.Trim()})");
        }
        catch (Exception ex)
        {
            DebugLog($"Failed to create boot relay schtask: {ex.Message}");
        }
    }

    /// <summary>
    /// Register a RunOnceEx entry that auto-launches the app in --sas mode AFTER the user logs in.
    /// Pairs with the boot-relay schtask: boot-relay covers the login screen, RunOnceEx takes over
    /// once the user is in.
    /// </summary>
    private static void RegisterRunOnceExSasEntry(string appPath)
    {
        try
        {
            using var runOnceExKey = Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnceEx\SteamViewer");
            runOnceExKey?.SetValue("", $"\"{appPath}\" --sas");
            DebugLog("Registered RunOnceEx --sas entry");
        }
        catch (Exception ex)
        {
            DebugLog($"Failed to write RunOnceEx: {ex.Message}");
        }
    }

    /// <summary>
    /// Fire `shutdown /r /t 0` to reboot immediately. Caller has already persisted reconnect
    /// state and registered both the pre-login boot relay schtask and the post-login RunOnceEx
    /// entry, so the session can re-establish after the OS comes back up.
    /// </summary>
    private static void InitiateSystemReboot()
    {
        DebugLog("Initiating system reboot");
        Process.Start(new ProcessStartInfo
        {
            FileName = "shutdown",
            Arguments = "/r /t 0",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static string? HandleInjectInput(JsonElement root)
    {
        try
        {
            var (defaultW, defaultH) = Win32Input.GetPrimaryMonitorSize();
            Win32Input.InjectInputFromJson(root, defaultW, defaultH,
                msg => DebugLog($"InjectInput: {msg}"));
        }
        catch (Exception ex)
        {
            DebugLog($"InjectInput error: {ex.Message}");
        }

        return null;
    }

    private readonly record struct LaunchSystemHelperRequest(
        string PipeName, string Nonce, string ExePath, uint ExpectedClientPid, string AllowedUserSid);

    private static string HandleLaunchSystemHelper(JsonElement root)
    {
        try
        {
            if (!TryBuildLaunchSystemHelperRequest(root, out var req, out var errorJson))
                return errorJson;

            // Off-cmdline nonce delivery (F6 LPE close-out): write nonce to a per-admin-PID file
            // with SYSTEM+user-only ACL. SYSTEM helper reads + deletes it on startup. The nonce
            // never appears in the SYSTEM helper's cmdline.
            var adminHelperPid = (uint)Environment.ProcessId;
            NonceFile.Write(adminHelperPid, req.Nonce);

            DebugLog($"LaunchSystemHelper: pipe={req.PipeName}, exe={req.ExePath}, hostClientPid={req.ExpectedClientPid}, adminHelperPid={adminHelperPid}, userSid={req.AllowedUserSid}, noncePath={NonceFile.PathFor(adminHelperPid)}");

            // adminHelperPid in cmdline so the SYSTEM helper can (a) watch our PID for B3 self-terminate,
            // and (b) derive the off-cmdline nonce-file path. Nonce itself is NOT on the cmdline.
            var arguments = $"--system-helper {req.PipeName} {req.ExpectedClientPid} {req.AllowedUserSid} {adminHelperPid}";
            if (ProcessLauncher.LaunchAsSystemFromAdmin(req.ExePath, arguments, out var pid, out var launchError))
            {
                DebugLog($"SYSTEM helper launched via token duplication: PID {pid}");
                return JsonSerializer.Serialize(new HelperResponse(true, null));
            }

            DebugLog($"Token duplication launch failed: {launchError}");
            return JsonSerializer.Serialize(new HelperResponse(false, launchError ?? "Failed to launch SYSTEM helper"));
        }
        catch (Exception ex)
        {
            DebugLog($"LaunchSystemHelper failed: {ex.Message}");
            return JsonSerializer.Serialize(new HelperResponse(false, ex.Message));
        }
    }

    /// <summary>
    /// Parse + validate the launchSystemHelper command in one shot. Returns false with a
    /// serialized error HelperResponse on any missing field, missing exePath, or uninitialized
    /// admin-helper state (Run() did not set _hostClientPid). On success, populates a
    /// LaunchSystemHelperRequest with the parsed fields + the resolved exe path and current user
    /// SID needed for the SYSTEM helper's ACL.
    /// </summary>
    private static bool TryBuildLaunchSystemHelperRequest(JsonElement root, out LaunchSystemHelperRequest req, out string errorJson)
    {
        req = default;

        var pipeName = root.TryGetProperty("pipeName", out var pn) ? pn.GetString() : null;
        var nonce = root.TryGetProperty("nonce", out var n) ? n.GetString() : null;
        if (string.IsNullOrEmpty(pipeName) || string.IsNullOrEmpty(nonce))
        {
            errorJson = JsonSerializer.Serialize(new HelperResponse(false, "Missing pipeName or nonce"));
            return false;
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            errorJson = JsonSerializer.Serialize(new HelperResponse(false, "Cannot determine exe path"));
            return false;
        }

        // SYSTEM helper's PID gate must match the host app (the actual connector), not this admin helper.
        if (_hostClientPid == 0)
        {
            DebugLog("LaunchSystemHelper: _hostClientPid not set - Run() did not initialize");
            errorJson = JsonSerializer.Serialize(new HelperResponse(false, "Admin helper not initialized"));
            return false;
        }

        var allowedUserSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Cannot determine user SID for SYSTEM helper ACL");

        req = new LaunchSystemHelperRequest(pipeName, nonce, exePath, _hostClientPid, allowedUserSid);
        errorJson = "";
        return true;
    }

    private static string HandleExit()
    {
        DebugLog("Exit command received - will terminate after sending acknowledgement.");
        _exitRequested = true;
        return JsonSerializer.Serialize(new HelperResponse(true, null));
    }

    /// <summary>
    /// Lifecycle setup that runs once on Run() entry, before the pipe-server connect-wait.
    /// Arms the parent-death watchdog so a host that dies during the 30s connect window tears us
    /// down; enables SeDebug (best-effort) so the orphan reap can terminate SYSTEM-level orphans;
    /// reaps any orphans left by a prior dead session; registers our own marker so a future admin
    /// helper can reap us if we ever orphan. See .claude/research/elevated-helper-lifecycle.
    /// </summary>
    private static void InitializeHelperLifecycle(uint expectedClientPid)
    {
        ParentDeathWatchdog.Arm(expectedClientPid, DebugLog, "host");

        var seDebug = ProcessLauncher.EnableDebugPrivilege();
        DebugLog($"SeDebugPrivilege enabled for orphan reap: {seDebug}");
        HelperRegistry.ReapOrphans(DebugLog);
        HelperRegistry.Register(expectedClientPid, "admin", DebugLog);
    }
}
