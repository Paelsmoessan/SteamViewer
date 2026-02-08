using System.Text.Json.Serialization;

namespace SteamViewer.Common.Protocol;

/// <summary>
/// Clipboard sync messages sent over WebRTC data channel.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Request), "clipboard_request")]
[JsonDerivedType(typeof(Response), "clipboard_data")]
[JsonDerivedType(typeof(Set), "clipboard_set")]
public abstract record ClipboardMessage
{
    /// <summary>
    /// Request the peer's clipboard contents.
    /// </summary>
    public sealed record Request() : ClipboardMessage;

    /// <summary>
    /// Response containing clipboard data.
    /// </summary>
    public sealed record Response(
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("data")] string Data
    ) : ClipboardMessage;

    /// <summary>
    /// Set the peer's clipboard contents.
    /// </summary>
    public sealed record Set(
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("data")] string Data
    ) : ClipboardMessage;
}
