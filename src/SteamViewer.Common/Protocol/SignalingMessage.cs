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
// Collaboration session messages
[JsonDerivedType(typeof(CreateSession), "create_session")]
[JsonDerivedType(typeof(SessionCreated), "session_created")]
[JsonDerivedType(typeof(JoinSession), "join_session")]
[JsonDerivedType(typeof(JoinedSession), "joined_session")]
[JsonDerivedType(typeof(JoinSessionFailed), "join_session_failed")]
[JsonDerivedType(typeof(ParticipantJoined), "participant_joined")]
[JsonDerivedType(typeof(ParticipantLeft), "participant_left")]
[JsonDerivedType(typeof(LeaveSession), "leave_session")]
[JsonDerivedType(typeof(ScreenShareStateChanged), "screen_share_state_changed")]
// Mesh WebRTC signaling (within session)
[JsonDerivedType(typeof(MeshSdpOffer), "mesh_sdp_offer")]
[JsonDerivedType(typeof(MeshSdpAnswer), "mesh_sdp_answer")]
[JsonDerivedType(typeof(MeshIceCandidate), "mesh_ice_candidate")]
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

    // ==================== Collaboration Session Messages ====================

    /// <summary>Create a new collaboration session</summary>
    public sealed record CreateSession(
        [property: JsonPropertyName("display_name")] string DisplayName,
        [property: JsonPropertyName("session_name")] string? SessionName = null
    ) : SignalingMessage;

    /// <summary>Session created successfully</summary>
    public sealed record SessionCreated(
        [property: JsonPropertyName("session_code")] string SessionCode,
        [property: JsonPropertyName("session_name")] string? SessionName
    ) : SignalingMessage;

    /// <summary>Join an existing session</summary>
    public sealed record JoinSession(
        [property: JsonPropertyName("session_code")] string SessionCode,
        [property: JsonPropertyName("display_name")] string DisplayName
    ) : SignalingMessage;

    /// <summary>Successfully joined session</summary>
    public sealed record JoinedSession(
        [property: JsonPropertyName("session_code")] string SessionCode,
        [property: JsonPropertyName("participants")] List<ParticipantInfo> Participants
    ) : SignalingMessage;

    /// <summary>Failed to join session</summary>
    public sealed record JoinSessionFailed(
        [property: JsonPropertyName("reason")] string Reason
    ) : SignalingMessage;

    /// <summary>New participant joined the session</summary>
    public sealed record ParticipantJoined(
        [property: JsonPropertyName("participant")] ParticipantInfo Participant
    ) : SignalingMessage;

    /// <summary>Participant left the session</summary>
    public sealed record ParticipantLeft(
        [property: JsonPropertyName("participant_id")] string ParticipantId
    ) : SignalingMessage;

    /// <summary>Leave current session</summary>
    public sealed record LeaveSession : SignalingMessage;

    /// <summary>Screen share state changed for a participant</summary>
    public sealed record ScreenShareStateChanged(
        [property: JsonPropertyName("participant_id")] string ParticipantId,
        [property: JsonPropertyName("is_sharing")] bool IsSharing
    ) : SignalingMessage;

    // ==================== Mesh WebRTC Signaling ====================

    /// <summary>SDP offer to specific peer in session (includes sender ID for routing)</summary>
    public sealed record MeshSdpOffer(
        [property: JsonPropertyName("target_id")] string TargetId,
        [property: JsonPropertyName("sdp")] string Sdp
    ) : SignalingMessage;

    /// <summary>SDP answer to specific peer in session</summary>
    public sealed record MeshSdpAnswer(
        [property: JsonPropertyName("target_id")] string TargetId,
        [property: JsonPropertyName("sdp")] string Sdp
    ) : SignalingMessage;

    /// <summary>ICE candidate to specific peer in session</summary>
    public sealed record MeshIceCandidate(
        [property: JsonPropertyName("target_id")] string TargetId,
        [property: JsonPropertyName("candidate")] string Candidate,
        [property: JsonPropertyName("sdp_mid")] string? SdpMid,
        [property: JsonPropertyName("sdp_m_line_index")] ushort? SdpMLineIndex
    ) : SignalingMessage;
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
