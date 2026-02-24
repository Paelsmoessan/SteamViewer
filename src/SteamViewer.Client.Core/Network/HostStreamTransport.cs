using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace SteamViewer.Client.Core.Network;

/// <summary>
/// Host-side QUIC transport. Listens for a viewer connection on an ephemeral UDP port.
/// Opens two QUIC streams: bidirectional for control, unidirectional for video (host→viewer).
///
/// Connection flow:
/// 1. Host calls StartListeningAsync() → gets ephemeral port
/// 2. Host sends port to viewer via signaling server (TransportEndpoint message)
/// 3. Viewer connects via QUIC (single UDP connection, TLS 1.3 handshake)
/// 4. Host opens control stream (bidirectional) + video stream (unidirectional)
/// 5. Read loops start
/// </summary>
public sealed class HostStreamTransport : StreamTransport
{
    private QuicListener? _listener;
    private X509Certificate2? _cert;
    private int _port;

    public int Port => _port;

    public HostStreamTransport(ILogger logger) : base(logger) { }

    /// <summary>
    /// Start listening on an ephemeral UDP port. Returns the port number.
    /// The viewer will connect to this port after receiving it via signaling.
    /// </summary>
    public async Task<int> StartListeningAsync()
    {
        if (!QuicListener.IsSupported)
            throw new PlatformNotSupportedException(
                "QUIC is not supported on this platform. Ensure .NET 8+ and msquic are available.");

        // Generate ephemeral self-signed cert for QUIC TLS 1.3
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=SteamViewer", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var tempCert = req.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddDays(1));
        // Re-import for Windows Schannel compatibility (ephemeral keys need export/import)
        _cert = new X509Certificate2(tempCert.Export(X509ContentType.Pfx));

        _listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Any, 0),
            ApplicationProtocols = new List<SslApplicationProtocol>
            {
                new("steamviewer")
            },
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 0,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ApplicationProtocols = new List<SslApplicationProtocol>
                    {
                        new("steamviewer")
                    },
                    ServerCertificate = _cert
                }
            })
        });

        _port = _listener.LocalEndPoint.Port;
        _logger.LogInformation("Host transport listening on port {Port} (QUIC/UDP)", _port);
        return _port;
    }

    /// <summary>
    /// Accept a QUIC connection from the viewer and open streams.
    /// Blocks until connection is established or timeout.
    /// </summary>
    public async Task AcceptViewerAsync(TimeSpan timeout)
    {
        if (_listener == null)
            throw new InvalidOperationException("Call StartListeningAsync first");

        using var timeoutCts = new CancellationTokenSource(timeout);

        _logger.LogInformation("Waiting for viewer QUIC connection...");
        _connection = await _listener.AcceptConnectionAsync(timeoutCts.Token);
        _logger.LogInformation("Viewer QUIC connected from {Remote}", _connection.RemoteEndPoint);

        // Host opens both streams — viewer accepts them by type
        _controlStream = await _connection.OpenOutboundStreamAsync(
            QuicStreamType.Bidirectional, timeoutCts.Token);
        _logger.LogInformation("Control stream opened (bidirectional)");

        _videoStream = await _connection.OpenOutboundStreamAsync(
            QuicStreamType.Unidirectional, timeoutCts.Token);
        _logger.LogInformation("Video stream opened (unidirectional host→viewer)");

        // Stop accepting — single viewer only
        await _listener.DisposeAsync();
        _listener = null;

        // Start read loops (video read skipped — outbound unidirectional can't read)
        StartReadLoops();
    }

    /// <summary>Get the host's local IP addresses for signaling.</summary>
    public static List<string> GetLocalIPs()
    {
        var ips = new List<string>();
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork) // IPv4 only for now
                    ips.Add(ip.ToString());
            }
        }
        catch { }
        return ips;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_listener != null)
        {
            try { await _listener.DisposeAsync(); } catch { }
            _listener = null;
        }
        _cert?.Dispose();
        await base.DisposeAsync();
    }
}
