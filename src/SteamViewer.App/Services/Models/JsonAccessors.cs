using System.Text.Json;

namespace SteamViewer.App.Services.Models;

/// <summary>
/// Value-returning accessor helpers for JsonElement property lookups.
/// Replaces the 29-site dup pattern `root.TryGetProperty("foo", out var p) ? p.Get{Type}() : default`
/// across HostSession.cs and ViewerSession.cs control-message handlers.
/// </summary>
/// <remarks>
/// Value-returning (not Try-style) because every existing call site immediately wraps
/// TryGetProperty in a ternary to produce a value-with-default - the Try out-param shape
/// would force callers to use 2-line if/else instead of 1-line expressions.
///
/// Methods aligned with System.Text.Json conventions (Get{Type}). Bool has no defaultValue
/// param because all existing sites short-circuit to false via the `&&` pattern.
/// String returns nullable; callers that need empty-string default chain `?? ""` after.
/// </remarks>
internal static class JsonAccessors
{
    public static string? GetString(JsonElement root, string property)
        => root.TryGetProperty(property, out var p) ? p.GetString() : null;

    public static int GetInt(JsonElement root, string property, int defaultValue = 0)
        => root.TryGetProperty(property, out var p) ? p.GetInt32() : defaultValue;

    public static bool GetBool(JsonElement root, string property)
        => root.TryGetProperty(property, out var p) && p.GetBoolean();

    public static uint GetUInt(JsonElement root, string property, uint defaultValue = 0u)
        => root.TryGetProperty(property, out var p) ? (uint)p.GetInt32() : defaultValue;
}
