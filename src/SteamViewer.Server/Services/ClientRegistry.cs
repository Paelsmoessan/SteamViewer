using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Server.Services;

/// <summary>
/// Result of a client registration attempt.
/// </summary>
public enum RegisterResult
{
    /// <summary>New client registered successfully.</summary>
    Success,
    /// <summary>Existing registration replaced (session takeover). Old client info returned.</summary>
    Takeover,
    /// <summary>Client ID exists but password hash doesn't match.</summary>
    PasswordMismatch,
    /// <summary>Existing registration is still active (recent activity); takeover refused.</summary>
    AlreadyActive
}

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

    /// <summary>Last time we received any data from this client (ping, pong, signaling, binary relay).
    /// Used by the takeover-liveness check to refuse hijack attempts against active sessions (F1).</summary>
    public DateTimeOffset LastActivityUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Raw WebSocket reference for binary relay.</summary>
    public WebSocket? WebSocket { get; set; }

    /// <summary>Write lock for thread-safe WebSocket sends (text + binary).</summary>
    public SemaphoreSlim WriteLock { get; } = new(1, 1);
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
    /// Window during which an existing registration is considered "still active" and takeover is refused.
    /// Tighter than the WebSocket receive timeout: we want a healthy client to keep its registration even
    /// if a hijacker tries to register concurrently.
    /// </summary>
    private static readonly TimeSpan ActivityWindow = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Register a client. If the client ID already exists with matching password,
    /// performs session takeover only if the existing registration is no longer active
    /// (no activity in the last <see cref="ActivityWindow"/>).
    /// Refusing takeover against a live client closes F1 (signaling session hijack).
    /// </summary>
    public (RegisterResult Result, ClientInfo? OldClient) Register(
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

        // Fast path: no existing registration
        if (_clients.TryAdd(clientId, info))
        {
            _connections[connectionId] = clientId;
            return (RegisterResult.Success, null);
        }

        // Client ID exists - check password for takeover
        lock (_peerLock)
        {
            if (!_clients.TryGetValue(clientId, out var existing))
            {
                // Removed between TryAdd and lock - retry
                if (_clients.TryAdd(clientId, info))
                {
                    _connections[connectionId] = clientId;
                    return (RegisterResult.Success, null);
                }
                return (RegisterResult.PasswordMismatch, null);
            }

            if (!ConstantTimeStringEquals(existing.PasswordHash, passwordHash))
            {
                return (RegisterResult.PasswordMismatch, null);
            }

            // Even with a matching hash, refuse takeover if the existing client is demonstrably alive.
            // Their ping interval is 25s; we allow 15s of grace before considering them dead.
            var idleTime = DateTimeOffset.UtcNow - existing.LastActivityUtc;
            if (idleTime < ActivityWindow)
            {
                return (RegisterResult.AlreadyActive, existing);
            }

            // Password matches and existing client appears dead - takeover: replace registration
            _connections.TryRemove(existing.ConnectionId, out _);
            _clients[clientId] = info;
            _connections[connectionId] = clientId;
            return (RegisterResult.Takeover, existing);
        }
    }

    /// <summary>
    /// Mark a client's connection as having received activity (ping, message, binary relay frame).
    /// Used by the takeover-liveness gate.
    /// </summary>
    public void TouchActivity(Guid connectionId)
    {
        if (_connections.TryGetValue(connectionId, out var clientId)
            && _clients.TryGetValue(clientId, out var client))
        {
            client.LastActivityUtc = DateTimeOffset.UtcNow;
        }
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
    /// Verify password hash for a client. Constant-time comparison to prevent timing attacks
    /// that could otherwise recover the stored hash byte-by-byte (F11).
    /// </summary>
    public bool VerifyPassword(string clientId, string passwordHash)
    {
        if (_clients.TryGetValue(clientId, out var client))
        {
            return ConstantTimeStringEquals(client.PasswordHash, passwordHash);
        }
        return false;
    }

    private static bool ConstantTimeStringEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
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

    /// <summary>
    /// Get the peer's WebSocket and WriteLock for binary relay.
    /// Returns null if peer not found or no WebSocket set.
    /// </summary>
    public (WebSocket ws, SemaphoreSlim writeLock)? GetPeerWebSocket(string clientId)
    {
        if (!_clients.TryGetValue(clientId, out var client)) return null;
        var peerId = client.PeerId;
        if (peerId == null) return null;
        if (!_clients.TryGetValue(peerId, out var peer)) return null;
        if (peer.WebSocket == null || peer.WebSocket.State != WebSocketState.Open) return null;
        return (peer.WebSocket, peer.WriteLock);
    }
}
