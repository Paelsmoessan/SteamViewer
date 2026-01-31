using System.Collections.Concurrent;
using System.Threading.Channels;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Server.Services;

/// <summary>
/// Client connection information stored in the registry.
/// </summary>
public sealed class ClientInfo
{
    /// <summary>Client's unique ID (9-digit number)</summary>
    public required string ClientId { get; init; }

    /// <summary>Password hash (BLAKE3)</summary>
    public required string PasswordHash { get; init; }

    /// <summary>Channel to send messages to this client</summary>
    public required ChannelWriter<SignalingMessage> MessageWriter { get; init; }

    /// <summary>Connection ID (for this WebSocket session)</summary>
    public required Guid ConnectionId { get; init; }

    /// <summary>Optional peer ID if currently connected</summary>
    public string? PeerId { get; set; }

    /// <summary>Timestamp when client registered</summary>
    public required DateTimeOffset RegisteredAt { get; init; }
}

/// <summary>
/// Thread-safe client registry for managing connected clients and their sessions.
/// Uses ConcurrentDictionary for lock-free concurrent access.
/// </summary>
public sealed class ClientRegistry
{
    private readonly ConcurrentDictionary<string, ClientInfo> _clients = new();
    private readonly ConcurrentDictionary<Guid, string> _connections = new();
    private readonly object _peerLock = new();

    /// <summary>
    /// Register a new client.
    /// </summary>
    /// <returns>True if registration succeeded, false if client ID already exists.</returns>
    public bool TryRegister(
        string clientId,
        string passwordHash,
        ChannelWriter<SignalingMessage> messageWriter,
        Guid connectionId)
    {
        var info = new ClientInfo
        {
            ClientId = clientId,
            PasswordHash = passwordHash,
            MessageWriter = messageWriter,
            ConnectionId = connectionId,
            PeerId = null,
            RegisteredAt = DateTimeOffset.UtcNow
        };

        if (!_clients.TryAdd(clientId, info))
        {
            return false;
        }

        _connections[connectionId] = clientId;
        return true;
    }

    /// <summary>
    /// Unregister a client by connection ID.
    /// </summary>
    /// <returns>The client ID that was removed, or null if not found.</returns>
    public string? UnregisterByConnection(Guid connectionId)
    {
        if (_connections.TryRemove(connectionId, out var clientId))
        {
            _clients.TryRemove(clientId, out _);
            return clientId;
        }
        return null;
    }

    /// <summary>
    /// Get client info by client ID.
    /// </summary>
    public ClientInfo? GetClient(string clientId)
    {
        return _clients.TryGetValue(clientId, out var info) ? info : null;
    }

    /// <summary>
    /// Get client ID by connection ID.
    /// </summary>
    public string? GetClientIdByConnection(Guid connectionId)
    {
        return _connections.TryGetValue(connectionId, out var clientId) ? clientId : null;
    }

    /// <summary>
    /// Verify password for a client.
    /// </summary>
    public bool VerifyPassword(string clientId, string passwordHash)
    {
        if (_clients.TryGetValue(clientId, out var client))
        {
            return client.PasswordHash == passwordHash;
        }
        return false;
    }

    /// <summary>
    /// Set peer ID for a client (when they connect to another client).
    /// Thread-safe operation.
    /// </summary>
    public void SetPeer(string clientId, string? peerId)
    {
        lock (_peerLock)
        {
            if (_clients.TryGetValue(clientId, out var client))
            {
                client.PeerId = peerId;
            }
        }
    }

    /// <summary>
    /// Get the number of registered clients.
    /// </summary>
    public int ClientCount => _clients.Count;

    /// <summary>
    /// Send a message to a specific client.
    /// </summary>
    /// <returns>True if message was queued, false if client not found or channel full.</returns>
    public bool TrySendToClient(string clientId, SignalingMessage message)
    {
        if (_clients.TryGetValue(clientId, out var client))
        {
            return client.MessageWriter.TryWrite(message);
        }
        return false;
    }

    /// <summary>
    /// Check if a client is online.
    /// </summary>
    public bool IsOnline(string clientId)
    {
        return _clients.ContainsKey(clientId);
    }
}
