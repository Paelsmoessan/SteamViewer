using System.Text.Json;

namespace SteamViewer.App.Services.Models;

/// <summary>
/// Generic dispatcher for control-channel JSON messages with a `type` discriminator.
/// Each session class (HostSession, ViewerSession) supplies its own handler table
/// keyed by message type; the dispatcher parses JSON, looks up the handler, invokes it.
/// Replaces the HandleControlMessage twin (HostSession cc=32, ViewerSession cc=43)
/// per R3 fraternal-twin diagnosis (CodeScene clean-delivery research).
/// </summary>
/// <remarks>
/// Handler signature: Func&lt;string, JsonElement, Task&gt;. The string arg is the matched
/// type, useful when one handler instance is shared across multiple keys (e.g.
/// ViewerSession's ctrlAltDelFailed/rebootFailed/elevationDenied trio that all log
/// "Session X: {type}: {message}").
///
/// onNoHandler receives a NULLABLE type. type=null means the message had no `type`
/// field at all (legitimate input event on host). type non-null + no handler match
/// means a control-shaped message with an unrecognized type discriminator.
///
/// JsonException is intentionally NOT caught here - host treats it as input fallthrough
/// (HandleInputMessage), viewer swallows. Per-caller try/catch is the right level for
/// that behavior split.
/// </remarks>
internal static class ControlMessageDispatcher
{
    public static async Task DispatchAsync(
        string json,
        IReadOnlyDictionary<string, Func<string, JsonElement, Task>> handlers,
        Func<string?, JsonElement, Task>? onNoHandler = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? type = null;
        if (root.TryGetProperty("type", out var typeEl))
            type = typeEl.GetString();

        if (type != null && handlers.TryGetValue(type, out var handler))
        {
            await handler(type, root);
            return;
        }

        if (onNoHandler != null)
            await onNoHandler(type, root);
    }
}
