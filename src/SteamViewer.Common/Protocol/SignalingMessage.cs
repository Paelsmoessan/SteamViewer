using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamViewer.Common.Protocol;

/// <summary>
/// Messages sent between client and signaling server.
/// Uses discriminated union pattern with JSON polymorphism.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Register), "register")]
[JsonDerivedType(typeof(RegisterSuccess), "register_success")]
[JsonDerivedType(typeof(RegisterFailed), "register_failed")]
[JsonDerivedType(typeof(ConnectRequest), "connect_request")]
[JsonDerivedType(typeof(IncomingConnection), "incoming_connection")]
[JsonDerivedType(typeof(ConnectionResponse), "connection_response")]
[JsonDerivedType(typeof(Connected), "connected")]
[JsonDerivedType(typeof(Disconnect), "disconnect")]
[JsonDerivedType(typeof(Disconnected), "disconnected")]
[JsonDerivedType(typeof(HostRecovered), "host_recovered")]
[JsonDerivedType(typeof(Error), "error")]
[JsonDerivedType(typeof(Ping), "ping")]
[JsonDerivedType(typeof(Pong), "pong")]
// Direct transport endpoint exchange for the custom UDP transport.
[JsonDerivedType(typeof(TransportEndpoint), "transport_endpoint")]
[JsonDerivedType(typeof(RelayReady), "relay_ready")]
[JsonDerivedType(typeof(TransportConfirmed), "transport_confirmed")]
public abstract record SignalingMessage
{
    /// <summary>Client registers with signaling server</summary>
    public sealed record Register(
        [property: JsonPropertyName("client_id")] string ClientId,
        [property: JsonPropertyName("password_hash")] string PasswordHash
    ) : SignalingMessage;

    /// <summary>Server confirms registration</summary>
    public sealed record RegisterSuccess(
        [property: JsonPropertyName("client_id")] string ClientId
    ) : SignalingMessage;

    /// <summary>Registration failed</summary>
    public sealed record RegisterFailed(
        [property: JsonPropertyName("reason")] string Reason
    ) : SignalingMessage;

    /// <summary>Request connection to peer. PasswordHash is the salted BLAKE3 hash
    /// (see SteamViewer.Client.Core.Session.PasswordHash); the server never sees plaintext.</summary>
    public sealed record ConnectRequest(
        [property: JsonPropertyName("target_id")] string TargetId,
        [property: JsonPropertyName("password_hash")] string PasswordHash
    ) : SignalingMessage;

    /// <summary>Server forwards connection request to target</summary>
    public sealed record IncomingConnection(
        [property: JsonPropertyName("from_id")] string FromId
    ) : SignalingMessage;

    /// <summary>Host approves/rejects connection</summary>
    public sealed record ConnectionResponse(
        [property: JsonPropertyName("target_id")] string TargetId,
        [property: JsonPropertyName("approved")] bool Approved
    ) : SignalingMessage;

    /// <summary>Connection established</summary>
    public sealed record Connected(
        [property: JsonPropertyName("peer_id")] string PeerId
    ) : SignalingMessage;

    /// <summary>Disconnect from peer</summary>
    public sealed record Disconnect(
        [property: JsonPropertyName("peer_id")] string PeerId
    ) : SignalingMessage;

    /// <summary>Client disconnected</summary>
    public sealed record Disconnected(
        [property: JsonPropertyName("peer_id")] string PeerId,
        [property: JsonPropertyName("reason")] string? Reason
    ) : SignalingMessage;

    /// <summary>
    /// Host sends to its previously-paired viewer after SIG-RECONNECT succeeds, to suppress
    /// the viewer's RemoveSessionAsync that Railway's stale-WS prune would otherwise trigger.
    /// Server routes by TargetId. On wire FromId is filled by the server (server-known peer id).
    /// </summary>
    public sealed record HostRecovered(
        [property: JsonPropertyName("target_id")] string TargetId,
        [property: JsonPropertyName("from_id")] string? FromId = null
    ) : SignalingMessage;

    /// <summary>Error occurred</summary>
    public sealed record Error(
        [property: JsonPropertyName("message")] string Message
    ) : SignalingMessage;

    /// <summary>Heartbeat/keepalive ping</summary>
    public sealed record Ping : SignalingMessage;

    /// <summary>Heartbeat/keepalive pong</summary>
    public sealed record Pong : SignalingMessage;

    // ==================== Direct Transport Endpoint Exchange ====================

    /// <summary>
    /// Sends transport candidates to peer for UDP hole-punching.
    /// Each candidate carries its own (ip, port, type) — local IPs use the local port,
    /// reflexive uses the STUN-mapped port, relay uses the TURN-allocated port.
    /// </summary>
    public sealed record TransportEndpoint(
        [property: JsonPropertyName("target_id")] string TargetId,
        [property: JsonPropertyName("candidates")] TransportCandidate[] Candidates
    ) : SignalingMessage;

    /// <summary>
    /// Signals readiness for binary WebSocket relay transport.
    /// Host sends after approving connection, includes encryption nonce.
    /// Server forwards to peer. Both sides derive AES-GCM key from password + nonce.
    /// </summary>
    public sealed record RelayReady(
        [property: JsonPropertyName("target_id")] string TargetId,
        [property: JsonPropertyName("encryption_nonce")] string EncryptionNonce
    ) : SignalingMessage;

    /// <summary>
    /// Confirms that a side's UDP probe succeeded and it is ready to switch.
    /// Both sides must send this before either disposes the relay backend.
    /// </summary>
    public sealed record TransportConfirmed(
        [property: JsonPropertyName("target_id")] string TargetId
    ) : SignalingMessage;
}

/// <summary>
/// A single transport candidate with its own IP, port, and type.
/// Types: "host" (local LAN), "srflx" (STUN reflexive), "relay" (TURN allocated).
/// </summary>
public sealed record TransportCandidate(
    [property: JsonPropertyName("ip")] string IP,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("type")] string Type
);

/// <summary>
/// JSON serialization options for signaling messages.
/// </summary>
public static class SignalingSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(SignalingMessage message)
        => JsonSerializer.Serialize(message, Options);

    public static SignalingMessage? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SignalingMessage>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns a log-safe view of the message with secret fields masked.
    /// Use at every log site that emits a SignalingMessage body. NEVER use for wire serialization.
    /// Non-secret message types are returned unchanged (no allocation).
    /// </summary>
    public static SignalingMessage SanitizeForLog(SignalingMessage message) => message switch
    {
        SignalingMessage.Register r => r with { PasswordHash = "***" },
        SignalingMessage.ConnectRequest c => c with { PasswordHash = "***" },
        SignalingMessage.RelayReady rr => rr with { EncryptionNonce = "***" },
        _ => message
    };
}
