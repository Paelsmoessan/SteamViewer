using Microsoft.Win32;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SteamViewer.Client.Core.Session;

namespace SteamViewer.Platform.Windows.Elevation;

/// <summary>
/// Named pipe server that runs in the elevated (admin) process.
/// Launched via SteamViewer.App.exe --elevated-helper {pipeName}.
/// Handles privileged operations: SendSAS, reboot with RunOnceEx.
/// Never initializes MAUI — runs as a headless pipe server.
/// </summary>
public static class ElevatedHelperServer
{
    [DllImport("sas.dll", SetLastError = true)]
    private static extern void SendSAS(bool asUser);

    private static string? _debugPath;

    private static void DebugLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Console.WriteLine($"[ElevatedHelper] {message}");
        try { if (_debugPath != null) File.AppendAllText(_debugPath, line + "\n"); } catch { }
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

        DebugLog($"Starting pipe server: {pipeName} (PID {Environment.ProcessId})");

        try
        {
            // Allow non-elevated (authenticated) users to connect to this elevated pipe
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
                PipeOptions.Asynchronous,
                0, 0,
                pipeSecurity);

            DebugLog("Pipe created. Waiting for client connection...");

            // Wait for client with 30s timeout
            var connectTask = pipeServer.WaitForConnectionAsync();
            if (!connectTask.Wait(TimeSpan.FromSeconds(30)))
            {
                DebugLog("Timeout waiting for client. Exiting.");
                return;
            }

            DebugLog("Client connected. Processing commands...");

            using var reader = new StreamReader(pipeServer, Encoding.UTF8);
            using var writer = new StreamWriter(pipeServer, Encoding.UTF8) { AutoFlush = true };

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
                    DebugLog($"Sending: {response}");
                    writer.WriteLine(response);
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

    private static string HandleCommand(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var command = doc.RootElement.GetProperty("command").GetString();

        Console.WriteLine($"[ElevatedHelper] Command: {command}");

        return command switch
        {
            "ping" => JsonSerializer.Serialize(new HelperResponse(true, null)),
            "sendSAS" => HandleSendSAS(),
            "reboot" => HandleReboot(doc.RootElement),
            "exit" => HandleExit(),
            _ => JsonSerializer.Serialize(new HelperResponse(false, $"Unknown command: {command}"))
        };
    }

    private static string HandleSendSAS()
    {
        try
        {
            SendSAS(false);
            Console.WriteLine("[ElevatedHelper] SendSAS succeeded");
            return JsonSerializer.Serialize(new HelperResponse(true, null));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ElevatedHelper] SendSAS failed: {ex.Message}");
            return JsonSerializer.Serialize(new HelperResponse(false, ex.Message));
        }
    }

    private static string HandleReboot(JsonElement root)
    {
        try
        {
            var appPath = Environment.ProcessPath;

            // Save reconnect credentials if provided
            var clientId = root.TryGetProperty("clientId", out var cid) ? cid.GetString() : null;
            var passwordHash = root.TryGetProperty("passwordHash", out var ph) ? ph.GetString() : null;
            var viewerPeerId = root.TryGetProperty("viewerPeerId", out var vp) ? vp.GetString() : null;

            if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(passwordHash) && !string.IsNullOrEmpty(viewerPeerId))
            {
                try
                {
                    ReconnectCredentials.Save(clientId, passwordHash, viewerPeerId);
                    Console.WriteLine("[ElevatedHelper] Saved reconnect credentials");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ElevatedHelper] Failed to save reconnect credentials: {ex.Message}");
                }
            }

            // Write RunOnceEx for auto-restart with --sas mode
            if (!string.IsNullOrEmpty(appPath))
            {
                try
                {
                    using var runOnceExKey = Registry.LocalMachine.CreateSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnceEx\SteamViewer");
                    runOnceExKey?.SetValue("", $"\"{appPath}\" --sas");
                    Console.WriteLine("[ElevatedHelper] Registered RunOnceEx --sas entry");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ElevatedHelper] Failed to write RunOnceEx: {ex.Message}");
                }
            }

            // Initiate reboot
            Console.WriteLine("[ElevatedHelper] Initiating system reboot");
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
            Console.WriteLine($"[ElevatedHelper] Reboot failed: {ex.Message}");
            return JsonSerializer.Serialize(new HelperResponse(false, ex.Message));
        }
    }

    private static string HandleExit()
    {
        Console.WriteLine("[ElevatedHelper] Exit command received");
        // Return response, then the loop will exit when client disconnects
        return JsonSerializer.Serialize(new HelperResponse(true, null));
    }

    private record HelperResponse(bool success, string? error);
}
