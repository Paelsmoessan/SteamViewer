using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;

namespace SteamViewer.Client.Core.Video;

/// <summary>
/// H.264 decoder using FFmpeg. Decodes NAL units to raw BGRA frames for rendering.
/// Handles H.264 High 4:4:4 Predictive profile (YUV444P → BGRA conversion).
///
/// Source: Moonlight-qt FFmpeg decode pipeline (GPL-3.0, reference only)
/// </summary>
public sealed unsafe class FFmpegDecoder : IDisposable
{
    private readonly ILogger _logger;
    private readonly Stopwatch _decodeSw = new();
    private AVCodecContext* _codecCtx;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwsContext* _swsCtx;
    private byte[]? _bgraBuffer;
    private int _width;
    private int _height;
    private bool _disposed;
    private long _frameCount;
    private long _totalBytesDecoded;
    private double _lastDecodeMs;

    public int Width => _width;
    public int Height => _height;
    public bool IsInitialized => _codecCtx != null;

    // Stats
    public long FrameCount => _frameCount;
    public long TotalBytesDecoded => _totalBytesDecoded;
    public double LastDecodeMs => _lastDecodeMs;

    public FFmpegDecoder(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Initialize the decoder. Call before DecodeFrame.
    /// SWS context is lazily created on first decoded frame (needs dimensions from SPS).
    /// </summary>
    public void Initialize()
    {
        FFmpegInit.EnsureInitialized();

        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec == null)
            throw new InvalidOperationException("H.264 decoder not found");

        _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
        if (_codecCtx == null)
            throw new InvalidOperationException("Failed to allocate decoder context");

        _codecCtx->thread_count = 4;
        _codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        // Don't set pix_fmt — let the decoder determine it from the stream

        var ret = ffmpeg.avcodec_open2(_codecCtx, codec, null);
        if (ret < 0)
            throw new InvalidOperationException($"Failed to open H.264 decoder: error {ret}");

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();

        _logger.LogInformation("FFmpeg H.264 decoder initialized");
    }

    /// <summary>
    /// Decode H.264 NAL units. Returns BGRA pixel data, or null if decoder needs more input.
    /// The returned byte array is reused — caller must copy/consume before next DecodeFrame call.
    /// </summary>
    /// <returns>(bgraData, width, height, stride) or null</returns>
    public (byte[] data, int width, int height, int stride)? DecodeFrame(byte[] nalus, int length)
    {
        if (_codecCtx == null || _frame == null || _packet == null)
            return null;

        _decodeSw.Restart();

        // Feed NALUs to decoder
        fixed (byte* naluPtr = nalus)
        {
            _packet->data = naluPtr;
            _packet->size = length;
        }

        var ret = ffmpeg.avcodec_send_packet(_codecCtx, _packet);
        if (ret < 0) return null;

        ret = ffmpeg.avcodec_receive_frame(_codecCtx, _frame);
        if (ret < 0) return null; // EAGAIN or error

        var frameWidth = _frame->width;
        var frameHeight = _frame->height;
        var pixFmt = (AVPixelFormat)_frame->format;

        // Create/recreate SWS context if dimensions or format changed
        if (_swsCtx == null || frameWidth != _width || frameHeight != _height)
        {
            if (_swsCtx != null)
                ffmpeg.sws_freeContext(_swsCtx);

            // SWS_POINT: direct pixel-level color matrix, no filter kernel. Avoids sws_scale phase
            // accumulator drift that causes left-right asymmetry with SWS_LANCZOS/FAST_BILINEAR.
            // Same-size conversion needs no interpolation — SWS_POINT is mathematically correct.
            _swsCtx = ffmpeg.sws_getContext(
                frameWidth, frameHeight, pixFmt,
                frameWidth, frameHeight, AVPixelFormat.AV_PIX_FMT_BGR0,
                (int)SwsFlags.SWS_POINT, null, null, null);

            if (_swsCtx == null)
            {
                _logger.LogError("Failed to create sws context: {PixFmt} → BGRA at {W}x{H}",
                    pixFmt, frameWidth, frameHeight);
                return null;
            }

            _width = frameWidth;
            _height = frameHeight;
            _bgraBuffer = new byte[frameWidth * frameHeight * 4];

            _logger.LogInformation("Decoder output: {W}x{H} {PixFmt} → BGRA", frameWidth, frameHeight, pixFmt);
        }

        // Convert decoded frame → BGRA
        var dstStride = frameWidth * 4;
        fixed (byte* dstPtr = _bgraBuffer)
        {
            var dstSlice = new byte_ptrArray4 { [0] = dstPtr };
            var dstLinesize = new int_array4 { [0] = dstStride };

            ffmpeg.sws_scale(_swsCtx,
                _frame->data, _frame->linesize, 0, frameHeight,
                dstSlice, dstLinesize);
        }

        _decodeSw.Stop();
        _lastDecodeMs = _decodeSw.Elapsed.TotalMilliseconds;
        _frameCount++;
        _totalBytesDecoded += length;

        return (_bgraBuffer!, frameWidth, frameHeight, dstStride);
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
