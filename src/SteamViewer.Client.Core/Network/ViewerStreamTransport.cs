using System.Net;
using System.Net.Sockets;
using System.Text.Json;
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
    private Timer? _networkStatsTimer;

    // ICE candidate pair pattern — buffer both sides, probe when both ready
    private TransportCandidate[]? _remoteCandidates;
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

            // Send our candidates to host
            if (candidates.Count > 0)
            {
                await sendSignaling(new SignalingMessage.TransportEndpoint(peerId, candidates.ToArray()));
                _logger.LogInformation("Viewer UDP candidates sent: {Candidates}",
                    string.Join(", ", candidates.Select(c => $"{c.Type}={c.IP}:{c.Port}")));
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
    /// Handle TransportEndpoint from the host (their UDP candidates).
    /// Buffers remote candidates — probing happens when both local and remote are ready (ICE pattern).
    /// </summary>
    public async Task HandleHostEndpointAsync(TransportCandidate[] hostCandidates)
    {
        _logger.LogDebug("[UDP-DIAG] HandleHostEndpointAsync: received {Count} candidates: [{Candidates}], localReady={LocalReady}",
            hostCandidates.Length,
            string.Join(", ", hostCandidates.Select(c => $"{c.Type}={c.IP}:{c.Port}")),
            _localCandidatesReady);

        // Buffer remote candidates
        _remoteCandidates = hostCandidates;

        // Probe if local socket is already initialized
        await TryProbeCandidatesAsync();
    }

    /// <summary>
    /// Probe remote candidates when both local socket and remote candidates are available.
    /// Called from both AttemptUdpUpgradeAsync (local ready) and HandleHostEndpointAsync (remote ready).
    /// </summary>
    private async Task TryProbeCandidatesAsync()
    {
        if (!_localCandidatesReady || _remoteCandidates == null || _udpBackend == null) return;

        var candidates = _remoteCandidates;
        _remoteCandidates = null; // Consume — don't re-probe

        _logger.LogDebug("[UDP-DIAG] TryProbeCandidatesAsync: probing {Count} candidates: [{Candidates}]",
            candidates.Length, string.Join(", ", candidates.Select(c => $"{c.Type}={c.IP}:{c.Port}")));

        // Probe host and srflx candidates first (direct)
        foreach (var candidate in candidates.Where(c => c.Type != "relay"))
        {
            try
            {
                var endpoint = new IPEndPoint(IPAddress.Parse(candidate.IP), candidate.Port);
                _logger.LogDebug("[UDP-DIAG] Starting probe to {Type} {Endpoint}", candidate.Type, endpoint);
                var probeOk = await _udpBackend.ProbeAsync(endpoint, TimeSpan.FromMilliseconds(1500));
                _logger.LogDebug("[UDP-DIAG] Probe to {Endpoint} result: {Result}", endpoint, probeOk);
                if (probeOk)
                {
                    AcceptUdpPath(endpoint, useTurnRelay: false);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Viewer: UDP probe to {Type} {IP}:{Port} failed", candidate.Type, candidate.IP, candidate.Port);
            }
        }

        // Direct failed — try TURN relay
        var relayCandidate = candidates.FirstOrDefault(c => c.Type == "relay");
        if (relayCandidate != null && _udpBackend.TurnRelayEndPoint != null)
        {
            _logger.LogInformation("Viewer: direct UDP failed, trying TURN relay to {IP}:{Port}", relayCandidate.IP, relayCandidate.Port);
            try
            {
                var relayEndpoint = new IPEndPoint(IPAddress.Parse(relayCandidate.IP), relayCandidate.Port);
                var turnOk = await _udpBackend.ProbeViaTurnRelayAsync(relayEndpoint, TimeSpan.FromSeconds(3));
                if (turnOk)
                {
                    AcceptUdpPath(relayEndpoint, useTurnRelay: true);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Viewer: TURN relay probe failed");
            }
        }

        _logger.LogInformation("Viewer: no UDP path available — staying on WebSocket relay");
    }

    /// <summary>
    /// Accept a working UDP path: connect, send TransportConfirmed, start retry loop.
    /// </summary>
    private void AcceptUdpPath(IPEndPoint endpoint, bool useTurnRelay)
    {
        if (_udpBackend == null) return;

        _udpBackend.ConnectToPeer(endpoint, useTurnRelay);
        _pendingUdpBackend = _udpBackend;
        _localUdpReady = true;
        _logger.LogInformation("Viewer: UDP probe succeeded via {Endpoint} (turn={Turn}) — waiting for peer confirmation", endpoint, useTurnRelay);

        // Send confirmation to host
        if (_sendSignaling != null && _peerId != null)
            _ = _sendSignaling(new SignalingMessage.TransportConfirmed(_peerId));

        // Check if peer already confirmed
        _ = TryCompleteSwitchAsync().ContinueWith(_ =>
        {
            if (IsDirectUdp && _networkStatsTimer == null)
                StartNetworkStatsTimer();
        }, TaskScheduler.Default);

        // Retry TransportConfirmed every 3s — peer may have missed it
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 5; i++)
            {
                await Task.Delay(3000);
                if (_remoteUdpReady || _pendingUdpBackend == null) return;

                if (_sendSignaling != null && _peerId != null)
                {
                    _logger.LogDebug("Viewer: re-sending TransportConfirmed (retry {Retry})", i + 1);
                    try { await _sendSignaling(new SignalingMessage.TransportConfirmed(_peerId)); }
                    catch { }
                }
            }

            if (_localUdpReady && !_remoteUdpReady && _pendingUdpBackend != null)
            {
                _logger.LogWarning("Viewer: peer did not confirm UDP within 15s — staying on relay");
                _localUdpReady = false;
                _pendingUdpBackend = null;
            }
        });
    }

    /// <summary>
    /// Handle TransportConfirmed from the host — host's probe also succeeded.
    /// If our probe already succeeded, complete the switch.
    /// If our probing failed but host proved the path works, accept it (asymmetric NAT recovery).
    /// </summary>
    public async Task HandleTransportConfirmedAsync()
    {
        _logger.LogInformation("Viewer: received UDP confirmation from host");

        // Asymmetric NAT recovery: host probed us successfully (echo worked),
        // so the NAT pinhole is open. Use the endpoint that probed us.
        if (!_localUdpReady && _udpBackend?.LastProbeReceivedFrom != null)
        {
            _logger.LogInformation("Viewer: asymmetric NAT recovery — host proved path via {Endpoint}, accepting",
                _udpBackend.LastProbeReceivedFrom);
            AcceptUdpPath(_udpBackend.LastProbeReceivedFrom, useTurnRelay: false);
        }

        _remoteUdpReady = true;
        await TryCompleteSwitchAsync();

        // Start network stats reporting once UDP is active
        if (IsDirectUdp && _networkStatsTimer == null)
            StartNetworkStatsTimer();
    }

    private void StartNetworkStatsTimer()
    {
        _networkStatsTimer = new Timer(SendNetworkStats, null, 2000, 2000);
        _logger.LogDebug("Network stats timer started (2s interval)");
    }

    private async void SendNetworkStats(object? state)
    {
        if (_backend is not UdpTransportBackend udp || !IsConnected) return;

        try
        {
            var lossRate = udp.GetAndResetLossRate();
            // Only send when there's meaningful data (avoid spamming on idle connections)
            if (lossRate < 0.001 && _lastReportedLossRate < 0.001) return;
            _lastReportedLossRate = lossRate;

            var json = JsonSerializer.Serialize(new { type = "networkStats", lossRate });
            await SendControlAsync(json);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send network stats");
        }
    }

    private double _lastReportedLossRate;

    public override async ValueTask DisposeAsync()
    {
        if (_networkStatsTimer != null)
        {
            await _networkStatsTimer.DisposeAsync();
            _networkStatsTimer = null;
        }
        if (_udpBackend != null && _udpBackend != _pendingUdpBackend)
        {
            await _udpBackend.DisposeAsync();
        }
        _udpBackend = null;
        await base.DisposeAsync();
    }
}
