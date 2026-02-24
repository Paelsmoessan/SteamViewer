using System.Net;
using System.Net.Quic;
using System.Net.Security;
using Microsoft.Extensions.Logging;

namespace SteamViewer.Client.Core.Network;

/// <summary>
/// Viewer-side QUIC transport. Connects to the host at the endpoint received via signaling.
/// Accepts two QUIC streams from host: bidirectional for control, unidirectional for video.
///
/// Connection flow:
/// 1. Viewer receives TransportEndpoint from signaling (host IPs + port)
/// 2. Viewer calls ConnectAsync(ips, port) — tries each IP until one connects
/// 3. QUIC connection established (TLS 1.3 with self-signed cert acceptance)
/// 4. Viewer accepts control stream (bidirectional) + video stream (unidirectional)
/// 5. Read loops start
/// </summary>
public sealed class ViewerStreamTransport : StreamTransport
{
    public ViewerStreamTransport(ILogger logger) : base(logger) { }

    /// <summary>
    /// Connect to the host at the given endpoint.
    /// Establishes QUIC connection and accepts streams.
    /// </summary>
    public async Task ConnectAsync(string host, int port, TimeSpan timeout)
    {
        if (!QuicConnection.IsSupported)
            throw new PlatformNotSupportedException(
                "QUIC is not supported on this platform. Ensure .NET 8+ and msquic are available.");

        using var timeoutCts = new CancellationTokenSource(timeout);

        _logger.LogInformation("Connecting to host {Host}:{Port} (QUIC/UDP)...", host, port);

        _connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse(host), port),
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = new List<SslApplicationProtocol>
                {
                    new("steamviewer")
                },
                // Accept self-signed cert from host
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }
        }, timeoutCts.Token);

        _logger.LogInformation("QUIC connected to {Remote}", _connection.RemoteEndPoint);

        // Accept streams opened by host (order-independent — identify by stream type)
        for (int i = 0; i < 2; i++)
        {
            var stream = await _connection.AcceptInboundStreamAsync(timeoutCts.Token);
            if (stream.Type == QuicStreamType.Bidirectional)
            {
                _controlStream = stream;
                _logger.LogInformation("Control stream accepted (bidirectional)");
            }
            else
            {
                _videoStream = stream;
                _logger.LogInformation("Video stream accepted (unidirectional)");
            }
        }

        if (_controlStream == null || _videoStream == null)
            throw new InvalidOperationException("Failed to accept required QUIC streams from host");

        // Start read loops (video send skipped — viewer doesn't send video)
        StartReadLoops();
    }

    /// <summary>
    /// Connect to host, trying multiple IP addresses (for multi-homed hosts).
    /// Tries each address in order until one succeeds.
    /// </summary>
    public async Task ConnectAsync(string[] hostIPs, int port, TimeSpan timeout)
    {
        Exception? lastEx = null;
        foreach (var ip in hostIPs)
        {
            try
            {
                await ConnectAsync(ip, port, timeout);
                return; // Success
            }
            catch (Exception ex)
            {
                lastEx = ex;
                _logger.LogWarning("Failed to connect to {IP}:{Port}: {Error}", ip, port, ex.Message);
                // Clean up failed attempt
                if (_connection != null)
                {
                    try { await _connection.CloseAsync(0); } catch { }
                    try { await _connection.DisposeAsync(); } catch { }
                    _connection = null;
                }
                _controlStream = null;
                _videoStream = null;
            }
        }

        throw new InvalidOperationException("Failed to connect to any host address", lastEx);
    }
}
