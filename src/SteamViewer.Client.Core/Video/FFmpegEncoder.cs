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
/// Source: Sunshine encoder architecture (GPL-3.0, reference only)
/// </summary>
public sealed unsafe class FFmpegEncoder : IDisposable
{
    private readonly ILogger _logger;
    private AVCodecContext* _codecCtx;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwsContext* _swsCtx;
    private int _width;
    private int _height;
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
    public void Initialize(int width, int height, int fps = 30, long bitrate = 20_000_000)
    {
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
        _codecCtx->bit_rate = bitrate;
        _codecCtx->gop_size = fps * 2; // Keyframe every 2 seconds
        _codecCtx->max_b_frames = 0;   // Zero latency = no B-frames
        _codecCtx->thread_count = 4;
        _codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;

        // H.264 High 4:4:4 Predictive profile (value = 244)
        _codecCtx->profile = 244; // FF_PROFILE_H264_HIGH_444_PREDICTIVE

        // Encoder presets for low-latency screen sharing
        ffmpeg.av_opt_set(_codecCtx->priv_data, "preset", "ultrafast", 0);
        ffmpeg.av_opt_set(_codecCtx->priv_data, "tune", "zerolatency", 0);

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

        // BGRA → YUV444P color space converter
        _swsCtx = ffmpeg.sws_getContext(
            width, height, AVPixelFormat.AV_PIX_FMT_BGRA,
            width, height, AVPixelFormat.AV_PIX_FMT_YUV444P,
            ffmpeg.SWS_FAST_BILINEAR, null, null, null);
        if (_swsCtx == null)
            throw new InvalidOperationException("Failed to create sws context (BGRA→YUV444P)");

        _width = width;
        _height = height;
        _pts = 0;

        _logger.LogInformation("FFmpeg encoder initialized: {W}x{H} @ {Fps}fps, {Bitrate}Mbps, libx264 high444",
            width, height, fps, bitrate / 1_000_000.0);
    }

    /// <summary>
    /// Reinitialize encoder if capture dimensions changed.
    /// </summary>
    public void ReinitializeIfNeeded(int width, int height)
    {
        if (width == _width && height == _height) return;

        _logger.LogInformation("Encoder resolution changed: {OldW}x{OldH} → {NewW}x{NewH}",
            _width, _height, width, height);

        Cleanup();
        Initialize(width, height);
    }

    /// <summary>
    /// Encode one BGRA frame. Returns H.264 NAL units, or null if encoder buffered the frame.
    /// The returned byte array is reused — caller must copy before the next EncodeFrame call.
    /// </summary>
    public (byte[] data, int length)? EncodeFrame(byte[] bgraData, int stride)
    {
        if (_codecCtx == null || _frame == null || _packet == null || _swsCtx == null)
            return null;

        ffmpeg.av_frame_make_writable(_frame);

        // Convert BGRA → YUV444P
        fixed (byte* srcPtr = bgraData)
        {
            var srcSlice = new byte_ptrArray4 { [0] = srcPtr };
            var srcStride = new int_array4 { [0] = stride };

            ffmpeg.sws_scale(_swsCtx,
                srcSlice, srcStride, 0, _height,
                _frame->data, _frame->linesize);
        }

        _frame->pts = _pts++;

        if (_forceKeyframe)
        {
            _frame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
            _frame->key_frame = 1;
            _forceKeyframe = false;
        }
        else
        {
            _frame->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
            _frame->key_frame = 0;
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

    /// <summary>Change bitrate dynamically.</summary>
    public void SetBitrate(long bitsPerSecond)
    {
        if (_codecCtx != null)
            _codecCtx->bit_rate = bitsPerSecond;
    }

    private void Cleanup()
    {
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
