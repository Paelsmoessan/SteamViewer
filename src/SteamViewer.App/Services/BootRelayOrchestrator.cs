using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SteamViewer.App.Services.Models;
using SteamViewer.Client.Core.Capture;
using SteamViewer.Client.Core.Network;
using SteamViewer.Client.Core.Session;
using SteamViewer.Common.Protocol;
using SteamViewer.Platform.Windows.Elevation;
using SteamViewer.Platform.Windows.Input;
using SteamViewer.Platform.Windows.ScreenCapture;

namespace SteamViewer.App.Services;

/// <summary>
/// Thin orchestrator for boot relay mode. Handles signaling and connection lifecycle,
/// then delegates ALL video/input/transport to HostSession (same code as normal hosting).
/// </summary>
public static class BootRelayOrchestrator
{
    private static HostSession? _hostSession;
    private static SignalingClient? _signalingClient;
    private static ILoggerFactory? _loggerFactory;
    private static volatile bool _stopping;
    private static volatile bool _signalingDisconnected;
    private static bool _isTestMode;

    public static void Run(string? taskName = null)
    {
        _isTestMode = string.Equals(taskName, "test", StringComparison.OrdinalIgnoreCase);

        // Win32 setup (WinSta0 attachment for input injection)
        BootRelayService.AttachWinSta0();

        BootRelayService.DebugLog($"Orchestrator starting (PID {Environment.ProcessId}, User: {Environment.UserName}, TestMode: {_isTestMode})");

        var creds = ReconnectCredentials.Load();
        if (creds == null)
        {
            BootRelayService.DebugLog("No reconnect credentials found. Exiting.");
            return;
        }

        if (string.IsNullOrEmpty(creds.ServerUrl))
        {
            BootRelayService.DebugLog("No server URL in credentials. Exiting.");
            return;
        }

        BootRelayService.DebugLog($"Loaded credentials: clientId={creds.ClientId}, serverUrl={creds.ServerUrl}");

        try
        {
            RunAsync(creds, taskName).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            BootRelayService.DebugLog($"FATAL: {ex}");
        }
        finally
        {
            _stopping = true;
            _hostSession?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _hostSession = null;
            _signalingClient?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _signalingClient = null;
            _loggerFactory?.Dispose();
            _loggerFactory = null;
            ReconnectCredentials.Delete();
            BootRelayService.DebugLog("Boot relay exited (reconnect credentials deleted).");
        }
    }

    private static async Task RunAsync(ReconnectCredentials.ReconnectResult creds, string? taskName)
    {
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddConsole();
            builder.AddProvider(new BootRelayFileLoggerProvider());
        });

        _signalingClient = new SignalingClient(
            creds.ServerUrl!,
            _loggerFactory.CreateLogger<SignalingClient>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(60));
        CancellationTokenSource? logonCts = null;
        try
        {
            // Connect + register with retry
            if (!await ConnectAndRegisterWithRetry(creds, cts.Token))
            {
                BootRelayService.DebugLog("All connection attempts failed. Exiting.");
                return;
            }

            // Logon monitor (skip in test mode)
            if (!_isTestMode)
            {
                logonCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                var logonToken = logonCts.Token;
                _ = Task.Run(() => BootRelayService.MonitorUserLogon(creds, logonToken, () => _stopping = true), logonToken);
            }
            else
            {
                BootRelayService.DebugLog("Test mode: skipping logon monitor");
            }

            // Main loop
            while (!cts.Token.IsCancellationRequested && !_stopping)
            {
                if (_signalingDisconnected)
                {
                    _signalingDisconnected = false;
                    BootRelayService.DebugLog("Signaling disconnected - attempting reconnect...");
                    if (!await ConnectAndRegisterWithRetry(creds, cts.Token))
                    {
                        BootRelayService.DebugLog("Reconnect failed. Exiting.");
                        _stopping = true;
                        break;
                    }
                }

                await Task.Delay(1000, cts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            BootRelayService.DebugLog("Main loop ended");
        }
        finally
        {
            logonCts?.Dispose();
        }
    }

    private static async Task<bool> ConnectAndRegisterWithRetry(
        ReconnectCredentials.ReconnectResult creds, CancellationToken ct)
    {
        const int maxAttempts = 4;
        const int baseDelayMs = 2000;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (ct.IsCancellationRequested || _stopping) return false;

            try
            {
                BootRelayService.DebugLog($"Connect attempt {attempt}/{maxAttempts}");
                await _signalingClient!.ConnectAsync(ct);
                BootRelayService.DebugLog("Connected to signaling server");

                _signalingClient.OnDisconnected += reason =>
                {
                    BootRelayService.DebugLog($"Signaling disconnected: {reason}");
                    _signalingDisconnected = true;
                };
                _signalingClient.OnMessageReceived += msg => OnSignalingMessage(msg, creds, ct);

                var registered = await _signalingClient.RegisterAsync(creds.ClientId, creds.PasswordHash, ct);
                if (!registered)
                {
                    BootRelayService.DebugLog($"Registration failed on attempt {attempt}");
                    if (attempt < maxAttempts)
                    {
                        var delay = baseDelayMs * (1 << (attempt - 1));
                        await Task.Delay(delay, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    }
                    continue;
                }

                BootRelayService.DebugLog($"Registered successfully on attempt {attempt}");
                return true;
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex)
            {
                BootRelayService.DebugLog($"Connect attempt {attempt} failed: {ex.Message}");
                if (attempt < maxAttempts)
                {
                    var delay = baseDelayMs * (1 << (attempt - 1));
                    await Task.Delay(delay, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
        }

        return false;
    }

    private static void OnSignalingMessage(
        SignalingMessage msg, ReconnectCredentials.ReconnectResult creds, CancellationToken ct)
    {
        switch (msg)
        {
            case SignalingMessage.IncomingConnection incoming:
                BootRelayService.DebugLog($"Incoming connection from {incoming.FromId}");
                _ = HandleIncomingConnectionAsync(incoming.FromId, creds, ct);
                break;

            // Route transport signaling to HostSession (same as Home.razor)
            case SignalingMessage.TransportEndpoint endpoint:
                if (_hostSession != null)
                {
                    BootRelayService.DebugLog($"Routing TransportEndpoint ({endpoint.Candidates.Length} candidates)");
                    _ = _hostSession.HandleViewerTransportEndpointAsync(endpoint.Candidates);
                }
                break;

            case SignalingMessage.TransportConfirmed:
                if (_hostSession != null)
                {
                    BootRelayService.DebugLog("Routing TransportConfirmed");
                    _ = _hostSession.HandleTransportConfirmedAsync();
                }
                break;

            case SignalingMessage.Disconnected disconnected:
                BootRelayService.DebugLog($"Peer disconnected: {disconnected.PeerId}");
                break;

            case SignalingMessage.Error error:
                BootRelayService.DebugLog($"Server error: {error.Message}");
                break;
        }
    }

    private static async Task HandleIncomingConnectionAsync(
        string viewerPeerId, ReconnectCredentials.ReconnectResult creds, CancellationToken ct)
    {
        try
        {
            // Auto-approve
            await _signalingClient!.RespondToConnectionAsync(viewerPeerId, true, ct);
            BootRelayService.DebugLog("Auto-approved connection");

            // Create screen capture (same as Home.razor gets from DI)
            IScreenCapture screenCapture;
            if (_isTestMode)
            {
                screenCapture = new DxgiScreenCapture(_loggerFactory!.CreateLogger<DxgiScreenCapture>());
                BootRelayService.DebugLog("Using DxgiScreenCapture (test mode)");
            }
            else
            {
                // Real boot relay: SecureDesktopCapture for Winlogon
                // TODO: HostSession.StartScreenShareAsync only handles DxgiScreenCapture currently.
                // For real boot relay, need to add SecureDesktopCapture support to HostSession.
                screenCapture = new DxgiScreenCapture(_loggerFactory!.CreateLogger<DxgiScreenCapture>());
                BootRelayService.DebugLog("Using DxgiScreenCapture (real mode - SecureDesktopCapture TODO)");
            }

            // Build minimal IConfiguration with signaling server URL
            // (TURN config is fetched at runtime via TurnConfigService)
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SignalingServer"] = creds.ServerUrl
                })
                .Build();
            var turnConfigService = new TurnConfigService(
                config, _loggerFactory!.CreateLogger<TurnConfigService>());

            // Create input injector (same as DI provides to Home.razor)
            var inputInjector = new WindowsInputInjector(_loggerFactory!.CreateLogger<WindowsInputInjector>());

            // Create HostSession - same as Home.razor:756, but jsRuntime=null (no WebView)
            _hostSession = new HostSession(
                viewerPeerId,
                jsRuntime: null, // No MAUI WebView in boot relay
                _loggerFactory!,
                inputInjector,
                config,
                msg => _signalingClient.SendAsync(msg, ct),
                signalingClient: _signalingClient,
                elevationService: null,
                monitorEnumerator: null,
                screenCapture: screenCapture,
                turnConfigService: turnConfigService,
                hostClientId: creds.ClientId,
                hostPasswordHash: creds.PasswordHash);

            // Auto-share screen when viewer connects (same as Home.razor reconnect path)
            _hostSession.AutoShareOnReady = true;

            _hostSession.OnDisconnected += reason =>
            {
                BootRelayService.DebugLog($"HostSession disconnected: {reason}");
                _stopping = true;
            };

            // Initialize transport (creates relay, sends RelayReady to viewer)
            await _hostSession.InitializeAsync();
            BootRelayService.DebugLog("HostSession initialized - transport relay started");
        }
        catch (Exception ex)
        {
            BootRelayService.DebugLog($"HandleIncomingConnection error: {ex.Message}");
        }
    }
}
