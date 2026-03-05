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

    // ICE candidate pair pattern — buffer both sides, probe when both ready
    private TransportCandidate[]? _remoteCandidates;
    private bool _localCandidatesReady;

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
        string? turnCredential = null)
    {
        // Store for sending TransportConfirmed later
        _sendSignaling = sendSignaling;
        _peerId = peerId;

        try
        {
            _udpBackend = new UdpTransportBackend(_logger);
            await _udpBackend.InitializeAsync(turnServerUri, turnUsername, turnCredential);

            // Gather candidates: local IPs (host) + reflexive (srflx) + TURN relay
            var candidates = new List<TransportCandidate>();
            var localPort = _udpBackend.LocalEndPoint?.Port ?? 0;

            // Local IPs — each uses the local socket port
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork && localPort > 0)
                        candidates.Add(new TransportCandidate(ip.ToString(), localPort, "host"));
                }
            }
            catch { }

            // Reflexive endpoint from STUN — uses the NAT-mapped port
            if (_udpBackend.ReflexiveEndPoint != null)
                candidates.Add(new TransportCandidate(
                    _udpBackend.ReflexiveEndPoint.Address.ToString(),
                    _udpBackend.ReflexiveEndPoint.Port,
                    "srflx"));

            // TURN relay endpoint
            if (_udpBackend.TurnRelayEndPoint != null)
                candidates.Add(new TransportCandidate(
                    _udpBackend.TurnRelayEndPoint.Address.ToString(),
                    _udpBackend.TurnRelayEndPoint.Port,
                    "relay"));

            // Send TransportEndpoint to viewer with per-candidate (ip, port, type)
            if (candidates.Count > 0)
            {
                await sendSignaling(new SignalingMessage.TransportEndpoint(peerId, candidates.ToArray()));
                _logger.LogInformation("Host UDP candidates sent: {Candidates}",
                    string.Join(", ", candidates.Select(c => $"{c.Type}={c.IP}:{c.Port}")));
            }

            // Local candidates ready — probe if remote already arrived (ICE pattern)
            _localCandidatesReady = true;
            _logger.LogDebug("[UDP-DIAG] Local candidates ready — checking for buffered remote candidates");
            await TryProbeCandidatesAsync();
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
    /// Handle a TransportEndpoint from the viewer (their UDP candidates).
    /// Buffers remote candidates — probing happens when both local and remote are ready (ICE pattern).
    /// </summary>
    public async Task HandleViewerEndpointAsync(TransportCandidate[] viewerCandidates)
    {
        _logger.LogDebug("[UDP-DIAG] HandleViewerEndpointAsync: received {Count} candidates: [{Candidates}], localReady={LocalReady}",
            viewerCandidates.Length,
            string.Join(", ", viewerCandidates.Select(c => $"{c.Type}={c.IP}:{c.Port}")),
            _localCandidatesReady);

        // Buffer remote candidates
        _remoteCandidates = viewerCandidates;

        // Probe if local socket is already initialized
        await TryProbeCandidatesAsync();
    }

    /// <summary>
    /// Probe remote candidates when both local socket and remote candidates are available.
    /// Called from both AttemptUdpUpgradeAsync (local ready) and HandleViewerEndpointAsync (remote ready).
    /// </summary>
    private async Task TryProbeCandidatesAsync()
    {
        if (!_localCandidatesReady || _remoteCandidates == null || _udpBackend == null) return;

        var candidates = _remoteCandidates;
        _remoteCandidates = null; // Consume — don't re-probe

        _logger.LogDebug("[UDP-DIAG] TryProbeCandidatesAsync: probing {Count} candidates: [{Candidates}]",
            candidates.Length, string.Join(", ", candidates.Select(c => $"{c.Type}={c.IP}:{c.Port}")));

        // Probe host and srflx candidates first (direct), relay last
        foreach (var candidate in candidates.OrderBy(c => c.Type == "relay" ? 1 : 0))
        {
            if (candidate.Type == "relay") continue; // Phase 4 handles relay candidates

            try
            {
                var endpoint = new IPEndPoint(IPAddress.Parse(candidate.IP), candidate.Port);
                _logger.LogDebug("[UDP-DIAG] Starting probe to {Type} {Endpoint}", candidate.Type, endpoint);
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

                    // Retry TransportConfirmed every 3s — peer may have missed it
                    _ = Task.Run(async () =>
                    {
                        for (int i = 0; i < 5; i++)
                        {
                            await Task.Delay(3000);
                            if (_remoteUdpReady || _pendingUdpBackend == null) return;

                            if (_sendSignaling != null && _peerId != null)
                            {
                                _logger.LogDebug("Host: re-sending TransportConfirmed (retry {Retry})", i + 1);
                                try { await _sendSignaling(new SignalingMessage.TransportConfirmed(_peerId)); }
                                catch { }
                            }
                        }

                        if (_localUdpReady && !_remoteUdpReady && _pendingUdpBackend != null)
                        {
                            _logger.LogWarning("Host: peer did not confirm UDP within 15s — staying on relay");
                            _localUdpReady = false;
                            _pendingUdpBackend = null;
                        }
                    });
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Host: UDP probe to {Type} {IP}:{Port} failed", candidate.Type, candidate.IP, candidate.Port);
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
