using System.Text.Json;
using Microsoft.Extensions.Logging;
using SteamViewer.Client.Core.Network;

namespace SteamViewer.App.Services.Models;

/// <summary>
/// Canonical send-side helper for serialize-and-send-with-warn-on-failure over the control channel.
/// Both <see cref="HostSession"/> and <see cref="ViewerSession"/> have ~30+ inline duplications of this
/// pattern (serialize payload, await transport.SendControlAsync, catch+warn). This consolidates them
/// into one implementation; each session class binds its dependencies once via a thin private wrapper.
/// </summary>
/// <remarks>
/// Polymorphic JSON note: when T is a base type with [JsonDerivedType] discriminators (e.g.
/// ClipboardMessage), callers MUST bind T explicitly to the base type at the call site, otherwise
/// JsonSerializer.Serialize will use the runtime subtype and skip the discriminator. Example:
/// SendAsync&lt;ClipboardMessage&gt;(new ClipboardMessage.Request(), "..."), NOT
/// SendAsync(new ClipboardMessage.Request(), "...") which would bind T to Request and lose the tag.
/// </remarks>
internal static class ControlMessageSender
{
    public static async Task SendAsync<T>(
        StreamTransport? transport,
        ILogger logger,
        string sessionId,
        T payload,
        string logLabel)
    {
        if (transport == null || !transport.IsConnected) return;
        try
        {
            var json = JsonSerializer.Serialize(payload);
            await transport.SendControlAsync(json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Session {SessionId}: Failed to send {Label}", sessionId, logLabel);
        }
    }

    /// <summary>
    /// Raw variant: serialize and send; lets exceptions propagate. Use when the call
    /// site has its own try/catch that must react to send failures - e.g. to send an
    /// error-response message back to the peer when the original send threw.
    /// </summary>
    public static async Task SendRawAsync<T>(StreamTransport? transport, T payload)
    {
        if (transport == null || !transport.IsConnected) return;
        var json = JsonSerializer.Serialize(payload);
        await transport.SendControlAsync(json);
    }
}
