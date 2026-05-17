using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SteamViewer.Platform.Windows.Input;

namespace SteamViewer.App.Services.Models;

public sealed partial class HostSession
{
    private readonly ConcurrentDictionary<string, bool> _pendingAcks = new();

    /// <summary>Send string data to the peer via control channel.</summary>
    public async Task<bool> SendDataAsync(string data)
    {
        if (_transport == null || !_transport.IsConnected) return false;
        return await _transport.SendControlAsync(data);
    }

    // Per-session wrappers binding _transport / _logger / SessionId to the shared helper.
    // SendAsync<T>     - fire-and-forget; helper catches+warn-logs on failure.
    // SendRawAsync<T>  - lets exceptions propagate; use when caller has its own try/catch
    //                    that needs to see send failures (e.g., to send an error response).
    private Task SendAsync<T>(T payload, string label)
        => ControlMessageSender.SendAsync(_transport, _logger, SessionId, payload, label);

    private Task SendRawAsync<T>(T payload)
        => ControlMessageSender.SendRawAsync(_transport, payload);

    // Dictionary-based dispatch replaces the previous switch-on-type. Handler table
    // built lazily on first use; lambdas capture this-scoped state (logger, _videoPipeline,
    // IsPeerSharingScreen, OnPeerSharingChanged, _pendingAcks, etc.). One entry per
    // control message type; 21 entries matching the previous switch cases.
    private Dictionary<string, Func<string, JsonElement, Task>>? _controlHandlers;
    private Dictionary<string, Func<string, JsonElement, Task>> ControlHandlers
        => _controlHandlers ??= BuildControlHandlers();

    private Dictionary<string, Func<string, JsonElement, Task>> BuildControlHandlers() => new()
    {
        ["viewerReady"] = async (_, _) =>
        {
            _logger.LogInformation("Viewer relay connected â€” sending initial state");
            await HandleTransportConnected();
        },
        ["rebootHost"] = async (_, _) =>
        {
            _logger.LogInformation("Received reboot request from viewer");
            await HandleRebootAsync();
        },
        ["ctrlAltDel"] = async (_, _) =>
        {
            _logger.LogInformation("Received Ctrl+Alt+Del request from viewer");
            await HandleCtrlAltDelAsync();
        },
        ["lockWorkstation"] = async (_, _) =>
        {
            _logger.LogInformation("Received lock workstation request from viewer");
            await HandleLockWorkstationAsync();
        },
        ["requestElevation"] = (_, _) => HandleRequestElevationAsync(),
        ["runElevated"] = (_, root) => HandleRunElevatedAsync(root),
        ["requestSystemElevation"] = (_, _) => HandleRequestSystemElevationAsync(),
        ["runAsSystem"] = (_, root) => HandleRunAsSystemAsync(root),
        ["clipboard_request"] = (_, _) => HandleClipboardRequestAsync(),
        ["clipboard_set"] = (_, root) => HandleClipboardSetAsync(root),
        ["clipboard_paste"] = (_, root) => HandleClipboardPasteAsync(root),
        ["switchDisplay"] = async (_, root) =>
        {
            var monitorId = JsonAccessors.GetInt(root, "monitorId", -1);
            if (monitorId >= 0) await HandleSwitchDisplayAsync(monitorId);
        },
        ["toggleCursor"] = (_, _) => _videoPipeline.HandleToggleCursorAsync(),
        ["inputLockChanged"] = (_, root) =>
        {
            var locked = JsonAccessors.GetBool(root, "locked");
            _videoPipeline.SetCursorVisible(!locked);
            _logger.LogInformation("Viewer input lock: {Locked} -> host cursor in video: {Visible}", locked, !locked);
            return Task.CompletedTask;
        },
        ["screenShareStarted"] = (_, _) =>
        {
            _logger.LogInformation("Peer started sharing their screen");
            IsPeerSharingScreen = true;
            OnPeerSharingChanged?.Invoke(true);
            return Task.CompletedTask;
        },
        ["screenShareStopped"] = (_, _) =>
        {
            _logger.LogInformation("Peer stopped sharing their screen");
            IsPeerSharingScreen = false;
            OnPeerSharingChanged?.Invoke(false);
            return Task.CompletedTask;
        },
        ["setResolution"] = (_, root) =>
        {
            _videoPipeline.HandleSetResolution(root);
            return Task.CompletedTask;
        },
        ["requestLosslessFrame"] = (_, root) =>
        {
            _videoPipeline.HandleRequestLosslessFrame(root);
            return Task.CompletedTask;
        },
        ["qualityReport"] = (_, root) =>
        {
            _videoPipeline.HandleQualityReport(root);
            return Task.CompletedTask;
        },
        ["ack"] = (_, root) =>
        {
            var ackType = JsonAccessors.GetString(root, "ackType");
            if (ackType != null) _pendingAcks[ackType] = true;
            return Task.CompletedTask;
        },
        ["keyboardLayout"] = (_, root) =>
        {
            HandleKeyboardLayoutMessage(root);
            return Task.CompletedTask;
        },
    };

    private async Task HandleControlMessage(string json)
    {
        try
        {
            await ControlMessageDispatcher.DispatchAsync(json, ControlHandlers,
                onNoHandler: (type, _) =>
                {
                    // Type present but no handler matched. If it's a known input event type
                    // (mouse_*, key_*), fall through silently to HandleInputMessage. Otherwise
                    // warn-log; HandleInputMessage will silently drop it. Either way we still
                    // route to HandleInputMessage to preserve the historical input-fallthrough
                    // contract for type-less or non-control JSON.
                    if (type != null && !Win32Input.IsKnownInputType(type))
                        _logger.LogWarning("HostSession: Unknown control message type \"{Type}\" - dropped (no handler)", type);
                    HandleInputMessage(json);
                    return Task.CompletedTask;
                });
        }
        catch (JsonException)
        {
            // Non-JSON or malformed input - treat as raw input message (historical behavior).
            HandleInputMessage(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle control message");
        }
    }

    /// <summary>
    /// Send a critical control message with ACK+retry. Resends until viewer ACKs or max retries reached.
    /// Use for infrequent state-change messages that can't tolerate loss over UDP.
    /// </summary>
    private async Task SendWithAckAsync(string json, string ackType, int retryIntervalMs = 500, int maxRetries = 5)
    {
        _pendingAcks[ackType] = false;

        for (int i = 0; i <= maxRetries; i++)
        {
            if (_transport == null || !IsDataChannelReady) break;

            await _transport.SendControlAsync(json);
            if (i > 0) _logger.LogDebug("Resending {Type} (retry {N})", ackType, i);

            await Task.Delay(retryIntervalMs);
            if (_pendingAcks.TryGetValue(ackType, out var acked) && acked)
            {
                _pendingAcks.TryRemove(ackType, out _);
                _logger.LogDebug("ACK received for {Type}", ackType);
                return;
            }
        }

        _logger.LogWarning("No ACK for {Type} after {N} retries", ackType, maxRetries);
        _pendingAcks.TryRemove(ackType, out _);
    }
}
