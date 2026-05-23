using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SteamViewer.App.Services;
using SteamViewer.Common.Protocol;
using System.Text.Json;

namespace SteamViewer.App.Services.Models;

// Control-message dispatch for ViewerSession: BuildControlHandlers table
// (host -> viewer messages) + monitor-layout parser. Cross-partial lambda
// captures reach fields/events/properties owned by the other partials --
// safe because partial classes share scope.
public sealed partial class ViewerSession
{
    /// <summary>
    /// Raised when the host sends its monitor layout.
    /// </summary>
    public event Action<List<MonitorInfo>, int>? OnMonitorLayoutReceived;

    // Dictionary-based dispatch replaces the previous switch-on-type. Handler table built
    // lazily on first use; lambdas capture this-scoped state. 14 distinct handler bodies
    // mapped to 20 keys (two case-groups share handler instances: failure-log trio +
    // system-failure 5-tuple use shared handlers that read the matched `type` for logging).
    private Dictionary<string, Func<string, JsonElement, Task>>? _controlHandlers;
    private Dictionary<string, Func<string, JsonElement, Task>> ControlHandlers
        => _controlHandlers ??= BuildControlHandlers();

    private Dictionary<string, Func<string, JsonElement, Task>> BuildControlHandlers()
    {
        // Shared handler: ctrlAltDelFailed / rebootFailed / elevationDenied all log
        // 'Session X: {type}: {message}' and invoke OnControlMessage with the message.
        Func<string, JsonElement, Task> failureLogHandler = (type, root) =>
        {
            var message = JsonAccessors.GetString(root, "message");
            _logger.LogWarning("Session {SessionId}: {Type}: {Message}", SessionId, type, message);
            OnControlMessage?.Invoke(type, message);
            return Task.CompletedTask;
        };

        // Shared handler: systemElevationAlready/Denied/Failed + runAsSystemSuccess/Failed
        // all read 'message' and invoke OnControlMessage.
        Func<string, JsonElement, Task> systemFailureHandler = (type, root) =>
        {
            var sysMessage = JsonAccessors.GetString(root, "message");
            OnControlMessage?.Invoke(type, sysMessage);
            return Task.CompletedTask;
        };

        return new Dictionary<string, Func<string, JsonElement, Task>>
        {
            ["screenShareStarted"] = (_, _) =>
            {
                _logger.LogInformation("Session {SessionId}: Peer started sharing", SessionId);
                IsPeerSharing = true;
                OnPeerSharingChanged?.Invoke(true);
                return Task.CompletedTask;
            },
            ["screenShareStopped"] = (_, _) =>
            {
                _logger.LogInformation("Session {SessionId}: Peer stopped sharing", SessionId);
                IsPeerSharing = false;
                OnPeerSharingChanged?.Invoke(false);
                return Task.CompletedTask;
            },
            ["hostStatus"] = (type, root) =>
            {
                var elevated = JsonAccessors.GetBool(root, "elevated");
                var systemLevel = JsonAccessors.GetBool(root, "systemLevel");
                IsHostElevated = elevated;
                IsHostSystemLevel = systemLevel;
                _logger.LogInformation("Session {SessionId}: Host elevated={Elevated}, systemLevel={SystemLevel}", SessionId, elevated, systemLevel);
                OnControlMessage?.Invoke(type, null);
                return Task.CompletedTask;
            },
            ["monitorLayout"] = (_, root) =>
            {
                HandleMonitorLayout(root);
                return Task.CompletedTask;
            },
            ["ctrlAltDelFailed"] = failureLogHandler,
            ["rebootFailed"] = failureLogHandler,
            ["elevationDenied"] = failureLogHandler,
            ["elevationAlready"] = (type, _) =>
            {
                OnControlMessage?.Invoke(type, null);
                return Task.CompletedTask;
            },
            ["systemElevationAlready"] = systemFailureHandler,
            ["systemElevationDenied"] = systemFailureHandler,
            ["systemElevationFailed"] = systemFailureHandler,
            ["runAsSystemSuccess"] = systemFailureHandler,
            ["runAsSystemFailed"] = systemFailureHandler,
            ["cursorVisibilityChanged"] = (type, root) =>
            {
                var visible = JsonAccessors.GetBool(root, "visible");
                OnControlMessage?.Invoke(type, visible.ToString());
                return Task.CompletedTask;
            },
            ["cursorShape"] = (type, root) =>
            {
                var cursor = JsonAccessors.GetString(root, "cursor");
                if (cursor != null) OnControlMessage?.Invoke(type, cursor);
                return Task.CompletedTask;
            },
            ["clipboard_data"] = (_, root) =>
            {
                var cbFormat = JsonAccessors.GetString(root, "format");
                var cbData = JsonAccessors.GetString(root, "data");
                if (cbFormat != null && cbData != null)
                    OnClipboardReceived?.Invoke(cbFormat, cbData);
                return Task.CompletedTask;
            },
            ["captureInfo"] = (_, root) =>
            {
                var capW = JsonAccessors.GetInt(root, "width");
                var capH = JsonAccessors.GetInt(root, "height");
                if (capW > 0 && capH > 0)
                {
                    CaptureWidth = capW;
                    CaptureHeight = capH;
                    _logger.LogInformation("Session {SessionId}: Host capture {W}x{H}", SessionId, capW, capH);
                    OnCaptureInfoReceived?.Invoke(capW, capH);
                }
                return Task.CompletedTask;
            },
            // Lambda param named `type` (not `_`) because the body uses `_ = ...` discard
            // for the InvokeVoidAsync ValueTask; a single-underscore lambda param would
            // shadow the body's `_` discard and cause CS0029.
            ["encodeInfo"] = (type, root) =>
            {
                var encW = JsonAccessors.GetInt(root, "width");
                var encH = JsonAccessors.GetInt(root, "height");
                if (encW > 0 && encH > 0)
                {
                    _logger.LogInformation("Session {SessionId}: Host encode resolution {W}x{H}", SessionId, encW, encH);
                    try { _ = _jsRuntime.InvokeVoidAsync("SteamViewerVideo.setEncodeResolution", SessionId, encW, encH); }
                    catch { /* JS not ready yet - next frame will use fallback path */ }
                }
                return Task.CompletedTask;
            },
            ["secureDesktopActive"] = (_, _) =>
            {
                IsSecureDesktopActive = true;
                OnSecureDesktopStateChanged?.Invoke(true);
                _ = _transport?.SendControlAsync(
                    JsonSerializer.Serialize(new { type = "ack", ackType = "secureDesktopActive" }));
                return Task.CompletedTask;
            },
            ["secureDesktopInactive"] = (_, _) =>
            {
                IsSecureDesktopActive = false;
                OnSecureDesktopStateChanged?.Invoke(false);
                _ = _transport?.SendControlAsync(
                    JsonSerializer.Serialize(new { type = "ack", ackType = "secureDesktopInactive" }));
                return Task.CompletedTask;
            },
            // Graceful host-initiated close. The host sends this control message immediately
            // before its signaling Disconnect + local teardown so the viewer has in-band proof
            // of intent (vs assuming any disconnect is a transport problem and showing the
            // reconnect overlay). Fires OnPeerDisconnecting so RemoteViewer.razor can suppress
            // the overlay and let OnSessionRemoved drive a clean window-close. Symmetric to
            // the viewer's client_disconnecting (commit 73bc168). See
            // plans/fix-host-disconnect-handshake.md.
            ["host_disconnecting"] = (_, _) =>
            {
                _logger.LogInformation("Session {SessionId}: Received host_disconnecting control - host gracefully closing", SessionId);
                OnPeerDisconnecting?.Invoke("Host gracefully disconnecting");
                return Task.CompletedTask;
            },
            // secureDesktopFrame: removed - SD frames now arrive via H.264 on channel 1
        };
    }

    private async Task HandleControlMessage(string json)
    {
        try
        {
            await ControlMessageDispatcher.DispatchAsync(json, ControlHandlers,
                onNoHandler: (type, _) =>
                {
                    if (type != null)
                        _logger.LogWarning("Session {SessionId}: Unknown control message type \"{Type}\" - dropped (no handler)", SessionId, type);
                    return Task.CompletedTask;
                });
        }
        catch (JsonException) { /* swallow - viewer has no input fallthrough */ }
    }

    private void HandleMonitorLayout(JsonElement root)
    {
        try
        {
            var monitors = new List<MonitorInfo>();
            if (root.TryGetProperty("monitors", out var monArr) && monArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in monArr.EnumerateArray())
                {
                    var id = JsonAccessors.GetUInt(m, "id");
                    var name = JsonAccessors.GetString(m, "name") ?? "";
                    var width = JsonAccessors.GetUInt(m, "width");
                    var height = JsonAccessors.GetUInt(m, "height");
                    var x = JsonAccessors.GetInt(m, "x");
                    var y = JsonAccessors.GetInt(m, "y");
                    var isPrimary = JsonAccessors.GetBool(m, "isPrimary");
                    monitors.Add(new MonitorInfo(id, name, width, height, x, y, isPrimary));
                }
            }

            var activeId = JsonAccessors.GetInt(root, "activeMonitorId");

            HostMonitors = monitors;
            ActiveMonitorId = activeId;

            OnMonitorLayoutReceived?.Invoke(monitors, activeId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session {SessionId}: Failed to parse monitor layout", SessionId);
        }
    }
}
