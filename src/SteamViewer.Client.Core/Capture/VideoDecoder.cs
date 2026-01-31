using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;

namespace SteamViewer.Client.Core.Capture;

/// <summary>
/// H.264 video decoder using FFmpeg.
/// Decodes H.264 NAL units to RGBA frames.
/// </summary>
public sealed unsafe class VideoDecoder : IDisposable
{
    private readonly ILogger<VideoDecoder> _logger;
    private AVCodecContext* _codecContext;
    private AVFrame* _frame;
    private AVFrame* _rgbaFrame;
    private AVPacket* _packet;
    private SwsContext* _swsContext;
    private bool _disposed;
    private bool _initialized;
    private int _currentWidth;
    private int _currentHeight;

    /// <summary>
    /// Current frame width (may change during decoding).
    /// </summary>
    public int Width => _currentWidth;

    /// <summary>
    /// Current frame height (may change during decoding).
    /// </summary>
    public int Height => _currentHeight;

    public VideoDecoder(ILogger<VideoDecoder> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Initialize the decoder.
    /// </summary>
    public void Initialize()
    {
        if (_initialized)
        {
            throw new InvalidOperationException("Decoder already initialized");
        }

        _logger.LogInformation("Initializing H.264 decoder");

        // Find H.264 decoder
        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec == null)
        {
            throw new InvalidOperationException("H.264 decoder not found. Ensure FFmpeg is properly installed.");
        }

        // Allocate codec context
        _codecContext = ffmpeg.avcodec_alloc_context3(codec);
        if (_codecContext == null)
        {
            throw new InvalidOperationException("Failed to allocate codec context");
        }

        // Configure decoder for low latency
        _codecContext->thread_count = Environment.ProcessorCount;
        _codecContext->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _codecContext->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;

        // Open decoder
        var ret = ffmpeg.avcodec_open2(_codecContext, codec, null);
        if (ret < 0)
        {
            throw new InvalidOperationException($"Failed to open decoder: {GetErrorMessage(ret)}");
        }

        // Allocate YUV frame (decoder output)
        _frame = ffmpeg.av_frame_alloc();
        if (_frame == null)
        {
            throw new InvalidOperationException("Failed to allocate frame");
        }

        // Allocate RGBA frame (output)
        _rgbaFrame = ffmpeg.av_frame_alloc();
        if (_rgbaFrame == null)
        {
            throw new InvalidOperationException("Failed to allocate RGBA frame");
        }

        // Allocate packet
        _packet = ffmpeg.av_packet_alloc();
        if (_packet == null)
        {
            throw new InvalidOperationException("Failed to allocate packet");
        }

        _initialized = true;
        _logger.LogInformation("H.264 decoder initialized successfully");
    }

    /// <summary>
    /// Set codec extradata (SPS/PPS for H.264).
    /// Must be called before decoding if extradata is available.
    /// </summary>
    public void SetExtraData(byte[] extraData)
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("Decoder not initialized");
        }

        // Free existing extradata
        if (_codecContext->extradata != null)
        {
            ffmpeg.av_free(_codecContext->extradata);
        }

        // Allocate and copy extradata
        _codecContext->extradata = (byte*)ffmpeg.av_malloc((ulong)extraData.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE);
        _codecContext->extradata_size = extraData.Length;

        Marshal.Copy(extraData, 0, (IntPtr)_codecContext->extradata, extraData.Length);

        // Zero out padding
        for (var i = 0; i < ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE; i++)
        {
            _codecContext->extradata[extraData.Length + i] = 0;
        }

        _logger.LogDebug("Decoder extradata set: {Size} bytes", extraData.Length);
    }

    /// <summary>
    /// Decode H.264 data to RGBA frames.
    /// </summary>
    /// <param name="data">Encoded H.264 data (NAL units)</param>
    /// <param name="pts">Presentation timestamp</param>
    /// <param name="dts">Decoding timestamp</param>
    /// <returns>List of decoded frames (may be empty if decoder is buffering)</returns>
    public List<DecodedFrame> DecodeFrame(byte[] data, long pts = 0, long dts = 0)
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("Decoder not initialized");
        }

        var results = new List<DecodedFrame>();

        fixed (byte* dataPtr = data)
        {
            _packet->data = dataPtr;
            _packet->size = data.Length;
            _packet->pts = pts;
            _packet->dts = dts;

            // Send packet to decoder
            var ret = ffmpeg.avcodec_send_packet(_codecContext, _packet);
            if (ret < 0)
            {
                // EAGAIN means we need to receive frames first
                if (ret != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    _logger.LogWarning("Failed to send packet to decoder: {Error}", GetErrorMessage(ret));
                    return results;
                }
            }

            // Receive decoded frames
            while (true)
            {
                ret = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                {
                    break;
                }
                if (ret < 0)
                {
                    _logger.LogWarning("Failed to receive frame: {Error}", GetErrorMessage(ret));
                    break;
                }

                // Convert YUV420P to RGBA
                var rgbaData = ConvertToRgba(_frame);
                if (rgbaData != null)
                {
                    results.Add(new DecodedFrame
                    {
                        Data = rgbaData,
                        Width = _frame->width,
                        Height = _frame->height,
                        Stride = _frame->width * 4,
                        Pts = _frame->pts,
                        IsKeyframe = (_frame->flags & ffmpeg.AV_FRAME_FLAG_KEY) != 0
                    });
                }

                ffmpeg.av_frame_unref(_frame);
            }
        }

        return results;
    }

    /// <summary>
    /// Flush any remaining frames from the decoder.
    /// </summary>
    public List<DecodedFrame> Flush()
    {
        if (!_initialized)
        {
            return new List<DecodedFrame>();
        }

        var results = new List<DecodedFrame>();

        // Send null packet to flush
        var ret = ffmpeg.avcodec_send_packet(_codecContext, null);
        if (ret < 0 && ret != ffmpeg.AVERROR_EOF)
        {
            _logger.LogWarning("Failed to flush decoder: {Error}", GetErrorMessage(ret));
            return results;
        }

        // Receive remaining frames
        while (true)
        {
            ret = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
            {
                break;
            }
            if (ret < 0)
            {
                break;
            }

            var rgbaData = ConvertToRgba(_frame);
            if (rgbaData != null)
            {
                results.Add(new DecodedFrame
                {
                    Data = rgbaData,
                    Width = _frame->width,
                    Height = _frame->height,
                    Stride = _frame->width * 4,
                    Pts = _frame->pts,
                    IsKeyframe = (_frame->flags & ffmpeg.AV_FRAME_FLAG_KEY) != 0
                });
            }

            ffmpeg.av_frame_unref(_frame);
        }

        return results;
    }

    private byte[]? ConvertToRgba(AVFrame* yuvFrame)
    {
        var width = yuvFrame->width;
        var height = yuvFrame->height;

        // Check if we need to recreate the scaler context
        if (_swsContext == null || width != _currentWidth || height != _currentHeight)
        {
            if (_swsContext != null)
            {
                ffmpeg.sws_freeContext(_swsContext);
            }

            _swsContext = ffmpeg.sws_getContext(
                width, height, (AVPixelFormat)yuvFrame->format,
                width, height, AVPixelFormat.AV_PIX_FMT_RGBA,
                ffmpeg.SWS_FAST_BILINEAR, null, null, null);

            if (_swsContext == null)
            {
                _logger.LogError("Failed to create scaler context for {Width}x{Height}", width, height);
                return null;
            }

            _currentWidth = width;
            _currentHeight = height;

            // Reallocate RGBA frame buffer
            ffmpeg.av_frame_unref(_rgbaFrame);
            _rgbaFrame->format = (int)AVPixelFormat.AV_PIX_FMT_RGBA;
            _rgbaFrame->width = width;
            _rgbaFrame->height = height;

            var ret = ffmpeg.av_frame_get_buffer(_rgbaFrame, 32);
            if (ret < 0)
            {
                _logger.LogError("Failed to allocate RGBA frame buffer: {Error}", GetErrorMessage(ret));
                return null;
            }

            _logger.LogDebug("Decoder resolution changed to {Width}x{Height}", width, height);
        }

        // Make RGBA frame writable
        var makeWritableRet = ffmpeg.av_frame_make_writable(_rgbaFrame);
        if (makeWritableRet < 0)
        {
            _logger.LogWarning("Failed to make RGBA frame writable: {Error}", GetErrorMessage(makeWritableRet));
            return null;
        }

        // Convert YUV to RGBA
        ffmpeg.sws_scale(
            _swsContext,
            yuvFrame->data,
            yuvFrame->linesize,
            0, height,
            _rgbaFrame->data,
            _rgbaFrame->linesize);

        // Copy RGBA data
        var dataSize = width * height * 4;
        var rgbaData = new byte[dataSize];

        // Handle stride if linesize differs from width * 4
        if (_rgbaFrame->linesize[0] == width * 4)
        {
            Marshal.Copy((IntPtr)_rgbaFrame->data[0], rgbaData, 0, dataSize);
        }
        else
        {
            // Copy row by row
            for (var y = 0; y < height; y++)
            {
                var srcOffset = y * _rgbaFrame->linesize[0];
                var dstOffset = y * width * 4;
                Marshal.Copy((IntPtr)(_rgbaFrame->data[0] + srcOffset), rgbaData, dstOffset, width * 4);
            }
        }

        return rgbaData;
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

        if (_rgbaFrame != null)
        {
            fixed (AVFrame** f = &_rgbaFrame)
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

        _logger.LogDebug("H.264 decoder disposed");
    }
}

/// <summary>
/// Represents a decoded video frame.
/// </summary>
public sealed class DecodedFrame
{
    /// <summary>Raw RGBA pixel data</summary>
    public required byte[] Data { get; init; }

    /// <summary>Frame width in pixels</summary>
    public required int Width { get; init; }

    /// <summary>Frame height in pixels</summary>
    public required int Height { get; init; }

    /// <summary>Bytes per row (stride)</summary>
    public required int Stride { get; init; }

    /// <summary>Presentation timestamp</summary>
    public required long Pts { get; init; }

    /// <summary>Whether this is a keyframe</summary>
    public required bool IsKeyframe { get; init; }
}
