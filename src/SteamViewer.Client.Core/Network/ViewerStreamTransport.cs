using System.Net;
using System.Net.Sockets;
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
/// </summary>
public sealed class ViewerStreamTransport : StreamTransport
{
    private readonly SignalingClient _signalingClient;
    private UdpTransportBackend? _udpBackend;
    private Func<SignalingMessage, Task>? _sendSignaling;
    private string? _peerId;

    // ICE candidate pair pattern — buffer both sides, probe when both ready
    private string[]? _remoteCandidateIPs;
    private int _remoteCandidatePort;
    private bool _localCandidatesReady;

    public ViewerStreamTransport(SignalingClient signalingClient, ILogger logger) : base(logger)
    {
        _signalingClient = signalingClient;
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
    public async Task AttemptUdpUpgradeAsync(
        Func<SignalingMessage, Task> sendSignaling,
        string peerId,
        string? turnServerUri = null,
        string? turnUsername = null,
        string? turnCredential = null)
    {
        // Store for sending TransportConfirmed later
        _sendSignaling = sendSignaling;
        _peerId = peerId;

        try
        {
            _udpBackend = new UdpTransportBackend(_logger);
            await _udpBackend.InitializeAsync(turnServerUri, turnUsername, turnCredential);

            // Gather endpoints
            var endpoints = new List<string>();
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

            if (_udpBackend.ReflexiveEndPoint != null)
                endpoints.Add(_udpBackend.ReflexiveEndPoint.Address.ToString());

            var port = _udpBackend.LocalEndPoint?.Port ?? 0;

            // Send our endpoints to host
            if (endpoints.Count > 0 && port > 0)
            {
                await sendSignaling(new SignalingMessage.TransportEndpoint(peerId, endpoints.ToArray(), port));
                _logger.LogInformation("Viewer UDP endpoints sent: {Endpoints}:{Port}", string.Join(",", endpoints), port);
            }

            // Local candidates ready — probe if remote already arrived (ICE pattern)
            _localCandidatesReady = true;
            _logger.LogDebug("[UDP-DIAG] Local candidates ready — checking for buffered remote candidates");
            await TryProbeCandidatesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Viewer UDP upgrade attempt failed — staying on WebSocket relay");
            if (_udpBackend != null)
            {
                await _udpBackend.DisposeAsync();
                _udpBackend = null;
            }
        }
    }

    /// <summary>
    /// Handle TransportEndpoint from the host (their UDP candidate).
    /// Buffers remote candidates — probing happens when both local and remote are ready (ICE pattern).
    /// </summary>
    public async Task HandleHostEndpointAsync(string[] hostIPs, int hostPort)
    {
        _logger.LogDebug("[UDP-DIAG] HandleHostEndpointAsync: received {Count} IPs on port {Port}: [{IPs}], localReady={LocalReady}",
            hostIPs.Length, hostPort, string.Join(", ", hostIPs), _localCandidatesReady);

        // Buffer remote candidates
        _remoteCandidateIPs = hostIPs;
        _remoteCandidatePort = hostPort;

        // Probe if local socket is already initialized
        await TryProbeCandidatesAsync();
    }

    /// <summary>
    /// Probe remote candidates when both local socket and remote candidates are available.
    /// Called from both AttemptUdpUpgradeAsync (local ready) and HandleHostEndpointAsync (remote ready).
    /// </summary>
    private async Task TryProbeCandidatesAsync()
    {
        if (!_localCandidatesReady || _remoteCandidateIPs == null || _udpBackend == null) return;

        var remoteIPs = _remoteCandidateIPs;
        var remotePort = _remoteCandidatePort;
        _remoteCandidateIPs = null; // Consume — don't re-probe

        _logger.LogDebug("[UDP-DIAG] TryProbeCandidatesAsync: probing {Count} IPs on port {Port}: [{IPs}]",
            remoteIPs.Length, remotePort, string.Join(", ", remoteIPs));

        foreach (var ip in remoteIPs)
        {
            try
            {
                var endpoint = new IPEndPoint(IPAddress.Parse(ip), remotePort);
                _logger.LogDebug("[UDP-DIAG] Starting probe to {Endpoint}", endpoint);
                var probeOk = await _udpBackend.ProbeAsync(endpoint, TimeSpan.FromSeconds(2));
                _logger.LogDebug("[UDP-DIAG] Probe to {Endpoint} result: {Result}", endpoint, probeOk);
                if (probeOk)
                {
                    _udpBackend.ConnectToPeer(endpoint, useTurnRelay: false);

                    // Store as pending — don't switch yet
                    _pendingUdpBackend = _udpBackend;
                    _localUdpReady = true;
                    _logger.LogInformation("Viewer: UDP probe succeeded via {Endpoint} — waiting for peer confirmation", endpoint);

                    // Send confirmation to host
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
                            _logger.LogWarning("Viewer: peer did not confirm UDP within 5s — staying on relay");
                            _localUdpReady = false;
                            _pendingUdpBackend = null;
                        }
                    });
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Viewer: UDP probe to {IP}:{Port} failed", ip, remotePort);
            }
        }

        _logger.LogInformation("Viewer: no UDP path available — staying on WebSocket relay");
    }

    /// <summary>
    /// Handle TransportConfirmed from the host — host's probe also succeeded.
    /// If our probe already succeeded, complete the switch.
    /// </summary>
    public async Task HandleTransportConfirmedAsync()
    {
        _remoteUdpReady = true;
        _logger.LogInformation("Viewer: received UDP confirmation from host");
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
