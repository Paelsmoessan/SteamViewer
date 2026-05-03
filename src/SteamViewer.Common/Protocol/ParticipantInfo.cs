using System.Text.Json.Serialization;

namespace SteamViewer.Common.Protocol;

/// <summary>
/// Information about a participant in a collaboration session.
/// </summary>
public record ParticipantInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("is_sharing")] bool IsSharing = false,
    [property: JsonPropertyName("joined_at")] DateTimeOffset JoinedAt = default
);
