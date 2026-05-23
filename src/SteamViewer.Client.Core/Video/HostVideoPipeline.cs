using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SteamViewer.Client.Core.Network;

namespace SteamViewer.Client.Core.Video;

/// <summary>
/// Encapsulates the host-side video pipeline: DXGI capture event handling,
/// FFmpeg encoding, frame enqueueing, quality adaptation, Secure Desktop
/// frame routing, lossless settle (QOI), cursor shape forwarding, and sleep
/// prevention.
///
/// Extracted from HostSession to keep the session orchestrator slim and to
/// allow reuse from the boot-relay path.
/// </summary>
public sealed class HostVideoPipeline : IDisposable
{
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;

    // ------------------------------------------------------------------
    //  Transport abstraction -- thin interface so pipeline doesn't depend
    //  on HostStreamTransport directly.
    // ------------------------------------------------------------------

    /// <summary>
    /// Minimal transport surface the video pipeline needs.
    /// Implemented by HostSession as a shim over HostStreamTransport.
    /// </summary>
    public interface IVideoTransport
    {
        bool IsConnected { get; }
        bool IsDirectUdp { get; }
        void EnqueueVideoFrame(byte[] data, int length);
        ValueTask<bool> SendControlAsync(string json);
        ValueTask<bool> SendLosslessFrameAsync(byte[] data, int offset, int length);
        UdpTransportBackend? GetUdpBackend();
        Task WaitForVideoSendReadyAsync(CancellationToken ct = default);
    }

    private IVideoTransport? _transport;

    // ------------------------------------------------------------------
    //  Encoder state
    // ------------------------------------------------------------------
    private FFmpegEncoder? _encoder;
    private readonly object _encoderLock = new();
    private int _encoderErrorCount;
    private long _encodeFrameCount;
    private readonly Stopwatch _encodeSw = new();
    private readonly Stopwatch _heartbeatSw = Stopwatch.StartNew();

    // Capture/encode info change tracking
    private int _lastSentCaptureW;
    private int _lastSentCaptureH;
    private int _lastSentEncodeW;
    private int _lastSentEncodeH;

    // Pending resolution request (received before encoder exists)
    private int _pendingResW;
    private int _pendingResH;

    // Deferred ForceKeyframe: set when SetTransport fires before encoder exists
    private bool _pendingForceKeyframe;

    // Defer first encoder init for up to FirstFrameDeferMs waiting for viewer's setResolution.
    // Closes the green-blob race on auto-share-on-reconnect: encoder used to init at
    // capture-native (1920x1080), emit IDR, then re-init at viewer-desired (1264x712), emit
    // second IDR with reference buffers that don't match the new resolution → ~0.5-1s of
    // green corruption visible to the user. The deferral waits a short window for
    // setResolution to land before init, eliminating the second IDR in the common case.
    // First-connect path is unaffected because setResolution typically arrives within a
    // single round-trip (<100ms LAN, <200ms WAN) — under the 500ms deadline.
    private Stopwatch? _firstFrameDeferSw;
    private const int FirstFrameDeferMs = 500;

    // Frame rate adaptation for constrained connections
    private long _dxgiFrameCounter;
    private ConnectionQuality _lastQuality = ConnectionQuality.Unknown;

    // Track frame identity to detect refires vs real changes
    private byte[]? _lastSeenFrameRef;

    // Smart SD pipeline: track frame index for QOI burst + H.264 switchover
    private int _sdFrameIndex;
    private volatile bool _isSecureDesktopActive;
    private int _sdHostFrameCount;

    // Lossless settle -- QOI snapshot when screen stops changing
    private int _consecutiveNullFrames;
    private bool _losslessRequested;
    private int _losslessRequestW;
    private int _losslessRequestH;
    private bool _losslessSent;

    // Cursor shape dedup
    private string? _lastSentCursorShape;

    // DXGI capture references (set via AttachCapture / detached via DetachCapture)
    private IDxgiCapture? _activeDxgi;

    private bool _disposed;

    // ------------------------------------------------------------------
    //  Events
    // ------------------------------------------------------------------

    /// <summary>Raised when screen share could not be auto-restarted after SD.</summary>
    public event Action? OnScreenShareRestartFailed;

    // ------------------------------------------------------------------
    //  DXGI abstraction -- keeps Platform.Windows out of Client.Core
    // ------------------------------------------------------------------

    /// <summary>
    /// Minimal DXGI capture surface the pipeline needs.
    /// Implemented by DxgiScreenCapture in Platform.Windows.
    /// </summary>
    public interface IDxgiCapture
    {
        bool IsCapturing { get; }
        bool ShowCursor { get; set; }
        byte[]? LastRawFrame { get; }
        int LastRawWidth { get; }
        int LastRawHeight { get; }
        int LastRawStride { get; }
        void StartCaptureLoop(uint outputIndex);
        void StopCaptureLoop();
        void NotifyDesktopAvailable();

        event Action<byte[], int, int, int> OnRawFrameCaptured;
        event Action<string> OnCursorShapeChanged;
        event Action OnFrameUnchanged;
    }

    // ------------------------------------------------------------------
    //  Construction / lifecycle
    // ------------------------------------------------------------------

    public HostVideoPipeline(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<HostVideoPipeline>();
    }

    /// <summary>Set or replace the transport. Call when transport connects.</summary>
    public void SetTransport(IVideoTransport transport)
    {
        _logger.LogInformation("SetTransport: wiring new transport (encoder {State})",
            _encoder != null ? "exists" : "null");
        _transport = transport;

        // Force IDR keyframe burst so the new viewer can decode immediately
        // without waiting up to 1s for the next natural GOP boundary.
        // Burst of 3 survives packet loss on initial connect.
        if (_encoder != null)
            _encoder.ForceKeyframe(3);
        else
        {
            _logger.LogInformation("SetTransport: encoder not yet initialized - deferring ForceKeyframe to first encode");
            _pendingForceKeyframe = true;
        }
    }

    /// <summary>Whether a DXGI capture is actively sending frames.</summary>
    public bool IsCapturing { get; private set; }

    /// <summary>Whether Secure Desktop is currently active (DXGI frames are suppressed).</summary>
    public bool IsSecureDesktopActive => _isSecureDesktopActive;

    /// <summary>The active DXGI capture instance, if any.</summary>
    public IDxgiCapture? ActiveCapture => _activeDxgi;

    // ------------------------------------------------------------------
    //  Start / Stop capture
    // ------------------------------------------------------------------

    /// <summary>
    /// Attach a DXGI capture source and start the capture loop.
    /// Returns true on success.
    /// </summary>
    public bool StartCapture(IDxgiCapture dxgi, uint outputIndex)
    {
        if (_transport == null || !_transport.IsConnected) return false;

        try
        {
            _logger.LogInformation("Starting DXGI capture on output {Output} via pipeline...", outputIndex);

            dxgi.OnRawFrameCaptured += OnDxgiRawFrameCaptured;
            dxgi.OnCursorShapeChanged += OnCursorShapeChanged;
            dxgi.OnFrameUnchanged += OnDxgiFrameUnchanged;

            dxgi.StartCaptureLoop(outputIndex);

            // Prevent host from sleeping/screen-off while sharing
            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);

            _activeDxgi = dxgi;
            IsCapturing = true;
            _logger.LogInformation("DXGI capture started on output {Output} via pipeline", outputIndex);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DXGI capture + FFmpeg encoding failed");
            try { dxgi.OnRawFrameCaptured -= OnDxgiRawFrameCaptured; } catch { }
            try { dxgi.OnCursorShapeChanged -= OnCursorShapeChanged; } catch { }
            try { dxgi.OnFrameUnchanged -= OnDxgiFrameUnchanged; } catch { }
            try { dxgi.StopCaptureLoop(); } catch { }
            SetThreadExecutionState(ES_CONTINUOUS);
            DisposeEncoder();
            return false;
        }
    }

    /// <summary>Stop the capture loop and dispose the encoder.</summary>
    public void StopCapture()
    {
        if (_activeDxgi != null)
        {
            _activeDxgi.OnRawFrameCaptured -= OnDxgiRawFrameCaptured;
            _activeDxgi.OnCursorShapeChanged -= OnCursorShapeChanged;
            _activeDxgi.OnFrameUnchanged -= OnDxgiFrameUnchanged;
            _activeDxgi.StopCaptureLoop();
        }

        DisposeEncoder();
        SetThreadExecutionState(ES_CONTINUOUS);
        IsCapturing = false;
    }

    /// <summary>Detach all DXGI event handlers without stopping the loop (used during SD restart).</summary>
    public void DetachCapture()
    {
        if (_activeDxgi != null)
        {
            _activeDxgi.OnRawFrameCaptured -= OnDxgiRawFrameCaptured;
            _activeDxgi.OnCursorShapeChanged -= OnCursorShapeChanged;
            _activeDxgi.OnFrameUnchanged -= OnDxgiFrameUnchanged;
        }
        DisposeEncoder();
        IsCapturing = false;
    }

    // ------------------------------------------------------------------
    //  DXGI frame handler (called from capture thread)
    // ------------------------------------------------------------------

    private void OnDxgiRawFrameCaptured(byte[] bgraData, int width, int height, int stride)
    {
        if (_transport == null || !_transport.IsConnected || !IsCapturing) return;

        // Skip DXGI frames during Secure Desktop - SD frames use the encoder instead
        if (_isSecureDesktopActive) return;

        // Frame rate adaptation: skip frames on constrained connections
        _dxgiFrameCounter++;
        if (_lastQuality == ConnectionQuality.Poor && _dxgiFrameCounter % 2 != 0)
            return; // 15fps on poor connections

        // Only reset lossless state on genuinely NEW frames (not refired cached frames)
        if (!ReferenceEquals(bgraData, _lastSeenFrameRef))
        {
            _lastSeenFrameRef = bgraData;
            _consecutiveNullFrames = 0;
            _losslessSent = false;
        }

        EncodeAndSend(bgraData, width, height, stride, isSecureDesktop: false, frameIndex: -1);
    }

    // ------------------------------------------------------------------
    //  Secure Desktop frame handler
    // ------------------------------------------------------------------

    /// <summary>
    /// Handle a Secure Desktop BGRA frame from the elevation service.
    /// Sends QOI burst for first 2 frames, then H.264 for all.
    /// </summary>
    public void HandleSecureDesktopFrame(byte[] bgraData, int width, int height, int stride)
    {
        _sdHostFrameCount++;
        if (_sdHostFrameCount <= 3 || _sdHostFrameCount % 100 == 0)
            _logger.LogInformation("SD frame #{Count}: {Bytes}b BGRA {W}x{H}, sdIdx={Idx}, transport={Transport}, ready={Ready}",
                _sdHostFrameCount, bgraData.Length, width, height, _sdFrameIndex, _transport != null, _transport?.IsConnected ?? false);

        if (_transport == null || !_transport.IsConnected) return;

        // Smart SD pipeline: QOI burst + H.264 unified
        var frameIdx = _sdFrameIndex++;

        // First 2 frames: send QOI on lossless channel (fire-and-forget lossless bonus)
        if (frameIdx < 2)
        {
            var frameCopy = new byte[stride * height];
            Buffer.BlockCopy(bgraData, 0, frameCopy, 0, frameCopy.Length);
            var captureW = width;
            var captureH = height;
            var captureStride = stride;

            _ = Task.Run(async () =>
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    var qoiData = QoiCodec.Encode(frameCopy, captureW, captureH, captureStride);
                    sw.Stop();
                    var sent = await _transport!.SendLosslessFrameAsync(qoiData, 0, qoiData.Length);
                    _logger.LogInformation("SD QOI frame #{Idx}: {W}x{H}, {Size}KB, encode={Ms:F1}ms, sent={Sent}",
                        frameIdx, captureW, captureH, qoiData.Length / 1024, sw.Elapsed.TotalMilliseconds, sent);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send SD QOI frame");
                }
            });
        }

        // ALL frames: send through H.264 encoder (guaranteed stream)
        EncodeAndSend(bgraData, width, height, stride, isSecureDesktop: true, frameIndex: frameIdx);
    }

    /// <summary>
    /// Notify the pipeline that Secure Desktop state changed.
    /// Handles encoder dimension reset, DXGI capture restart after SD, etc.
    /// </summary>
    /// <param name="active">True if entering SD, false if leaving.</param>
    /// <param name="restartCaptureAsync">
    /// Callback to restart screen sharing after SD if DXGI capture died.
    /// Takes an optional output index, returns Task.
    /// </param>
    public void HandleSecureDesktopStateChanged(bool active, Func<uint?, Task>? restartCaptureAsync = null)
    {
        LogSdStateTransition(active);
        _isSecureDesktopActive = active;

        if (active)
        {
            _sdFrameIndex = 0;
            _logger.LogInformation("SD pipeline: active, sdFrameIndex reset, encoder={HasEncoder}, encoderDims={W}x{H}",
                _encoder != null, _encoder?.Width ?? 0, _encoder?.Height ?? 0);
            return;
        }

        // SD-exit path: reset encode-info so viewer canvas re-syncs CSS, wake DXGI retry, restart capture if dead.
        _logger.LogInformation("SD pipeline: inactive, sdFrameIndex was {Idx}", _sdFrameIndex);
        ResetEncodeInfoOnSdExit();
        _activeDxgi?.NotifyDesktopAvailable();
        RestartDxgiAfterSdIfDead(restartCaptureAsync);
    }

    /// <summary>Entry log capturing SD transition + transport state at the moment of handoff.</summary>
    private void LogSdStateTransition(bool active)
    {
        _logger.LogInformation("SD state handler: active={Active}, transport={Transport}, ready={Ready}",
            active, _transport != null, _transport?.IsConnected ?? false);
    }

    /// <summary>
    /// Force re-send of encodeInfo on the next encoded frame after SD exit so the viewer
    /// canvas re-syncs its CSS. Called from SD-exit path only.
    /// </summary>
    private void ResetEncodeInfoOnSdExit()
    {
        _lastSentEncodeW = 0;
        _lastSentEncodeH = 0;
    }

    /// <summary>
    /// On SD exit, DXGI desktop duplication may have been invalidated. If capture was
    /// active before SD and is now dead, tear down stale state and request a restart
    /// via the caller-provided callback. Silent no-op if capture is still healthy or
    /// no callback was provided.
    /// </summary>
    private void RestartDxgiAfterSdIfDead(Func<uint?, Task>? restartCaptureAsync)
    {
        if (!IsCapturing || _activeDxgi == null) return;
        if (_activeDxgi.IsCapturing)
        {
            _logger.LogInformation("DXGI capture still running after SD - no restart needed");
            return;
        }

        _logger.LogInformation("DXGI capture died during SD - restarting capture");
        _activeDxgi.StopCaptureLoop();
        DetachCapture();

        if (restartCaptureAsync == null) return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            try
            {
                await restartCaptureAsync(0);
                _logger.LogInformation("DXGI capture restarted after SD");
            }
            catch (Exception restartEx)
            {
                _logger.LogWarning(restartEx, "Failed to restart DXGI capture after SD");
                OnScreenShareRestartFailed?.Invoke();
            }
        });
    }

    /// <summary>
    /// True if the caller should SKIP this frame and wait for setResolution. False if either
    /// the deferral window has elapsed (init at capture-native) OR a pending resolution has
    /// already arrived (init at that resolution). SD bypasses the deferral entirely.
    /// </summary>
    private bool ShouldDeferEncoderInit(bool isSecureDesktop, int width, int height)
    {
        if (isSecureDesktop) return false;
        if (_pendingResW > 0 || _pendingResH > 0) return false;

        if (_firstFrameDeferSw == null)
        {
            _firstFrameDeferSw = Stopwatch.StartNew();
            _logger.LogDebug("Encoder init deferred up to {Ms}ms waiting for viewer setResolution (first frame {W}x{H})",
                FirstFrameDeferMs, width, height);
            return true;
        }
        if (_firstFrameDeferSw.ElapsedMilliseconds < FirstFrameDeferMs)
        {
            return true;
        }
        _logger.LogInformation("Encoder init defer window ({Ms}ms) elapsed without setResolution - initializing at capture-native {W}x{H}",
            FirstFrameDeferMs, width, height);
        return false;
    }

    /// <summary>Reset the encode-info tracking so the next frame re-sends dimensions to viewer.</summary>
    public void ResetEncodeInfo()
    {
        _lastSentEncodeW = 0;
        _lastSentEncodeH = 0;
    }

    // ------------------------------------------------------------------
    //  Shared encode path
    // ------------------------------------------------------------------

    private void EncodeAndSend(byte[] bgraData, int width, int height, int stride, bool isSecureDesktop, int frameIndex)
    {
        lock (_encoderLock)
        {
            try
            {
                if (!EnsureEncoderInitialized(width, height, isSecureDesktop)) return;
                LogSdResolutionMismatchIfRelevant(isSecureDesktop, frameIndex, width, height);

                // Notify viewer if capture dims changed
                SendCaptureInfoIfChanged(width, height);

                _encodeSw.Restart();
                var result = _encoder!.EncodeFrame(bgraData, stride, width, height);
                _encodeSw.Stop();

                // Notify viewer of actual encode resolution
                SendEncodeInfoIfChanged();

                if (result is var (naluData, naluLength))
                {
                    _transport!.EnqueueVideoFrame(naluData, naluLength);
                    _encodeFrameCount++;
                    LogEncodedFrame(naluLength, _encodeSw.Elapsed.TotalMilliseconds, isSecureDesktop, frameIndex);
                }

                EmitEncodeHeartbeatIfDue();
            }
            catch (Exception ex)
            {
                LogEncodeError(ex, isSecureDesktop);
            }
        }
    }

    /// <summary>
    /// Ensure the encoder is constructed + initialized. Returns false if the caller should
    /// SKIP this frame (defer window not elapsed yet, waiting for viewer setResolution).
    /// Returns true if the encoder is ready (either pre-existing or just-constructed).
    /// Caller holds _encoderLock.
    /// </summary>
    private bool EnsureEncoderInitialized(int width, int height, bool isSecureDesktop)
    {
        if (_encoder != null) return true;

        if (ShouldDeferEncoderInit(isSecureDesktop, width, height))
        {
            return false;
        }

        if (isSecureDesktop)
            _logger.LogInformation("SD: encoder is null, initializing for {W}x{H}", width, height);

        FFmpegInit.EnsureInitialized();
        var encoder = new FFmpegEncoder(_loggerFactory.CreateLogger<FFmpegEncoder>());
        if (_pendingResW > 0 && _pendingResH > 0)
        {
            encoder.SetRequestedResolution(_pendingResW, _pendingResH);
            // Initialize at pending resolution directly - avoids wasted 1920x1080 keyframe
            var initW = Math.Min(_pendingResW, width);
            var initH = Math.Min(_pendingResH, height);
            encoder.Initialize(initW, initH, 30, 20_000_000, crf: 14);
            _logger.LogInformation("Encoder initialized at pending resolution {W}x{H} (capture: {CW}x{CH})",
                initW, initH, width, height);
        }
        else
        {
            encoder.Initialize(width, height, 30, 20_000_000, crf: 14);
            _logger.LogInformation("Encoder initialized at capture resolution {W}x{H}", width, height);
        }
        _encoder = encoder;
        SendCaptureInfoIfChanged(width, height);

        // Apply deferred ForceKeyframe from SetTransport (encoder didn't exist yet)
        if (_pendingForceKeyframe)
        {
            _pendingForceKeyframe = false;
            _encoder.ForceKeyframe(3);
            _logger.LogInformation("Deferred ForceKeyframe burst applied after encoder init");
        }
        return true;
    }

    /// <summary>
    /// Log a one-shot warning on the first SD frame if the existing encoder's resolution
    /// doesn't match the incoming SD frame. Silent no-op for non-SD frames or non-first-frame.
    /// </summary>
    private void LogSdResolutionMismatchIfRelevant(bool isSecureDesktop, int frameIndex, int width, int height)
    {
        if (!isSecureDesktop) return;
        if (frameIndex != 0) return;
        if (_encoder == null) return;
        var resolutionMatches = _encoder.Width == width && _encoder.Height == height;
        _logger.LogInformation("SD: encoder exists ({EW}x{EH}), SD frame is {W}x{H} - {Action}",
            _encoder.Width, _encoder.Height, width, height,
            resolutionMatches ? "same resolution" : "REINIT expected");
    }

    /// <summary>
    /// Verbose per-frame log. SD path: first 3 frames + every 300th. Normal path: every 300th.
    /// </summary>
    private void LogEncodedFrame(int naluLength, double elapsedMs, bool isSecureDesktop, int frameIndex)
    {
        if (isSecureDesktop)
        {
            if (frameIndex < 3 || _encodeFrameCount % 300 == 0)
                _logger.LogInformation("SD H.264 frame #{Idx}: {Ms:F1}ms, {Size}KB",
                    frameIndex, elapsedMs, naluLength / 1024);
        }
        else if (_encodeFrameCount % 300 == 0)
        {
            _logger.LogInformation("Encode #{Count}: {Ms:F1}ms, {Size}KB",
                _encodeFrameCount, elapsedMs, naluLength / 1024);
        }
    }

    /// <summary>
    /// Heartbeat log every 60s to prove the encode pipeline is alive.
    /// </summary>
    private void EmitEncodeHeartbeatIfDue()
    {
        if (_heartbeatSw.ElapsedMilliseconds < 60_000) return;
        _logger.LogInformation("Encode heartbeat: #{Count} frames, encoder {W}x{H}",
            _encodeFrameCount, _encoder?.Width ?? 0, _encoder?.Height ?? 0);
        _heartbeatSw.Restart();
    }

    /// <summary>
    /// Throttled encode-error log: fires on the first error and every 300th error after that.
    /// Increments _encoderErrorCount on every call.
    /// </summary>
    private void LogEncodeError(Exception ex, bool isSecureDesktop)
    {
        if (_encoderErrorCount == 0 || _encoderErrorCount % 300 == 0)
            _logger.LogError(ex, "{Prefix}encode error #{Count}",
                isSecureDesktop ? "SD " : "FFmpeg ", _encoderErrorCount);
        _encoderErrorCount++;
    }

    // ------------------------------------------------------------------
    //  Capture/encode info change notifications
    // ------------------------------------------------------------------

    private void SendEncodeInfoIfChanged()
    {
        var w = _encoder?.Width ?? 0;
        var h = _encoder?.Height ?? 0;
        if (w <= 0 || h <= 0) return;
        if (w == _lastSentEncodeW && h == _lastSentEncodeH) return;
        _lastSentEncodeW = w;
        _lastSentEncodeH = h;
        _logger.LogInformation("Sending encodeInfo to viewer: {W}x{H}", w, h);
        _ = _transport?.SendControlAsync(JsonSerializer.Serialize(new
        {
            type = "encodeInfo",
            width = w,
            height = h
        }));
    }

    private void SendCaptureInfoIfChanged(int width, int height)
    {
        if (width == _lastSentCaptureW && height == _lastSentCaptureH) return;
        _lastSentCaptureW = width;
        _lastSentCaptureH = height;
        _logger.LogInformation("Sending captureInfo to viewer: {W}x{H}", width, height);
        _ = _transport?.SendControlAsync(JsonSerializer.Serialize(new
        {
            type = "captureInfo",
            width,
            height
        }));
    }

    // ------------------------------------------------------------------
    //  Viewer requests
    // ------------------------------------------------------------------

    /// <summary>Handle viewer resolution request - encoder will downscale using Bicubic.</summary>
    public void HandleSetResolution(JsonElement root)
    {
        var w = root.TryGetProperty("width", out var wp) ? wp.GetInt32() : 0;
        var h = root.TryGetProperty("height", out var hp) ? hp.GetInt32() : 0;

        if (w <= 0 || h <= 0)
        {
            _logger.LogWarning("Invalid resolution request: {W}x{H}", w, h);
            return;
        }

        _logger.LogInformation("Viewer requested encode resolution: {W}x{H}", w, h);
        _pendingResW = w;
        _pendingResH = h;
        _encoder?.SetRequestedResolution(w, h);
    }

    /// <summary>Handle viewer request for a lossless QOI settle frame.</summary>
    public void HandleRequestLosslessFrame(JsonElement root)
    {
        if (_isSecureDesktopActive) return;

        var w = root.TryGetProperty("width", out var wp) ? wp.GetInt32() : 0;
        var h = root.TryGetProperty("height", out var hp) ? hp.GetInt32() : 0;

        if (w <= 0 || h <= 0)
        {
            _logger.LogWarning("Invalid lossless frame request: {W}x{H}", w, h);
            return;
        }

        _losslessRequestW = w;
        _losslessRequestH = h;
        _losslessRequested = true;
        _losslessSent = false;
        _logger.LogDebug("Viewer requested lossless frame: {W}x{H}", w, h);

        if (_consecutiveNullFrames >= 3)
            FulfillLosslessRequest();
    }

    // ------------------------------------------------------------------
    //  Connection quality adaptation
    // ------------------------------------------------------------------

    /// <summary>Handle a quality report from the viewer.</summary>
    public void HandleQualityReport(JsonElement root)
    {
        var qualityStr = root.TryGetProperty("quality", out var qProp) ? qProp.GetString() : null;
        if (qualityStr == null) return;

        if (!Enum.TryParse<ConnectionQuality>(qualityStr, out var quality)) return;

        var lossRate = root.TryGetProperty("lossRate", out var lProp) ? lProp.GetDouble() : -1;
        var rttMs = root.TryGetProperty("rttMs", out var rProp) ? rProp.GetDouble() : -1;

        _logger.LogInformation("[QualityReport] Viewer reports: {Quality}, loss={Loss:P1}, RTT={Rtt:F0}ms",
            quality, lossRate, rttMs);

        ApplyQualityAdaptation(quality);
    }

    /// <summary>Per-quality adaptation settings: encoder bitrate cap + UDP FEC tuning + log label.</summary>
    private readonly record struct QualityProfile(long MaxBitrate, double FecScaleFactor, string FecLabel);

    private static QualityProfile GetQualityProfile(ConnectionQuality quality) => quality switch
    {
        ConnectionQuality.Poor => new QualityProfile(5_000_000L, 1.6, "35%"),
        ConnectionQuality.Fair => new QualityProfile(12_000_000L, 1.3, "25%"),
        ConnectionQuality.Good => new QualityProfile(20_000_000L, 0, "off"),
        // Default keeps the historical asymmetric values: Good's bitrate cap + Fair's FEC.
        _ => new QualityProfile(20_000_000L, 1.3, "25%"),
    };

    private void ApplyQualityAdaptation(ConnectionQuality quality)
    {
        if (quality == _lastQuality) return;
        _lastQuality = quality;

        var profile = GetQualityProfile(quality);

        lock (_encoderLock)
        {
            _encoder?.SetMaxBitrate(profile.MaxBitrate);
        }

        ApplyFecScaleIfDirectUdp(profile.FecScaleFactor);

        _logger.LogInformation("[QualityAdapt] {Quality}: bitrate cap={Mbps}Mbps, FEC={Fec}",
            quality, profile.MaxBitrate / 1_000_000, profile.FecLabel);
    }

    /// <summary>
    /// Push the FEC scale factor onto the UDP backend if direct UDP is active. Silent no-op
    /// on relay or when the UDP backend is not yet bound.
    /// </summary>
    private void ApplyFecScaleIfDirectUdp(double fecScale)
    {
        if (_transport?.IsDirectUdp != true) return;
        var udp = _transport.GetUdpBackend();
        if (udp == null) return;
        udp.FecScaleFactor = fecScale;
    }

    // ------------------------------------------------------------------
    //  Cursor shape
    // ------------------------------------------------------------------

    private void OnCursorShapeChanged(string cssValue)
    {
        if (cssValue == _lastSentCursorShape) return;
        _lastSentCursorShape = cssValue;

        if (_transport?.IsConnected == true)
        {
            _ = _transport.SendControlAsync(JsonSerializer.Serialize(new
            {
                type = "cursorShape",
                cursor = cssValue
            }));
        }
    }

    /// <summary>Toggle cursor visibility in the captured video stream.</summary>
    public async Task HandleToggleCursorAsync()
    {
        if (_activeDxgi != null)
        {
            _activeDxgi.ShowCursor = !_activeDxgi.ShowCursor;
            _logger.LogInformation("Host cursor visibility: {Visible}", _activeDxgi.ShowCursor);

            if (_transport != null)
            {
                await _transport.SendControlAsync(JsonSerializer.Serialize(new
                {
                    type = "cursorVisibilityChanged",
                    visible = _activeDxgi.ShowCursor
                }));
            }
        }
    }

    /// <summary>Set cursor visibility (e.g., from input lock state).</summary>
    public void SetCursorVisible(bool visible)
    {
        if (_activeDxgi != null)
            _activeDxgi.ShowCursor = visible;
    }

    // ------------------------------------------------------------------
    //  Lossless settle
    // ------------------------------------------------------------------

    private void OnDxgiFrameUnchanged()
    {
        _consecutiveNullFrames++;
        if (!_losslessRequested || _losslessSent) return;
        if (_consecutiveNullFrames < 3) return;

        FulfillLosslessRequest();
    }

    /// <summary>Captured raw frame snapshot used for a lossless send.</summary>
    private readonly record struct LosslessFrameSource(byte[] Frame, int Width, int Height, int Stride);

    private void FulfillLosslessRequest()
    {
        if (_activeDxgi == null) return;
        if (_transport == null) return;
        if (!_transport.IsConnected) return;

        var source = SnapshotLosslessSource();
        if (source == null) return;

        _losslessSent = true;
        _losslessRequested = false;

        _ = Task.Run(() => EncodeAndSendLosslessAsync(source.Value));
    }

    /// <summary>
    /// Snapshot the current raw DXGI frame for lossless encoding. Returns null if the
    /// DXGI capture has no frame yet or the frame is in an invalid state.
    /// </summary>
    private LosslessFrameSource? SnapshotLosslessSource()
    {
        var rawFrame = _activeDxgi?.LastRawFrame;
        if (rawFrame == null) return null;
        var rawW = _activeDxgi!.LastRawWidth;
        if (rawW <= 0) return null;
        var rawH = _activeDxgi.LastRawHeight;
        if (rawH <= 0) return null;
        var rawStride = _activeDxgi.LastRawStride;

        var frameCopy = new byte[rawFrame.Length];
        Buffer.BlockCopy(rawFrame, 0, frameCopy, 0, rawFrame.Length);
        return new LosslessFrameSource(frameCopy, rawW, rawH, rawStride);
    }

    /// <summary>
    /// Background task: downscale if the requested resolution differs from the capture
    /// resolution, encode to QOI, send via the transport's lossless channel, log result.
    /// </summary>
    private async Task EncodeAndSendLosslessAsync(LosslessFrameSource source)
    {
        try
        {
            byte[] bgraToEncode = source.Frame;
            int encodeW = source.Width, encodeH = source.Height, encodeStride = source.Stride;

            if (_losslessRequestW != source.Width || _losslessRequestH != source.Height)
            {
                bgraToEncode = DownscaleBgra(source.Frame, source.Width, source.Height, source.Stride,
                    _losslessRequestW, _losslessRequestH);
                encodeW = _losslessRequestW;
                encodeH = _losslessRequestH;
                encodeStride = encodeW * 4;
            }

            var sw = Stopwatch.StartNew();
            var qoiData = QoiCodec.Encode(bgraToEncode, encodeW, encodeH, encodeStride);
            sw.Stop();

            var sent = await _transport!.SendLosslessFrameAsync(qoiData, 0, qoiData.Length);
            _logger.LogInformation("Lossless QOI frame: {W}x{H}, {Size}KB, encode={Ms:F1}ms, sent={Sent}",
                encodeW, encodeH, qoiData.Length / 1024, sw.Elapsed.TotalMilliseconds, sent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to encode/send lossless frame");
        }
    }

    private byte[] DownscaleBgra(byte[] srcBgra, int srcW, int srcH, int srcStride, int dstW, int dstH)
    {
        try
        {
            return FFmpegEncoder.DownscaleBgra(srcBgra, srcW, srcH, srcStride, dstW, dstH);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "sws_scale downscale failed - sending at capture resolution");
            return srcBgra;
        }
    }

    // ------------------------------------------------------------------
    //  Sleep Prevention (Windows)
    // ------------------------------------------------------------------

    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    // ------------------------------------------------------------------
    //  Dispose
    // ------------------------------------------------------------------

    private void DisposeEncoder()
    {
        lock (_encoderLock)
        {
            _encoder?.Dispose();
            _encoder = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopCapture();
        _transport = null;
    }
}
