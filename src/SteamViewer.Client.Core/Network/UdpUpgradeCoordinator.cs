using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Client.Core.Network;

/// <summary>TURN server credentials for the UDP-relay fallback - groups the three wire params
/// (was three separate string args on the upgrade entrypoint).</summary>
internal sealed record TurnCredentials(string Uri, string? Username, string? Credential);

/// <summary>
/// Drives the WebSocket-relay → direct-UDP upgrade for a <see cref="StreamTransport"/>.
/// Shared by <see cref="HostStreamTransport"/> and <see cref="ViewerStreamTransport"/> — the
/// candidate-gather / probe / accept logic is identical on both sides. Only two things differ,
/// and both are constructor parameters:
///   - the role label used in log lines ("Host" / "Viewer"), and
///   - the confirmation-timeout policy: Host tears the pending path down (stays on the known-good
///     relay); Viewer promotes to UDP anyway when its own probe succeeded (defense-in-depth for a
///     HostRecovered re-pair signaling drop — see commit fba0d10).
///
/// Switch-state (_pendingUdpBackend / ready-flags / the actual backend swap) is owned by
/// <see cref="StreamTransport"/>. This coordinator drives that state through the transport's
/// internal API (RegisterPendingUdpBackend / TryCompleteUdpSwitchAsync / MarkRemoteUdpReadyAsync /
/// AbandonPendingUdpBackend) rather than holding it itself.
/// </summary>
internal sealed class UdpUpgradeCoordinator : IAsyncDisposable
{
    /// <summary>What to do when the peer never returns TransportConfirmed within the retry window.</summary>
    public enum ConfirmTimeoutPolicy
    {
        /// <summary>Host: abandon the pending UDP path, stay on the known-good WebSocket relay.</summary>
        TearDown,

        /// <summary>
        /// Viewer: promote to UDP anyway if our own probe succeeded. The missing confirmation is most
        /// likely a Railway routing drop during HostRecovered re-pair, not a true asymmetric NAT.
        /// Gated on the transport's LocalUdpReady so we only promote if our own probe reached the peer.
        /// </summary>
        PromoteAnyway
    }

    private readonly StreamTransport _transport;
    private readonly string _role;
    private readonly ConfirmTimeoutPolicy _timeoutPolicy;
    private readonly ILogger _logger;
    private readonly string _instanceId;

    private UdpTransportBackend? _udpBackend;
    private bool _handedOff; // _udpBackend registered with the transport (transport now owns disposal)

    // ICE candidate pair pattern — buffer both sides, probe when both ready
    private TransportCandidate[]? _remoteCandidates;
    private bool _localCandidatesReady;

    private Func<SignalingMessage, Task>? _sendSignaling;
    private string? _peerId;

    public UdpUpgradeCoordinator(
        StreamTransport transport,
        string role,
        ConfirmTimeoutPolicy timeoutPolicy)
    {
        _transport = transport;
        _role = role;
        _timeoutPolicy = timeoutPolicy;
        _logger = transport.Logger;
        _instanceId = transport.InstanceId;
    }

    /// <summary>
    /// Attempt to upgrade from WebSocket relay to direct UDP (or TURN relay).
    /// Call after relay is established. Runs in background — does not block.
    /// If the upgrade fails, the WebSocket relay continues working.
    /// </summary>
    public async Task AttemptUpgradeAsync(
        string peerId,
        Func<SignalingMessage, Task> sendSignaling,
        TurnCredentials? turn = null)
    {
        // Store for sending TransportConfirmed later
        _sendSignaling = sendSignaling;
        _peerId = peerId;

        try
        {
            _udpBackend = new UdpTransportBackend(_logger);
            await _udpBackend.InitializeAsync(turn?.Uri, turn?.Username, turn?.Credential);

            // Gather candidates: local IPs (host) + reflexive (srflx) + TURN relay
            var candidates = BuildLocalCandidates();

            if (candidates.Count > 0)
            {
                await sendSignaling(new SignalingMessage.TransportEndpoint(peerId, candidates.ToArray()));
                _logger.LogInformation("{Role} UDP candidates sent: {Candidates}", _role,
                    string.Join(", ", candidates.Select(c => $"{c.Type}={c.IP}:{c.Port}")));
            }

            // Local candidates ready — probe if remote already arrived (ICE pattern)
            _localCandidatesReady = true;
            _logger.LogDebug("[UDP-DIAG] {Role}: local candidates ready — checking for buffered remote candidates", _role);
            await TryProbeCandidatesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Role} UDP upgrade attempt failed — staying on WebSocket relay", _role);
            if (_udpBackend != null)
            {
                await _udpBackend.DisposeAsync();
                _udpBackend = null;
            }
        }
    }

    /// <summary>
    /// Handle a TransportEndpoint from the peer (their UDP candidates).
    /// Buffers remote candidates — probing happens when both local and remote are ready (ICE pattern).
    /// </summary>
    public async Task HandleRemoteEndpointAsync(TransportCandidate[] remoteCandidates)
    {
        _logger.LogDebug("[UDP-DIAG] {Role}: HandleRemoteEndpointAsync received {Count} candidates: [{Candidates}], localReady={LocalReady}",
            _role, remoteCandidates.Length,
            string.Join(", ", remoteCandidates.Select(c => $"{c.Type}={c.IP}:{c.Port}")),
            _localCandidatesReady);

        // Buffer remote candidates
        _remoteCandidates = remoteCandidates;

        // Probe if local socket is already initialized
        await TryProbeCandidatesAsync();
    }

    /// <summary>
    /// Handle TransportConfirmed from the peer — the peer's probe also succeeded.
    /// If our probe already succeeded, complete the switch.
    /// If our probing failed but the peer proved the path works, accept it (asymmetric NAT recovery).
    /// </summary>
    public async Task HandleTransportConfirmedAsync()
    {
        _logger.LogInformation("{Role}: received UDP confirmation from peer", _role);

        // Asymmetric NAT recovery: peer probed us successfully (echo worked),
        // so the NAT pinhole is open. Use the endpoint that probed us.
        if (!_transport.LocalUdpReady && _udpBackend?.LastProbeReceivedFrom != null)
        {
            _logger.LogInformation("{Role}: asymmetric NAT recovery — peer proved path via {Endpoint}, accepting",
                _role, _udpBackend.LastProbeReceivedFrom);
            AcceptUdpPath(_udpBackend.LastProbeReceivedFrom, useTurnRelay: false);
        }

        await _transport.MarkRemoteUdpReadyAsync();
    }

    /// <summary>
    /// Collect this side's UDP candidates (local IPs at the bound port, reflexive via STUN,
    /// TURN relay if configured) into the order the peer side expects.
    /// Caller is responsible for non-null _udpBackend (assigned just before).
    /// </summary>
    private List<TransportCandidate> BuildLocalCandidates()
    {
        _logger.LogDebug("[T:{InstanceId}] BuildLocalCandidates entry: localPort={LocalPort}, reflexive={Reflexive}, turnRelay={TurnRelay}",
            _instanceId, _udpBackend!.LocalEndPoint?.Port ?? 0, _udpBackend.ReflexiveEndPoint?.ToString() ?? "null", _udpBackend.TurnRelayEndPoint?.ToString() ?? "null");

        var candidates = new List<TransportCandidate>();
        AddLocalHostCandidates(candidates, _udpBackend.LocalEndPoint?.Port ?? 0);
        AddEndpointCandidate(candidates, _udpBackend.ReflexiveEndPoint, "srflx");
        AddEndpointCandidate(candidates, _udpBackend.TurnRelayEndPoint, "relay");

        _logger.LogDebug("[T:{InstanceId}] BuildLocalCandidates exit: {Count} candidate(s) collected", _instanceId, candidates.Count);
        return candidates;
    }

    /// <summary>Add this host's local IPv4 addresses at the bound port (DNS lookup, best-effort).</summary>
    private static void AddLocalHostCandidates(List<TransportCandidate> candidates, int localPort)
    {
        if (localPort <= 0) return;
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                    candidates.Add(new TransportCandidate(ip.ToString(), localPort, "host"));
            }
        }
        catch { }
    }

    /// <summary>Add a single non-null endpoint (reflexive/relay) as a typed candidate.</summary>
    private static void AddEndpointCandidate(List<TransportCandidate> candidates, IPEndPoint? endpoint, string type)
    {
        if (endpoint != null)
            candidates.Add(new TransportCandidate(endpoint.Address.ToString(), endpoint.Port, type));
    }

    /// <summary>
    /// Probe remote candidates when both local socket and remote candidates are available.
    /// Called from both AttemptUpgradeAsync (local ready) and HandleRemoteEndpointAsync (remote ready).
    /// </summary>
    /// <summary>True when both sides are ready to probe: local candidates gathered, remote candidates
    /// buffered, and the UDP backend initialized.</summary>
    [MemberNotNullWhen(true, nameof(_remoteCandidates), nameof(_udpBackend))]
    private bool ReadyToProbe() => _localCandidatesReady && _remoteCandidates != null && _udpBackend != null;

    private async Task TryProbeCandidatesAsync()
    {
        if (!ReadyToProbe()) return;

        var candidates = _remoteCandidates;
        _remoteCandidates = null; // Consume - don't re-probe

        _logger.LogDebug("[UDP-DIAG] {Role}: TryProbeCandidatesAsync probing {Count} candidates: [{Candidates}]",
            _role, candidates.Length, string.Join(", ", candidates.Select(c => $"{c.Type}={c.IP}:{c.Port}")));

        // Direct (host/srflx) first, then TURN relay as fallback. Each accepts the path on first success.
        if (await TryAcceptDirectCandidateAsync(candidates)) return;
        if (await TryAcceptRelayCandidateAsync(candidates)) return;

        _logger.LogInformation("{Role}: no UDP path available — staying on WebSocket relay", _role);
    }

    /// <summary>Probe the direct (host/srflx) candidates in order; accept and return true on the first success.</summary>
    private async Task<bool> TryAcceptDirectCandidateAsync(TransportCandidate[] candidates)
    {
        foreach (var candidate in candidates.Where(c => c.Type != "relay"))
        {
            try
            {
                var endpoint = new IPEndPoint(IPAddress.Parse(candidate.IP), candidate.Port);
                _logger.LogDebug("[UDP-DIAG] {Role}: starting probe to {Type} {Endpoint}", _role, candidate.Type, endpoint);
                var probeOk = await _udpBackend!.ProbeAsync(endpoint, TimeSpan.FromMilliseconds(1500));
                _logger.LogDebug("[UDP-DIAG] {Role}: probe to {Endpoint} result: {Result}", _role, endpoint, probeOk);
                if (probeOk)
                {
                    AcceptUdpPath(endpoint, useTurnRelay: false);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "{Role}: UDP probe to {Type} {IP}:{Port} failed", _role, candidate.Type, candidate.IP, candidate.Port);
            }
        }
        return false;
    }

    /// <summary>Direct probing failed — try the offered TURN relay candidate if any. Returns true if accepted.</summary>
    private async Task<bool> TryAcceptRelayCandidateAsync(TransportCandidate[] candidates)
    {
        var relayCandidate = candidates.FirstOrDefault(c => c.Type == "relay");
        if (relayCandidate == null || _udpBackend!.TurnRelayEndPoint == null)
            return false;

        _logger.LogInformation("{Role}: direct UDP failed, trying TURN relay to {IP}:{Port}", _role, relayCandidate.IP, relayCandidate.Port);
        try
        {
            var relayEndpoint = new IPEndPoint(IPAddress.Parse(relayCandidate.IP), relayCandidate.Port);
            var turnOk = await _udpBackend.ProbeViaTurnRelayAsync(relayEndpoint, TimeSpan.FromSeconds(3));
            if (turnOk)
            {
                AcceptUdpPath(relayEndpoint, useTurnRelay: true);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Role}: TURN relay probe failed", _role);
        }
        return false;
    }

    /// <summary>
    /// Accept a working UDP path: connect, hand the backend to the transport, send TransportConfirmed,
    /// start the confirmation-retry loop.
    /// </summary>
    private void AcceptUdpPath(IPEndPoint endpoint, bool useTurnRelay)
    {
        if (_udpBackend == null) return;

        _udpBackend.ConnectToPeer(endpoint, useTurnRelay);
        _transport.RegisterPendingUdpBackend(_udpBackend); // subscribe data handler + mark local ready
        _handedOff = true;
        _logger.LogInformation("{Role}: UDP probe succeeded via {Endpoint} (turn={Turn}) — waiting for peer confirmation", _role, endpoint, useTurnRelay);

        // Send confirmation to peer
        if (_sendSignaling != null && _peerId != null)
            _ = _sendSignaling(new SignalingMessage.TransportConfirmed(_peerId));

        // Check if peer already confirmed
        _ = _transport.TryCompleteUdpSwitchAsync();

        // Retry TransportConfirmed every 3s — peer may have missed it
        _ = Task.Run(RetryConfirmationLoopAsync);
    }

    /// <summary>
    /// Re-send TransportConfirmed up to 5 times (15s). If the peer still hasn't confirmed, apply the
    /// role-specific timeout policy. This is the ONLY behavior that differs between Host and Viewer —
    /// kept in one place and switched explicitly, rather than copy-pasted with a divergent tail.
    /// </summary>
    private async Task RetryConfirmationLoopAsync()
    {
        // Peer acked, or the switch is no longer pending - nothing more to do.
        if (await ResendConfirmationUntilAckedAsync()) return;

        // All retries elapsed unacked - apply the role-specific policy if it still applies.
        if (ShouldApplyTimeoutPolicy())
            await ApplyConfirmTimeoutPolicyAsync();
    }

    /// <summary>
    /// Re-send TransportConfirmed up to 5 times (15s) - the peer may have missed it. Returns true if
    /// the peer acked or the switch is no longer pending (caller stops); false if all retries elapsed
    /// without an incoming ack.
    /// </summary>
    private async Task<bool> ResendConfirmationUntilAckedAsync()
    {
        for (int i = 0; i < 5; i++)
        {
            await Task.Delay(3000);
            if (_transport.RemoteUdpReady || !_transport.HasPendingUdp) return true;

            if (_sendSignaling != null && _peerId != null)
            {
                _logger.LogDebug("{Role}: re-sending TransportConfirmed (retry {Retry})", _role, i + 1);
                try { await _sendSignaling(new SignalingMessage.TransportConfirmed(_peerId)); }
                catch { }
            }
        }
        return false;
    }

    /// <summary>True when the confirmation-timeout policy should run: our OUTGOING send worked (6 copies)
    /// but the peer's response never reached us, our own probe succeeded, and the switch is still pending.</summary>
    private bool ShouldApplyTimeoutPolicy()
        => _transport.LocalUdpReady && !_transport.RemoteUdpReady && _transport.HasPendingUdp;

    /// <summary>
    /// Peer never returned TransportConfirmed in the retry window. Role-specific: Host tears the pending
    /// path down (stays on relay); Viewer promotes to UDP anyway as defense-in-depth for a HostRecovered
    /// re-pair signaling drop - see fba0d10 (smoke 2026-05-23 11:44: host upgraded + disposed its relay
    /// 10s later while the viewer stayed on relay with no incoming video).
    /// </summary>
    private async Task ApplyConfirmTimeoutPolicyAsync()
    {
        switch (_timeoutPolicy)
        {
            case ConfirmTimeoutPolicy.PromoteAnyway:
                _logger.LogWarning("{Role}: peer did not confirm UDP within 15s but local probe succeeded - promoting to UDP backend anyway (defense-in-depth for HostRecovered re-pair signaling drop)", _role);
                await _transport.MarkRemoteUdpReadyAsync();
                break;

            case ConfirmTimeoutPolicy.TearDown:
                _logger.LogWarning("{Role}: peer did not confirm UDP within 15s - staying on relay", _role);
                _transport.AbandonPendingUdpBackend();
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Once handed off, the transport owns the backend (as _pendingUdpBackend or the active _backend)
        // and disposes it in StreamTransport.DisposeAsync. Only dispose here for the never-accepted case
        // (init succeeded but no path was ever accepted), which the transport never learned about.
        if (_udpBackend != null && !_handedOff)
        {
            await _udpBackend.DisposeAsync();
        }
        _udpBackend = null;
    }
}
