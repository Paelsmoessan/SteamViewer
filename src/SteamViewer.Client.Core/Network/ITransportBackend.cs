namespace SteamViewer.Client.Core.Network;

/// <summary>
/// Abstraction over the transport I/O layer.
/// StreamTransport sends/receives encrypted frames through this backend.
/// Implementations: WebSocketRelayBackend (Phase 1), UdpTransportBackend (Phase 2).
/// </summary>
public interface ITransportBackend : IAsyncDisposable
{
    /// <summary>Send raw bytes to the peer.</summary>
    Task SendAsync(byte[] data, int offset, int length, CancellationToken ct = default);

    /// <summary>Raised when data is received from the peer. Parameters: (byte[] data, int length).</summary>
    event Action<byte[], int>? OnDataReceived;

    /// <summary>Whether this backend is currently active and can send/receive.</summary>
    bool IsActive { get; }
}
