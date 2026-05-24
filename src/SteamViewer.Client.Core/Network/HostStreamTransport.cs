using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Client.Core.Network;

/// <summary>
/// Host-side transport. Starts with WebSocket relay (Phase 1), optionally upgrades to direct UDP (Phase 2).
///
/// Connection flow:
/// 1. Host approves incoming connection
/// 2. StartRelayAsync(): WebSocket relay via signaling server (immediate)
/// 3. In background: AttemptUdpUpgradeAsync() tries STUN/TURN for direct P2P
/// 4. If UDP probe succeeds → send TransportConfirmed, wait for peer confirmation → switch backend
///
/// The UDP upgrade itself is delegated to a shared <see cref="UdpUpgradeCoordinator"/> (identical
/// logic to the viewer side); only the confirmation-timeout policy differs — the host tears the
/// pending path down and stays on the known-good relay.
/// </summary>
public sealed class HostStreamTransport : StreamTransport
{
    private readonly SignalingClient _signalingClient;
    private readonly UdpUpgradeCoordinator _udpUpgrade;

    /// <summary>Get the active UDP backend for runtime tuning (FEC, etc.). Null if not on UDP.</summary>
    public UdpTransportBackend? GetUdpBackend() => _backend as UdpTransportBackend;

    public HostStreamTransport(SignalingClient signalingClient, ILogger logger) : base(logger)
    {
        _signalingClient = signalingClient;
        _udpUpgrade = new UdpUpgradeCoordinator(
            this, "Host", UdpUpgradeCoordinator.ConfirmTimeoutPolicy.TearDown);
    }

    /// <summary>
    /// Start the relay transport. Generates encryption nonce, sets up encryption,
    /// sends RelayReady to the viewer, and starts the transport.
    /// </summary>
    public async Task StartRelayAsync(string peerId, string passwordHashHex, Func<SignalingMessage, Task> sendSignaling)
    {
        // Generate random 32-byte encryption nonce for this session
        var nonce = new byte[32];
        RandomNumberGenerator.Fill(nonce);
        var nonceHex = Convert.ToHexString(nonce).ToLowerInvariant();

        _logger.LogInformation("Host transport: starting relay for peer {PeerId}", peerId);

        // Setup AES-256-GCM encryption (host direction)
        _encryption = new TransportEncryption(passwordHashHex, nonce, isHost: true);

        // Start with WebSocket relay backend
        var relayBackend = new WebSocketRelayBackend(_signalingClient, _logger);
        relayBackend.Start();
        StartTransport(relayBackend, enableVideoSend: true);

        // Send RelayReady to viewer (includes nonce for key derivation)
        await sendSignaling(new SignalingMessage.RelayReady(peerId, nonceHex));
        _logger.LogInformation("Host transport: RelayReady sent to {PeerId}", peerId);
    }

    /// <summary>
    /// Attempt to upgrade from WebSocket relay to direct UDP (or TURN relay).
    /// Call after relay is established. Runs in background — does not block.
    /// If UDP upgrade fails, the WebSocket relay continues working.
    /// </summary>
    public Task AttemptUdpUpgradeAsync(
        string peerId,
        Func<SignalingMessage, Task> sendSignaling,
        string? turnServerUri = null,
        string? turnUsername = null,
        string? turnCredential = null)
        => _udpUpgrade.AttemptUpgradeAsync(peerId, sendSignaling,
            turnServerUri is null ? null : new TurnCredentials(turnServerUri, turnUsername, turnCredential));

    /// <summary>Handle a TransportEndpoint from the viewer (their UDP candidates).</summary>
    public Task HandleViewerEndpointAsync(TransportCandidate[] viewerCandidates)
        => _udpUpgrade.HandleRemoteEndpointAsync(viewerCandidates);

    /// <summary>Handle TransportConfirmed from the viewer — viewer's probe also succeeded.</summary>
    public Task HandleTransportConfirmedAsync()
        => _udpUpgrade.HandleTransportConfirmedAsync();

    public override async ValueTask DisposeAsync()
    {
        await _udpUpgrade.DisposeAsync();
        await base.DisposeAsync();
    }
}
