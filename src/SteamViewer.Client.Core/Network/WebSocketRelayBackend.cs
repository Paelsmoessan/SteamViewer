namespace SteamViewer.Client.Core.Network;

/// <summary>
/// Transport backend that relays binary data through the signaling server's WebSocket.
/// Phase 1 transport — works through any NAT, firewall, proxy.
/// </summary>
public sealed class WebSocketRelayBackend : ITransportBackend
{
    private readonly SignalingClient _signalingClient;
    private bool _active;
    private bool _disposed;

    public event Action<byte[], int>? OnDataReceived;
    public bool IsActive => _active && !_disposed;

    public WebSocketRelayBackend(SignalingClient signalingClient)
    {
        _signalingClient = signalingClient;
    }

    /// <summary>Start relaying binary data through the signaling WebSocket.</summary>
    public void Start()
    {
        _signalingClient.OnBinaryReceived += HandleBinaryReceived;
        _active = true;
    }

    public async Task SendAsync(byte[] data, int offset, int length, CancellationToken ct = default)
    {
        if (!_active || _disposed) return;
        await _signalingClient.SendBinaryAsync(data, offset, length, ct);
    }

    private void HandleBinaryReceived(byte[] data, int length)
    {
        if (!_active || _disposed) return;
        OnDataReceived?.Invoke(data, length);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _active = false;
        _signalingClient.OnBinaryReceived -= HandleBinaryReceived;
        return ValueTask.CompletedTask;
    }
}
