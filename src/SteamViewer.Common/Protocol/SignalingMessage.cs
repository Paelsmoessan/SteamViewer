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
[JsonDerivedType(typeof(SdpOffer), "sdp_offer")]
[JsonDerivedType(typeof(SdpAnswer), "sdp_answer")]
[JsonDerivedType(typeof(IceCandidate), "ice_candidate")]
[JsonDerivedType(typeof(Connected), "connected")]
[JsonDerivedType(typeof(Disconnect), "disconnect")]
[JsonDerivedType(typeof(Disconnected), "disconnected")]
[JsonDerivedType(typeof(Error), "error")]
[JsonDerivedType(typeof(Ping), "ping")]
[JsonDerivedType(typeof(Pong), "pong")]
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

    /// <summary>Request connection to peer</summary>
    public sealed record ConnectRequest(
        [property: JsonPropertyName("target_id")] string TargetId,
        [property: JsonPropertyName("password")] string Password
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

    /// <summary>WebRTC SDP offer</summary>
    public sealed record SdpOffer(
        [property: JsonPropertyName("target_id")] string TargetId,
        [property: JsonPropertyName("sdp")] string Sdp
    ) : SignalingMessage;

    /// <summary>WebRTC SDP answer</summary>
    public sealed record SdpAnswer(
        [property: JsonPropertyName("target_id")] string TargetId,
        [property: JsonPropertyName("sdp")] string Sdp
    ) : SignalingMessage;

    /// <summary>ICE candidate</summary>
    public sealed record IceCandidate(
        [property: JsonPropertyName("target_id")] string TargetId,
        [property: JsonPropertyName("candidate")] string Candidate,
        [property: JsonPropertyName("sdp_mid")] string? SdpMid,
        [property: JsonPropertyName("sdp_m_line_index")] ushort? SdpMLineIndex
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

    /// <summary>Error occurred</summary>
    public sealed record Error(
        [property: JsonPropertyName("message")] string Message
    ) : SignalingMessage;

    /// <summary>Heartbeat/keepalive ping</summary>
    public sealed record Ping : SignalingMessage;

    /// <summary>Heartbeat/keepalive pong</summary>
    public sealed record Pong : SignalingMessage;
}

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
}
