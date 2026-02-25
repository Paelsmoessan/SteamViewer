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
/// 4. If UDP works → switch backend (transparent to upper layers)
/// </summary>
public sealed class ViewerStreamTransport : StreamTransport
{
    private readonly SignalingClient _signalingClient;
    private UdpTransportBackend? _udpBackend;

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
        var relayBackend = new WebSocketRelayBackend(_signalingClient);
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
    /// Probes the endpoint and switches to UDP if successful.
    /// </summary>
    public async Task HandleHostEndpointAsync(string[] hostIPs, int hostPort)
    {
        if (_udpBackend == null) return;

        foreach (var ip in hostIPs)
        {
            try
            {
                var endpoint = new IPEndPoint(IPAddress.Parse(ip), hostPort);
                var probeOk = await _udpBackend.ProbeAsync(endpoint, TimeSpan.FromSeconds(2));
                if (probeOk)
                {
                    _udpBackend.ConnectToPeer(endpoint, useTurnRelay: false);
                    await SwitchBackendAsync(_udpBackend);
                    _logger.LogInformation("Viewer: switched to direct UDP via {Endpoint}", endpoint);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Viewer: UDP probe to {IP}:{Port} failed", ip, hostPort);
            }
        }

        _logger.LogInformation("Viewer: no UDP path available — staying on WebSocket relay");
    }

    public override async ValueTask DisposeAsync()
    {
        if (_udpBackend != null)
        {
            await _udpBackend.DisposeAsync();
            _udpBackend = null;
        }
        await base.DisposeAsync();
    }
}
