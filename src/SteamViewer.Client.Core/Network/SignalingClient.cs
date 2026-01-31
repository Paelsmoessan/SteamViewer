using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Client.Core.Network;

/// <summary>
/// Client-side WebSocket client for signaling server communication.
/// </summary>
public sealed class SignalingClient : IAsyncDisposable
{
    private readonly ILogger<SignalingClient> _logger;
    private readonly string _serverUrl;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Channel<SignalingMessage> _incomingMessages;

    /// <summary>
    /// Event raised when a message is received from the server.
    /// </summary>
    public event Action<SignalingMessage>? OnMessageReceived;

    /// <summary>
    /// Event raised when the connection is closed.
    /// </summary>
    public event Action<string?>? OnDisconnected;

    /// <summary>
    /// Event raised when an error occurs.
    /// </summary>
    public event Action<Exception>? OnError;

    /// <summary>
    /// Current connection state.
    /// </summary>
    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public SignalingClient(string serverUrl, ILogger<SignalingClient> logger)
    {
        _serverUrl = serverUrl;
        _logger = logger;
        _incomingMessages = Channel.CreateUnbounded<SignalingMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
    }

    /// <summary>
    /// Connect to the signaling server.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        // Clean up any existing connection first (allows reconnection)
        if (_webSocket != null)
        {
            _logger.LogInformation("Cleaning up previous connection before reconnecting");
            await DisposeInternalAsync();
        }

        // Recreate the channel for the new connection (previous channel was completed)
        _incomingMessages = Channel.CreateUnbounded<SignalingMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        _webSocket = new ClientWebSocket();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            var uri = new Uri(_serverUrl);
            await _webSocket.ConnectAsync(uri, _cts.Token);
            _logger.LogInformation("Connected to signaling server at {Url}", _serverUrl);

            // Start receive loop
            _receiveTask = ReceiveLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to signaling server");
            await DisposeInternalAsync();
            throw;
        }
    }

    /// <summary>
    /// Send a message to the signaling server.
    /// </summary>
    public async Task SendAsync(SignalingMessage message, CancellationToken cancellationToken = default)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Not connected");
        }

        var json = SignalingSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _webSocket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);

        _logger.LogDebug("Sent message: {MessageType}", message.GetType().Name);
    }

    /// <summary>
    /// Register with the signaling server.
    /// </summary>
    public async Task<bool> RegisterAsync(string clientId, string passwordHash, CancellationToken cancellationToken = default)
    {
        await SendAsync(new SignalingMessage.Register(clientId, passwordHash), cancellationToken);

        // Wait for response
        var response = await WaitForMessageAsync<SignalingMessage>(
            m => m is SignalingMessage.RegisterSuccess or SignalingMessage.RegisterFailed,
            TimeSpan.FromSeconds(10),
            cancellationToken);

        return response is SignalingMessage.RegisterSuccess;
    }

    /// <summary>
    /// Request connection to a peer.
    /// </summary>
    public async Task RequestConnectionAsync(string targetId, string password, CancellationToken cancellationToken = default)
    {
        await SendAsync(new SignalingMessage.ConnectRequest(targetId, password), cancellationToken);
    }

    /// <summary>
    /// Respond to an incoming connection request.
    /// </summary>
    public async Task RespondToConnectionAsync(string targetId, bool approved, CancellationToken cancellationToken = default)
    {
        await SendAsync(new SignalingMessage.ConnectionResponse(targetId, approved), cancellationToken);
    }

    /// <summary>
    /// Send SDP offer to a peer.
    /// </summary>
    public async Task SendSdpOfferAsync(string targetId, string sdp, CancellationToken cancellationToken = default)
    {
        await SendAsync(new SignalingMessage.SdpOffer(targetId, sdp), cancellationToken);
    }

    /// <summary>
    /// Send SDP answer to a peer.
    /// </summary>
    public async Task SendSdpAnswerAsync(string targetId, string sdp, CancellationToken cancellationToken = default)
    {
        await SendAsync(new SignalingMessage.SdpAnswer(targetId, sdp), cancellationToken);
    }

    /// <summary>
    /// Send ICE candidate to a peer.
    /// </summary>
    public async Task SendIceCandidateAsync(string targetId, string candidate, string? sdpMid, ushort? sdpMLineIndex, CancellationToken cancellationToken = default)
    {
        await SendAsync(new SignalingMessage.IceCandidate(targetId, candidate, sdpMid, sdpMLineIndex), cancellationToken);
    }

    /// <summary>
    /// Disconnect from a peer.
    /// </summary>
    public async Task DisconnectFromPeerAsync(string peerId, CancellationToken cancellationToken = default)
    {
        await SendAsync(new SignalingMessage.Disconnect(peerId), cancellationToken);
    }

    /// <summary>
    /// Send a ping to keep the connection alive.
    /// </summary>
    public async Task PingAsync(CancellationToken cancellationToken = default)
    {
        await SendAsync(new SignalingMessage.Ping(), cancellationToken);
    }

    /// <summary>
    /// Wait for a specific message type.
    /// </summary>
    public async Task<T> WaitForMessageAsync<T>(Func<T, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken = default) where T : SignalingMessage
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        await foreach (var message in _incomingMessages.Reader.ReadAllAsync(linkedCts.Token))
        {
            if (message is T typed && predicate(typed))
            {
                return typed;
            }

            // Re-queue non-matching messages by raising the event
            OnMessageReceived?.Invoke(message);
        }

        throw new TimeoutException($"Timeout waiting for message of type {typeof(T).Name}");
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var messageBuilder = new StringBuilder();

        try
        {
            while (_webSocket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    OnDisconnected?.Invoke(result.CloseStatusDescription);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    if (result.EndOfMessage)
                    {
                        var json = messageBuilder.ToString();
                        messageBuilder.Clear();

                        var message = SignalingSerializer.Deserialize(json);
                        if (message != null)
                        {
                            _logger.LogDebug("Received message: {MessageType}", message.GetType().Name);
                            await _incomingMessages.Writer.WriteAsync(message, cancellationToken);
                            OnMessageReceived?.Invoke(message);
                        }
                        else
                        {
                            _logger.LogWarning("Failed to deserialize message: {Json}", json);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket error in receive loop");
            OnError?.Invoke(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in receive loop");
            OnError?.Invoke(ex);
        }
        finally
        {
            _incomingMessages.Writer.Complete();
        }
    }

    /// <summary>
    /// Disconnect from the signaling server.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_webSocket != null && _webSocket.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting", CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during graceful disconnect");
            }
        }

        await DisposeInternalAsync();
    }

    private async Task DisposeInternalAsync()
    {
        _cts?.Cancel();

        if (_receiveTask != null)
        {
            try
            {
                await _receiveTask;
            }
            catch
            {
                // Ignore exceptions during cleanup
            }
        }

        _webSocket?.Dispose();
        _webSocket = null;
        _cts?.Dispose();
        _cts = null;
        _receiveTask = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
