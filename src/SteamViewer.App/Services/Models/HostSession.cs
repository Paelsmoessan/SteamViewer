using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SteamViewer.Client.Core.Capture;
using SteamViewer.Client.Core.Network;
using SteamViewer.Common.Protocol;

namespace SteamViewer.App.Services.Models;

/// <summary>
/// Connection state for a host session.
/// </summary>
public enum HostSessionState
{
    /// <summary>Session is being initialized (WebRTC setup).</summary>
    Initializing,
    /// <summary>WebRTC initialized, waiting for data channel to open.</summary>
    WaitingForDataChannel,
    /// <summary>Data channel is open; ready for screen sharing and input.</summary>
    Connected,
    /// <summary>Session has been disconnected.</summary>
    Disconnected,
    /// <summary>Session encountered an error.</summary>
    Error
}

/// <summary>
/// Represents a single host session with a connected viewer peer.
/// Encapsulates WebRTC connection, screen sharing, input injection, and file transfer.
/// </summary>
public sealed class HostSession : IAsyncDisposable
{
    private readonly IJSRuntime _jsRuntime;
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IInputInjector _inputInjector;
    private readonly IConfiguration _configuration;
    private readonly Func<SignalingMessage, Task> _sendSignaling;
    private WebRTCManager? _webrtc;
    private bool _webrtcInitialized;
    private bool _disposed;

    // Pending SDP/ICE (buffered if received before WebRTC init)
    private string? _pendingSdpOffer;
    private readonly List<(string candidate, string? sdpMid, int? sdpMLineIndex)> _pendingIceCandidates = new();

    // Track capture dimensions from viewer's mouse events
    private int _lastCaptureWidth = 1920;
    private int _lastCaptureHeight = 1080;

    /// <summary>Session ID for JS interop — always "host".</summary>
    public string SessionId => "host";

    /// <summary>The connected viewer's peer ID.</summary>
    public string PeerId { get; }

    /// <summary>Current session state.</summary>
    public HostSessionState State { get; private set; } = HostSessionState.Initializing;

    /// <summary>Whether the data channel is open and ready.</summary>
    public bool IsDataChannelReady => _webrtc?.IsDataChannelOpen ?? false;

    /// <summary>Whether this host is sharing its screen to the viewer.</summary>
    public bool IsSharingScreen { get; private set; }

    /// <summary>Whether the connected viewer is sharing their screen.</summary>
    public bool IsPeerSharingScreen { get; private set; }

    #region Events

    /// <summary>Raised when session state changes.</summary>
    public event Action<HostSessionState>? OnStateChanged;

    /// <summary>Raised when the data channel opens (ready for screen share/input).</summary>
    public event Action? OnReady;

    /// <summary>Raised when the session disconnects.</summary>
    public event Action<string?>? OnDisconnected;

    /// <summary>Raised when the peer starts/stops sharing their screen.</summary>
    public event Action<bool>? OnPeerSharingChanged;

    /// <summary>Raised when an ICE candidate needs to be sent via signaling.</summary>
    public event Func<string, string?, ushort?, Task>? OnIceCandidate;

    /// <summary>Raised when an SDP offer/answer needs to be sent via signaling.</summary>
    public event Func<string, string, Task>? OnSdpMessage;

    /// <summary>Raised when a control message error needs to be shown to the viewer.</summary>
    public event Action<string, string?>? OnControlMessage;

    #endregion

    public HostSession(
        string peerId,
        IJSRuntime jsRuntime,
        ILoggerFactory loggerFactory,
        IInputInjector inputInjector,
        IConfiguration configuration,
        Func<SignalingMessage, Task> sendSignaling)
    {
        PeerId = peerId;
        _jsRuntime = jsRuntime;
        _logger = loggerFactory.CreateLogger<HostSession>();
        _loggerFactory = loggerFactory;
        _inputInjector = inputInjector;
        _configuration = configuration;
        _sendSignaling = sendSignaling;
    }

    /// <summary>
    /// Initialize WebRTC as host: configure TURN, create peer connection,
    /// create data channel, create and send SDP offer.
    /// </summary>
    public async Task InitializeAsync()
    {
        _logger.LogInformation("Initializing host session for peer {PeerId}", PeerId);

        // Configure TURN server before WebRTC init
        await SetTurnConfigFromSettings();

        // Set logger to host mode
        await _jsRuntime.InvokeVoidAsync("SteamViewerLogger.setMode", true);

        _webrtc = new WebRTCManager(
            _jsRuntime,
            _loggerFactory.CreateLogger<WebRTCManager>(),
            SessionId,
            PeerId,
            _sendSignaling);

        // Subscribe to WebRTC events
        _webrtc.OnIceCandidate += HandleIceCandidate;
        _webrtc.OnDataChannelMessage += HandleDataChannelMessage;
        _webrtc.OnRenegotiationNeeded += HandleRenegotiationNeeded;
        _webrtc.OnDataChannelOpen += HandleDataChannelOpen;
        _webrtc.OnDataChannelClose += HandleDataChannelClose;
        _webrtc.OnConnectionStateChange += HandleConnectionStateChange;

        await _webrtc.InitializeAsync();
        _webrtcInitialized = true;
        await _webrtc.CreateDataChannelAsync("data");

        // Create and send initial SDP offer
        var offer = await _webrtc.CreateOfferAsync();
        if (OnSdpMessage != null)
            await OnSdpMessage.Invoke(PeerId, offer);

        SetState(HostSessionState.WaitingForDataChannel);
        _logger.LogInformation("SDP offer sent to {PeerId}", PeerId);

        // Flush any buffered signaling messages
        await FlushPendingSignalingAsync();
    }

    #region SDP/ICE Handling

    /// <summary>Handle incoming SDP offer (renegotiation from viewer).</summary>
    public async Task HandleSdpOfferAsync(string sdp)
    {
        if (_webrtc != null && _webrtcInitialized)
        {
            _logger.LogInformation("Received SDP offer from {PeerId}", PeerId);
            await _webrtc.SetRemoteDescriptionAsync(sdp);
            var answer = await _webrtc.CreateAnswerAsync();
            if (OnSdpMessage != null)
                await OnSdpMessage.Invoke(PeerId, answer);
            _logger.LogInformation("SDP answer sent");
        }
        else
        {
            _logger.LogWarning("WebRTC not ready, buffering SDP offer");
            _pendingSdpOffer = sdp;
        }
    }

    /// <summary>Handle incoming SDP answer from viewer.</summary>
    public async Task HandleSdpAnswerAsync(string sdp)
    {
        if (_webrtc == null) return;
        _logger.LogInformation("Received SDP answer from {PeerId}", PeerId);
        await _webrtc.SetRemoteDescriptionAsync(sdp);
    }

    /// <summary>Handle incoming ICE candidate from viewer.</summary>
    public async Task HandleIceCandidateAsync(string candidate, string? sdpMid, int? sdpMLineIndex)
    {
        if (_webrtc != null && _webrtcInitialized)
        {
            var candidateJson = JsonSerializer.Serialize(new { candidate, sdpMid, sdpMLineIndex });
            await _webrtc.AddIceCandidateAsync(candidateJson);
        }
        else
        {
            _logger.LogDebug("WebRTC not ready, buffering ICE candidate");
            _pendingIceCandidates.Add((candidate, sdpMid, sdpMLineIndex));
        }
    }

    private async Task FlushPendingSignalingAsync()
    {
        if (_webrtc == null || !_webrtcInitialized) return;

        if (_pendingSdpOffer != null)
        {
            _logger.LogInformation("Flushing buffered SDP offer");
            var sdp = _pendingSdpOffer;
            _pendingSdpOffer = null;
            await HandleSdpOfferAsync(sdp);
        }

        if (_pendingIceCandidates.Count > 0)
        {
            _logger.LogInformation("Flushing {Count} buffered ICE candidates", _pendingIceCandidates.Count);
            var candidates = _pendingIceCandidates.ToList();
            _pendingIceCandidates.Clear();
            foreach (var (candidate, sdpMid, sdpMLineIndex) in candidates)
            {
                await HandleIceCandidateAsync(candidate, sdpMid, sdpMLineIndex);
            }
        }
    }

    #endregion

    #region Screen Sharing

    /// <summary>Start sharing screen to the connected viewer.</summary>
    public async Task<bool> StartScreenShareAsync()
    {
        if (_webrtc == null) return false;

        try
        {
            _logger.LogInformation("Starting screen share...");
            var success = await _webrtc.StartScreenCaptureAsync();
            if (success)
            {
                IsSharingScreen = true;
                _logger.LogInformation("Screen sharing started, notifying peer...");

                // Wait for track to propagate, then notify peer
                await Task.Delay(500);
                for (int i = 0; i < 3; i++)
                {
                    var sent = await _webrtc.SendDataAsync(
                        JsonSerializer.Serialize(new { type = "screenShareStarted" }));
                    _logger.LogInformation("screenShareStarted message sent: {Sent}", sent);
                    if (sent) break;
                    await Task.Delay(200);
                }
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start screen sharing");
            return false;
        }
    }

    /// <summary>Stop sharing screen.</summary>
    public async Task StopScreenShareAsync()
    {
        if (_webrtc == null) return;

        try
        {
            _logger.LogInformation("Stopping screen share...");
            await _webrtc.StopScreenCaptureAsync();
            IsSharingScreen = false;
            await _webrtc.SendDataAsync(
                JsonSerializer.Serialize(new { type = "screenShareStopped" }));
            _logger.LogInformation("Screen sharing stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop screen sharing");
        }
    }

    #endregion

    #region Data Channel

    /// <summary>Send string data to the peer via data channel.</summary>
    public async Task<bool> SendDataAsync(string data)
    {
        if (_webrtc == null || !_webrtc.IsDataChannelOpen) return false;
        return await _webrtc.SendDataAsync(data);
    }

    private async Task HandleDataChannelOpen()
    {
        _logger.LogInformation("Host: Data channel opened - ready for communication");
        SetState(HostSessionState.Connected);
        OnReady?.Invoke();

        // Send elevation status to viewer
        try
        {
            var elevated = IsCurrentProcessElevated();
            await _webrtc!.SendDataAsync(JsonSerializer.Serialize(new
            {
                type = "hostStatus",
                elevated
            }));
            _logger.LogInformation("Sent elevation status to viewer: elevated={Elevated}", elevated);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send elevation status");
        }
    }

    private async Task HandleDataChannelClose()
    {
        _logger.LogWarning("Host: Data channel closed");
        if (State == HostSessionState.Connected)
        {
            SetState(HostSessionState.Disconnected);
            OnDisconnected?.Invoke("Data channel closed");
        }
        await Task.CompletedTask;
    }

    private async Task HandleDataChannelMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var typeElement))
            {
                var type = typeElement.GetString();
                switch (type)
                {
                    case "rebootHost":
                        _logger.LogInformation("Received reboot request from viewer");
                        var rebootResult = _inputInjector.RebootWithAutoRestart();
                        if (!rebootResult && _webrtc != null)
                        {
                            await _webrtc.SendDataAsync(JsonSerializer.Serialize(new
                            {
                                type = "rebootError",
                                message = "Host needs to run as administrator to reboot"
                            }));
                        }
                        return;

                    case "ctrlAltDel":
                        _logger.LogInformation("Received Ctrl+Alt+Del request from viewer");
                        var sasResult = _inputInjector.SendSecureAttentionSequence();
                        if (!sasResult && _webrtc != null)
                        {
                            await _webrtc.SendDataAsync(JsonSerializer.Serialize(new
                            {
                                type = "ctrlAltDelError",
                                message = "Host needs to run as administrator"
                            }));
                        }
                        return;

                    case "requestElevation":
                        await HandleRequestElevationAsync();
                        return;

                    case "clipboard_request":
                        await HandleClipboardRequestAsync();
                        return;

                    case "clipboard_set":
                        await HandleClipboardSetAsync(root);
                        return;

                    case "screenShareStarted":
                        _logger.LogInformation("Peer started sharing their screen");
                        IsPeerSharingScreen = true;
                        OnPeerSharingChanged?.Invoke(true);
                        return;

                    case "screenShareStopped":
                        _logger.LogInformation("Peer stopped sharing their screen");
                        IsPeerSharingScreen = false;
                        OnPeerSharingChanged?.Invoke(false);
                        return;
                }
            }

            // Not a control message — treat as input event
            HandleInputMessage(json);
        }
        catch (JsonException)
        {
            // Not JSON, try as input
            HandleInputMessage(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle data channel message");
        }
    }

    #endregion

    #region Input Injection

    private void HandleInputMessage(string json)
    {
        // Only process input if we're sharing our screen
        if (!IsSharingScreen) return;

        try
        {
            var inputEvent = JsonSerializer.Deserialize<InputEvent>(json);
            if (inputEvent != null)
            {
                // Track dimensions from mouse events
                switch (inputEvent)
                {
                    case InputEvent.MouseMove move when move.CaptureWidth > 0 && move.CaptureHeight > 0:
                        _lastCaptureWidth = move.CaptureWidth;
                        _lastCaptureHeight = move.CaptureHeight;
                        break;
                    case InputEvent.MouseDown down when down.CaptureWidth > 0 && down.CaptureHeight > 0:
                        _lastCaptureWidth = down.CaptureWidth;
                        _lastCaptureHeight = down.CaptureHeight;
                        break;
                    case InputEvent.MouseUp up when up.CaptureWidth > 0 && up.CaptureHeight > 0:
                        _lastCaptureWidth = up.CaptureWidth;
                        _lastCaptureHeight = up.CaptureHeight;
                        break;
                }

                _inputInjector.InjectInput(inputEvent, _lastCaptureWidth, _lastCaptureHeight);
            }
        }
        catch
        {
            // Silently ignore parse errors to reduce latency
        }
    }

    #endregion

    #region Clipboard

    private async Task HandleClipboardRequestAsync()
    {
        if (_webrtc == null) return;
        try
        {
            var text = await _jsRuntime.InvokeAsync<string>("navigator.clipboard.readText");
            if (!string.IsNullOrEmpty(text))
            {
                var response = JsonSerializer.Serialize<ClipboardMessage>(
                    new ClipboardMessage.Response("text", text));
                await _webrtc.SendDataAsync(response);
                _logger.LogDebug("Sent clipboard to viewer: {Length} chars", text.Length);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read clipboard for viewer request");
        }
    }

    private async Task HandleClipboardSetAsync(JsonElement root)
    {
        var data = root.TryGetProperty("data", out var d) ? d.GetString() : null;
        if (data == null) return;
        try
        {
            await _jsRuntime.InvokeVoidAsync("navigator.clipboard.writeText", data);
            _logger.LogDebug("Set clipboard from viewer: {Length} chars", data.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set clipboard from viewer");
        }
    }

    #endregion

    #region Elevation

    private async Task HandleRequestElevationAsync()
    {
        if (_webrtc == null) return;

        if (IsCurrentProcessElevated())
        {
            _logger.LogInformation("Already elevated, notifying viewer");
            await _webrtc.SendDataAsync(JsonSerializer.Serialize(new
            {
                type = "elevationAlready"
            }));
            return;
        }

        _logger.LogInformation("Attempting to restart as admin...");
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath == null)
            {
                _logger.LogError("Could not determine current process path");
                await _webrtc.SendDataAsync(JsonSerializer.Serialize(new
                {
                    type = "elevationDenied",
                    message = "Could not determine app path"
                }));
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas"
            };

            Process.Start(psi);
            _logger.LogInformation("Elevated instance started, shutting down current instance");

            // Notify viewer that elevation is in progress (they'll need to reconnect)
            await _webrtc.SendDataAsync(JsonSerializer.Serialize(new
            {
                type = "elevationRestarting"
            }));

            // Give the message time to send before exiting
            await Task.Delay(500);
            Environment.Exit(0);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED (UAC denied)
        {
            _logger.LogWarning("UAC prompt denied by user");
            await _webrtc.SendDataAsync(JsonSerializer.Serialize(new
            {
                type = "elevationDenied",
                message = "UAC prompt was denied on host"
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart as admin");
            await _webrtc.SendDataAsync(JsonSerializer.Serialize(new
            {
                type = "elevationDenied",
                message = ex.Message
            }));
        }
    }

    private static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    #endregion

    #region WebRTC Event Handlers

    private async Task HandleIceCandidate(string candidateJson)
    {
        try
        {
            var candidate = JsonSerializer.Deserialize<IceCandidateData>(candidateJson);
            if (candidate != null && OnIceCandidate != null)
            {
                await OnIceCandidate.Invoke(candidate.candidate, candidate.sdpMid, (ushort?)candidate.sdpMLineIndex);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle ICE candidate");
        }
    }

    private async Task HandleRenegotiationNeeded(string offerJson)
    {
        _logger.LogInformation("Renegotiation: sending new offer to {PeerId}", PeerId);
        if (OnSdpMessage != null)
            await OnSdpMessage.Invoke(PeerId, offerJson);
    }

    private async Task HandleConnectionStateChange(string state)
    {
        _logger.LogInformation("Connection state changed to {State}", state);
        switch (state)
        {
            case "connected":
                SetState(HostSessionState.Connected);
                break;
            case "disconnected":
            case "failed":
            case "closed":
                SetState(HostSessionState.Disconnected);
                OnDisconnected?.Invoke(state);
                break;
        }
        await Task.CompletedTask;
    }

    #endregion

    #region TURN Configuration

    private async Task SetTurnConfigFromSettings()
    {
        var turnEnabled = _configuration.GetValue<bool>("TurnServer:Enabled");
        if (!turnEnabled)
        {
            _logger.LogInformation("TURN server disabled in config");
            return;
        }

        var urls = _configuration.GetSection("TurnServer:Urls").Get<string[]>();
        var username = _configuration["TurnServer:Username"];
        var credential = _configuration["TurnServer:Credential"];

        if (urls == null || urls.Length == 0 || string.IsNullOrEmpty(username))
        {
            _logger.LogWarning("TURN server enabled but not configured");
            return;
        }

        if (urls[0].Contains("YOUR_TURN_SERVER"))
        {
            _logger.LogWarning("TURN server URL is placeholder");
            return;
        }

        _logger.LogInformation("Configuring TURN server: {Urls}", string.Join(", ", urls));
        await _jsRuntime.InvokeVoidAsync("SteamViewerWebRTC.setTurnConfig", urls, username, credential);
    }

    #endregion

    private void SetState(HostSessionState newState)
    {
        if (State != newState)
        {
            State = newState;
            OnStateChanged?.Invoke(newState);
        }
    }

    /// <summary>Disconnect and clean up the session.</summary>
    public async Task DisconnectAsync()
    {
        if (_webrtc != null)
        {
            await _webrtc.CloseAsync();
        }
        IsSharingScreen = false;
        IsPeerSharingScreen = false;
        SetState(HostSessionState.Disconnected);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_webrtc != null)
        {
            _webrtc.OnIceCandidate -= HandleIceCandidate;
            _webrtc.OnDataChannelMessage -= HandleDataChannelMessage;
            _webrtc.OnRenegotiationNeeded -= HandleRenegotiationNeeded;
            _webrtc.OnDataChannelOpen -= HandleDataChannelOpen;
            _webrtc.OnDataChannelClose -= HandleDataChannelClose;
            _webrtc.OnConnectionStateChange -= HandleConnectionStateChange;

            await _webrtc.DisposeAsync();
            _webrtc = null;
        }

        _pendingSdpOffer = null;
        _pendingIceCandidates.Clear();
    }

    private record IceCandidateData(string candidate, string? sdpMid, int? sdpMLineIndex);
}
