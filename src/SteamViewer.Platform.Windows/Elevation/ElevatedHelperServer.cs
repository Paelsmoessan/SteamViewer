using Microsoft.Win32;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SteamViewer.Client.Core.Session;
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

    private static void DebugLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Console.WriteLine($"[ElevatedHelper] {message}");
        try { if (_debugPath != null) File.AppendAllText(_debugPath, line + "\n"); } catch { }
        try { if (_debugPathLocal != null) File.AppendAllText(_debugPathLocal, line + "\n"); } catch { }
    }

    /// <summary>
    /// Run the elevated helper pipe server. Blocks until the client disconnects or sends "exit".
    /// </summary>
    public static void Run(string pipeName)
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

        DebugLog($"Starting pipe server: {pipeName} (PID {Environment.ProcessId})");

        try
        {
            // Allow non-elevated (authenticated) users to connect to this elevated pipe
            var pipeSecurity = new PipeSecurity();
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));

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

            DebugLog("Client connected. Processing commands...");

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

            DebugLog("Client disconnected. Exiting normally.");
        }
        catch (Exception ex)
        {
            DebugLog($"FATAL: {ex}");
        }
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

            // Save reconnect credentials if provided (with server URL + STUN/TURN for boot relay)
            var clientId = root.TryGetProperty("clientId", out var cid) ? cid.GetString() : null;
            var passwordHash = root.TryGetProperty("passwordHash", out var ph) ? ph.GetString() : null;
            var viewerPeerId = root.TryGetProperty("viewerPeerId", out var vp) ? vp.GetString() : null;
            var serverUrl = root.TryGetProperty("serverUrl", out var su) ? su.GetString() : null;
            var turnUsername = root.TryGetProperty("turnUsername", out var tu) ? tu.GetString() : null;
            var turnCredential = root.TryGetProperty("turnCredential", out var tc) ? tc.GetString() : null;

            string[]? stunUrls = null;
            if (root.TryGetProperty("stunUrls", out var stunArr) && stunArr.ValueKind == JsonValueKind.Array)
                stunUrls = stunArr.EnumerateArray().Select(e => e.GetString()!).ToArray();

            string[]? turnUrls = null;
            if (root.TryGetProperty("turnUrls", out var turnArr) && turnArr.ValueKind == JsonValueKind.Array)
                turnUrls = turnArr.EnumerateArray().Select(e => e.GetString()!).ToArray();

            if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(passwordHash) && !string.IsNullOrEmpty(viewerPeerId))
            {
                try
                {
                    ReconnectCredentials.Save(clientId, passwordHash, viewerPeerId,
                        serverUrl, stunUrls, turnUrls, turnUsername, turnCredential);
                    DebugLog($"Saved reconnect credentials (serverUrl={serverUrl}, stunUrls={stunUrls?.Length ?? 0}, turnUrls={turnUrls?.Length ?? 0})");
                }
                catch (Exception ex)
                {
                    DebugLog($"Failed to save reconnect credentials: {ex.Message}");
                }
            }

            // Disable "Press Ctrl+Alt+Del to log in" — persists across reboots.
            // Allows login screen to show password field directly.
            // Domain GPO may overwrite, but survives at least one reboot.
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

            if (!string.IsNullOrEmpty(appPath))
            {
                // Create boot relay schtask — runs as SYSTEM at boot (before login)
                // Streams login screen so viewer can type password
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

                // Write RunOnceEx for auto-restart with --sas mode (after user logs in)
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

            // Initiate reboot
            DebugLog("Initiating system reboot");
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown",
                Arguments = "/r /t 0",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            return JsonSerializer.Serialize(new HelperResponse(true, null));
        }
        catch (Exception ex)
        {
            DebugLog($"Reboot failed: {ex.Message}");
            return JsonSerializer.Serialize(new HelperResponse(false, ex.Message));
        }
    }

    private static string? HandleInjectInput(JsonElement root)
    {
        try
        {
            var (defaultW, defaultH) = Win32Input.GetPrimaryMonitorSize();
            var sw = root.TryGetProperty("sw", out var swProp) ? swProp.GetInt32() : defaultW;
            var sh = root.TryGetProperty("sh", out var shProp) ? shProp.GetInt32() : defaultH;
            var type = root.GetProperty("type").GetString();

            // Extract fields directly from JSON — avoids InputEvent polymorphic deserialization
            // (System.Text.Json can't deserialize InputEvent when extra fields like "command" are present)
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

        return null;
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

    private static string HandleLaunchSystemHelper(JsonElement root)
    {
        try
        {
            var pipeName = root.TryGetProperty("pipeName", out var pn) ? pn.GetString() : null;
            var nonce = root.TryGetProperty("nonce", out var n) ? n.GetString() : null;

            if (string.IsNullOrEmpty(pipeName) || string.IsNullOrEmpty(nonce))
                return JsonSerializer.Serialize(new HelperResponse(false, "Missing pipeName or nonce"));

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
                return JsonSerializer.Serialize(new HelperResponse(false, "Cannot determine exe path"));

            DebugLog($"LaunchSystemHelper: pipe={pipeName}, exe={exePath}");

            var arguments = $"--system-helper {pipeName} {nonce}";
            if (ProcessLauncher.LaunchAsSystemFromAdmin(exePath, arguments, out var pid, out var launchError))
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

    private static string HandleExit()
    {
        DebugLog("Exit command received");
        return JsonSerializer.Serialize(new HelperResponse(true, null));
    }

    private record HelperResponse(bool Success, string? Error);
}
