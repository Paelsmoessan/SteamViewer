using Microsoft.Extensions.Logging;
using SteamViewer.Client.Core.Network;
using System.Text.Json;

namespace SteamViewer.App.Services.Models;

// Stats + quality-reporting concerns for ViewerSession: periodic FPS/bitrate
// stats push, per-30s connection-quality report to host.
public sealed partial class ViewerSession
{
    private PeriodicTimer? _statsTimer;
    private CancellationTokenSource? _statsCts;
    private long _lastFrameCount;
    private long _lastBytesDecoded;
    private Timer? _qualityReportTimer;

    /// <summary>
    /// Raised when transport stats are available.
    /// </summary>
    public event Action<string>? OnStatsUpdated;

    /// <summary>
    /// Start collecting and pushing stats every 1 second.
    /// </summary>
    public Task EnableStatsRelayAsync()
    {
        if (_statsTimer != null) return Task.CompletedTask;

        _statsCts = new CancellationTokenSource();
        _statsTimer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        _lastFrameCount = _decoder?.FrameCount ?? 0;
        _lastBytesDecoded = _decoder?.TotalBytesDecoded ?? 0;

        _ = Task.Run(async () =>
        {
            try
            {
                while (await _statsTimer.WaitForNextTickAsync(_statsCts.Token))
                {
                    CollectAndPushStats();
                }
            }
            catch (OperationCanceledException) { }
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stop stats collection.
    /// </summary>
    public Task DisableStatsRelayAsync()
    {
        _statsCts?.Cancel();
        _statsTimer?.Dispose();
        _statsTimer = null;
        _statsCts?.Dispose();
        _statsCts = null;
        return Task.CompletedTask;
    }

    private void CollectAndPushStats()
    {
        var frameCount = _decoder?.FrameCount ?? 0;
        var bytesDecoded = _decoder?.TotalBytesDecoded ?? 0;
        var decodeMs = _decoder?.LastDecodeMs ?? 0;
        var width = _decoder?.Width ?? 0;
        var height = _decoder?.Height ?? 0;

        var fps = frameCount - _lastFrameCount; // frames in last 1 second
        var bytesPerSec = bytesDecoded - _lastBytesDecoded;
        _lastFrameCount = frameCount;
        _lastBytesDecoded = bytesDecoded;

        var json = JsonSerializer.Serialize(new
        {
            fps,
            decodeMs = Math.Round(decodeMs, 1),
            resolution = width > 0 ? $"{width}x{height}" : "?",
            bitrateMbps = Math.Round(bytesPerSec * 8.0 / 1_000_000, 1),
            totalFrames = frameCount,
            totalBytes = bytesDecoded,
            transport = _transport?.IsDirectUdp == true ? "FFmpeg+Direct" : "FFmpeg+Relay"
        });

        OnStatsUpdated?.Invoke(json);
    }

    private void HandleConnectionQualityChanged(ConnectionQuality quality)
    {
        // Start periodic quality report timer on first classification
        if (_qualityReportTimer == null)
        {
            _qualityReportTimer = new Timer(SendQualityReport, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
            _logger.LogInformation("Session {SessionId}: Quality report timer started", SessionId);
        }
    }

    private void SendQualityReport(object? state)
    {
        if (_transport == null || !_transport.IsConnected) return;

        var monitor = _transport.QualityMonitor;
        if (monitor == null) return;

        var quality = monitor.CurrentQuality;
        var lossRate = monitor.SmoothedLossRate;
        var rtt = monitor.SmoothedRttMs;

        try
        {
            var json = JsonSerializer.Serialize(new
            {
                type = "qualityReport",
                quality = quality.ToString(),
                lossRate = Math.Round(lossRate, 4),
                rttMs = Math.Round(rtt, 1)
            });
            _ = _transport.SendControlAsync(json);
            _logger.LogDebug("Session {SessionId}: Sent quality report: {Quality}, loss={Loss:P1}, RTT={Rtt:F0}ms",
                SessionId, quality, lossRate, rtt);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send quality report");
        }
    }

    private void StopQualityReporting()
    {
        _qualityReportTimer?.Dispose();
        _qualityReportTimer = null;
    }
}
