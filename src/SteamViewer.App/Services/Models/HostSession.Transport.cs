using Microsoft.Extensions.Logging;
using SteamViewer.Client.Core.Network;
using SteamViewer.Client.Core.Video;
using SteamViewer.Common.Protocol;
using SteamViewer.Platform.Windows.ScreenCapture;

namespace SteamViewer.App.Services.Models;

public sealed partial class HostSession
{
    private HostStreamTransport? _transport;

    /// <summary>
    /// Handle a TransportEndpoint from the viewer (their UDP candidates).
    /// Called from Home.razor when signaling routes TransportEndpoint to this session.
    /// </summary>
    public async Task HandleViewerTransportEndpointAsync(TransportCandidate[] candidates)
    {
        if (_transport == null)
        {
            _logger.LogWarning("Received viewer TransportEndpoint but transport is null");
            return;
        }

        _logger.LogInformation("Host: Received viewer UDP candidates ({Count} candidates)", candidates.Length);
        await _transport.HandleViewerEndpointAsync(candidates);
    }

    /// <summary>
    /// Handle TransportConfirmed from the viewer â€” viewer's UDP probe succeeded.
    /// Called from Home.razor when signaling routes TransportConfirmed to this session.
    /// </summary>
    public async Task HandleTransportConfirmedAsync()
    {
        if (_transport == null)
        {
            _logger.LogWarning("Received TransportConfirmed but transport is null");
            return;
        }

        _logger.LogInformation("Host: Received TransportConfirmed from viewer");
        await _transport.HandleTransportConfirmedAsync();
    }

    private async Task HandleTransportConnected()
    {
        _logger.LogInformation("Host: Transport connected - ready for communication");
        SetState(HostSessionState.Connected);
        OnReady?.Invoke();

        // Wire video pipeline to transport
        _videoPipeline.SetTransport(new VideoTransportShim(_transport!));

        // Start clipboard file monitoring (detect CF_HDROP on host clipboard)
        StartClipboardFileTransfer();

        // Send elevation status to viewer (can go over relay immediately)
        try
        {
            var elevated = _elevationService?.IsAdminConnected ?? false;
            var systemLevel = _elevationService?.IsSystemConnected ?? false;
            await SendAsync(new { type = "hostStatus", elevated, systemLevel }, "hostStatus");
            _logger.LogInformation("Sent elevation status: elevated={Elevated}, systemLevel={SystemLevel}", elevated, systemLevel);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send elevation status");
        }

        // Send monitor layout to viewer
        await SendMonitorLayoutAsync();

        // Attempt UDP upgrade BEFORE starting video - wait for it to complete or fail
        // so video starts on a stable transport (no mid-stream relayâ†’UDP switch artifacts)
        try
        {
            var turnConfig = _turnConfigService != null
                ? await _turnConfigService.GetConfigAsync(_hostClientId)
                : TurnConfig.Disabled;
            var turnUri = turnConfig.Enabled ? turnConfig.Urls.FirstOrDefault() : null;
            var turnUser = turnConfig.Username;
            var turnCred = turnConfig.Credential;
            _logger.LogInformation("Host: Starting UDP upgrade (TURN uri={TurnUri}, user={TurnUser}, cred={HasCred})",
                turnUri ?? "null", turnUser ?? "null", turnCred != null ? "yes" : "no");
            await _transport!.AttemptUdpUpgradeAsync(
                PeerId, _sendSignaling, turnUri, turnUser, turnCred);
            _logger.LogInformation("Host: UDP upgrade completed (isDirectUdp={IsDirect})",
                _transport?.IsDirectUdp ?? false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UDP upgrade attempt failed - continuing on relay");
        }

        // Auto-start full screen sharing on reconnect after reboot
        if (AutoShareOnReady)
        {
            AutoShareOnReady = false;
            try
            {
                // Ensure video send loop is consuming before pushing frames
                _logger.LogInformation("Auto-share: waiting for video send loop ready...");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await _transport!.WaitForVideoSendReadyAsync();
                sw.Stop();
                _logger.LogInformation("Auto-share: video send loop ready ({ElapsedMs}ms)", sw.ElapsedMilliseconds);
                _logger.LogInformation("Auto-sharing screen on {Transport} transport after reboot reconnect",
                    _transport.IsDirectUdp ? "UDP direct" : "relay");
                await StartScreenShareAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-start screen share after reconnect");
            }
        }
    }

    private void HandleTransportStateChanged(string state)
    {
        _logger.LogInformation("Transport state changed: {State}", state);
        if (state == "disconnected")
        {
            _inputInjector.ReleaseAllModifiers();
            _inputInjector.RestoreKeyboardLayout();

            if (State == HostSessionState.Connected)
            {
                SetState(HostSessionState.Disconnected);
                OnDisconnected?.Invoke("Transport disconnected");
            }
        }
    }

    #region Pipeline Adapters

    /// <summary>
    /// Adapts HostStreamTransport to HostVideoPipeline.IVideoTransport.
    /// Thin shim -- just forwards calls, no logic.
    /// </summary>
    private sealed class VideoTransportShim : HostVideoPipeline.IVideoTransport
    {
        private readonly HostStreamTransport _transport;
        public VideoTransportShim(HostStreamTransport transport) => _transport = transport;
        public bool IsConnected => _transport.IsConnected;
        public bool IsDirectUdp => _transport.IsDirectUdp;
        public void EnqueueVideoFrame(byte[] data, int length) => _transport.EnqueueVideoFrame(data, length);
        public ValueTask<bool> SendControlAsync(string json) => _transport.SendControlAsync(json);
        public ValueTask<bool> SendLosslessFrameAsync(byte[] data, int offset, int length) => _transport.SendLosslessFrameAsync(data, offset, length);
        public UdpTransportBackend? GetUdpBackend() => _transport.GetUdpBackend();
        public Task WaitForVideoSendReadyAsync(CancellationToken ct = default) => _transport.WaitForVideoSendReadyAsync(ct);
    }

    /// <summary>
    /// Adapts DxgiScreenCapture to HostVideoPipeline.IDxgiCapture.
    /// Lives in App layer since Platform.Windows can't reference Client.Core's pipeline interface.
    /// </summary>
    private sealed class DxgiCaptureAdapter : HostVideoPipeline.IDxgiCapture
    {
        private readonly DxgiScreenCapture _dxgi;
        public DxgiCaptureAdapter(DxgiScreenCapture dxgi) => _dxgi = dxgi;
        public bool IsCapturing => _dxgi.IsCapturing;
        public bool ShowCursor { get => _dxgi.ShowCursor; set => _dxgi.ShowCursor = value; }
        public byte[]? LastRawFrame => _dxgi.LastRawFrame;
        public int LastRawWidth => _dxgi.LastRawWidth;
        public int LastRawHeight => _dxgi.LastRawHeight;
        public int LastRawStride => _dxgi.LastRawStride;
        public void StartCaptureLoop(uint outputIndex) => _dxgi.StartCaptureLoop(outputIndex);
        public void StopCaptureLoop() => _dxgi.StopCaptureLoop();
        public void NotifyDesktopAvailable() => _dxgi.NotifyDesktopAvailable();

        public event Action<byte[], int, int, int> OnRawFrameCaptured
        {
            add => _dxgi.OnRawFrameCaptured += value;
            remove => _dxgi.OnRawFrameCaptured -= value;
        }
        public event Action<string> OnCursorShapeChanged
        {
            add => _dxgi.OnCursorShapeChanged += value;
            remove => _dxgi.OnCursorShapeChanged -= value;
        }
        public event Action OnFrameUnchanged
        {
            add => _dxgi.OnFrameUnchanged += value;
            remove => _dxgi.OnFrameUnchanged -= value;
        }
    }

    #endregion
}
