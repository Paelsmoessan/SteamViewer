using Microsoft.Extensions.Logging;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Client.Core.Network;

/// <summary>
/// Viewer-side transport. Starts with WebSocket relay (Phase 1), optionally upgrades to direct UDP (Phase 2).
///
/// Connection flow:
/// 1. Viewer receives RelayReady(nonce) from host via signaling
/// 2. ConnectRelay(): WebSocket relay via signaling server (immediate)
/// 3. In background: AttemptUdpUpgradeAsync() tries STUN/TURN for direct P2P
/// 4. If UDP probe succeeds → send TransportConfirmed, wait for peer confirmation → switch backend
///
/// The UDP upgrade itself is delegated to a shared <see cref="UdpUpgradeCoordinator"/> (identical
/// logic to the host side); only the confirmation-timeout policy differs — the viewer promotes to
/// UDP anyway when its own probe succeeded (defense-in-depth for a HostRecovered re-pair signaling
/// drop, see fba0d10), so it keeps receiving video even if the host's confirmation was lost.
/// </summary>
public sealed class ViewerStreamTransport : StreamTransport
{
    private readonly SignalingClient _signalingClient;
    private readonly UdpUpgradeCoordinator _udpUpgrade;

    public ViewerStreamTransport(SignalingClient signalingClient, ILogger logger) : base(logger)
    {
        _signalingClient = signalingClient;
        _udpUpgrade = new UdpUpgradeCoordinator(
            this, "Viewer", UdpUpgradeCoordinator.ConfirmTimeoutPolicy.PromoteAnyway);
    }

    /// <summary>
    /// Connect to the relay transport using the encryption nonce received from the host.
    /// </summary>
    public void ConnectRelay(string encryptionNonceHex, string passwordHashHex)
    {
        var nonce = Convert.FromHexString(encryptionNonceHex);

        _logger.LogInformation("Viewer transport: connecting relay with encryption nonce");

        // Setup AES-256-GCM encryption (viewer direction)
        _encryption = new TransportEncryption(passwordHashHex, nonce, isHost: false);

        // Start with WebSocket relay backend
        var relayBackend = new WebSocketRelayBackend(_signalingClient, _logger);
        relayBackend.Start();
        StartTransport(relayBackend, enableVideoSend: false);

        _logger.LogInformation("Viewer transport: relay connected and encrypted");
    }

    /// <summary>
    /// Attempt to upgrade from WebSocket relay to direct UDP.
    /// Call after relay is established. If UDP fails, WebSocket relay continues.
    /// </summary>
    public Task AttemptUdpUpgradeAsync(
        Func<SignalingMessage, Task> sendSignaling,
        string peerId,
        string? turnServerUri = null,
        string? turnUsername = null,
        string? turnCredential = null)
        => _udpUpgrade.AttemptUpgradeAsync(peerId, sendSignaling,
            turnServerUri is null ? null : new TurnCredentials(turnServerUri, turnUsername, turnCredential));

    /// <summary>Handle TransportEndpoint from the host (their UDP candidates).</summary>
    public Task HandleHostEndpointAsync(TransportCandidate[] hostCandidates)
        => _udpUpgrade.HandleRemoteEndpointAsync(hostCandidates);

    /// <summary>Handle TransportConfirmed from the host — host's probe also succeeded.</summary>
    public Task HandleTransportConfirmedAsync()
        => _udpUpgrade.HandleTransportConfirmedAsync();

    public override async ValueTask DisposeAsync()
    {
        await _udpUpgrade.DisposeAsync();
        await base.DisposeAsync();
    }
}
