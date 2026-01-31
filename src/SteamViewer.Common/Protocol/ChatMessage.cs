using System.Text.Json.Serialization;

namespace SteamViewer.Common.Protocol;

/// <summary>
/// Chat message sent between peers.
/// </summary>
public sealed record ChatMessage(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("sender")] Role Sender,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp
);
