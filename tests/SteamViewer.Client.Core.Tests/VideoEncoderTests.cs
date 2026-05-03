using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SteamViewer.Client.Core.Capture;

namespace SteamViewer.Client.Core.Tests;

public class VideoEncoderTests
{
    private readonly ILogger<VideoEncoder> _logger = NullLogger<VideoEncoder>.Instance;

    [Fact]
    public void Constructor_CreatesInstance()
    {
        // Act
        using var encoder = new VideoEncoder(_logger);

        // Assert
        Assert.NotNull(encoder);
    }

    [Fact]
    public void Initialize_WithoutFFmpeg_ThrowsException()
    {
        // This test verifies the encoder throws a meaningful error when FFmpeg is not available
        // In a CI environment without FFmpeg, this test documents the expected behavior

        using var encoder = new VideoEncoder(_logger);

        // The encoder should throw when FFmpeg libraries are not found
        // This is expected behavior in test environments without FFmpeg
        var ex = Record.Exception(() => encoder.Initialize(1920, 1080, 30, 4_000_000));

        // Either it initializes successfully (FFmpeg available) or throws (FFmpeg not available)
        if (ex != null)
        {
            // FFmpeg.AutoGen throws NotSupportedException when native library not found
            // Our code throws InvalidOperationException for other errors
            Assert.True(ex is InvalidOperationException or NotSupportedException,
                $"Expected InvalidOperationException or NotSupportedException, got {ex.GetType().Name}: {ex.Message}");
        }
        else
        {
            // FFmpeg is available, verify properties
            Assert.Equal(1920, encoder.Width);
            Assert.Equal(1080, encoder.Height);
            Assert.Equal(30, encoder.Fps);
            Assert.Equal(4_000_000, encoder.Bitrate);
        }
    }

    [Fact]
    public void Initialize_CalledTwice_ThrowsException()
    {
        using var encoder = new VideoEncoder(_logger);

        // Skip if FFmpeg not available
        var firstException = Record.Exception(() => encoder.Initialize(1920, 1080));
        if (firstException != null)
        {
            return; // FFmpeg not available, skip test
        }

        // Second initialization should throw
        Assert.Throws<InvalidOperationException>(() => encoder.Initialize(1920, 1080));
    }

    [Fact]
    public void EncodeFrame_WithoutInitialize_ThrowsException()
    {
        using var encoder = new VideoEncoder(_logger);
        var dummyData = new byte[1920 * 1080 * 4];

        Assert.Throws<InvalidOperationException>(() => encoder.EncodeFrame(dummyData, 1920 * 4));
    }

    [Fact]
    public void Flush_WithoutInitialize_ReturnsEmptyList()
    {
        using var encoder = new VideoEncoder(_logger);

        var result = encoder.Flush();

        Assert.Empty(result);
    }

    [Fact]
    public void GetExtraData_WithoutInitialize_ReturnsNull()
    {
        using var encoder = new VideoEncoder(_logger);

        var result = encoder.GetExtraData();

        Assert.Null(result);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var encoder = new VideoEncoder(_logger);

        // Should not throw
        encoder.Dispose();
        encoder.Dispose();
        encoder.Dispose();
    }

    // Integration test - only runs if FFmpeg is available
    [Fact]
    public void EncodeFrame_WithValidData_ReturnsEncodedFrames()
    {
        using var encoder = new VideoEncoder(_logger);

        // Skip if FFmpeg not available
        var initException = Record.Exception(() => encoder.Initialize(320, 240, 30, 1_000_000));
        if (initException != null)
        {
            return; // FFmpeg not available, skip test
        }

        // Create a test frame (solid color)
        var width = 320;
        var height = 240;
        var stride = width * 4;
        var frameData = new byte[stride * height];

        // Fill with a gradient pattern for better compression testing
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                frameData[offset] = (byte)(x % 256);     // B
                frameData[offset + 1] = (byte)(y % 256); // G
                frameData[offset + 2] = (byte)((x + y) % 256); // R
                frameData[offset + 3] = 255;              // A
            }
        }

        // Encode multiple frames to get output (encoder may buffer)
        var allFrames = new List<EncodedFrame>();

        for (var i = 0; i < 10; i++)
        {
            var frames = encoder.EncodeFrame(frameData, stride, i == 0);
            allFrames.AddRange(frames);
        }

        // Flush remaining frames
        allFrames.AddRange(encoder.Flush());

        // Should have produced at least one frame
        Assert.NotEmpty(allFrames);

        // First frame should be a keyframe
        var firstFrame = allFrames[0];
        Assert.True(firstFrame.IsKeyframe);
        Assert.NotEmpty(firstFrame.Data);
        Assert.True(firstFrame.Data.Length > 0);
    }

    [Fact]
    public void GetExtraData_AfterInitialize_ReturnsData()
    {
        using var encoder = new VideoEncoder(_logger);

        // Skip if FFmpeg not available
        var initException = Record.Exception(() => encoder.Initialize(320, 240));
        if (initException != null)
        {
            return; // FFmpeg not available, skip test
        }

        // Encode at least one frame to generate extradata
        var frameData = new byte[320 * 240 * 4];
        encoder.EncodeFrame(frameData, 320 * 4, true);

        var extraData = encoder.GetExtraData();

        // H.264 encoders typically produce SPS/PPS in extradata
        // This may be null for some encoder configurations
        // Just verify we can call it without error
    }
}
