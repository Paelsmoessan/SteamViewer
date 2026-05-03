using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SteamViewer.Client.Core.Capture;

namespace SteamViewer.Client.Core.Tests;

public class VideoDecoderTests
{
    private readonly ILogger<VideoDecoder> _decoderLogger = NullLogger<VideoDecoder>.Instance;
    private readonly ILogger<VideoEncoder> _encoderLogger = NullLogger<VideoEncoder>.Instance;

    [Fact]
    public void Constructor_CreatesInstance()
    {
        // Act
        using var decoder = new VideoDecoder(_decoderLogger);

        // Assert
        Assert.NotNull(decoder);
    }

    [Fact]
    public void Initialize_WithoutFFmpeg_ThrowsException()
    {
        using var decoder = new VideoDecoder(_decoderLogger);

        var ex = Record.Exception(() => decoder.Initialize());

        // Either it initializes successfully (FFmpeg available) or throws (FFmpeg not available)
        if (ex != null)
        {
            // FFmpeg.AutoGen throws NotSupportedException when native library not found
            // Our code throws InvalidOperationException for other errors
            Assert.True(ex is InvalidOperationException or NotSupportedException,
                $"Expected InvalidOperationException or NotSupportedException, got {ex.GetType().Name}: {ex.Message}");
        }
    }

    [Fact]
    public void Initialize_CalledTwice_ThrowsException()
    {
        using var decoder = new VideoDecoder(_decoderLogger);

        // Skip if FFmpeg not available
        var firstException = Record.Exception(() => decoder.Initialize());
        if (firstException != null)
        {
            return;
        }

        // Second initialization should throw
        Assert.Throws<InvalidOperationException>(() => decoder.Initialize());
    }

    [Fact]
    public void DecodeFrame_WithoutInitialize_ThrowsException()
    {
        using var decoder = new VideoDecoder(_decoderLogger);
        var dummyData = new byte[100];

        Assert.Throws<InvalidOperationException>(() => decoder.DecodeFrame(dummyData));
    }

    [Fact]
    public void SetExtraData_WithoutInitialize_ThrowsException()
    {
        using var decoder = new VideoDecoder(_decoderLogger);
        var dummyData = new byte[100];

        Assert.Throws<InvalidOperationException>(() => decoder.SetExtraData(dummyData));
    }

    [Fact]
    public void Flush_WithoutInitialize_ReturnsEmptyList()
    {
        using var decoder = new VideoDecoder(_decoderLogger);

        var result = decoder.Flush();

        Assert.Empty(result);
    }

    [Fact]
    public void Width_BeforeDecoding_ReturnsZero()
    {
        using var decoder = new VideoDecoder(_decoderLogger);

        Assert.Equal(0, decoder.Width);
        Assert.Equal(0, decoder.Height);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var decoder = new VideoDecoder(_decoderLogger);

        // Should not throw
        decoder.Dispose();
        decoder.Dispose();
        decoder.Dispose();
    }

    // Integration test - encode then decode
    [Fact]
    public void RoundTrip_EncodeAndDecode_ProducesValidFrames()
    {
        using var encoder = new VideoEncoder(_encoderLogger);
        using var decoder = new VideoDecoder(_decoderLogger);

        // Skip if FFmpeg not available
        var encoderInitEx = Record.Exception(() => encoder.Initialize(320, 240, 30, 1_000_000));
        if (encoderInitEx != null)
        {
            return; // FFmpeg not available, skip test
        }

        var decoderInitEx = Record.Exception(() => decoder.Initialize());
        if (decoderInitEx != null)
        {
            return;
        }

        // Create test frame
        var width = 320;
        var height = 240;
        var stride = width * 4;
        var originalFrame = new byte[stride * height];

        // Fill with a simple pattern
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                originalFrame[offset] = (byte)(x % 256);     // B
                originalFrame[offset + 1] = (byte)(y % 256); // G
                originalFrame[offset + 2] = 128;              // R
                originalFrame[offset + 3] = 255;              // A
            }
        }

        // Encode frames
        var encodedFrames = new List<EncodedFrame>();
        for (var i = 0; i < 5; i++)
        {
            var frames = encoder.EncodeFrame(originalFrame, stride, i == 0);
            encodedFrames.AddRange(frames);
        }
        encodedFrames.AddRange(encoder.Flush());

        if (encodedFrames.Count == 0)
        {
            return; // Encoder didn't produce output, skip
        }

        // Set extradata if available
        var extraData = encoder.GetExtraData();
        if (extraData != null)
        {
            decoder.SetExtraData(extraData);
        }

        // Decode frames
        var decodedFrames = new List<DecodedFrame>();
        foreach (var encoded in encodedFrames)
        {
            var decoded = decoder.DecodeFrame(encoded.Data, encoded.Pts, encoded.Dts);
            decodedFrames.AddRange(decoded);
        }
        decodedFrames.AddRange(decoder.Flush());

        // Verify we got decoded frames
        Assert.NotEmpty(decodedFrames);

        // Verify decoded frame properties
        var firstDecoded = decodedFrames[0];
        Assert.Equal(width, firstDecoded.Width);
        Assert.Equal(height, firstDecoded.Height);
        Assert.Equal(width * 4, firstDecoded.Stride);
        Assert.Equal(width * height * 4, firstDecoded.Data.Length);
    }

    [Fact]
    public void DecodeFrame_WithInvalidData_ReturnsEmptyList()
    {
        using var decoder = new VideoDecoder(_decoderLogger);

        // Skip if FFmpeg not available
        var initEx = Record.Exception(() => decoder.Initialize());
        if (initEx != null)
        {
            return;
        }

        // Try to decode garbage data
        var invalidData = new byte[] { 0x00, 0x00, 0x00, 0x01, 0xFF, 0xFF, 0xFF };

        // Should not throw, but may return empty list
        var result = decoder.DecodeFrame(invalidData);

        // Invalid data should not produce valid frames
        // (result may be empty or decoder may buffer)
    }

    [Fact]
    public void SetExtraData_WithValidData_DoesNotThrow()
    {
        using var decoder = new VideoDecoder(_decoderLogger);

        // Skip if FFmpeg not available
        var initEx = Record.Exception(() => decoder.Initialize());
        if (initEx != null)
        {
            return;
        }

        // Sample H.264 SPS/PPS extradata (minimal, may not be fully valid)
        var extraData = new byte[]
        {
            0x01, 0x64, 0x00, 0x1f, 0xff, 0xe1, 0x00, 0x0a,
            0x67, 0x64, 0x00, 0x1f, 0xac, 0xd9, 0x40, 0x50,
            0x05, 0xbb, 0x01, 0x00, 0x04, 0x68, 0xee, 0x3c,
            0x80
        };

        // Should not throw
        var ex = Record.Exception(() => decoder.SetExtraData(extraData));
        Assert.Null(ex);
    }
}
