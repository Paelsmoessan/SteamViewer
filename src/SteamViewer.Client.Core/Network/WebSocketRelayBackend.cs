using Microsoft.Extensions.Logging;

namespace SteamViewer.Client.Core.Network;

/// <summary>
/// Transport backend that relays binary data through the signaling server's WebSocket.
/// Phase 1 transport — works through any NAT, firewall, proxy.
/// </summary>
public sealed class WebSocketRelayBackend : ITransportBackend
{
    private readonly SignalingClient _signalingClient;
    private readonly ILogger _logger;
    private bool _active;
    private bool _disposed;
    private long _bytesSent;
    private long _bytesReceived;

    public event Action<byte[], int>? OnDataReceived;
    public event Action? OnDisconnected;
    public bool IsActive => _active && !_disposed;

    public WebSocketRelayBackend(SignalingClient signalingClient, ILogger logger)
    {
        _signalingClient = signalingClient;
        _logger = logger;
    }

    /// <summary>Start relaying binary data through the signaling WebSocket.</summary>
    public void Start()
    {
        _signalingClient.OnBinaryReceived += HandleBinaryReceived;
        _signalingClient.OnDisconnected += HandleSignalingDisconnected;
        _active = true;
        _logger.LogDebug("[RELAY] WebSocketRelayBackend started");
    }

    public async Task SendAsync(byte[] data, int offset, int length, CancellationToken ct = default)
    {
        if (!_active || _disposed)
        {
            _logger.LogDebug("[RELAY] SendAsync dropped — active={Active}, disposed={Disposed}", _active, _disposed);
            return;
        }
        _bytesSent += length;
        await _signalingClient.SendBinaryAsync(data, offset, length, ct);
    }

    private void HandleBinaryReceived(byte[] data, int length)
    {
        if (!_active || _disposed) return;
        _bytesReceived += length;
        OnDataReceived?.Invoke(data, length);
    }

    private void HandleSignalingDisconnected(string? reason)
    {
        if (!_active || _disposed) return;
        _logger.LogDebug("[RELAY] Signaling disconnected: {Reason} (sent={Sent}KB, received={Received}KB)",
            reason ?? "unknown", _bytesSent / 1024, _bytesReceived / 1024);
        _active = false;
        OnDisconnected?.Invoke();
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _active = false;
        _logger.LogDebug("[RELAY] WebSocketRelayBackend disposed (sent={Sent}KB, received={Received}KB)",
            _bytesSent / 1024, _bytesReceived / 1024);
        _signalingClient.OnBinaryReceived -= HandleBinaryReceived;
        _signalingClient.OnDisconnected -= HandleSignalingDisconnected;
        return ValueTask.CompletedTask;
    }
}
