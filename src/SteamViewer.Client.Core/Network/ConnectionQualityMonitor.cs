using Microsoft.Extensions.Logging;

namespace SteamViewer.Client.Core.Network;

/// <summary>
/// Connection quality classification: Good (LAN/fiber), Fair (WiFi/decent WAN), Poor (mobile/lossy WAN).
/// </summary>
public enum ConnectionQuality { Unknown, Good, Fair, Poor }

/// <summary>
/// Monitors connection quality using EMA-smoothed loss rate and RTT.
/// Standalone class - no coupling to SD capture or quality settings.
///
/// Classification thresholds (from industry research):
///   Good: loss &lt; 1%, RTT &lt; 100ms
///   Fair: loss 1-3% OR RTT 100-300ms
///   Poor: loss &gt; 3% OR RTT &gt; 300ms
///
/// Hysteresis prevents oscillation:
///   Degrade: 2 consecutive updates in worse band (20s)
///   Recover: 3 consecutive updates in better band (30s)
///   Min 10s between quality changes
/// </summary>
public sealed class ConnectionQualityMonitor
{
    private readonly ILogger _logger;
    private readonly object _lock = new();

    // EMA state
    private double _smoothedLossRate;
    private double _smoothedRtt; // milliseconds
    private int _updateCount;
    private const double ColdStartAlpha = 0.5;  // First 10 updates - respond quickly
    private const double SteadyStateAlpha = 0.2; // After 10 updates - smooth

    // Classification state
    private ConnectionQuality _currentQuality = ConnectionQuality.Unknown;
    private ConnectionQuality _rawQuality = ConnectionQuality.Unknown;
    private int _consecutiveInRaw; // How many updates the raw classification has been stable
    private DateTime _lastQualityChange = DateTime.MinValue;
    private int _totalMessagesRecorded;

    // Thresholds
    private const double LossGoodMax = 0.01;   // 1%
    private const double LossFairMax = 0.03;    // 3%
    private const double RttGoodMaxMs = 100.0;
    private const double RttFairMaxMs = 300.0;
    private const int MinMessagesForClassification = 100;
    private static readonly TimeSpan MinTimeBetweenChanges = TimeSpan.FromSeconds(10);

    // Hysteresis: require consecutive updates before changing
    private const int DegradeConsecutiveRequired = 2;  // 20s at 10s interval
    private const int RecoverConsecutiveRequired = 3;   // 30s at 10s interval

    public ConnectionQuality CurrentQuality
    {
        get { lock (_lock) return _currentQuality; }
    }

    public double SmoothedLossRate
    {
        get { lock (_lock) return _smoothedLossRate; }
    }

    public double SmoothedRttMs
    {
        get { lock (_lock) return _smoothedRtt; }
    }

    public event Action<ConnectionQuality>? OnQualityChanged;

    public ConnectionQualityMonitor(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Record the initial probe RTT. Called once after successful UDP probe.
    /// </summary>
    public void RecordProbeRtt(TimeSpan rtt)
    {
        lock (_lock)
        {
            _smoothedRtt = rtt.TotalMilliseconds;
            _logger.LogInformation("[QualityMonitor] Initial probe RTT: {Rtt:F1}ms", _smoothedRtt);
        }
    }

    /// <summary>
    /// Update with current loss rate from transport. Call every 10 seconds.
    /// </summary>
    /// <param name="lossRate">Message loss rate 0.0-1.0 from GetAndResetLossRate()</param>
    /// <param name="messageCount">Number of messages in this measurement window</param>
    public void Update(double lossRate, int messageCount)
    {
        lock (_lock)
        {
            _totalMessagesRecorded += messageCount;
            _updateCount++;

            // EMA smoothing - faster convergence during cold start
            var alpha = _updateCount <= 10 ? ColdStartAlpha : SteadyStateAlpha;
            _smoothedLossRate = _updateCount == 1
                ? lossRate
                : _smoothedLossRate * (1.0 - alpha) + lossRate * alpha;

            // Classify raw quality from smoothed values
            var rawNow = Classify(_smoothedLossRate, _smoothedRtt);

            // Don't classify until we have enough data
            if (_totalMessagesRecorded < MinMessagesForClassification)
            {
                _logger.LogDebug("[QualityMonitor] Update #{Count}: loss={Loss:P1} (smoothed={Smoothed:P1}), RTT={Rtt:F0}ms, msgs={MsgCount}/{MinMsgs} - waiting for data",
                    _updateCount, lossRate, _smoothedLossRate, _smoothedRtt, _totalMessagesRecorded, MinMessagesForClassification);
                return;
            }

            // Track consecutive readings in the same raw band
            if (rawNow == _rawQuality)
            {
                _consecutiveInRaw++;
            }
            else
            {
                _rawQuality = rawNow;
                _consecutiveInRaw = 1;
            }

            // Apply hysteresis
            var qualityChanged = false;
            if (rawNow != _currentQuality)
            {
                var isDegrade = rawNow > _currentQuality; // Higher enum = worse
                var required = isDegrade ? DegradeConsecutiveRequired : RecoverConsecutiveRequired;

                if (_consecutiveInRaw >= required &&
                    DateTime.UtcNow - _lastQualityChange >= MinTimeBetweenChanges)
                {
                    var previous = _currentQuality;
                    _currentQuality = rawNow;
                    _lastQualityChange = DateTime.UtcNow;
                    qualityChanged = true;

                    _logger.LogInformation("[QualityMonitor] Quality changed: {Previous} -> {Current} (loss={Loss:P1}, RTT={Rtt:F0}ms, after {Count} consecutive readings)",
                        previous, _currentQuality, _smoothedLossRate, _smoothedRtt, _consecutiveInRaw);
                }
            }

            _logger.LogDebug("[QualityMonitor] Update #{Count}: loss={Loss:P1} (smoothed={Smoothed:P1}), RTT={Rtt:F0}ms, quality={Quality} (raw={Raw}, consecutive={Consec})",
                _updateCount, lossRate, _smoothedLossRate, _smoothedRtt, _currentQuality, rawNow, _consecutiveInRaw);

            // Fire event outside lock
            if (qualityChanged)
            {
                var quality = _currentQuality;
                // Release lock before firing event
                _ = Task.Run(() =>
                {
                    try { OnQualityChanged?.Invoke(quality); }
                    catch (Exception ex) { _logger.LogWarning(ex, "[QualityMonitor] OnQualityChanged handler error"); }
                });
            }
        }
    }

    private static ConnectionQuality Classify(double lossRate, double rttMs)
    {
        if (lossRate > LossFairMax || rttMs > RttFairMaxMs)
            return ConnectionQuality.Poor;
        if (lossRate > LossGoodMax || rttMs > RttGoodMaxMs)
            return ConnectionQuality.Fair;
        return ConnectionQuality.Good;
    }
}
