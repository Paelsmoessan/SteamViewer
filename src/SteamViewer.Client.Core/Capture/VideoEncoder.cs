using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;

namespace SteamViewer.Client.Core.Capture;

/// <summary>
/// H.264 video encoder using FFmpeg.
/// Encodes BGRA frames to H.264 NAL units.
/// </summary>
public sealed unsafe class VideoEncoder : IDisposable
{
    private readonly ILogger<VideoEncoder> _logger;
    private AVCodecContext* _codecContext;
    private AVFrame* _frame;
    private AVFrame* _yuvFrame;
    private AVPacket* _packet;
    private SwsContext* _swsContext;
    private bool _disposed;
    private bool _initialized;
    private int _frameCount;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Fps { get; private set; }
    public int Bitrate { get; private set; }

    public VideoEncoder(ILogger<VideoEncoder> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Initialize the encoder with the given parameters.
    /// </summary>
    /// <param name="width">Frame width</param>
    /// <param name="height">Frame height</param>
    /// <param name="fps">Frames per second</param>
    /// <param name="bitrate">Target bitrate in bits per second</param>
    public void Initialize(int width, int height, int fps = 30, int bitrate = 4_000_000)
    {
        if (_initialized)
        {
            throw new InvalidOperationException("Encoder already initialized");
        }

        Width = width;
        Height = height;
        Fps = fps;
        Bitrate = bitrate;

        _logger.LogInformation("Initializing H.264 encoder: {Width}x{Height} @ {Fps}fps, {Bitrate}bps",
            width, height, fps, bitrate);

        // Find H.264 encoder
        var codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec == null)
        {
            throw new InvalidOperationException("H.264 encoder not found. Ensure FFmpeg is properly installed.");
        }

        // Allocate codec context
        _codecContext = ffmpeg.avcodec_alloc_context3(codec);
        if (_codecContext == null)
        {
            throw new InvalidOperationException("Failed to allocate codec context");
        }

        // Configure encoder
        _codecContext->width = width;
        _codecContext->height = height;
        _codecContext->time_base = new AVRational { num = 1, den = fps };
        _codecContext->framerate = new AVRational { num = fps, den = 1 };
        _codecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
        _codecContext->bit_rate = bitrate;
        _codecContext->gop_size = fps * 2; // Keyframe every 2 seconds
        _codecContext->max_b_frames = 0; // No B-frames for lower latency
        _codecContext->thread_count = Environment.ProcessorCount;

        // Set preset for low latency
        ffmpeg.av_opt_set(_codecContext->priv_data, "preset", "ultrafast", 0);
        ffmpeg.av_opt_set(_codecContext->priv_data, "tune", "zerolatency", 0);
        ffmpeg.av_opt_set(_codecContext->priv_data, "profile", "baseline", 0);

        // Open encoder
        var ret = ffmpeg.avcodec_open2(_codecContext, codec, null);
        if (ret < 0)
        {
            throw new InvalidOperationException($"Failed to open encoder: {GetErrorMessage(ret)}");
        }

        // Allocate BGRA frame (input)
        _frame = ffmpeg.av_frame_alloc();
        if (_frame == null)
        {
            throw new InvalidOperationException("Failed to allocate input frame");
        }
        _frame->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
        _frame->width = width;
        _frame->height = height;

        // Allocate YUV420P frame (for encoder)
        _yuvFrame = ffmpeg.av_frame_alloc();
        if (_yuvFrame == null)
        {
            throw new InvalidOperationException("Failed to allocate YUV frame");
        }
        _yuvFrame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
        _yuvFrame->width = width;
        _yuvFrame->height = height;

        ret = ffmpeg.av_frame_get_buffer(_yuvFrame, 32);
        if (ret < 0)
        {
            throw new InvalidOperationException($"Failed to allocate YUV frame buffer: {GetErrorMessage(ret)}");
        }

        // Allocate packet
        _packet = ffmpeg.av_packet_alloc();
        if (_packet == null)
        {
            throw new InvalidOperationException("Failed to allocate packet");
        }

        // Create scaler context for BGRA -> YUV420P conversion
        _swsContext = ffmpeg.sws_getContext(
            width, height, AVPixelFormat.AV_PIX_FMT_BGRA,
            width, height, AVPixelFormat.AV_PIX_FMT_YUV420P,
            ffmpeg.SWS_FAST_BILINEAR, null, null, null);

        if (_swsContext == null)
        {
            throw new InvalidOperationException("Failed to create scaler context");
        }

        _initialized = true;
        _logger.LogInformation("H.264 encoder initialized successfully");
    }

    /// <summary>
    /// Encode a BGRA frame to H.264.
    /// </summary>
    /// <param name="bgraData">Raw BGRA pixel data</param>
    /// <param name="stride">Bytes per row</param>
    /// <param name="forceKeyframe">Force this frame to be a keyframe</param>
    /// <returns>List of encoded NAL units (may be empty if encoder is buffering)</returns>
    public List<EncodedFrame> EncodeFrame(byte[] bgraData, int stride, bool forceKeyframe = false)
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("Encoder not initialized");
        }

        var results = new List<EncodedFrame>();

        fixed (byte* srcData = bgraData)
        {
            // Set up source data pointers
            var srcLinesize = new[] { stride };
            var srcSlice = new byte_ptrArray8 { [0] = srcData };

            // Convert BGRA to YUV420P
            ffmpeg.sws_scale(
                _swsContext,
                srcSlice,
                srcLinesize,
                0, Height,
                _yuvFrame->data,
                _yuvFrame->linesize);
        }

        // Set frame properties
        _yuvFrame->pts = _frameCount++;

        if (forceKeyframe)
        {
            _yuvFrame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
        }
        else
        {
            _yuvFrame->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
        }

        // Make frame writable
        var ret = ffmpeg.av_frame_make_writable(_yuvFrame);
        if (ret < 0)
        {
            _logger.LogWarning("Failed to make frame writable: {Error}", GetErrorMessage(ret));
            return results;
        }

        // Send frame to encoder
        ret = ffmpeg.avcodec_send_frame(_codecContext, _yuvFrame);
        if (ret < 0)
        {
            _logger.LogWarning("Failed to send frame to encoder: {Error}", GetErrorMessage(ret));
            return results;
        }

        // Receive encoded packets
        while (true)
        {
            ret = ffmpeg.avcodec_receive_packet(_codecContext, _packet);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
            {
                break;
            }
            if (ret < 0)
            {
                _logger.LogWarning("Failed to receive packet: {Error}", GetErrorMessage(ret));
                break;
            }

            // Copy packet data
            var data = new byte[_packet->size];
            Marshal.Copy((IntPtr)_packet->data, data, 0, _packet->size);

            var isKeyframe = (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
            var timestamp = _packet->pts * 1000 / Fps; // Convert to milliseconds

            results.Add(new EncodedFrame
            {
                Data = data,
                IsKeyframe = isKeyframe,
                Timestamp = timestamp,
                Pts = _packet->pts,
                Dts = _packet->dts
            });

            ffmpeg.av_packet_unref(_packet);
        }

        return results;
    }

    /// <summary>
    /// Flush any remaining frames from the encoder.
    /// </summary>
    public List<EncodedFrame> Flush()
    {
        if (!_initialized)
        {
            return new List<EncodedFrame>();
        }

        var results = new List<EncodedFrame>();

        // Send null frame to flush
        var ret = ffmpeg.avcodec_send_frame(_codecContext, null);
        if (ret < 0)
        {
            _logger.LogWarning("Failed to flush encoder: {Error}", GetErrorMessage(ret));
            return results;
        }

        // Receive remaining packets
        while (true)
        {
            ret = ffmpeg.avcodec_receive_packet(_codecContext, _packet);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
            {
                break;
            }
            if (ret < 0)
            {
                break;
            }

            var data = new byte[_packet->size];
            Marshal.Copy((IntPtr)_packet->data, data, 0, _packet->size);

            var isKeyframe = (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
            var timestamp = _packet->pts * 1000 / Fps;

            results.Add(new EncodedFrame
            {
                Data = data,
                IsKeyframe = isKeyframe,
                Timestamp = timestamp,
                Pts = _packet->pts,
                Dts = _packet->dts
            });

            ffmpeg.av_packet_unref(_packet);
        }

        return results;
    }

    /// <summary>
    /// Get the codec extradata (SPS/PPS for H.264).
    /// </summary>
    public byte[]? GetExtraData()
    {
        if (!_initialized || _codecContext->extradata == null || _codecContext->extradata_size == 0)
        {
            return null;
        }

        var data = new byte[_codecContext->extradata_size];
        Marshal.Copy((IntPtr)_codecContext->extradata, data, 0, _codecContext->extradata_size);
        return data;
    }

    private static string GetErrorMessage(int error)
    {
        var buffer = stackalloc byte[1024];
        ffmpeg.av_strerror(error, buffer, 1024);
        return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"Error {error}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_swsContext != null)
        {
            ffmpeg.sws_freeContext(_swsContext);
            _swsContext = null;
        }

        if (_packet != null)
        {
            fixed (AVPacket** p = &_packet)
            {
                ffmpeg.av_packet_free(p);
            }
        }

        if (_frame != null)
        {
            fixed (AVFrame** f = &_frame)
            {
                ffmpeg.av_frame_free(f);
            }
        }

        if (_yuvFrame != null)
        {
            fixed (AVFrame** f = &_yuvFrame)
            {
                ffmpeg.av_frame_free(f);
            }
        }

        if (_codecContext != null)
        {
            fixed (AVCodecContext** c = &_codecContext)
            {
                ffmpeg.avcodec_free_context(c);
            }
        }

        _logger.LogDebug("H.264 encoder disposed");
    }
}

/// <summary>
/// Represents an encoded video frame.
/// </summary>
public sealed class EncodedFrame
{
    /// <summary>Raw encoded data (NAL units)</summary>
    public required byte[] Data { get; init; }

    /// <summary>Whether this is a keyframe (I-frame)</summary>
    public required bool IsKeyframe { get; init; }

    /// <summary>Timestamp in milliseconds</summary>
    public required long Timestamp { get; init; }

    /// <summary>Presentation timestamp</summary>
    public required long Pts { get; init; }

    /// <summary>Decoding timestamp</summary>
    public required long Dts { get; init; }
}
