using System.Net;
using System.Net.Sockets;
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
/// </summary>
public sealed class HostStreamTransport : StreamTransport
{
    private readonly SignalingClient _signalingClient;
    private UdpTransportBackend? _udpBackend;
    private Func<SignalingMessage, Task>? _sendSignaling;
    private string? _peerId;

    public HostStreamTransport(SignalingClient signalingClient, ILogger logger) : base(logger)
    {
        _signalingClient = signalingClient;
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
    public async Task AttemptUdpUpgradeAsync(
        string peerId,
        Func<SignalingMessage, Task> sendSignaling,
        string? turnServerUri = null,
        string? turnUsername = null,
        string? turnCredential = null,
        Func<string[], int, Task>? onEndpointReady = null)
    {
        // Store for sending TransportConfirmed later
        _sendSignaling = sendSignaling;
        _peerId = peerId;

        try
        {
            _udpBackend = new UdpTransportBackend(_logger);
            await _udpBackend.InitializeAsync(turnServerUri, turnUsername, turnCredential);

            // Gather endpoints: local IPs + reflexive + TURN relay
            var endpoints = new List<string>();
            // Local IPs
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                        endpoints.Add(ip.ToString());
                }
            }
            catch { }

            // Reflexive endpoint from STUN
            if (_udpBackend.ReflexiveEndPoint != null)
                endpoints.Add(_udpBackend.ReflexiveEndPoint.Address.ToString());

            var port = _udpBackend.LocalEndPoint?.Port ?? 0;

            // Send TransportEndpoint to viewer with all candidate IPs and the local port
            if (endpoints.Count > 0 && port > 0)
            {
                await sendSignaling(new SignalingMessage.TransportEndpoint(peerId, endpoints.ToArray(), port));
                onEndpointReady?.Invoke(endpoints.ToArray(), port);
                _logger.LogInformation("Host UDP endpoints sent: {Endpoints}:{Port}", string.Join(",", endpoints), port);
            }

            // The viewer will try to probe us. We wait for incoming data on UDP.
            // For now, the upgrade is triggered when viewer sends TransportEndpoint back.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UDP upgrade attempt failed — staying on WebSocket relay");
            if (_udpBackend != null)
            {
                await _udpBackend.DisposeAsync();
                _udpBackend = null;
            }
        }
    }

    /// <summary>
    /// Handle a TransportEndpoint from the viewer (their UDP candidate).
    /// Probes the endpoint. If successful, stores as pending and sends TransportConfirmed.
    /// Actual switch happens only when both sides confirm.
    /// </summary>
    public async Task HandleViewerEndpointAsync(string[] viewerIPs, int viewerPort)
    {
        if (_udpBackend == null) return;

        _logger.LogDebug("[UDP-DIAG] HandleViewerEndpointAsync: probing {Count} IPs on port {Port}: [{IPs}]",
            viewerIPs.Length, viewerPort, string.Join(", ", viewerIPs));

        foreach (var ip in viewerIPs)
        {
            try
            {
                var endpoint = new IPEndPoint(IPAddress.Parse(ip), viewerPort);
                _logger.LogDebug("[UDP-DIAG] Starting probe to {Endpoint}", endpoint);
                var probeOk = await _udpBackend.ProbeAsync(endpoint, TimeSpan.FromSeconds(2));
                _logger.LogDebug("[UDP-DIAG] Probe to {Endpoint} result: {Result}", endpoint, probeOk);
                if (probeOk)
                {
                    _udpBackend.ConnectToPeer(endpoint, useTurnRelay: false);

                    // Store as pending — don't switch yet
                    _pendingUdpBackend = _udpBackend;
                    _localUdpReady = true;
                    _logger.LogInformation("Host: UDP probe succeeded via {Endpoint} — waiting for peer confirmation", endpoint);

                    // Send confirmation to viewer
                    if (_sendSignaling != null && _peerId != null)
                        await _sendSignaling(new SignalingMessage.TransportConfirmed(_peerId));

                    // Check if peer already confirmed
                    await TryCompleteSwitchAsync();

                    // Start timeout — if peer doesn't confirm within 5s, stay on relay
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(5000);
                        if (_localUdpReady && !_remoteUdpReady && _pendingUdpBackend != null)
                        {
                            _logger.LogWarning("Host: peer did not confirm UDP within 5s — staying on relay");
                            _localUdpReady = false;
                            _pendingUdpBackend = null;
                        }
                    });
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Host: UDP probe to {IP}:{Port} failed", ip, viewerPort);
            }
        }

        // Direct failed — try TURN relay
        if (_udpBackend.TurnRelayEndPoint != null)
        {
            _logger.LogInformation("Host: direct UDP failed, trying TURN relay");
            _logger.LogInformation("Host: TURN relay upgrade not yet implemented — staying on WebSocket relay");
        }
        else
        {
            _logger.LogInformation("Host: no UDP path available — staying on WebSocket relay");
        }
    }

    /// <summary>
    /// Handle TransportConfirmed from the viewer — viewer's probe also succeeded.
    /// If our probe already succeeded, complete the switch.
    /// </summary>
    public async Task HandleTransportConfirmedAsync()
    {
        _remoteUdpReady = true;
        _logger.LogInformation("Host: received UDP confirmation from viewer");
        await TryCompleteSwitchAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        if (_udpBackend != null && _udpBackend != _pendingUdpBackend)
        {
            await _udpBackend.DisposeAsync();
        }
        _udpBackend = null;
        await base.DisposeAsync();
    }
}
