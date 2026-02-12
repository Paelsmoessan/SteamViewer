using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SIPSorcery.Net;
using SteamViewer.Client.Core.Session;
using SteamViewer.Common.Protocol;
using SteamViewer.Platform.Windows.Input;

namespace SteamViewer.Platform.Windows.Elevation;

/// <summary>
/// Boot-time relay service that runs as SYSTEM before user login.
/// Reads ReconnectCredentials, connects to the signaling server,
/// establishes a SIPSorcery WebRTC data channel with the viewer,
/// captures the login screen (Winlogon desktop), and relays
/// JPEG frames + keyboard/mouse input.
/// Exits when the main app re-registers (duplicate clientId kicks us).
/// </summary>
public static class BootRelayService
{
    private static string? _debugPath;
    private static SecureDesktopCapture? _capture;
    private static RTCPeerConnection? _peerConnection;
    private static RTCDataChannel? _dataChannel;
    private static ClientWebSocket? _ws;
    private static string? _viewerPeerId;
    private static volatile bool _stopping;

    // Input thread — same pattern as SystemHelperServer
    private static BlockingCollection<(string json, int sw, int sh)>? _inputQueue;
    private static Thread? _inputThread;

    // WinSta0 attachment
    private const uint WINSTA_ALL_ACCESS = 0x37F;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenWindowStation(string lpszWinSta, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessWindowStation(IntPtr hWinSta);

    // User logon detection
    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    // Desktop switching for input thread
    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;
    private const uint GENERIC_ALL = 0x10000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetThreadDesktop(uint dwThreadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

    private static void DebugLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [BootRelay] {message}";
        Console.WriteLine(line);
        try { if (_debugPath != null) File.AppendAllText(_debugPath, line + "\n"); } catch { }
    }

    /// <summary>
    /// Main entry point. Called from Program.cs when --boot-relay is passed.
    /// Blocks until the viewer disconnects, main app takes over, or timeout.
    /// </summary>
    public static void Run()
    {
        _debugPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SteamViewer", "boot-relay-debug.txt");
        try { Directory.CreateDirectory(Path.GetDirectoryName(_debugPath)!); } catch { }

        DebugLog($"Starting boot relay (PID {Environment.ProcessId}, User: {Environment.UserName})");

        // Read reconnect credentials
        var creds = ReconnectCredentials.Load();
        if (creds == null)
        {
            DebugLog("No reconnect credentials found (or stale). Exiting.");
            return;
        }

        DebugLog($"Loaded credentials: clientId={creds.ClientId}, serverUrl={creds.ServerUrl}");

        if (string.IsNullOrEmpty(creds.ServerUrl))
        {
            DebugLog("No server URL in credentials. Exiting.");
            return;
        }

        // Attach to interactive window station
        var hWinSta = OpenWindowStation("WinSta0", false, WINSTA_ALL_ACCESS);
        if (hWinSta != IntPtr.Zero)
        {
            if (SetProcessWindowStation(hWinSta))
                DebugLog("Attached to WinSta0");
            else
                DebugLog($"SetProcessWindowStation failed (error {Marshal.GetLastWin32Error()})");
        }
        else
        {
            DebugLog($"OpenWindowStation('WinSta0') failed (error {Marshal.GetLastWin32Error()})");
        }

        // Start input thread
        _inputQueue = new BlockingCollection<(string json, int sw, int sh)>();
        _inputThread = new Thread(InputThreadProc)
        {
            IsBackground = true,
            Name = "BootRelay-Input"
        };
        _inputThread.SetApartmentState(ApartmentState.MTA);
        _inputThread.Start();

        try
        {
            RunAsync(creds).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            DebugLog($"FATAL: {ex}");
        }
        finally
        {
            _stopping = true;
            try { _inputQueue?.CompleteAdding(); } catch { }
            _inputThread?.Join(2000);
            _capture?.Dispose();
            _capture = null;
            _peerConnection?.Dispose();
            _peerConnection = null;
            DebugLog("Boot relay exited.");
        }
    }

    private static async Task RunAsync(ReconnectCredentials.ReconnectResult creds)
    {
        // Connect to signaling server
        _ws = new ClientWebSocket();
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(60)); // Max lifetime

        try
        {
            DebugLog($"Connecting to signaling server: {creds.ServerUrl}");
            await _ws.ConnectAsync(new Uri(creds.ServerUrl!), cts.Token);
            DebugLog("Connected to signaling server");
        }
        catch (Exception ex)
        {
            DebugLog($"Failed to connect to signaling server: {ex.Message}");
            return;
        }

        // Register with saved credentials
        var registerMsg = SignalingSerializer.Serialize(
            new SignalingMessage.Register(creds.ClientId, creds.PasswordHash));
        await WsSendAsync(registerMsg, cts.Token);
        DebugLog($"Sent register: clientId={creds.ClientId}");

        // Start logon monitor on a background thread
        var logonMonitorCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        _ = Task.Run(() => MonitorUserLogon(creds, logonMonitorCts.Token), logonMonitorCts.Token);

        // Start keepalive pinger
        _ = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                try
                {
                    await Task.Delay(30_000, cts.Token);
                    var ping = SignalingSerializer.Serialize(new SignalingMessage.Ping());
                    await WsSendAsync(ping, cts.Token);
                }
                catch { break; }
            }
        }, cts.Token);

        // Message receive loop
        var buffer = new byte[16384];
        var msgBuilder = new StringBuilder();

        while (_ws.State == WebSocketState.Open && !cts.Token.IsCancellationRequested && !_stopping)
        {
            try
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    DebugLog("Signaling server closed connection (likely main app re-registered)");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    msgBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (result.EndOfMessage)
                    {
                        var json = msgBuilder.ToString();
                        msgBuilder.Clear();
                        await HandleSignalingMessage(json, creds, cts.Token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (WebSocketException ex)
            {
                DebugLog($"WebSocket error: {ex.Message}");
                break;
            }
        }

        logonMonitorCts.Cancel();
        DebugLog("Signaling loop ended");
    }

    private static async Task HandleSignalingMessage(string json, ReconnectCredentials.ReconnectResult creds, CancellationToken ct)
    {
        var msg = SignalingSerializer.Deserialize(json);
        if (msg == null)
        {
            DebugLog($"Unknown message: {json[..Math.Min(json.Length, 200)]}");
            return;
        }

        switch (msg)
        {
            case SignalingMessage.RegisterSuccess success:
                DebugLog($"Registered successfully as {success.ClientId}");
                break;

            case SignalingMessage.RegisterFailed failed:
                DebugLog($"Registration failed: {failed.Reason}");
                _stopping = true;
                break;

            case SignalingMessage.IncomingConnection incoming:
                DebugLog($"Incoming connection from {incoming.FromId}");
                _viewerPeerId = incoming.FromId;

                // Auto-approve
                var approveMsg = SignalingSerializer.Serialize(
                    new SignalingMessage.ConnectionResponse(incoming.FromId, true));
                await WsSendAsync(approveMsg, ct);
                DebugLog("Auto-approved connection");

                // Create WebRTC peer connection and send SDP offer
                await CreatePeerConnectionAndOffer(creds, ct);
                break;

            case SignalingMessage.SdpAnswer answer:
                DebugLog("Received SDP answer from viewer");
                if (_peerConnection != null)
                {
                    var sdpAnswer = new RTCSessionDescriptionInit
                    {
                        type = RTCSdpType.answer,
                        sdp = answer.Sdp
                    };
                    var setResult = _peerConnection.setRemoteDescription(sdpAnswer);
                    DebugLog($"Set remote description: {setResult}");
                }
                break;

            case SignalingMessage.IceCandidate ice:
                if (_peerConnection != null)
                {
                    var candidate = new RTCIceCandidateInit
                    {
                        candidate = ice.Candidate,
                        sdpMid = ice.SdpMid ?? "0",
                        sdpMLineIndex = ice.SdpMLineIndex ?? 0
                    };
                    _peerConnection.addIceCandidate(candidate);
                }
                break;

            case SignalingMessage.Disconnected disconnected:
                DebugLog($"Peer disconnected: {disconnected.PeerId}");
                break;

            case SignalingMessage.Pong:
                // Keepalive response
                break;

            case SignalingMessage.Error error:
                DebugLog($"Server error: {error.Message}");
                break;

            default:
                DebugLog($"Unhandled message type: {msg.GetType().Name}");
                break;
        }
    }

    private static async Task CreatePeerConnectionAndOffer(ReconnectCredentials.ReconnectResult creds, CancellationToken ct)
    {
        // Configure ICE servers
        var iceServers = new List<RTCIceServer>();

        // STUN servers
        if (creds.StunUrls is { Length: > 0 })
        {
            iceServers.Add(new RTCIceServer { urls = string.Join(",", creds.StunUrls) });
        }
        else
        {
            // Default Google STUN servers
            iceServers.Add(new RTCIceServer { urls = "stun:stun.l.google.com:19302" });
        }

        // TURN servers
        if (creds.TurnUrls is { Length: > 0 } && !string.IsNullOrEmpty(creds.TurnUsername))
        {
            foreach (var turnUrl in creds.TurnUrls)
            {
                iceServers.Add(new RTCIceServer
                {
                    urls = turnUrl,
                    username = creds.TurnUsername,
                    credential = creds.TurnCredential
                });
            }
        }

        var config = new RTCConfiguration
        {
            iceServers = iceServers
        };

        _peerConnection?.Dispose();
        _peerConnection = new RTCPeerConnection(config);

        _peerConnection.onconnectionstatechange += (state) =>
        {
            DebugLog($"Connection state: {state}");
            if (state == RTCPeerConnectionState.disconnected || state == RTCPeerConnectionState.failed)
            {
                DebugLog("WebRTC disconnected/failed — stopping");
                _stopping = true;
            }
        };

        _peerConnection.onicecandidate += (candidate) =>
        {
            if (candidate != null && _viewerPeerId != null && _ws?.State == WebSocketState.Open)
            {
                var iceMsg = SignalingSerializer.Serialize(
                    new SignalingMessage.IceCandidate(
                        _viewerPeerId,
                        candidate.candidate,
                        candidate.sdpMid,
                        candidate.sdpMLineIndex));
                _ = WsSendAsync(iceMsg, CancellationToken.None);
            }
        };

        // Create data channel
        _dataChannel = await _peerConnection.createDataChannel("data");

        _dataChannel.onopen += () =>
        {
            DebugLog("Data channel OPEN — starting screen capture");
            StartCapture();
        };

        _dataChannel.onclose += () =>
        {
            DebugLog("Data channel closed");
            StopCapture();
        };

        _dataChannel.onmessage += (dc, protocol, data) =>
        {
            HandleDataChannelMessage(data);
        };

        // Create and send SDP offer
        var offer = _peerConnection.createOffer();
        await _peerConnection.setLocalDescription(offer);

        DebugLog("Created SDP offer, sending to viewer");

        var sdpMsg = SignalingSerializer.Serialize(
            new SignalingMessage.SdpOffer(_viewerPeerId!, offer.sdp));
        await WsSendAsync(sdpMsg, ct);
    }

    private static void StartCapture()
    {
        if (_capture != null) return;

        _capture = new SecureDesktopCapture();
        _capture.OnFrameCaptured += OnFrameCaptured;
        _capture.OnSecureDesktopActive += (w, h) =>
        {
            DebugLog($"Desktop active for capture: {w}x{h}");
            SendDataChannelMessage(JsonSerializer.Serialize(new { type = "secureDesktopActive" }));
        };
        _capture.OnSecureDesktopInactive += () =>
        {
            DebugLog("Desktop inactive — user may have logged in");
            SendDataChannelMessage(JsonSerializer.Serialize(new { type = "secureDesktopInactive" }));
        };

        // At boot, immediately send secureDesktopActive since we know Winlogon is the active desktop
        SendDataChannelMessage(JsonSerializer.Serialize(new { type = "secureDesktopActive" }));

        _capture.Start();
        DebugLog("SecureDesktopCapture started");
    }

    private static void StopCapture()
    {
        if (_capture == null) return;
        _capture.OnFrameCaptured -= OnFrameCaptured;
        _capture.Dispose();
        _capture = null;
        DebugLog("SecureDesktopCapture stopped");
    }

    private static int _frameCount;

    private static void OnFrameCaptured(byte[] jpegData, int width, int height)
    {
        _frameCount++;
        if (_frameCount <= 3 || _frameCount % 100 == 0)
            DebugLog($"Frame #{_frameCount}: {jpegData.Length}b, {width}x{height}");

        var base64 = Convert.ToBase64String(jpegData);
        var msg = JsonSerializer.Serialize(new
        {
            type = "secureDesktopFrame",
            data = base64,
            width,
            height
        });
        SendDataChannelMessage(msg);
    }

    private static void SendDataChannelMessage(string message)
    {
        try
        {
            if (_dataChannel?.readyState == RTCDataChannelState.open)
            {
                _dataChannel.send(message);
            }
        }
        catch (Exception ex)
        {
            DebugLog($"Data channel send error: {ex.Message}");
        }
    }

    private static void HandleDataChannelMessage(byte[] data)
    {
        try
        {
            var json = Encoding.UTF8.GetString(data);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

            if (type == null) return;

            // Handle input events
            switch (type)
            {
                case "mouse_move":
                case "mouse_down":
                case "mouse_up":
                case "mouse_wheel":
                case "key_down":
                case "key_up":
                    var sw = root.TryGetProperty("sw", out var swP) ? swP.GetInt32() :
                             root.TryGetProperty("captureWidth", out var cwP) ? cwP.GetInt32() : 1920;
                    var sh = root.TryGetProperty("sh", out var shP) ? shP.GetInt32() :
                             root.TryGetProperty("captureHeight", out var chP) ? chP.GetInt32() : 1080;
                    _inputQueue?.TryAdd((json, sw, sh));
                    break;
            }
        }
        catch (Exception ex)
        {
            DebugLog($"Data channel message error: {ex.Message}");
        }
    }

    /// <summary>
    /// Dedicated input thread. Attaches to the active desktop and injects input.
    /// At boot, the active desktop is Winlogon (login screen).
    /// </summary>
    private static void InputThreadProc()
    {
        try
        {
            // Attach to current input desktop
            var hDesk = OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP);
            if (hDesk != IntPtr.Zero)
            {
                SetThreadDesktop(hDesk);
                DebugLog("Input thread attached to input desktop");
            }

            foreach (var (json, sw, sh) in _inputQueue!.GetConsumingEnumerable())
            {
                try
                {
                    // Always try to switch to the current input desktop (may be Winlogon)
                    var hCurrent = OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP);
                    if (hCurrent == IntPtr.Zero)
                    {
                        // Try explicit Winlogon desktop
                        hCurrent = OpenDesktop("Winlogon", 0, false, GENERIC_ALL);
                    }

                    if (hCurrent != IntPtr.Zero)
                    {
                        SetThreadDesktop(hCurrent);

                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        var type = root.GetProperty("type").GetString();

                        switch (type)
                        {
                            case "mouse_move":
                                Win32Input.InjectMouseMove(
                                    root.GetProperty("x").GetDouble(),
                                    root.GetProperty("y").GetDouble(), sw, sh);
                                break;
                            case "mouse_down":
                                Win32Input.InjectMouseButton(
                                    ParseMouseButton(root.GetProperty("button").GetString()),
                                    root.GetProperty("x").GetDouble(),
                                    root.GetProperty("y").GetDouble(), sw, sh, isDown: true);
                                break;
                            case "mouse_up":
                                Win32Input.InjectMouseButton(
                                    ParseMouseButton(root.GetProperty("button").GetString()),
                                    root.GetProperty("x").GetDouble(),
                                    root.GetProperty("y").GetDouble(), sw, sh, isDown: false);
                                break;
                            case "mouse_wheel":
                                Win32Input.InjectMouseWheel(
                                    root.GetProperty("delta_x").GetDouble(),
                                    root.GetProperty("delta_y").GetDouble());
                                break;
                            case "key_down":
                                Win32Input.InjectKey(
                                    root.GetProperty("key").GetString()!,
                                    ParseModifiers(root), isDown: true);
                                break;
                            case "key_up":
                                Win32Input.InjectKey(
                                    root.GetProperty("key").GetString()!,
                                    ParseModifiers(root), isDown: false);
                                break;
                        }

                        CloseDesktop(hCurrent);
                    }
                }
                catch (Exception ex)
                {
                    DebugLog($"Input error: {ex.Message}");
                }
            }

            if (hDesk != IntPtr.Zero)
                CloseDesktop(hDesk);
        }
        catch (Exception ex)
        {
            DebugLog($"Input thread fatal: {ex.Message}");
        }
    }

    /// <summary>
    /// Monitors for user logon. When detected, launches the main app as the user.
    /// </summary>
    private static void MonitorUserLogon(ReconnectCredentials.ReconnectResult creds, CancellationToken ct)
    {
        DebugLog("Logon monitor started");

        while (!ct.IsCancellationRequested && !_stopping)
        {
            try
            {
                Thread.Sleep(2000);

                var sessionId = WTSGetActiveConsoleSessionId();
                if (sessionId == 0xFFFFFFFF)
                    continue;

                if (!WTSQueryUserToken(sessionId, out var userToken))
                    continue;

                try
                {
                    DebugLog($"User logged in (session {sessionId}). Waiting for desktop to settle...");
                    Thread.Sleep(5000); // Let desktop initialize

                    // Launch main app as the logged-in user
                    var appPath = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(appPath))
                    {
                        DebugLog($"Launching main app as user: {appPath}");
                        // Reuse SasMode's pattern — CreateProcessAsUser
                        if (LaunchAppAsUser(userToken, appPath))
                        {
                            DebugLog("Main app launched. Waiting for it to take over...");
                            // Wait for main app to re-register with signaling server
                            // which will kick our connection (duplicate clientId)
                            Thread.Sleep(30_000);
                        }
                        else
                        {
                            DebugLog("Failed to launch main app as user");
                        }
                    }

                    // Either main app took over or timeout — we should exit
                    _stopping = true;
                    return;
                }
                finally
                {
                    CloseHandle(userToken);
                }
            }
            catch (Exception ex)
            {
                DebugLog($"Logon monitor error: {ex.Message}");
            }
        }

        DebugLog("Logon monitor stopped");
    }

    #region LaunchAppAsUser (from SasMode pattern)

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess,
        IntPtr lpTokenAttributes, int impersonationLevel, int tokenType, out IntPtr phNewToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(IntPtr hToken, string? lpApplicationName,
        string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken,
        [MarshalAs(UnmanagedType.Bool)] bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    private static bool LaunchAppAsUser(IntPtr userToken, string appPath)
    {
        IntPtr dupToken = IntPtr.Zero;
        IntPtr envBlock = IntPtr.Zero;

        try
        {
            if (!DuplicateTokenEx(userToken, 0, IntPtr.Zero, 2 /* SecurityImpersonation */,
                1 /* TokenPrimary */, out dupToken))
            {
                DebugLog($"DuplicateTokenEx failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            if (!CreateEnvironmentBlock(out envBlock, dupToken, false))
            {
                DebugLog($"CreateEnvironmentBlock failed: {Marshal.GetLastWin32Error()}");
                CloseHandle(dupToken);
                return false;
            }

            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(si);
            si.lpDesktop = "winsta0\\default";

            var cmdLine = $"\"{appPath}\"";
            var result = CreateProcessAsUser(dupToken, null, cmdLine,
                IntPtr.Zero, IntPtr.Zero, false,
                CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE,
                envBlock, Path.GetDirectoryName(appPath),
                ref si, out var pi);

            if (result)
            {
                DebugLog($"Main app launched as user: PID {pi.dwProcessId}");
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
            }
            else
            {
                DebugLog($"CreateProcessAsUser failed: {Marshal.GetLastWin32Error()}");
            }

            return result;
        }
        finally
        {
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
            if (dupToken != IntPtr.Zero) CloseHandle(dupToken);
        }
    }

    #endregion

    #region Helpers

    private static readonly object _wsLock = new();

    private static async Task WsSendAsync(string message, CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open) return;

        var bytes = Encoding.UTF8.GetBytes(message);
        // WebSocket SendAsync is not thread-safe — serialize sends
        lock (_wsLock)
        {
            _ws.SendAsync(new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text, true, ct).GetAwaiter().GetResult();
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

    #endregion
}
