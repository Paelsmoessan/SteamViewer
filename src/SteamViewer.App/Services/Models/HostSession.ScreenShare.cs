using System.Text.Json;
using Microsoft.Extensions.Logging;
using SteamViewer.Client.Core.Capture;
using SteamViewer.Common.Protocol;
using SteamViewer.Platform.Windows.ScreenCapture;

namespace SteamViewer.App.Services.Models;

public sealed partial class HostSession
{
    private DxgiCaptureAdapter? _dxgiAdapter;

    // Track capture dimensions from viewer's mouse events (0 = not yet received)
    private int _lastCaptureWidth;
    private int _lastCaptureHeight;

    private int? _requestedMonitorId;

    #region Screen Sharing

    /// <summary>
    /// Start sharing screen to the connected viewer via DXGI capture + FFmpeg encoding.
    /// </summary>
    /// <param name="outputIndex">DXGI output index to capture (null = auto-select primary)</param>
    public async Task<bool> StartScreenShareAsync(uint? outputIndex = null)
    {
        if (_transport == null || !_transport.IsConnected) return false;

        if (_screenCapture is DxgiScreenCapture dxgi)
        {
            var targetOutput = outputIndex ?? 0;
            _logger.LogInformation("Starting DXGI capture on output {Output} via pipeline...", targetOutput);

            _dxgiAdapter = new DxgiCaptureAdapter(dxgi);
            var success = _videoPipeline.StartCapture(_dxgiAdapter, targetOutput);

            if (success)
            {
                IsSharingScreen = true;
                await NotifyScreenShareStarted();
                return true;
            }

            _dxgiAdapter = null;
            return false;
        }

        _logger.LogWarning("No DXGI screen capture available");
        return false;
    }

    /// <summary>Stop sharing screen.</summary>
    public async Task StopScreenShareAsync()
    {
        if (_transport == null) return;

        try
        {
            _logger.LogInformation("Stopping screen share...");
            _videoPipeline.StopCapture();
            _dxgiAdapter = null;

            IsSharingScreen = false;
            _inputInjector.ClearCapturedMonitor();
            await _transport.SendControlAsync(
                JsonSerializer.Serialize(new { type = "screenShareStopped" }));
            _logger.LogInformation("Screen sharing stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop screen sharing");
        }
    }

    private async Task NotifyScreenShareStarted()
    {
        await Task.Delay(500);
        for (int i = 0; i < 3; i++)
        {
            var sent = await _transport!.SendControlAsync(
                JsonSerializer.Serialize(new { type = "screenShareStarted" }));
            _logger.LogInformation("screenShareStarted message sent: {Sent}", sent);
            if (sent) break;
            await Task.Delay(200);
        }
    }

    #endregion

    #region Monitor Layout

    private async Task SendMonitorLayoutAsync(int? activeMonitorId = null)
    {
        if (_transport == null || !IsDataChannelReady || _monitorEnumerator == null) return;

        try
        {
            var monitors = _monitorEnumerator.GetMonitors();
            if (monitors.Count == 0) return;

            var layout = new
            {
                type = "monitorLayout",
                monitors = monitors.Select(m => new
                {
                    id = (int)m.Id,
                    name = m.Name,
                    width = (int)m.Width,
                    height = (int)m.Height,
                    x = m.X,
                    y = m.Y,
                    isPrimary = m.IsPrimary
                }),
                activeMonitorId = activeMonitorId
                    ?? (int)(monitors.FirstOrDefault(m => m.IsPrimary)?.Id ?? monitors[0].Id)
            };

            var json = JsonSerializer.Serialize(layout);
            await _transport.SendControlAsync(json);
            _logger.LogInformation("Sent monitor layout to viewer: {Count} monitors, active={Active}",
                monitors.Count, layout.activeMonitorId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send monitor layout");
        }
    }

    private int? MatchCaptureToMonitor(int captureWidth, int captureHeight)
    {
        if (_monitorEnumerator == null) return null;

        var monitors = _monitorEnumerator.GetMonitors();
        MonitorInfo? firstMatch = null;
        var matchCount = 0;
        foreach (var m in monitors)
        {
            if (m.Width == captureWidth && m.Height == captureHeight)
            {
                firstMatch ??= m;
                matchCount++;
            }
        }

        if (matchCount == 1) return (int)firstMatch!.Id;
        if (matchCount > 1)
        {
            var primary = monitors.FirstOrDefault(m => m.Width == captureWidth && m.Height == captureHeight && m.IsPrimary);
            return (int)(primary?.Id ?? firstMatch!.Id);
        }

        return null;
    }

    private async Task HandleSwitchDisplayAsync(int monitorId)
    {
        _requestedMonitorId = monitorId;
        var monitor = _monitorEnumerator?.GetMonitors().FirstOrDefault(m => m.Id == (uint)monitorId);
        var name = monitor?.Name ?? $"Display {monitorId}";
        _logger.LogInformation("Viewer requested switch to {Monitor} (id={Id})", name, monitorId);

        await StopScreenShareAsync();
        await StartScreenShareAsync(outputIndex: (uint)monitorId);
    }

    #endregion

    private void TrackCaptureDimensions(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("captureWidth", out var cw) && cw.ValueKind == JsonValueKind.Number)
            {
                var w = cw.GetInt32();
                if (w > 0) _lastCaptureWidth = w;
            }
            if (root.TryGetProperty("captureHeight", out var ch) && ch.ValueKind == JsonValueKind.Number)
            {
                var h = ch.GetInt32();
                if (h > 0) _lastCaptureHeight = h;
            }
        }
        catch { }
    }

    #region Secure Desktop (Phase 2)

    private void HandleSecureDesktopFrame(byte[] bgraData, int width, int height, int stride)
    {
        _videoPipeline.HandleSecureDesktopFrame(bgraData, width, height, stride);
    }

    private void HandleSecureDesktopStateChanged(bool active)
    {
        // Delegate encoder/capture state management to pipeline
        _videoPipeline.HandleSecureDesktopStateChanged(active,
            restartCaptureAsync: async (outputIndex) =>
            {
                await StartScreenShareAsync(outputIndex);
            });

        if (_transport == null || !IsDataChannelReady) return;

        try
        {
            var messageType = active ? "secureDesktopActive" : "secureDesktopInactive";
            var message = JsonSerializer.Serialize(new { type = messageType });

            // Send with ACK+retry - critical state change that can't be lost over UDP
            _ = SendWithAckAsync(message, messageType);

            _logger.LogInformation("Sent {Type} to viewer (with ACK+retry)", messageType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send secure desktop state change");
        }
    }

    #endregion
}
