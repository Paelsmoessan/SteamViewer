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
public static class SystemHelperServer
{
    private static readonly UTF8Encoding PipeEncoding = new(encoderShouldEmitUTF8Identifier: false);

    [DllImport("sas.dll", SetLastError = true)]
    private static extern void SendSAS(bool asUser);

    // Desktop switching — SYSTEM process needs explicit desktop attachment for SendInput
    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    // Window station — SYSTEM process may need to attach to WinSta0 (interactive)
    private const uint WINSTA_ALL_ACCESS = 0x37F;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenWindowStation(string lpszWinSta, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessWindowStation(IntPtr hWinSta);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetThreadDesktop(uint dwThreadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    // Token privilege management — needed to enable SeTcbPrivilege for SendSAS
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    private const string SE_TCB_NAME = "SeTcbPrivilege";

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    // Winlogon token impersonation — fallback for SendSAS when process token lacks SeTcbPrivilege
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImpersonateLoggedOnUser(IntPtr hToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RevertToSelf();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess,
        IntPtr lpTokenAttributes, int impersonationLevel, int tokenType, out IntPtr phNewToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    // Desktop access by name — needed for explicit Winlogon desktop access
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const uint GENERIC_ALL = 0x10000000;
    private const int SecurityImpersonation = 2;
    private const int TokenImpersonation = 2;

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public int PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;
    }

    // Dedicated input thread — SetThreadDesktop requires a thread with zero prior user32 calls
    private static BlockingCollection<(string json, int sw, int sh)>? _inputQueue;
    private static Thread? _inputThread;

    private static string? _debugPath;
    private static SecureDesktopCapture? _capture;

    // Video pipe for binary JPEG frames (server → client)
    private static NamedPipeServerStream? _videoPipeServer;
    private static BinaryWriter? _videoWriter;
    private static readonly object _videoWriteLock = new();
    private static volatile bool _videoConnected;

    // Notify pipe for server-push notifications (server → client)
    private static NamedPipeServerStream? _notifyPipeServer;
    private static StreamWriter? _notifyWriter;
    private static readonly object _notifyWriteLock = new();
    private static volatile bool _notifyConnected;

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

            // Create notify pipe for server-push notifications (server → client, outbound only)
            var notifyPipeName = $"{pipeName}_notify";
            var notifyPipeSecurity = new PipeSecurity();
            notifyPipeSecurity.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));

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
                    lock (_notifyWriteLock)
                    {
                        _notifyWriter = new StreamWriter(_notifyPipeServer, PipeEncoding) { AutoFlush = true };
                        _notifyConnected = true;
                    }
                    DebugLog("Notify pipe client connected");
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

    private static void CleanupNotifyPipe()
    {
        _notifyConnected = false;
        try { _notifyWriter?.Dispose(); } catch { }
        _notifyWriter = null;
        try { _notifyPipeServer?.Dispose(); } catch { }
        _notifyPipeServer = null;
    }

    #region SecureDesktopCapture event handlers

    private static void OnCaptureSecureDesktopActive(int width, int height)
    {
        DebugLog($"Secure Desktop active notification → notify pipe ({width}x{height})");
        SendNotification(new { notification = "secureDesktopActive", width, height });
    }

    private static void OnCaptureSecureDesktopInactive()
    {
        DebugLog("Secure Desktop inactive notification → notify pipe");
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
    /// Send a server-initiated notification over the dedicated notify pipe.
    /// Uses a separate pipe from the control pipe to avoid synchronous I/O deadlocks.
    /// </summary>
    private static void SendNotification(object notification)
    {
        if (!_notifyConnected || _notifyWriter == null) return;

        lock (_notifyWriteLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(notification);
                _notifyWriter.WriteLine(json);
            }
            catch (Exception ex)
            {
                DebugLog($"Notification write error: {ex.Message}");
                _notifyConnected = false;
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

    private static string HandleSendSAS()
    {
        try
        {
            // Re-check registry before each call — GPO can overwrite between calls
            EnsureSoftwareSASEnabled();

            // Try process token first (cheap)
            if (EnableTcbPrivilege())
            {
                DebugLog("Calling SendSAS(false) with process token...");
                SendSAS(false);
                DebugLog("SendSAS(false) returned with process token");
            }
            else
            {
                // Fallback: impersonate winlogon's token which has SeTcbPrivilege
                DebugLog("Process token lacks SeTcbPrivilege — trying winlogon impersonation");
                if (!CallSendSASWithImpersonation())
                {
                    return JsonSerializer.Serialize(new HelperResponse(false, "Both SAS methods failed"));
                }
            }

            return JsonSerializer.Serialize(new HelperResponse(true, null));
        }
        catch (Exception ex)
        {
            DebugLog($"SendSAS failed: {ex.Message}");
            return JsonSerializer.Serialize(new HelperResponse(false, ex.Message));
        }
    }

    /// <summary>
    /// Ensure SoftwareSASGeneration registry value is set to 3 (services + applications).
    /// Required for SendSAS(false) from sas.dll to work. SYSTEM has HKLM write access.
    /// Called at startup and before each SendSAS call (GPO can overwrite between calls).
    /// </summary>
    private static void EnsureSoftwareSASEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Policies\System", writable: true);
            if (key != null)
            {
                var current = key.GetValue("SoftwareSASGeneration");
                if (current == null || (int)current < 3)
                {
                    key.SetValue("SoftwareSASGeneration", 3, Microsoft.Win32.RegistryValueKind.DWord);
                    DebugLog("Set SoftwareSASGeneration=3 (enable software SAS)");
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog($"Failed to set SoftwareSASGeneration: {ex.Message}");
        }
    }

    /// <summary>
    /// Enable SeTcbPrivilege on the current process token.
    /// Required for SendSAS(false) from sas.dll — SYSTEM tokens have it but it may be disabled.
    /// </summary>
    private static bool EnableTcbPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
        {
            DebugLog($"EnableTcbPrivilege: OpenProcessToken failed (error {Marshal.GetLastWin32Error()})");
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, SE_TCB_NAME, out var luid))
            {
                DebugLog($"EnableTcbPrivilege: LookupPrivilegeValue failed (error {Marshal.GetLastWin32Error()})");
                return false;
            }

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES
                {
                    Luid = luid,
                    Attributes = SE_PRIVILEGE_ENABLED
                }
            };

            if (AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)
                && Marshal.GetLastWin32Error() == 0)
            {
                DebugLog("SeTcbPrivilege enabled successfully");
                return true;
            }

            DebugLog($"EnableTcbPrivilege: AdjustTokenPrivileges failed (error {Marshal.GetLastWin32Error()})");
            return false;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>
    /// Enable a named privilege on a specific token handle.
    /// </summary>
    private static bool EnablePrivilegeOnToken(IntPtr token, string privilegeName)
    {
        if (!LookupPrivilegeValue(null, privilegeName, out var luid))
            return false;

        var tp = new TOKEN_PRIVILEGES
        {
            PrivilegeCount = 1,
            Privileges = new LUID_AND_ATTRIBUTES
            {
                Luid = luid,
                Attributes = SE_PRIVILEGE_ENABLED
            }
        };

        AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        var err = Marshal.GetLastWin32Error();
        if (err == 0)
        {
            DebugLog($"EnablePrivilegeOnToken({privilegeName}): enabled on impersonation token");
            return true;
        }
        DebugLog($"EnablePrivilegeOnToken({privilegeName}): failed (error {err})");
        return false;
    }

    /// <summary>
    /// Impersonate winlogon.exe's token (which has SeTcbPrivilege), call SendSAS, revert.
    /// Fallback when the process's own token lacks SeTcbPrivilege.
    /// </summary>
    private static bool CallSendSASWithImpersonation()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        Process? winlogon = null;
        foreach (var p in Process.GetProcessesByName("winlogon"))
        {
            try
            {
                if (p.SessionId == (int)sessionId) { winlogon = p; break; }
            }
            catch { /* Access denied for some processes */ }
        }
        if (winlogon == null)
        {
            DebugLog($"CallSendSASWithImpersonation: winlogon.exe not found in session {sessionId}");
            return false;
        }

        DebugLog($"CallSendSASWithImpersonation: found winlogon PID {winlogon.Id} in session {sessionId}");

        var hProcess = OpenProcess(PROCESS_QUERY_INFORMATION, false, (uint)winlogon.Id);
        if (hProcess == IntPtr.Zero)
        {
            DebugLog($"CallSendSASWithImpersonation: OpenProcess failed ({Marshal.GetLastWin32Error()})");
            return false;
        }

        try
        {
            if (!OpenProcessToken(hProcess, TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_PRIVILEGES, out var hToken))
            {
                DebugLog($"CallSendSASWithImpersonation: OpenProcessToken failed ({Marshal.GetLastWin32Error()})");
                return false;
            }

            try
            {
                // Duplicate as impersonation token (SecurityImpersonation level)
                if (!DuplicateTokenEx(hToken, MAXIMUM_ALLOWED, IntPtr.Zero,
                    SecurityImpersonation, TokenImpersonation, out var hDup))
                {
                    DebugLog($"CallSendSASWithImpersonation: DuplicateTokenEx failed ({Marshal.GetLastWin32Error()})");
                    return false;
                }

                try
                {
                    // Enable SeTcbPrivilege on the impersonation token
                    EnablePrivilegeOnToken(hDup, SE_TCB_NAME);

                    // Impersonate winlogon
                    if (!ImpersonateLoggedOnUser(hDup))
                    {
                        DebugLog($"CallSendSASWithImpersonation: ImpersonateLoggedOnUser failed ({Marshal.GetLastWin32Error()})");
                        return false;
                    }

                    try
                    {
                        DebugLog("Calling SendSAS(false) under winlogon impersonation...");
                        SendSAS(false);
                        DebugLog("SendSAS(false) returned under impersonation");
                        return true;
                    }
                    finally
                    {
                        RevertToSelf();
                    }
                }
                finally { CloseHandle(hDup); }
            }
            finally { CloseHandle(hToken); }
        }
        finally { CloseHandle(hProcess); }
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

            // Always enqueue — the input thread handles both Default and Secure Desktop
            // by switching desktops dynamically (clean thread, no prior user32 calls)
            _inputQueue?.TryAdd((rawJson, sw, sh));
        }
        catch (Exception ex)
        {
            DebugLog($"InjectInput error: {ex.Message}");
        }

        return null; // Fire-and-forget
    }

    /// <summary>
    /// Dedicated input thread — attaches to Default desktop as its very first user32 call,
    /// then processes input from queue. Handles both Default and Secure Desktop input by
    /// dynamically switching desktops when _capture.IsActive changes.
    /// SetThreadDesktop works because this thread never creates windows.
    /// </summary>
    private static int _sdInputLogCount;

    private static void InputThreadProc()
    {
        try
        {
            // First user32 call on this thread — attach to Default desktop
            // Keep the handle open so we can switch back from Winlogon later
            var hDefaultDesk = OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP);
            if (hDefaultDesk != IntPtr.Zero)
            {
                var ok = SetThreadDesktop(hDefaultDesk);
                DebugLog($"Input thread SetThreadDesktop(Default): {ok} (error {Marshal.GetLastWin32Error()})");
            }
            else
            {
                DebugLog($"Input thread OpenInputDesktop failed (error {Marshal.GetLastWin32Error()})");
            }

            // Process input commands from queue
            foreach (var (json, sw, sh) in _inputQueue!.GetConsumingEnumerable())
            {
                try
                {
                    var onSecureDesktop = _capture != null && _capture.IsActive;

                    IntPtr hWinlogon = IntPtr.Zero;
                    if (onSecureDesktop)
                    {
                        // Use explicit desktop name — OpenInputDesktop may not return Winlogon desktop
                        hWinlogon = OpenDesktop("Winlogon", 0, false, GENERIC_ALL);
                        _sdInputLogCount++;
                        if (_sdInputLogCount <= 5 || _sdInputLogCount % 50 == 0)
                            DebugLog($"SD input #{_sdInputLogCount}: OpenDesktop(Winlogon)={hWinlogon != IntPtr.Zero} (err {Marshal.GetLastWin32Error()})");

                        if (hWinlogon != IntPtr.Zero)
                        {
                            var switched = SetThreadDesktop(hWinlogon);
                            if (!switched)
                            {
                                DebugLog($"SD input: SetThreadDesktop(Winlogon) failed (error {Marshal.GetLastWin32Error()})");
                                CloseDesktop(hWinlogon);
                                continue;
                            }
                            if (_sdInputLogCount <= 5)
                                DebugLog("SD input: SetThreadDesktop(Winlogon) succeeded");
                        }
                        else
                        {
                            if (_sdInputLogCount <= 5)
                                DebugLog("SD input: Can't open Winlogon desktop — skipping event");
                            continue;
                        }
                    }

                    // Parse and inject input
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var type = root.GetProperty("type").GetString();

                    switch (type)
                    {
                        case "mouse_move":
                            if (onSecureDesktop)
                            {
                                var (ax, ay) = Win32Input.ConvertToAbsoluteDirect(
                                    root.GetProperty("x").GetDouble(),
                                    root.GetProperty("y").GetDouble(), sw, sh);
                                Win32Input.InjectMouseMoveRaw(ax, ay);
                            }
                            else
                            {
                                Win32Input.InjectMouseMove(
                                    root.GetProperty("x").GetDouble(),
                                    root.GetProperty("y").GetDouble(), sw, sh);
                            }
                            break;
                        case "mouse_down":
                            if (onSecureDesktop)
                            {
                                var (ax, ay) = Win32Input.ConvertToAbsoluteDirect(
                                    root.GetProperty("x").GetDouble(),
                                    root.GetProperty("y").GetDouble(), sw, sh);
                                Win32Input.InjectMouseButtonRaw(
                                    ParseMouseButton(root.GetProperty("button").GetString()),
                                    ax, ay, isDown: true);
                            }
                            else
                            {
                                Win32Input.InjectMouseButton(
                                    ParseMouseButton(root.GetProperty("button").GetString()),
                                    root.GetProperty("x").GetDouble(),
                                    root.GetProperty("y").GetDouble(), sw, sh, isDown: true);
                            }
                            break;
                        case "mouse_up":
                            if (onSecureDesktop)
                            {
                                var (ax, ay) = Win32Input.ConvertToAbsoluteDirect(
                                    root.GetProperty("x").GetDouble(),
                                    root.GetProperty("y").GetDouble(), sw, sh);
                                Win32Input.InjectMouseButtonRaw(
                                    ParseMouseButton(root.GetProperty("button").GetString()),
                                    ax, ay, isDown: false);
                            }
                            else
                            {
                                Win32Input.InjectMouseButton(
                                    ParseMouseButton(root.GetProperty("button").GetString()),
                                    root.GetProperty("x").GetDouble(),
                                    root.GetProperty("y").GetDouble(), sw, sh, isDown: false);
                            }
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

                    // Switch back to Default desktop after SD input
                    if (onSecureDesktop && hWinlogon != IntPtr.Zero)
                    {
                        var switchedBack = SetThreadDesktop(hDefaultDesk);
                        if (_sdInputLogCount <= 5)
                            DebugLog($"SD input: SetThreadDesktop(Default) back={switchedBack}");
                        CloseDesktop(hWinlogon);
                    }
                }
                catch (Exception ex)
                {
                    DebugLog($"Input thread error: {ex.Message}");
                }
            }

            // Cleanup
            if (hDefaultDesk != IntPtr.Zero)
                CloseDesktop(hDefaultDesk);
        }
        catch (Exception ex)
        {
            DebugLog($"Input thread fatal: {ex.Message}");
        }
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
