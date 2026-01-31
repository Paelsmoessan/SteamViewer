using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Blake3;
using Microsoft.Extensions.Logging;
using SteamViewer.Common.Protocol;
using SteamViewer.Server.Services;

namespace SteamViewer.Server.Handlers;

/// <summary>
/// Handles WebSocket connections for signaling between clients.
/// </summary>
public sealed class SignalingHandler
{
    private readonly ClientRegistry _registry;
    private readonly ILogger<SignalingHandler> _logger;

    public SignalingHandler(ClientRegistry registry, ILogger<SignalingHandler> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// Handle an individual WebSocket connection.
    /// </summary>
    public async Task HandleConnectionAsync(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var connectionId = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<SignalingMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        string? clientId = null;

        _logger.LogInformation("WebSocket connection {ConnectionId} opened", connectionId);

        try
        {
            // Run send and receive concurrently
            var sendTask = SendLoopAsync(webSocket, channel.Reader, cancellationToken);
            var receiveTask = ReceiveLoopAsync(webSocket, channel.Writer, connectionId, id => clientId = id, cancellationToken);

            // Wait for either to complete
            await Task.WhenAny(sendTask, receiveTask);

            // Cancel and cleanup
            channel.Writer.Complete();
        }
        finally
        {
            await CleanupClientAsync(clientId, connectionId);
            _logger.LogInformation("WebSocket connection {ConnectionId} closed", connectionId);
        }
    }

    private async Task SendLoopAsync(
        WebSocket webSocket,
        ChannelReader<SignalingMessage> reader,
        CancellationToken cancellationToken)
    {
        await foreach (var message in reader.ReadAllAsync(cancellationToken))
        {
            var json = SignalingSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(json);

            await webSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        }
    }

    private async Task ReceiveLoopAsync(
        WebSocket webSocket,
        ChannelWriter<SignalingMessage> writer,
        Guid connectionId,
        Action<string> setClientId,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var messageBuilder = new StringBuilder();

        while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
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

                            var response = await HandleMessageAsync(message, connectionId, writer, setClientId);
                            if (response != null)
                            {
                                await writer.WriteAsync(response, cancellationToken);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Failed to deserialize message: {Json}", json);
                            await writer.WriteAsync(new SignalingMessage.Error("Invalid message format"), cancellationToken);
                        }
                    }
                }
            }
            catch (WebSocketException ex)
            {
                _logger.LogWarning(ex, "WebSocket error for connection {ConnectionId}", connectionId);
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private Task<SignalingMessage?> HandleMessageAsync(
        SignalingMessage message,
        Guid connectionId,
        ChannelWriter<SignalingMessage> writer,
        Action<string> setClientId)
    {
        var currentClientId = _registry.GetClientIdByConnection(connectionId);

        var result = message switch
        {
            SignalingMessage.Register register => HandleRegister(register, connectionId, writer, setClientId),
            SignalingMessage.ConnectRequest request => HandleConnectRequest(request, currentClientId),
            SignalingMessage.ConnectionResponse response => HandleConnectionResponse(response, currentClientId),
            SignalingMessage.SdpOffer offer => HandleSdpOffer(offer, currentClientId),
            SignalingMessage.SdpAnswer answer => HandleSdpAnswer(answer, currentClientId),
            SignalingMessage.IceCandidate candidate => HandleIceCandidate(candidate, currentClientId),
            SignalingMessage.Disconnect disconnect => HandleDisconnect(disconnect, currentClientId),
            SignalingMessage.Ping => new SignalingMessage.Pong(),
            // Server-only messages that shouldn't be received from clients
            SignalingMessage.RegisterSuccess or
            SignalingMessage.RegisterFailed or
            SignalingMessage.IncomingConnection or
            SignalingMessage.Connected or
            SignalingMessage.Disconnected or
            SignalingMessage.Error or
            SignalingMessage.Pong => new SignalingMessage.Error("Invalid message from client"),
            _ => new SignalingMessage.Error("Unknown message type")
        };

        return Task.FromResult(result);
    }

    private SignalingMessage HandleRegister(
        SignalingMessage.Register register,
        Guid connectionId,
        ChannelWriter<SignalingMessage> writer,
        Action<string> setClientId)
    {
        if (_registry.TryRegister(register.ClientId, register.PasswordHash, writer, connectionId))
        {
            setClientId(register.ClientId);
            _logger.LogInformation("Client {ClientId} registered", register.ClientId);
            return new SignalingMessage.RegisterSuccess(register.ClientId);
        }
        else
        {
            _logger.LogWarning("Client ID {ClientId} already registered", register.ClientId);
            return new SignalingMessage.RegisterFailed("Client ID already registered");
        }
    }

    private SignalingMessage? HandleConnectRequest(SignalingMessage.ConnectRequest request, string? fromId)
    {
        if (fromId == null)
        {
            return new SignalingMessage.Error("Not registered");
        }

        if (!_registry.IsOnline(request.TargetId))
        {
            return new SignalingMessage.Error($"Target client {request.TargetId} is not online");
        }

        // Verify password by hashing and comparing
        var passwordHash = Convert.ToHexString(Hasher.Hash(Encoding.UTF8.GetBytes(request.Password)).AsSpan()).ToLowerInvariant();
        if (!_registry.VerifyPassword(request.TargetId, passwordHash))
        {
            return new SignalingMessage.Error("Invalid password");
        }

        // Forward connection request to target
        if (!_registry.TrySendToClient(request.TargetId, new SignalingMessage.IncomingConnection(fromId)))
        {
            return new SignalingMessage.Error($"Failed to contact target client {request.TargetId}");
        }

        _logger.LogInformation("Connection request from {FromId} to {TargetId}", fromId, request.TargetId);
        return null; // No direct response needed
    }

    private SignalingMessage? HandleConnectionResponse(SignalingMessage.ConnectionResponse response, string? fromId)
    {
        if (fromId == null)
        {
            return new SignalingMessage.Error("Not registered");
        }

        if (response.Approved)
        {
            _registry.SetPeer(fromId, response.TargetId);
            _registry.SetPeer(response.TargetId, fromId);
            _logger.LogInformation("Connection approved between {FromId} and {TargetId}", fromId, response.TargetId);
        }
        else
        {
            _logger.LogInformation("Connection rejected by {FromId}", fromId);
        }

        // Forward response to requester
        _registry.TrySendToClient(response.TargetId, new SignalingMessage.ConnectionResponse(fromId, response.Approved));
        return null;
    }

    private SignalingMessage? HandleSdpOffer(SignalingMessage.SdpOffer offer, string? fromId)
    {
        if (fromId == null)
        {
            return new SignalingMessage.Error("Not registered");
        }

        _registry.TrySendToClient(offer.TargetId, new SignalingMessage.SdpOffer(fromId, offer.Sdp));
        _logger.LogDebug("SDP offer forwarded from {FromId} to {TargetId}", fromId, offer.TargetId);
        return null;
    }

    private SignalingMessage? HandleSdpAnswer(SignalingMessage.SdpAnswer answer, string? fromId)
    {
        if (fromId == null)
        {
            return new SignalingMessage.Error("Not registered");
        }

        _registry.TrySendToClient(answer.TargetId, new SignalingMessage.SdpAnswer(fromId, answer.Sdp));
        _logger.LogDebug("SDP answer forwarded from {FromId} to {TargetId}", fromId, answer.TargetId);
        return null;
    }

    private SignalingMessage? HandleIceCandidate(SignalingMessage.IceCandidate candidate, string? fromId)
    {
        if (fromId == null)
        {
            return new SignalingMessage.Error("Not registered");
        }

        _registry.TrySendToClient(candidate.TargetId, new SignalingMessage.IceCandidate(
            fromId,
            candidate.Candidate,
            candidate.SdpMid,
            candidate.SdpMLineIndex));
        _logger.LogDebug("ICE candidate forwarded from {FromId} to {TargetId}", fromId, candidate.TargetId);
        return null;
    }

    private SignalingMessage? HandleDisconnect(SignalingMessage.Disconnect disconnect, string? fromId)
    {
        if (fromId == null)
        {
            return new SignalingMessage.Error("Not registered");
        }

        _registry.SetPeer(fromId, null);
        _registry.SetPeer(disconnect.PeerId, null);

        // Notify peer
        _registry.TrySendToClient(disconnect.PeerId, new SignalingMessage.Disconnected(fromId, "Peer disconnected"));
        _logger.LogInformation("Disconnect between {FromId} and {PeerId}", fromId, disconnect.PeerId);
        return null;
    }

    private Task CleanupClientAsync(string? clientId, Guid connectionId)
    {
        if (clientId == null)
        {
            return Task.CompletedTask;
        }

        // Get peer ID before unregistering
        var client = _registry.GetClient(clientId);
        var peerId = client?.PeerId;

        // Unregister the client
        _registry.UnregisterByConnection(connectionId);

        // Notify peer if there was one
        if (peerId != null)
        {
            _registry.SetPeer(peerId, null);
            _registry.TrySendToClient(peerId, new SignalingMessage.Disconnected(clientId, "Peer disconnected"));
        }

        _logger.LogInformation("Client {ClientId} disconnected and cleaned up", clientId);
        return Task.CompletedTask;
    }
}
