using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SteamViewer.Client.Core.Network;
using SteamViewer.Client.Core.Session;
using SteamViewer.Client.Core.Video;
using SteamViewer.Common.Protocol;
using SteamViewer.Platform.Windows.Input;

namespace SteamViewer.Platform.Windows.Elevation;

/// <summary>
/// Boot-time relay service that runs as SYSTEM before user login.
/// Reads ReconnectCredentials, connects to the signaling server via SignalingClient,
/// establishes a StreamTransport relay with the viewer, captures the login screen
/// (Winlogon desktop) via SecureDesktopCapture, encodes H.264 frames, and relays
/// keyboard/mouse input.
/// Exits when the main app re-registers (duplicate clientId kicks us).
/// </summary>
public static class BootRelayService
{
    private static string? _debugPath;
    private static string? _debugPathLocal;
    private static SecureDesktopCapture? _capture;
    private static SignalingClient? _signalingClient;
    private static HostStreamTransport? _transport;
    private static FFmpegEncoder? _encoder;
    private static string? _viewerPeerId;
    private static volatile bool _stopping;
    private static ILoggerFactory? _loggerFactory;

    // Input thread - same pattern as SystemHelperServer
    private static BlockingCollection<(string json, int sw, int sh)>? _inputQueue;
    private static Thread? _inputThread;

    // Frame tracking
    private static int _frameCount;
    private static int _lastSentCaptureW;
    private static int _lastSentCaptureH;
    private static int _lastSentEncodeW;
    private static int _lastSentEncodeH;

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

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

    private static void DebugLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [BootRelay] {message}";
        Console.WriteLine(line);
        try { if (_debugPath != null) File.AppendAllText(_debugPath, line + "\n"); } catch { }
        try { if (_debugPathLocal != null) File.AppendAllText(_debugPathLocal, line + "\n"); } catch { }
    }

    /// <summary>
    /// Main entry point. Called from Program.cs when --boot-relay is passed.
    /// Blocks until the viewer disconnects, main app takes over, or timeout.
    /// </summary>
    public static void Run(string? taskName = null)
    {
        _debugPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SteamViewer", "boot-relay-debug.txt");
        try { Directory.CreateDirectory(Path.GetDirectoryName(_debugPath)!); } catch { }

        // Also log next to exe (readable via network share from Dev PC)
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (exeDir != null)
        {
            _debugPathLocal = Path.Combine(exeDir, "logs", "boot-relay-debug.txt");
            try { Directory.CreateDirectory(Path.GetDirectoryName(_debugPathLocal)!); } catch { }
        }

        DebugLog($"Starting boot relay (PID {Environment.ProcessId}, User: {Environment.UserName}, Task: {taskName ?? "none"})");

        // TODO: Self-delete scheduled task here (Step 3 of reboot-reconnect plan)
        // if (!string.IsNullOrEmpty(taskName)) BootTaskManager.DeleteTask(taskName);

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
            _encoder?.Dispose();
            _encoder = null;
            _transport?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _transport = null;
            _signalingClient?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _signalingClient = null;
            _loggerFactory?.Dispose();
            _loggerFactory = null;
            DebugLog("Boot relay exited.");
        }
    }

    private static async Task RunAsync(ReconnectCredentials.ReconnectResult creds)
    {
        // Create logger factory (console output + debug log level)
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddConsole();
        });

        var logger = _loggerFactory.CreateLogger("BootRelay");

        // Create SignalingClient and connect
        _signalingClient = new SignalingClient(
            creds.ServerUrl!,
            _loggerFactory.CreateLogger<SignalingClient>());

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(60)); // Max lifetime

        try
        {
            DebugLog($"Connecting to signaling server: {creds.ServerUrl}");
            await _signalingClient.ConnectAsync(cts.Token);
            DebugLog("Connected to signaling server");
        }
        catch (Exception ex)
        {
            DebugLog($"Failed to connect to signaling server: {ex.Message}");
            return;
        }

        // Handle signaling disconnect
        _signalingClient.OnDisconnected += reason =>
        {
            DebugLog($"Signaling disconnected: {reason} (likely main app re-registered)");
            _stopping = true;
        };

        // Handle incoming connections
        _signalingClient.OnMessageReceived += msg =>
        {
            switch (msg)
            {
                case SignalingMessage.IncomingConnection incoming:
                    DebugLog($"Incoming connection from {incoming.FromId}");
                    _viewerPeerId = incoming.FromId;
                    _ = HandleIncomingConnectionAsync(incoming.FromId, creds, cts.Token);
                    break;

                case SignalingMessage.RelayReady:
                case SignalingMessage.TransportEndpoint:
                case SignalingMessage.TransportConfirmed:
                    // These are handled by HostStreamTransport internally via sendSignaling callback
                    break;

                case SignalingMessage.Disconnected disconnected:
                    DebugLog($"Peer disconnected: {disconnected.PeerId}");
                    break;

                case SignalingMessage.Error error:
                    DebugLog($"Server error: {error.Message}");
                    break;
            }
        };

        // Register with saved credentials
        DebugLog($"Registering: clientId={creds.ClientId}");
        var registered = await _signalingClient.RegisterAsync(creds.ClientId, creds.PasswordHash, cts.Token);
        if (!registered)
        {
            DebugLog("Registration failed. Exiting.");
            return;
        }
        DebugLog("Registered successfully");

        // Start logon monitor on a background thread
        var logonMonitorCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        _ = Task.Run(() => MonitorUserLogon(creds, logonMonitorCts.Token), logonMonitorCts.Token);

        // Wait until stopping
        while (!cts.Token.IsCancellationRequested && !_stopping)
        {
            await Task.Delay(1000, cts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        logonMonitorCts.Cancel();
        DebugLog("Main loop ended");
    }

    private static async Task HandleIncomingConnectionAsync(
        string viewerPeerId, ReconnectCredentials.ReconnectResult creds, CancellationToken ct)
    {
        try
        {
            // Auto-approve
            await _signalingClient!.RespondToConnectionAsync(viewerPeerId, true, ct);
            DebugLog("Auto-approved connection");

            // Create transport
            _transport = new HostStreamTransport(
                _signalingClient,
                _loggerFactory!.CreateLogger<HostStreamTransport>());

            // Subscribe to input messages
            _transport.OnControlMessage += HandleControlMessage;
            _transport.OnConnectionStateChanged += state =>
            {
                DebugLog($"Transport state: {state}");
                if (state == "disconnected")
                    _stopping = true;
            };

            // Start relay (generates nonce, sets up encryption, sends RelayReady to viewer)
            await _transport.StartRelayAsync(
                viewerPeerId,
                creds.PasswordHash,
                msg => _signalingClient.SendAsync(msg, ct));

            DebugLog("StreamTransport relay started, waiting for viewer...");

            // Wait for viewerReady - the transport.OnControlMessage will handle it
            // Start capture immediately - frames will queue until viewer is ready
            StartCapture();
        }
        catch (Exception ex)
        {
            DebugLog($"HandleIncomingConnection error: {ex.Message}");
        }
    }

    private static Task HandleControlMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

            if (type == null) return Task.CompletedTask;

            switch (type)
            {
                case "viewerReady":
                    DebugLog("Viewer relay connected - sending boot relay state");
                    // Tell viewer we're in secure desktop mode (login screen)
                    _ = _transport?.SendControlAsync(JsonSerializer.Serialize(new
                    {
                        type = "secureDesktopActive"
                    }));
                    // Send capture/encode info if we have frames already
                    if (_lastSentCaptureW > 0)
                    {
                        _ = _transport?.SendControlAsync(JsonSerializer.Serialize(new
                        {
                            type = "captureInfo",
                            width = _lastSentCaptureW,
                            height = _lastSentCaptureH
                        }));
                    }
                    if (_lastSentEncodeW > 0)
                    {
                        _ = _transport?.SendControlAsync(JsonSerializer.Serialize(new
                        {
                            type = "encodeInfo",
                            width = _lastSentEncodeW,
                            height = _lastSentEncodeH
                        }));
                    }
                    break;

                case "mouse_move":
                case "mouse_down":
                case "mouse_up":
                case "mouse_wheel":
                case "key_down":
                case "key_up":
                    var (defaultW, defaultH) = Win32Input.GetPrimaryMonitorSize();
                    var sw = root.TryGetProperty("sw", out var swP) ? swP.GetInt32() :
                             root.TryGetProperty("captureWidth", out var cwP) ? cwP.GetInt32() : defaultW;
                    var sh = root.TryGetProperty("sh", out var shP) ? shP.GetInt32() :
                             root.TryGetProperty("captureHeight", out var chP) ? chP.GetInt32() : defaultH;
                    _inputQueue?.TryAdd((json, sw, sh));
                    break;
            }
        }
        catch (Exception ex)
        {
            DebugLog($"Control message error: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    #region Screen Capture + H.264 Encoding

    private static void StartCapture()
    {
        if (_capture != null) return;

        _capture = new SecureDesktopCapture();
        _capture.OnFrameCaptured += OnFrameCaptured;
        _capture.OnSecureDesktopActive += (w, h) =>
        {
            DebugLog($"Desktop active for capture: {w}x{h}");
        };
        _capture.OnSecureDesktopInactive += () =>
        {
            DebugLog("Desktop inactive - user may have logged in");
        };

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

    private static void OnFrameCaptured(byte[] bgraData, int width, int height, int stride)
    {
        if (_transport == null || !_transport.IsConnected) return;

        _frameCount++;
        if (_frameCount <= 3 || _frameCount % 100 == 0)
            DebugLog($"Frame #{_frameCount}: {bgraData.Length}b BGRA, {width}x{height}");

        try
        {
            // Lazy-init encoder on first frame
            if (_encoder == null)
            {
                FFmpegInit.EnsureInitialized();
                _encoder = new FFmpegEncoder(_loggerFactory!.CreateLogger<FFmpegEncoder>());
                _encoder.Initialize(width, height, 15, 10_000_000, crf: 18); // 15fps, CRF 18, 10Mbps cap (login screen is mostly static)
                DebugLog($"FFmpeg encoder initialized: {width}x{height}");
            }

            // Send captureInfo if dimensions changed
            if (width != _lastSentCaptureW || height != _lastSentCaptureH)
            {
                _lastSentCaptureW = width;
                _lastSentCaptureH = height;
                _ = _transport.SendControlAsync(JsonSerializer.Serialize(new
                {
                    type = "captureInfo",
                    width,
                    height
                }));
            }

            // Encode BGRA to H.264
            var result = _encoder.EncodeFrame(bgraData, stride, width, height);

            // Send encodeInfo if dimensions changed
            var ew = _encoder.Width;
            var eh = _encoder.Height;
            if (ew != _lastSentEncodeW || eh != _lastSentEncodeH)
            {
                _lastSentEncodeW = ew;
                _lastSentEncodeH = eh;
                _ = _transport.SendControlAsync(JsonSerializer.Serialize(new
                {
                    type = "encodeInfo",
                    width = ew,
                    height = eh
                }));
            }

            if (result is var (naluData, naluLength))
            {
                _transport.EnqueueVideoFrame(naluData, naluLength);

                if (_frameCount <= 3 || _frameCount % 100 == 0)
                    DebugLog($"H.264 frame #{_frameCount}: {naluLength / 1024}KB");
            }
        }
        catch (Exception ex)
        {
            DebugLog($"Frame encode error: {ex.Message}");
        }
    }

    #endregion

    #region Input Injection

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

    #endregion

    #region User Logon Monitoring

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

                    // Launch main app in user's session as SYSTEM (ServiceUI technique)
                    var appPath = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(appPath))
                    {
                        DebugLog($"Launching main app via ProcessLauncher: {appPath}");
                        if (ProcessLauncher.LaunchInUserSession(appPath, null, out var pid))
                        {
                            DebugLog($"Main app launched in user session: PID {pid}");
                            DebugLog("Waiting for main app to take over signaling...");
                            // Wait for main app to re-register with signaling server
                            // which will kick our connection (duplicate clientId)
                            Thread.Sleep(30_000);
                        }
                        else
                        {
                            DebugLog($"ProcessLauncher.LaunchInUserSession failed (error {Marshal.GetLastWin32Error()})");
                            // Fallback: try launching as user
                            if (LaunchAppAsUser(userToken, appPath))
                                DebugLog("Fallback: launched main app as user");
                            else
                                DebugLog("Failed to launch main app via any method");
                        }
                    }

                    // Either main app took over or timeout - we should exit
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

    #endregion

    #region LaunchAppAsUser (fallback - from user token)

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
