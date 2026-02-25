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
    private readonly SessionRegistry _sessionRegistry;
    private readonly ILogger<SignalingHandler> _logger;

    public SignalingHandler(ClientRegistry registry, SessionRegistry sessionRegistry, ILogger<SignalingHandler> logger)
    {
        _registry = registry;
        _sessionRegistry = sessionRegistry;
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
            var sendTask = SendLoopAsync(webSocket, channel.Reader, connectionId, cancellationToken);
            var receiveTask = ReceiveLoopAsync(webSocket, channel.Writer, connectionId, id =>
            {
                clientId = id;
                // Store WebSocket reference for binary relay
                var client = _registry.GetClient(id);
                if (client != null) client.WebSocket = webSocket;
            }, cancellationToken);

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
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        // Get write lock for this client (needed to coordinate with binary relay)
        SemaphoreSlim? writeLock = null;

        await foreach (var message in reader.ReadAllAsync(cancellationToken))
        {
            var json = SignalingSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(json);

            // Lazily get the write lock (client may not be registered yet for first messages)
            writeLock ??= GetWriteLock(connectionId);

            if (writeLock != null)
            {
                await writeLock.WaitAsync(cancellationToken);
                try
                {
                    await webSocket.SendAsync(
                        new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        cancellationToken);
                }
                finally { writeLock.Release(); }
            }
            else
            {
                await webSocket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken);
            }
        }
    }

    private SemaphoreSlim? GetWriteLock(Guid connectionId)
    {
        var clientId = _registry.GetClientIdByConnection(connectionId);
        if (clientId == null) return null;
        return _registry.GetClient(clientId)?.WriteLock;
    }

    private async Task ReceiveLoopAsync(
        WebSocket webSocket,
        ChannelWriter<SignalingMessage> writer,
        Guid connectionId,
        Action<string> setClientId,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[65536]; // 64KB for video frame relay
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

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // Binary relay: forward to peer's WebSocket (streaming, chunk by chunk)
                    var clientId = _registry.GetClientIdByConnection(connectionId);
                    if (clientId != null)
                    {
                        var peer = _registry.GetPeerWebSocket(clientId);
                        if (peer.HasValue)
                        {
                            await peer.Value.writeLock.WaitAsync(cancellationToken);
                            try
                            {
                                await peer.Value.ws.SendAsync(
                                    new ArraySegment<byte>(buffer, 0, result.Count),
                                    WebSocketMessageType.Binary,
                                    result.EndOfMessage,
                                    cancellationToken);
                            }
                            finally { peer.Value.writeLock.Release(); }
                        }
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Text)
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
            SignalingMessage.TransportEndpoint endpoint => HandleTransportEndpoint(endpoint, currentClientId),
            SignalingMessage.RelayReady relayReady => HandleRelayReady(relayReady, currentClientId),
            SignalingMessage.Ping => new SignalingMessage.Pong(),
            // Collaboration session messages
            SignalingMessage.CreateSession createSession => HandleCreateSession(createSession, currentClientId),
            SignalingMessage.JoinSession joinSession => HandleJoinSession(joinSession, currentClientId),
            SignalingMessage.LeaveSession => HandleLeaveSession(currentClientId),
            SignalingMessage.ScreenShareStateChanged shareState => HandleScreenShareStateChanged(shareState, currentClientId),
            SignalingMessage.MeshSdpOffer meshOffer => HandleMeshSdpOffer(meshOffer, currentClientId),
            SignalingMessage.MeshSdpAnswer meshAnswer => HandleMeshSdpAnswer(meshAnswer, currentClientId),
            SignalingMessage.MeshIceCandidate meshCandidate => HandleMeshIceCandidate(meshCandidate, currentClientId),
            // Server-only messages that shouldn't be received from clients
            SignalingMessage.RegisterSuccess or
            SignalingMessage.RegisterFailed or
            SignalingMessage.IncomingConnection or
            SignalingMessage.Connected or
            SignalingMessage.Disconnected or
            SignalingMessage.Error or
            SignalingMessage.Pong or
            SignalingMessage.SessionCreated or
            SignalingMessage.JoinedSession or
            SignalingMessage.JoinSessionFailed or
            SignalingMessage.ParticipantJoined or
            SignalingMessage.ParticipantLeft => new SignalingMessage.Error("Invalid message from client"),
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

    private SignalingMessage? HandleTransportEndpoint(SignalingMessage.TransportEndpoint endpoint, string? fromId)
    {
        if (fromId == null)
        {
            return new SignalingMessage.Error("Not registered");
        }

        _registry.TrySendToClient(endpoint.TargetId, new SignalingMessage.TransportEndpoint(fromId, endpoint.IPs, endpoint.Port));
        _logger.LogDebug("Transport endpoint forwarded from {FromId} to {TargetId}", fromId, endpoint.TargetId);
        return null;
    }

    private SignalingMessage? HandleRelayReady(SignalingMessage.RelayReady relayReady, string? fromId)
    {
        if (fromId == null)
        {
            return new SignalingMessage.Error("Not registered");
        }

        // Forward relay-ready to peer (swap target_id → fromId so peer knows who it's from)
        _registry.TrySendToClient(relayReady.TargetId, new SignalingMessage.RelayReady(fromId, relayReady.EncryptionNonce));
        _logger.LogInformation("Relay ready from {FromId} to {TargetId}", fromId, relayReady.TargetId);
        return null;
    }

    // ==================== Collaboration Session Handlers ====================

    private SignalingMessage HandleCreateSession(SignalingMessage.CreateSession createSession, string? fromId)
    {
        if (fromId == null)
        {
            return new SignalingMessage.Error("Not registered");
        }

        var (sessionCode, _) = _sessionRegistry.CreateSession(fromId, createSession.DisplayName, createSession.SessionName);
        _logger.LogInformation("Session {SessionCode} created by {ClientId}", sessionCode, fromId);
        return new SignalingMessage.SessionCreated(sessionCode, createSession.SessionName);
    }

    private SignalingMessage HandleJoinSession(SignalingMessage.JoinSession joinSession, string? fromId)
    {
        if (fromId == null)
        {
            return new SignalingMessage.Error("Not registered");
        }

        if (!_sessionRegistry.TryJoinSession(joinSession.SessionCode, fromId, joinSession.DisplayName, out var session, out var error))
        {
            return new SignalingMessage.JoinSessionFailed(error ?? "Unknown error");
        }

        // Get current participants (including self)
        var participants = session!.Participants.Values.ToList();

        // Notify existing participants that someone joined
        var newParticipant = session.Participants[fromId];
        foreach (var participantId in session.Participants.Keys.Where(id => id != fromId))
        {
            _registry.TrySendToClient(participantId, new SignalingMessage.ParticipantJoined(newParticipant));
        }

        _logger.LogInformation("Client {ClientId} joined session {SessionCode}", fromId, joinSession.SessionCode);
        return new SignalingMessage.JoinedSession(joinSession.SessionCode, participants);
    }

    private SignalingMessage? HandleLeaveSession(string? fromId)
    {
        if (fromId == null)
        {
            return new SignalingMessage.Error("Not registered");
        }

        var (session, _) = _sessionRegistry.LeaveSession(fromId);
        if (session == null)
        {
            return new SignalingMessage.Error("Not in a session");
        }

        // Notify remaining participants
        foreach (var participantId in session.Participants.Keys)
        {
            _registry.TrySendToClient(participantId, new SignalingMessage.ParticipantLeft(fromId));
        }

        _logger.LogInformation("Client {ClientId} left session {SessionCode}", fromId, session.SessionCode);
        return null;
    }

    private SignalingMessage? HandleScreenShareStateChanged(SignalingMessage.ScreenShareStateChanged shareState, string? fromId)
    {
        if (fromId == null)
        {
            return new SignalingMessage.Error("Not registered");
        }

        var session = _sessionRegistry.GetSessionByClient(fromId);
        if (session == null)
        {
            return new SignalingMessage.Error("Not in a session");
        }

        _sessionRegistry.SetParticipantSharing(fromId, shareState.IsSharing);

        // Broadcast to all other participants
        foreach (var participantId in session.Participants.Keys.Where(id => id != fromId))
        {
            _registry.TrySendToClient(participantId, new SignalingMessage.ScreenShareStateChanged(fromId, shareState.IsSharing));
        }

        _logger.LogInformation("Client {ClientId} screen share: {IsSharing}", fromId, shareState.IsSharing);
        return null;
    }

    private SignalingMessage? HandleMeshSdpOffer(SignalingMessage.MeshSdpOffer offer, string? fromId)
    {
        if (fromId == null)
        {
            return new SignalingMessage.Error("Not registered");
        }

        // Forward to target with sender ID
        _registry.TrySendToClient(offer.TargetId, new SignalingMessage.MeshSdpOffer(fromId, offer.Sdp));
        _logger.LogDebug("Mesh SDP offer from {FromId} to {TargetId}", fromId, offer.TargetId);
        return null;
    }

    private SignalingMessage? HandleMeshSdpAnswer(SignalingMessage.MeshSdpAnswer answer, string? fromId)
    {
        if (fromId == null)
        {
            return new SignalingMessage.Error("Not registered");
        }

        // Forward to target with sender ID
        _registry.TrySendToClient(answer.TargetId, new SignalingMessage.MeshSdpAnswer(fromId, answer.Sdp));
        _logger.LogDebug("Mesh SDP answer from {FromId} to {TargetId}", fromId, answer.TargetId);
        return null;
    }

    private SignalingMessage? HandleMeshIceCandidate(SignalingMessage.MeshIceCandidate candidate, string? fromId)
    {
        if (fromId == null)
        {
            return new SignalingMessage.Error("Not registered");
        }

        // Forward to target with sender ID
        _registry.TrySendToClient(candidate.TargetId, new SignalingMessage.MeshIceCandidate(
            fromId,
            candidate.Candidate,
            candidate.SdpMid,
            candidate.SdpMLineIndex));
        _logger.LogDebug("Mesh ICE candidate from {FromId} to {TargetId}", fromId, candidate.TargetId);
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

        // Clean up session membership
        var (session, _) = _sessionRegistry.LeaveSession(clientId);
        if (session != null)
        {
            // Notify remaining session participants
            foreach (var participantId in session.Participants.Keys)
            {
                _registry.TrySendToClient(participantId, new SignalingMessage.ParticipantLeft(clientId));
            }
            _logger.LogInformation("Client {ClientId} removed from session {SessionCode} on disconnect", clientId, session.SessionCode);
        }

        // Unregister the client
        _registry.UnregisterByConnection(connectionId);

        // Notify peer if there was one (1:1 mode)
        if (peerId != null)
        {
            _registry.SetPeer(peerId, null);
            _registry.TrySendToClient(peerId, new SignalingMessage.Disconnected(clientId, "Peer disconnected"));
        }

        _logger.LogInformation("Client {ClientId} disconnected and cleaned up", clientId);
        return Task.CompletedTask;
    }
}
