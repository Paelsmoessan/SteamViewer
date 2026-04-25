using System.Text.Json.Serialization;

namespace SteamViewer.Common.Protocol;

/// <summary>
/// Input events sent from viewer to host.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MouseMove), "mouse_move")]
[JsonDerivedType(typeof(MouseDown), "mouse_down")]
[JsonDerivedType(typeof(MouseUp), "mouse_up")]
[JsonDerivedType(typeof(MouseWheel), "mouse_wheel")]
[JsonDerivedType(typeof(KeyDown), "key_down")]
[JsonDerivedType(typeof(KeyUp), "key_up")]
public abstract record InputEvent
{
    public sealed record MouseMove(
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y,
        [property: JsonPropertyName("captureWidth")] int CaptureWidth = 0,
        [property: JsonPropertyName("captureHeight")] int CaptureHeight = 0
    ) : InputEvent;

    public sealed record MouseDown(
        [property: JsonPropertyName("button")] MouseButton Button,
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y,
        [property: JsonPropertyName("captureWidth")] int CaptureWidth = 0,
        [property: JsonPropertyName("captureHeight")] int CaptureHeight = 0
    ) : InputEvent;

    public sealed record MouseUp(
        [property: JsonPropertyName("button")] MouseButton Button,
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y,
        [property: JsonPropertyName("captureWidth")] int CaptureWidth = 0,
        [property: JsonPropertyName("captureHeight")] int CaptureHeight = 0
    ) : InputEvent;

    public sealed record MouseWheel(
        [property: JsonPropertyName("delta_x")] double DeltaX,
        [property: JsonPropertyName("delta_y")] double DeltaY
    ) : InputEvent;

    public sealed record KeyDown(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("modifiers")] KeyModifiers Modifiers,
        [property: JsonPropertyName("code")] string? Code = null,
        [property: JsonPropertyName("altGr")] bool AltGr = false
    ) : InputEvent;

    public sealed record KeyUp(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("modifiers")] KeyModifiers Modifiers,
        [property: JsonPropertyName("code")] string? Code = null,
        [property: JsonPropertyName("altGr")] bool AltGr = false
    ) : InputEvent;
}

/// <summary>
/// Mouse button types.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MouseButton>))]
public enum MouseButton
{
    Left,
    Right,
    Middle,
    XButton1,  // Back (browser back, mouse button 4)
    XButton2   // Forward (browser forward, mouse button 5)
}

/// <summary>
/// Keyboard modifiers state.
/// </summary>
public sealed record KeyModifiers(
    [property: JsonPropertyName("ctrl")] bool Ctrl = false,
    [property: JsonPropertyName("shift")] bool Shift = false,
    [property: JsonPropertyName("alt")] bool Alt = false,
    [property: JsonPropertyName("meta")] bool Meta = false
)
{
    public static readonly KeyModifiers None = new();
}
