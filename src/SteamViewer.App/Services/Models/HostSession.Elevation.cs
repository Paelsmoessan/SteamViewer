using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SteamViewer.Client.Core.Elevation;
using SteamViewer.Client.Core.Network;

namespace SteamViewer.App.Services.Models;

public sealed partial class HostSession
{
    private bool _elevationDetached;

    /// <summary>
    /// Detach the elevation service so it survives this session's disposal.
    /// Unsubscribes event handlers but does NOT dispose the service.
    /// Returns the elevation service reference for reuse in a new session.
    /// </summary>
    public IElevationService? DetachElevationService()
    {
        if (_elevationService == null || _elevationDetached) return null;

        _elevationDetached = true;

        _elevationService.OnSecureDesktopFrame -= HandleSecureDesktopFrame;
        _elevationService.OnSecureDesktopStateChanged -= HandleSecureDesktopStateChanged;
        _elevationService.OnSystemStateChanged -= HandleSystemStateChanged;

        return _elevationService;
    }

    private void HandleSystemStateChanged(bool connected)
    {
        if (_transport == null || !IsDataChannelReady) return;

        try
        {
            _ = SendAsync(new
            {
                type = "hostStatus",
                elevated = _elevationService?.IsAdminConnected ?? false,
                systemLevel = connected
            }, "hostStatus");
            _logger.LogInformation("SYSTEM helper state changed: {Connected} - notified viewer", connected);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send SYSTEM state change to viewer");
        }
    }

    #region Elevation & System Controls

    private Task HandleRequestElevationAsync()
    {
        if (_transport == null || _elevationService == null) return Task.CompletedTask;

        if (_elevationService.IsAdminConnected)
        {
            _logger.LogInformation("Elevated helper already connected");
            return SendAsync(new { type = "elevationAlready" }, "elevationAlready");
        }

        _ = Task.Run(async () =>
        {
            _logger.LogInformation("Requesting admin elevation...");
            try
            {
                var success = await _elevationService.RequestAdminElevationAsync();

                if (success)
                {
                    _logger.LogInformation("Admin elevation succeeded — admin features enabled");
                    await SendRawAsync(new { type = "hostStatus", elevated = true });
                }
                else
                {
                    _logger.LogWarning("Admin elevation failed (UAC denied or error)");
                    await SendRawAsync(new { type = "elevationDenied", message = "UAC prompt was denied or helper failed to start" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to request admin elevation");
                try
                {
                    await SendRawAsync(new { type = "elevationDenied", message = ex.Message });
                }
                catch { }
            }
        });

        return Task.CompletedTask;
    }

    private async Task HandleCtrlAltDelAsync()
    {
        if (_transport == null) return;

        if (_elevationService != null && (_elevationService.IsAdminConnected || _elevationService.IsSystemConnected))
        {
            var success = await _elevationService.SendSASAsync();
            if (success)
            {
                _logger.LogInformation("Ctrl+Alt+Del sent via elevation service");
            }
            else
            {
                _logger.LogWarning("Ctrl+Alt+Del failed via elevation service");
                await SendAsync(new { type = "ctrlAltDelFailed", message = "SendSAS failed via elevated helper" }, "ctrlAltDelFailed");
            }
        }
        else
        {
            _logger.LogWarning("Ctrl+Alt+Del requested but no elevated helper connected");
            await SendAsync(new { type = "ctrlAltDelFailed", message = "Admin features not enabled — request elevation first" }, "ctrlAltDelFailed");
        }
    }

    private async Task HandleLockWorkstationAsync()
    {
        if (_elevationService != null)
        {
            var success = await _elevationService.LockWorkStationAsync();
            if (success)
            {
                _logger.LogInformation("Workstation locked via elevation service");
                return;
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = "user32.dll,LockWorkStation",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            _logger.LogInformation("Workstation locked via rundll32 fallback");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to lock workstation");
        }
    }

    private async Task HandleRebootAsync()
    {
        if (_elevationService?.IsAdminConnected == true)
        {
            var serverUrl = _configuration["SignalingServer"];
            var stunUrls = new[] { "stun:stun.l.google.com:19302", "stun:stun1.l.google.com:19302" };
            var rebootTurnConfig = _turnConfigService != null
                ? await _turnConfigService.GetConfigAsync(_hostClientId)
                : TurnConfig.Disabled;
            var turnUrls = rebootTurnConfig.Enabled ? rebootTurnConfig.Urls : null;
            var turnUser = rebootTurnConfig.Username;
            var turnCred = rebootTurnConfig.Credential;
            var success = await _elevationService.RebootAsync(_hostClientId, _hostPasswordHash, PeerId,
                serverUrl, stunUrls, turnUrls, turnUser, turnCred);
            if (success)
            {
                _logger.LogInformation("Reboot initiated via elevation service (with auto-restart)");
            }
            else
            {
                _logger.LogWarning("Reboot failed via elevation service");
                await SendAsync(new { type = "rebootFailed", message = "Reboot command failed" }, "rebootFailed");
            }
        }
        else
        {
            _logger.LogWarning("Reboot requested without elevated helper — rebooting without auto-restart");
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "shutdown",
                    Arguments = "/r /t 0",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initiate reboot");
                await SendAsync(new { type = "rebootFailed", message = ex.Message }, "rebootFailed");
            }
        }
    }

    private async Task HandleRunElevatedAsync(JsonElement root)
    {
        if (_transport == null) return;
        var path = JsonAccessors.GetString(root, "path");
        var args = JsonAccessors.GetString(root, "args");

        if (string.IsNullOrEmpty(path))
        {
            await SendAsync(new { type = "runElevatedFailed", message = "No path specified" }, "runElevatedFailed");
            return;
        }

        if (_elevationService?.IsAdminConnected == true)
        {
            var success = await _elevationService.RunElevatedAsync(path, args);
            var responseType = success ? "runElevatedSuccess" : "runElevatedFailed";
            await SendAsync(new { type = responseType, path, message = success ? (string?)null : $"Failed to launch: {path}" }, "runElevated response");
        }
        else
        {
            await SendAsync(new { type = "runElevatedFailed", message = "Admin features not enabled — request elevation first" }, "runElevatedFailed");
        }
    }

    private Task HandleRequestSystemElevationAsync()
    {
        if (_transport == null || _elevationService == null) return Task.CompletedTask;

        if (_elevationService.IsSystemConnected)
        {
            return SendAsync(new { type = "systemElevationAlready" }, "systemElevationAlready");
        }

        if (!_elevationService.IsAdminConnected)
        {
            return SendAsync(new { type = "systemElevationDenied", message = "Admin features must be enabled first" }, "systemElevationDenied");
        }

        _ = Task.Run(async () =>
        {
            _logger.LogInformation("Requesting SYSTEM elevation...");
            try
            {
                var success = await _elevationService.RequestSystemElevationAsync();
                if (success)
                {
                    await SendRawAsync(new { type = "hostStatus", elevated = true, systemLevel = true });
                }
                else
                {
                    await SendRawAsync(new { type = "systemElevationFailed", message = "Failed to create SYSTEM helper" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to request SYSTEM elevation");
                try
                {
                    await SendRawAsync(new { type = "systemElevationFailed", message = ex.Message });
                }
                catch { }
            }
        });

        return Task.CompletedTask;
    }

    private async Task HandleRunAsSystemAsync(JsonElement root)
    {
        if (_transport == null) return;
        var path = JsonAccessors.GetString(root, "path");
        var args = JsonAccessors.GetString(root, "args");

        if (string.IsNullOrEmpty(path))
        {
            await SendAsync(new { type = "runAsSystemFailed", message = "No path specified" }, "runAsSystemFailed");
            return;
        }

        if (_elevationService?.IsSystemConnected == true)
        {
            var success = await _elevationService.RunAsSystemAsync(path, args);
            var responseType = success ? "runAsSystemSuccess" : "runAsSystemFailed";
            await SendAsync(new { type = responseType, path, message = success ? (string?)null : $"Failed to launch: {path}" }, "runAsSystem response");
        }
        else
        {
            await SendAsync(new { type = "runAsSystemFailed", message = "SYSTEM features not enabled — request system elevation first" }, "runAsSystemFailed");
        }
    }

    #endregion
}
