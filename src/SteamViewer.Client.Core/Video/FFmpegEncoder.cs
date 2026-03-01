using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;

namespace SteamViewer.Client.Core.Video;

/// <summary>
/// H.264 encoder using FFmpeg libx264 with High 4:4:4 Predictive profile.
/// Encodes raw BGRA frames to H.264 NAL units for network transport.
///
/// Key config: YUV444P pixel format preserves full chroma (no 4:2:0 subsampling),
/// giving crisp text rendering that WebRTC H.264 couldn't achieve.
///
/// Supports server-side resolution negotiation: when viewer requests a different
/// resolution, the encoder downscales BGRA using Lanczos (RGB-space) before
/// converting to YUV444P for encoding. Two-step pipeline preserves text sharpness
/// better than single-pass cross-format conversion.
///
/// Source: Sunshine encoder architecture (GPL-3.0, reference only)
/// </summary>
public sealed unsafe class FFmpegEncoder : IDisposable
{
    private readonly ILogger _logger;
    private AVCodecContext* _codecCtx;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwsContext* _swsCtx;          // BGRA → YUV444P color convert (at encode resolution, no resize)
    private SwsContext* _downscaleCtx;    // BGRA capture → BGRA encode (Lanczos downscale, RGB-space)
    private byte[]? _downscaleBuffer;     // Intermediate BGRA buffer at encode resolution
    private int _width;                   // Encode width (may differ from capture)
    private int _height;                  // Encode height (may differ from capture)
    private int _captureWidth;            // Last seen capture width
    private int _captureHeight;           // Last seen capture height
    private int _requestedWidth;          // Viewer-requested width (0 = use capture)
    private int _requestedHeight;         // Viewer-requested height (0 = use capture)
    private int _fps;                    // Stored for reinit
    private int _crf;                    // Stored for reinit
    private long _maxBitrate;            // Stored for reinit
    private long _pts;
    private bool _forceKeyframe;
    private byte[]? _outputBuffer;
    private bool _disposed;

    public int Width => _width;
    public int Height => _height;
    public bool IsInitialized => _codecCtx != null;

    public FFmpegEncoder(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Initialize the encoder for given dimensions.
    /// Must be called before EncodeFrame.
    /// </summary>
    public void Initialize(int width, int height, int fps = 30, long maxBitrate = 20_000_000, int crf = 14)
    {
        // Store params for reinit (first call sets them, subsequent reinits reuse)
        if (_fps == 0) { _fps = fps; _crf = crf; _maxBitrate = maxBitrate; }
        fps = _fps; crf = _crf; maxBitrate = _maxBitrate;

        FFmpegInit.EnsureInitialized();

        var codec = ffmpeg.avcodec_find_encoder_by_name("libx264");
        if (codec == null)
            throw new InvalidOperationException("libx264 encoder not found. Ensure FFmpeg DLLs with libx264 are available.");

        _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
        if (_codecCtx == null)
            throw new InvalidOperationException("Failed to allocate encoder context");

        _codecCtx->width = width;
        _codecCtx->height = height;
        _codecCtx->time_base = new AVRational { num = 1, den = fps };
        _codecCtx->framerate = new AVRational { num = fps, den = 1 };
        _codecCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV444P; // 4:4:4 chroma — the whole point
        _codecCtx->bit_rate = 0;                     // CRF mode — quality-based, not bitrate-based
        _codecCtx->gop_size = fps;                    // Keyframe every 1 second (halves P-frame drift)
        _codecCtx->max_b_frames = 0;                  // Zero latency = no B-frames
        _codecCtx->thread_count = 2;                  // 2 threads — 1 slice boundary (minimal CABAC cold-start), halves encode time vs threads=1
        _codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _codecCtx->rc_max_rate = maxBitrate;          // VBV cap: transport safety limit
        _codecCtx->rc_buffer_size = (int)(maxBitrate * 2); // VBV buffer: 2 seconds (5MB at 20Mbps) — prevents mid-frame quality degradation

        // H.264 High 4:4:4 Predictive profile (value = 244)
        _codecCtx->profile = 244; // FF_PROFILE_H264_HIGH_444_PREDICTIVE

        // Encoder presets for low-latency screen sharing
        // superfast: CABAC + better prediction → less P-frame drift, ~1ms more than ultrafast
        ffmpeg.av_opt_set(_codecCtx->priv_data, "preset", "superfast", 0);
        ffmpeg.av_opt_set(_codecCtx->priv_data, "tune", "zerolatency", 0);
        ffmpeg.av_opt_set(_codecCtx->priv_data, "crf", crf.ToString(), 0);

        // Text sharpness tuning (can't use tune=stillimage — conflicts with zerolatency)
        // aq-strength 1.2: boost flat-area quality (default 1.0, stillimage uses 1.2)
        // deblock -2:-2: minimal deblocking — preserves sharp text edges (default 0:0 over-smooths)
        // psy-rd 1.0:0.15: perceptual optimization — preserves high-frequency detail (text edges)
        //   psy-rd=1.0 = psychovisual strength, psy-trellis=0.15 = quantization-domain sharpness
        ffmpeg.av_opt_set(_codecCtx->priv_data, "aq-strength", "1.2", 0);
        ffmpeg.av_opt_set(_codecCtx->priv_data, "deblock", "-2:-2", 0);
        ffmpeg.av_opt_set(_codecCtx->priv_data, "psy-rd", "1.0:0.15", 0);

        var ret = ffmpeg.avcodec_open2(_codecCtx, codec, null);
        if (ret < 0)
            throw new InvalidOperationException($"Failed to open libx264 encoder: error {ret}");

        // Allocate reusable frame (YUV444P)
        _frame = ffmpeg.av_frame_alloc();
        _frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV444P;
        _frame->width = width;
        _frame->height = height;
        ret = ffmpeg.av_frame_get_buffer(_frame, 0);
        if (ret < 0)
            throw new InvalidOperationException($"Failed to allocate frame buffer: error {ret}");

        // Allocate reusable packet
        _packet = ffmpeg.av_packet_alloc();

        // BGR0 → YUV444P color convert (same-size, no resize)
        // SWS_POINT: direct pixel-level color matrix, no filter kernel. Avoids sws_scale phase
        // accumulator drift that causes left-sharp/right-blurry asymmetry with any other filter.
        // For same-size conversion, SWS_POINT is mathematically correct (no interpolation needed).
        _swsCtx = ffmpeg.sws_getContext(
            width, height, AVPixelFormat.AV_PIX_FMT_BGR0,
            width, height, AVPixelFormat.AV_PIX_FMT_YUV444P,
            (int)SwsFlags.SWS_POINT, null, null, null);
        if (_swsCtx == null)
            throw new InvalidOperationException("Failed to create sws context (BGRA→YUV444P)");

        _width = width;
        _height = height;
        _pts = 0;

        _logger.LogInformation("FFmpeg encoder initialized: {W}x{H} @ {Fps}fps, CRF {Crf}, VBV cap {MaxBitrate}Mbps, superfast high444, threads=2, aq=1.2, deblock=-2:-2, psy-rd=1.0:0.15",
            width, height, fps, crf, maxBitrate / 1_000_000.0);
    }

    /// <summary>
    /// Reinitialize encoder if dimensions changed.
    /// Considers viewer-requested resolution for server-side downscale.
    /// </summary>
    public void ReinitializeIfNeeded(int captureWidth, int captureHeight)
    {
        _captureWidth = captureWidth;
        _captureHeight = captureHeight;

        // Determine encode resolution (viewer-requested or capture)
        var (encW, encH) = ComputeEncodeResolution(captureWidth, captureHeight);

        if (encW == _width && encH == _height) return;

        _logger.LogInformation("Encoder resolution changed: {OldW}x{OldH} → {NewW}x{NewH} (capture: {CW}x{CH})",
            _width, _height, encW, encH, captureWidth, captureHeight);

        Cleanup();
        Initialize(encW, encH);

        // Rebuild downscale context if encode != capture
        RebuildDownscaleContext(captureWidth, captureHeight, encW, encH);
    }

    /// <summary>
    /// Set viewer-requested encode resolution. Encoder will downscale BGRA using Lanczos
    /// in RGB-space before encoding. Pass (0, 0) to disable and use capture resolution.
    /// </summary>
    public void SetRequestedResolution(int width, int height)
    {
        if (width == _requestedWidth && height == _requestedHeight) return;

        _logger.LogInformation("Viewer requested resolution: {W}x{H} (was {OldW}x{OldH})",
            width, height, _requestedWidth, _requestedHeight);

        _requestedWidth = width;
        _requestedHeight = height;
        // Reinit deferred to next EncodeFrame call (capture thread owns all FFmpeg state)
    }

    /// <summary>
    /// Compute encode resolution from capture dimensions and viewer request.
    /// Maintains aspect ratio, ensures 8-aligned dimensions.
    /// </summary>
    private (int w, int h) ComputeEncodeResolution(int captureW, int captureH)
    {
        if (_requestedWidth <= 0 || _requestedHeight <= 0)
            return (captureW, captureH);

        // Only downscale, never upscale
        if (_requestedWidth >= captureW && _requestedHeight >= captureH)
            return (captureW, captureH);

        // Fit capture aspect ratio into requested dimensions
        double captureAspect = (double)captureW / captureH;
        double requestAspect = (double)_requestedWidth / _requestedHeight;

        int encW, encH;
        if (captureAspect > requestAspect)
        {
            // Capture is wider — fit by width
            encW = _requestedWidth;
            encH = (int)(encW / captureAspect);
        }
        else
        {
            // Capture is taller — fit by height
            encH = _requestedHeight;
            encW = (int)(encH * captureAspect);
        }

        // Align to 8 — sws_scale SIMD (SSE/AVX) processes 8 pixels at a time,
        // non-8-aligned widths cause right-side blur/artifacts
        encW &= ~7;
        encH &= ~7;

        // Minimum encode resolution
        if (encW < 320) encW = 320;
        if (encH < 240) encH = 240;

        return (encW, encH);
    }

    /// <summary>
    /// Create or update the BGRA downscale context (Lanczos, RGB-space) for server-side resolution matching.
    /// Two-step pipeline: BGRA→BGRA Lanczos preserves text sharpness in RGB-space,
    /// then BGRA→YUV444P converts without resize.
    /// </summary>
    private void RebuildDownscaleContext(int captureW, int captureH, int encodeW, int encodeH)
    {
        // Free old downscale context
        if (_downscaleCtx != null)
        {
            ffmpeg.sws_freeContext(_downscaleCtx);
            _downscaleCtx = null;
        }
        _downscaleBuffer = null;

        // No downscale needed if capture == encode
        if (captureW == encodeW && captureH == encodeH) return;

        // BGR0 → BGR0 downscale using Bicubic (uniform quality — operates in RGB-space)
        // BGR0: padding byte ignored by swscale, avoids alpha interference in filter kernel
        _downscaleCtx = ffmpeg.sws_getContext(
            captureW, captureH, AVPixelFormat.AV_PIX_FMT_BGR0,
            encodeW, encodeH, AVPixelFormat.AV_PIX_FMT_BGR0,
            (int)(SwsFlags.SWS_BICUBIC | SwsFlags.SWS_ACCURATE_RND), null, null, null);

        if (_downscaleCtx == null)
        {
            _logger.LogWarning("Failed to create Lanczos downscale context {CW}x{CH} → {EW}x{EH}",
                captureW, captureH, encodeW, encodeH);
            return;
        }

        _downscaleBuffer = new byte[encodeW * encodeH * 4]; // BGRA = 4 bytes/pixel
        _logger.LogInformation("Server-side downscale active: {CW}x{CH} → {EW}x{EH} (Bicubic RGB-space)",
            captureW, captureH, encodeW, encodeH);
    }

    /// <summary>
    /// Encode one BGRA frame. Returns H.264 NAL units, or null if encoder buffered the frame.
    /// The returned byte array is reused — caller must copy before the next EncodeFrame call.
    /// If server-side downscale is active, the frame is downscaled in RGB-space before encoding.
    /// </summary>
    public (byte[] data, int length)? EncodeFrame(byte[] bgraData, int stride, int captureWidth = 0, int captureHeight = 0)
    {
        if (_codecCtx == null || _frame == null || _packet == null || _swsCtx == null)
            return null;

        // Handle resolution changes (capture size changed OR viewer requested new size)
        if (captureWidth > 0 && captureHeight > 0)
        {
            bool captureChanged = captureWidth != _captureWidth || captureHeight != _captureHeight;
            if (captureChanged)
            {
                _captureWidth = captureWidth;
                _captureHeight = captureHeight;
            }

            var (encW, encH) = ComputeEncodeResolution(captureWidth, captureHeight);
            if (encW != _width || encH != _height)
            {
                _logger.LogInformation("Re-initializing encoder for new resolution: {W}x{H} (capture: {CW}x{CH})",
                    encW, encH, captureWidth, captureHeight);
                Cleanup();
                Initialize(encW, encH);
                RebuildDownscaleContext(captureWidth, captureHeight, encW, encH);
            }
            else if (captureChanged && _downscaleCtx != null)
            {
                // Same encode size but capture changed — rebuild downscale
                RebuildDownscaleContext(captureWidth, captureHeight, encW, encH);
            }
        }

        byte[] frameData = bgraData;
        int frameStride = stride;

        // Step 1: Server-side downscale if active (BGRA → BGRA, Lanczos in RGB-space)
        if (_downscaleCtx != null && _downscaleBuffer != null)
        {
            fixed (byte* srcPtr = bgraData)
            fixed (byte* dstPtr = _downscaleBuffer)
            {
                var srcSlice = new byte_ptrArray4 { [0] = srcPtr };
                var srcStride = new int_array4 { [0] = stride };
                var dstSlice = new byte_ptrArray4 { [0] = dstPtr };
                var dstStride = new int_array4 { [0] = _width * 4 };

                ffmpeg.sws_scale(_downscaleCtx,
                    srcSlice, srcStride, 0, _captureHeight,
                    dstSlice, dstStride);
            }
            frameData = _downscaleBuffer;
            frameStride = _width * 4;
        }

        ffmpeg.av_frame_make_writable(_frame);

        // Step 2: Convert BGRA → YUV444P (at encode resolution, no resize)
        fixed (byte* srcPtr = frameData)
        {
            var srcSlice = new byte_ptrArray4 { [0] = srcPtr };
            var srcStride = new int_array4 { [0] = frameStride };

            ffmpeg.sws_scale(_swsCtx,
                srcSlice, srcStride, 0, _height,
                _frame->data, _frame->linesize);
        }

        _frame->pts = _pts++;

        if (_forceKeyframe)
        {
            _frame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
            _frame->flags |= ffmpeg.AV_FRAME_FLAG_KEY;
            _forceKeyframe = false;
        }
        else
        {
            _frame->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
            _frame->flags &= ~ffmpeg.AV_FRAME_FLAG_KEY;
        }

        var ret = ffmpeg.avcodec_send_frame(_codecCtx, _frame);
        if (ret < 0) return null;

        ret = ffmpeg.avcodec_receive_packet(_codecCtx, _packet);
        if (ret < 0) return null; // EAGAIN or error — encoder needs more input

        var size = _packet->size;
        if (_outputBuffer == null || _outputBuffer.Length < size)
            _outputBuffer = new byte[size + 4096]; // Over-allocate to reduce re-allocs

        Marshal.Copy((IntPtr)_packet->data, _outputBuffer, 0, size);

        ffmpeg.av_packet_unref(_packet);
        return (_outputBuffer, size);
    }

    /// <summary>Force the next frame to be a keyframe (I-frame).</summary>
    public void ForceKeyframe() => _forceKeyframe = true;

    /// <summary>Change VBV max rate cap dynamically (CRF mode — adjusts transport ceiling, not quality).</summary>
    public void SetMaxBitrate(long bitsPerSecond)
    {
        if (_codecCtx != null)
            _codecCtx->rc_max_rate = bitsPerSecond;
    }

    /// <summary>
    /// Static helper: downscale a BGRA frame using sws_scale Lanczos.
    /// Used by lossless settle to match viewer display resolution.
    /// Caller owns the returned buffer.
    /// </summary>
    public static byte[] DownscaleBgra(byte[] srcBgra, int srcW, int srcH, int srcStride, int dstW, int dstH)
    {
        FFmpegInit.EnsureInitialized();

        var ctx = ffmpeg.sws_getContext(
            srcW, srcH, AVPixelFormat.AV_PIX_FMT_BGRA,
            dstW, dstH, AVPixelFormat.AV_PIX_FMT_BGRA,
            (int)(SwsFlags.SWS_BICUBIC | SwsFlags.SWS_ACCURATE_RND), null, null, null);

        if (ctx == null)
            throw new InvalidOperationException("Failed to create sws_scale context for lossless downscale");

        try
        {
            var dstStridePx = dstW * 4;
            var dst = new byte[dstStridePx * dstH];

            fixed (byte* srcPtr = srcBgra)
            fixed (byte* dstPtr = dst)
            {
                var srcSlice = new byte_ptrArray4 { [0] = srcPtr };
                var srcStrides = new int_array4 { [0] = srcStride };
                var dstSlice = new byte_ptrArray4 { [0] = dstPtr };
                var dstStrides = new int_array4 { [0] = dstStridePx };

                ffmpeg.sws_scale(ctx,
                    srcSlice, srcStrides, 0, srcH,
                    dstSlice, dstStrides);
            }

            return dst;
        }
        finally
        {
            ffmpeg.sws_freeContext(ctx);
        }
    }

    private void Cleanup()
    {
        if (_downscaleCtx != null) { ffmpeg.sws_freeContext(_downscaleCtx); _downscaleCtx = null; }
        _downscaleBuffer = null;
        if (_swsCtx != null) { ffmpeg.sws_freeContext(_swsCtx); _swsCtx = null; }
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_packet != null) { var p = _packet; ffmpeg.av_packet_free(&p); _packet = null; }
        if (_codecCtx != null) { var c = _codecCtx; ffmpeg.avcodec_free_context(&c); _codecCtx = null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cleanup();
    }
}

// SwsFlags enum — FFmpeg.AutoGen doesn't export these as an enum
internal enum SwsFlags
{
    SWS_FAST_BILINEAR = 1,
    SWS_BILINEAR = 2,
    SWS_BICUBIC = 4,
    SWS_POINT = 0x10,
    SWS_LANCZOS = 0x200,
    SWS_ACCURATE_RND = 0x40000,
}
