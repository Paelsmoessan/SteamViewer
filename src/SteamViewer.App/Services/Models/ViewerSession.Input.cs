using Microsoft.Extensions.Logging;
using SteamViewer.Common.Protocol;
using System.Text.Json;

namespace SteamViewer.App.Services.Models;

// Input concerns for ViewerSession: input-event send path + drop accounting.
public sealed partial class ViewerSession
{
    private int _inputDropCount;

    /// <summary>
    /// Send an input event to the remote peer.
    /// All input goes over the control channel (TCP is already ordered/reliable).
    /// </summary>
    public async Task SendInputAsync(InputEvent inputEvent)
    {
        if (_transport == null)
        {
            if (++_inputDropCount <= 5)
                _logger.LogWarning("Session {SessionId}: Input dropped — transport is null (drop #{Count})", SessionId, _inputDropCount);
            return;
        }
        if (!_transport.IsConnected)
        {
            if (++_inputDropCount <= 5)
                _logger.LogWarning("Session {SessionId}: Input dropped — transport not connected (drop #{Count})", SessionId, _inputDropCount);
            return;
        }

        // Track input activity for lossless settle
        _lastInputTime = DateTime.UtcNow;
        if (_losslessActive)
        {
            _losslessActive = false;
            _losslessRequestPending = false;
        }

        try
        {
            var json = JsonSerializer.Serialize(inputEvent);
            await _transport.SendControlAsync(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send input for session {SessionId}", SessionId);
        }
    }

    private bool? _lastSentInputLock;

    /// <summary>
    /// Notify the host that viewer input lock state changed. Deduped: one user toggle fires this from
    /// two paths (the button handler + the JS InputMessageRouter callback). Sending the same lock state
    /// twice churns the AES-GCM nonce enough to trip steady-state decryption failures and kill the
    /// transport, so only send on an actual state change.
    /// </summary>
    public Task SendInputLockStateAsync(bool locked)
    {
        if (_lastSentInputLock == locked)
        {
            _logger.LogDebug("Session {SessionId}: inputLockChanged={Locked} dedup - already sent, skipping redundant resend", SessionId, locked);
            return Task.CompletedTask;
        }
        _lastSentInputLock = locked;
        return SendAsync(new { type = "inputLockChanged", locked }, "inputLockChanged");
    }
}
