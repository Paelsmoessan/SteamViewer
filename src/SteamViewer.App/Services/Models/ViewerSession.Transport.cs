using Microsoft.Extensions.Logging;
using SteamViewer.Client.Core.Network;
using SteamViewer.Client.Core.Video;
using SteamViewer.Common.Protocol;
using System.Text.Json;

namespace SteamViewer.App.Services.Models;

// Transport concerns for ViewerSession: relay/UDP transport lifecycle, candidate
// handshake, connection-state changes.
public sealed partial class ViewerSession
{
    private ViewerStreamTransport? _transport;

    /// <summary>
    /// Handle RelayReady from host — setup encrypted WebSocket relay transport.
    /// Replaces the old TransportEndpoint/QUIC connection.
    /// </summary>
    public async Task HandleRelayReadyAsync(string encryptionNonce)
    {
        _logger.LogInformation("Session {SessionId}: Received RelayReady with encryption nonce", SessionId);

        try
        {
            // Compute salted password hash (must match what host uses for register and key derivation).
            var passwordHash = SteamViewer.Client.Core.Session.PasswordHash.Compute(PeerId, StoredPassword ?? "");

            _transport = new ViewerStreamTransport(_signalingClient, _loggerFactory.CreateLogger<ViewerStreamTransport>());
            _transport.OnControlMessage += HandleControlMessage;
            _transport.OnVideoData += HandleVideoData;
            _transport.OnLosslessFrame += HandleLosslessFrame;
            _transport.OnFileData += HandleFileDataBinary;
            _transport.OnFileSignalingMessage += HandleFileChannelMessage;
            // Channel 5 (SD JPEG) removed - SD frames now arrive via H.264 on channel 1
            _transport.OnConnectionStateChanged += HandleTransportStateChanged;
            _transport.OnConnectionQualityChanged += HandleConnectionQualityChanged;

            // Connect relay (derives encryption key, subscribes to binary messages)
            _transport.ConnectRelay(encryptionNonce, passwordHash);

            // Tell host we're ready — host waits for this before sending initial state
            _logger.LogInformation("Session {SessionId}: Sending viewerReady handshake", SessionId);
            await _transport.SendControlAsync(JsonSerializer.Serialize(new { type = "viewerReady" }));

            _logger.LogInformation("Session {SessionId}: Relay transport connected, viewerReady sent", SessionId);

            // Initialize FFmpeg decoder (field owned by Video partial; cross-partial write).
            FFmpegInit.EnsureInitialized();
            _decoder = new FFmpegDecoder(_loggerFactory.CreateLogger<FFmpegDecoder>());
            _decoder.Initialize();

            SetState(ViewerSessionState.Connected);
            OnReady?.Invoke();

            // Start clipboard file monitoring (defined in Clipboard partial).
            StartClipboardFileTransfer();

            // Fire-and-forget UDP upgrade attempt (relay continues working in background)
            _ = Task.Run(AttemptUdpUpgradeAsync);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session {SessionId}: Failed to setup relay transport", SessionId);
            SetState(ViewerSessionState.Error);
            OnDisconnected?.Invoke($"Relay transport setup failed: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Handle TransportEndpoint from host — contains host's UDP candidates.
    /// Probes each candidate and switches to direct UDP if successful.
    /// </summary>
    public async Task HandleTransportEndpointAsync(TransportCandidate[] candidates)
    {
        if (_transport == null)
        {
            _logger.LogWarning("Session {SessionId}: Received TransportEndpoint but transport is null", SessionId);
            return;
        }

        _logger.LogInformation("Session {SessionId}: Received host UDP candidates ({Count} candidates)",
            SessionId, candidates.Length);
        await _transport.HandleHostEndpointAsync(candidates);
    }

    /// <summary>
    /// Handle TransportConfirmed from host — host's UDP probe succeeded.
    /// </summary>
    public async Task HandleTransportConfirmedAsync()
    {
        if (_transport == null)
        {
            _logger.LogWarning("Session {SessionId}: Received TransportConfirmed but transport is null", SessionId);
            return;
        }

        _logger.LogInformation("Session {SessionId}: Received TransportConfirmed from host", SessionId);
        await _transport.HandleTransportConfirmedAsync();
    }

    /// <summary>
    /// Send a raw string message to the remote peer via transport control channel.
    /// </summary>
    public async Task<bool> SendDataAsync(string data)
    {
        if (_transport == null || !_transport.IsConnected) return false;
        return await _transport.SendControlAsync(data);
    }

    /// <summary>
    /// Fetch TURN config, then ask the transport to probe direct UDP and (if it
    /// succeeds) switch over from the relay. Fire-and-forget shape - relay
    /// continues working if this fails. Extracted from HandleRelayReadyAsync as
    /// a named, testable unit. (Note: not unit-tested - depends on _transport
    /// and _turnConfigService; behavior preserved by mechanical-only move,
    /// verified by stage-merge smoke matrix.)
    /// </summary>
    private async Task AttemptUdpUpgradeAsync()
    {
        try
        {
            var turnConfig = _turnConfigService != null
                ? await _turnConfigService.GetConfigAsync(_localClientId)
                : TurnConfig.Disabled;
            var turnUri = turnConfig.Enabled ? turnConfig.Urls.FirstOrDefault() : null;
            var turnUser = turnConfig.Username;
            var turnCred = turnConfig.Credential;
            _logger.LogInformation("Session {SessionId}: Starting UDP upgrade (TURN uri={TurnUri}, user={TurnUser}, cred={HasCred})",
                SessionId, turnUri ?? "null", turnUser ?? "null", turnCred != null ? "yes" : "no");
            await _transport!.AttemptUdpUpgradeAsync(
                _sendSignaling, PeerId, turnUri, turnUser, turnCred);
            _logger.LogInformation("Session {SessionId}: UDP upgrade completed (isDirectUdp={IsDirect})",
                SessionId, _transport?.IsDirectUdp ?? false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session {SessionId}: UDP upgrade attempt failed", SessionId);
        }
    }

    private void HandleTransportStateChanged(string state)
    {
        _logger.LogInformation("Session {SessionId}: Transport state changed to {State}", SessionId, state);
        if (state == "disconnected")
        {
            SetState(ViewerSessionState.Disconnected);
            OnDisconnected?.Invoke("Transport disconnected");
        }
        else if (state == "connected" || state == "udp-upgraded")
        {
            // Mark this session-instance as having confirmed transport for the current epoch.
            // ViewerSessionManager.HandlePeerDisconnected uses this to decide whether a
            // signaling Disconnected warrants the 5s grace timer (live-session prune) or
            // should be ignored (fresh-reconnect that never came up - let max-outage handle).
            if (!HasTransportConfirmedThisEpoch)
            {
                MarkTransportConfirmed();
                _logger.LogDebug("Session {SessionId}: HasTransportConfirmedThisEpoch=true (state={State})", SessionId, state);
            }

            if (state == "udp-upgraded")
            {
                // Re-send desired resolution on new UDP backend.
                // The initial setResolution was sent on the WS relay which the host may
                // have already unsubscribed from. Re-sending ensures the host gets it.
                if (_lastDesiredWidth > 0 && _lastDesiredHeight > 0)
                {
                    _logger.LogInformation("Session {SessionId}: Re-sending resolution {W}x{H} after UDP upgrade",
                        SessionId, _lastDesiredWidth, _lastDesiredHeight);
                    _ = SendDesiredResolutionAsync(_lastDesiredWidth, _lastDesiredHeight);
                }
            }
        }
    }
}
