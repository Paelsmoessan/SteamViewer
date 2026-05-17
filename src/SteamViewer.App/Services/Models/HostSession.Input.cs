using System.Text.Json;
using Microsoft.Extensions.Logging;
using SteamViewer.Common.Protocol;

namespace SteamViewer.App.Services.Models;

public sealed partial class HostSession
{
    #region Keyboard Layout Sync

    private void HandleKeyboardLayoutMessage(JsonElement root)
    {
        var klid = JsonAccessors.GetString(root, "klid");
        if (string.IsNullOrEmpty(klid))
        {
            _logger.LogWarning("Received keyboardLayout message with empty KLID");
            return;
        }

        _inputInjector.ActivateKeyboardLayout(klid);
    }

    #endregion

    #region Input Injection

    private int _inputCount;

    private void HandleInputMessage(string json)
    {
        if (!IsSharingScreen) return;

        _inputCount++;

        try
        {
            TrackCaptureDimensions(json);

            if (_lastCaptureWidth <= 0 || _lastCaptureHeight <= 0) return;

            if (_elevationService != null && (_elevationService.IsAdminConnected || _elevationService.IsSystemConnected))
            {
                if (_inputCount <= 3 || _inputCount % 500 == 0)
                    _logger.LogInformation("Input #{Count}: routing via elevation (admin={Admin}, system={System})",
                        _inputCount, _elevationService.IsAdminConnected, _elevationService.IsSystemConnected);
                _ = InjectInputViaElevationAsync(json);
                return;
            }

            if (_inputCount <= 3 || _inputCount % 500 == 0)
                _logger.LogInformation("Input #{Count}: local injection", _inputCount);

            var inputEvent = JsonSerializer.Deserialize<InputEvent>(json);
            if (inputEvent != null)
            {
                _inputInjector.InjectInput(inputEvent, _lastCaptureWidth, _lastCaptureHeight);
            }
        }
        catch
        {
            // Silently ignore parse errors to reduce latency
        }
    }

    private async Task InjectInputViaElevationAsync(string json)
    {
        try
        {
            var success = await _elevationService!.InjectInputAsync(json, _lastCaptureWidth, _lastCaptureHeight);
            if (!success)
            {
                _logger.LogWarning("Elevation service returned false — falling back to local injection");
                FallbackToLocalInjection(json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Elevated input failed — falling back to local injection");
            FallbackToLocalInjection(json);
        }
    }

    private void FallbackToLocalInjection(string json)
    {
        try
        {
            var inputEvent = JsonSerializer.Deserialize<InputEvent>(json);
            if (inputEvent != null)
                _inputInjector.InjectInput(inputEvent, _lastCaptureWidth, _lastCaptureHeight);
        }
        catch { }
    }

    #endregion
}
