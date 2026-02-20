using Microsoft.Extensions.Logging;
using SteamViewer.Client.Core.Capture;
using SteamViewer.Common.Protocol;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DXGIFactory = Vortice.DXGI.DXGI;

namespace SteamViewer.Platform.Windows.ScreenCapture;

/// <summary>
/// Windows screen capture using DXGI Desktop Duplication API.
/// GPU-accelerated for high performance. Provides both single-frame capture (IScreenCapture)
/// and a continuous capture loop with JPEG encoding for the canvas bridge pipeline.
///
/// Canvas bridge flow:
///   DXGI AcquireNextFrame → BGRA staging texture → JPEG encode
///   → OnFrameCaptured event → JSInterop → hidden canvas → captureStream → WebRTC
/// </summary>
public sealed class DxgiScreenCapture : IScreenCapture
{
    private readonly ILogger<DxgiScreenCapture> _logger;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingTexture;
    private bool _disposed;
    private bool _isCapturing;
    private int _width;
    private int _height;
    private int _monitorX; // Monitor left edge in virtual desktop coords
    private int _monitorY; // Monitor top edge in virtual desktop coords
    private string? _monitorId;

    // Capture loop fields
    private Thread? _captureThread;
    private volatile bool _stopRequested;
    private uint _currentOutputIndex;

    // Reusable frame buffer — avoids ~8MB allocation per frame (240MB/s GC pressure at 30fps)
    private byte[]? _frameBuffer;

    public DxgiScreenCapture(ILogger<DxgiScreenCapture> logger)
    {
        _logger = logger;
    }

    public (int Width, int Height) Resolution => (_width, _height);

    public bool IsCapturing => _isCapturing;

    /// <summary>Whether to composite the host cursor onto captured frames. Default true.</summary>
    public bool ShowCursor { get; set; } = true;

    /// <summary>Raised when a JPEG frame is captured. Parameters: (jpegData, width, height).</summary>
    public event Action<byte[], int, int>? OnFrameCaptured;

    /// <summary>Raised with raw BGRA pixel data (no JPEG encode). Parameters: (bgraData, width, height, stride).</summary>
    public event Action<byte[], int, int, int>? OnRawFrameCaptured;

    #region Capture Loop (high-level API for canvas bridge)

    /// <summary>
    /// Start continuous DXGI capture on a dedicated thread.
    /// Captures at ~30 FPS, JPEG-encodes each frame, fires OnFrameCaptured.
    /// Auto-recovers on DXGI_ERROR_ACCESS_LOST (lock screen, display mode change).
    /// </summary>
    /// <param name="outputIndex">DXGI output index (0 = primary monitor)</param>
    public void StartCaptureLoop(uint outputIndex)
    {
        if (_captureThread != null)
        {
            _logger.LogWarning("DXGI capture loop already running");
            return;
        }

        _stopRequested = false;
        _currentOutputIndex = outputIndex;
        _captureThread = new Thread(CaptureLoop)
        {
            Name = "DxgiCaptureLoop",
            IsBackground = true
        };
        _captureThread.Start();
        _logger.LogInformation("DXGI capture loop started for output {Output}", outputIndex);
    }

    /// <summary>
    /// Stop the capture loop and wait for the thread to finish.
    /// </summary>
    public void StopCaptureLoop()
    {
        if (_captureThread == null) return;

        _stopRequested = true;
        _captureThread.Join(5000);
        _captureThread = null;
        _logger.LogInformation("DXGI capture loop stopped");
    }

    private void CaptureLoop()
    {
        try
        {
            CaptureLoopInner();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FATAL: DXGI capture loop crashed");
        }
    }

    /// <summary>
    /// Main capture loop. Initializes DXGI, captures frames, JPEG-encodes,
    /// fires events. Handles ACCESS_LOST by reinitializing.
    /// Pattern modeled after SecureDesktopCapture (proven).
    /// </summary>
    private void CaptureLoopInner()
    {
        // Initialize DXGI on this thread
        try
        {
            InitializeAsync(_currentOutputIndex).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize DXGI capture for output {Output}", _currentOutputIndex);
            return;
        }

        // JPEG encoder setup (quality 75% — higher than SD's 65% since this is primary desktop)
        // Pattern from SecureDesktopCapture.cs
        var jpegEncoder = GetJpegEncoder();
        if (jpegEncoder == null)
        {
            _logger.LogError("JPEG encoder not found");
            return;
        }

        var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 75L);
        var jpegStream = new MemoryStream(512 * 1024); // Pre-allocate 512KB, reuse across frames
        var frameCount = 0;
        var consecutiveErrors = 0;
        const int targetIntervalMs = 33; // ~30 FPS target
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            while (!_stopRequested)
            {
                sw.Restart();

                try
                {
                    var frame = CaptureFrameAsync().GetAwaiter().GetResult();

                    if (frame == null)
                    {
                        // Desktop unchanged since last frame — brief sleep and retry
                        // DXGI returns null when no pixels changed (efficient)
                        Thread.Sleep(5);
                        continue;
                    }

                    consecutiveErrors = 0;

                    // JPEG encode BGRA frame and fire event
                    using (frame)
                    {
                        EncodeAndFireFrame(frame, jpegEncoder, encoderParams, jpegStream, ref frameCount);
                    }

                    // Adaptive sleep — maintain ~30 FPS regardless of encode time
                    var elapsed = (int)sw.ElapsedMilliseconds;
                    var sleepMs = targetIntervalMs - elapsed;
                    if (sleepMs > 1)
                        Thread.Sleep(sleepMs);
                }
                catch (InvalidOperationException ex) when (
                    ex.Message.Contains("access lost", StringComparison.OrdinalIgnoreCase))
                {
                    // DXGI_ERROR_ACCESS_LOST — display mode change, hot-plug, lock screen
                    // Must reinitialize the output duplication
                    consecutiveErrors++;
                    _logger.LogWarning("DXGI access lost (attempt {Count}), reinitializing...", consecutiveErrors);

                    if (consecutiveErrors > 10)
                    {
                        _logger.LogError("Too many consecutive DXGI errors, stopping capture");
                        break;
                    }

                    // Brief wait then reinitialize
                    Thread.Sleep(500);

                    try
                    {
                        Reinitialize();
                    }
                    catch (Exception reinitEx)
                    {
                        _logger.LogWarning(reinitEx, "DXGI reinitialize failed, will retry");
                        Thread.Sleep(1000);
                    }
                }
                catch (Exception ex)
                {
                    consecutiveErrors++;
                    _logger.LogWarning(ex, "DXGI capture loop error (attempt {Count})", consecutiveErrors);

                    if (consecutiveErrors > 10)
                    {
                        _logger.LogError("Too many consecutive errors, stopping DXGI capture");
                        break;
                    }

                    Thread.Sleep(100);
                }
            }
        }
        finally
        {
            encoderParams.Dispose();
            jpegStream.Dispose();
            ReleaseResources();
            _logger.LogInformation("DXGI capture loop exited (frames: {Count})", frameCount);
        }
    }

    /// <summary>
    /// JPEG-encode a captured BGRA frame and fire the OnFrameCaptured event.
    /// Reuses the MemoryStream across frames to reduce GC pressure at 30 FPS.
    /// </summary>
    private void EncodeAndFireFrame(CapturedFrame frame, ImageCodecInfo jpegEncoder,
        EncoderParameters encoderParams, MemoryStream jpegStream, ref int frameCount)
    {
        unsafe
        {
            fixed (byte* ptr = frame.Data)
            {
                // BGRA from DXGI maps directly to Format32bppArgb (same byte layout on little-endian)
                using var bitmap = new Bitmap(frame.Width, frame.Height, frame.Stride,
                    PixelFormat.Format32bppArgb, (IntPtr)ptr);

                // Composite mouse cursor onto frame before encoding
                if (ShowCursor)
                    DrawCursorOnBitmap(bitmap);

                frameCount++;

                // Raw BGRA path — skip JPEG encode entirely (saves 15-30ms per frame)
                // Cursor is composited via DrawIconEx which writes directly into frame.Data
                if (OnRawFrameCaptured != null)
                {
                    if (frameCount <= 3 || frameCount % 300 == 0)
                    {
                        _logger.LogDebug("DXGI raw frame #{Count}: {Size}b, {W}x{H}, stride={Stride}",
                            frameCount, frame.Data.Length, frame.Width, frame.Height, frame.Stride);
                    }
                    OnRawFrameCaptured.Invoke(frame.Data, frame.Width, frame.Height, frame.Stride);
                    return;
                }

                // Fallback: JPEG encode for base64 path
                jpegStream.SetLength(0);
                bitmap.Save(jpegStream, jpegEncoder, encoderParams);
                var jpegData = jpegStream.ToArray();

                if (frameCount <= 3 || frameCount % 300 == 0)
                {
                    _logger.LogDebug("DXGI frame #{Count}: {Size}b, {W}x{H}",
                        frameCount, jpegData.Length, frame.Width, frame.Height);
                }

                OnFrameCaptured?.Invoke(jpegData, frame.Width, frame.Height);
            }
        }
    }

    /// <summary>
    /// Release and reinitialize DXGI resources on the same output.
    /// Called on DXGI_ERROR_ACCESS_LOST (display mode change, hot-plug, lock screen).
    /// </summary>
    private void Reinitialize()
    {
        ReleaseResources();
        InitializeAsync(_currentOutputIndex).GetAwaiter().GetResult();
        _logger.LogInformation("DXGI reinitialized on output {Output} ({W}x{H})",
            _currentOutputIndex, _width, _height);
    }

    /// <summary>
    /// Release all DXGI/D3D11 resources without disposing the outer object.
    /// Safe to call multiple times (null checks).
    /// </summary>
    private void ReleaseResources()
    {
        _isCapturing = false;

        _stagingTexture?.Dispose();
        _stagingTexture = null;

        _duplication?.Dispose();
        _duplication = null;

        _context?.Dispose();
        _context = null;

        _device?.Dispose();
        _device = null;
    }

    #endregion

    #region Monitor Enumeration

    /// <summary>
    /// Enumerate all DXGI outputs (monitors) with names and bounds.
    /// Uses IDXGIOutput.Description.DesktopCoordinates for exact monitor geometry.
    /// Can be called without an active capture session.
    /// </summary>
    public static List<MonitorInfo> EnumerateMonitors(ILogger? logger = null)
    {
        var monitors = new List<MonitorInfo>();

        try
        {
            using var factory = DXGIFactory.CreateDXGIFactory1<IDXGIFactory1>();
            uint adapterId = 0;

            while (factory.EnumAdapters1((int)adapterId, out var adapter).Success && adapter != null)
            {
                using (adapter)
                {
                    uint outputId = 0;
                    while (adapter.EnumOutputs((int)outputId, out var output).Success && output != null)
                    {
                        using (output)
                        {
                            var desc = output.Description;
                            var rect = desc.DesktopCoordinates;
                            var name = new string(desc.DeviceName).TrimEnd('\0');
                            var width = (uint)(rect.Right - rect.Left);
                            var height = (uint)(rect.Bottom - rect.Top);
                            // Primary monitor is at (0,0) in Windows virtual desktop coordinates
                            var isPrimary = rect.Left == 0 && rect.Top == 0;

                            monitors.Add(new MonitorInfo(
                                Id: outputId + adapterId * 100, // Unique across adapters
                                Name: name,
                                Width: width,
                                Height: height,
                                X: rect.Left,
                                Y: rect.Top,
                                IsPrimary: isPrimary
                            ));

                            logger?.LogDebug("DXGI monitor: {Name} ({W}x{H}) at ({X},{Y}){Primary}",
                                name, width, height, rect.Left, rect.Top,
                                isPrimary ? " [PRIMARY]" : "");
                        }
                        outputId++;
                    }
                }
                adapterId++;
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to enumerate DXGI monitors");
        }

        return monitors;
    }

    #endregion

    #region IScreenCapture Implementation (single-frame API, used internally by capture loop)

    public async Task InitializeAsync(uint monitorId, CancellationToken cancellationToken = default)
    {
        if (_isCapturing)
        {
            throw new InvalidOperationException("Already capturing");
        }

        _logger.LogInformation("Initializing DXGI screen capture for monitor {MonitorId}", monitorId);

        // Parse monitor ID - format is "adapterN_outputM" but we accept numeric for simple cases
        var (adapterIndex, outputIndex) = ParseMonitorId(monitorId);

        // Create DXGI factory
        using var factory = DXGIFactory.CreateDXGIFactory1<IDXGIFactory1>();

        // Get the specified adapter
        var adapterResult = factory.EnumAdapters1((int)adapterIndex, out var adapter);
        if (adapterResult.Failure || adapter == null)
        {
            throw new InvalidOperationException($"Failed to get DXGI adapter {adapterIndex}");
        }
        using var adapterDisposer = adapter;

        // Get the specified output
        var outputResult = adapter.EnumOutputs((int)outputIndex, out var output);
        if (outputResult.Failure || output == null)
        {
            throw new InvalidOperationException($"Failed to get DXGI output {outputIndex}");
        }
        using var outputDisposer = output;

        // Get output description
        var desc = output.Description;
        var rect = desc.DesktopCoordinates;
        _width = rect.Right - rect.Left;
        _height = rect.Bottom - rect.Top;
        _monitorX = rect.Left;
        _monitorY = rect.Top;
        _monitorId = $"adapter{adapterIndex}_output{outputIndex}";

        _logger.LogInformation("Monitor: {Name}, Resolution: {Width}x{Height}",
            new string(desc.DeviceName).TrimEnd('\0'), _width, _height);

        // Create D3D11 device on this adapter
        var featureLevels = new[] { FeatureLevel.Level_11_0 };
        var createResult = D3D11.D3D11CreateDevice(
            adapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out _device,
            out var featureLevel,
            out _context);

        if (createResult.Failure || _device == null || _context == null)
        {
            throw new InvalidOperationException("Failed to create D3D11 device");
        }

        _logger.LogDebug("Created D3D11 device with feature level {FeatureLevel}", featureLevel);

        // Get IDXGIOutput1 for desktop duplication
        using var output1 = output.QueryInterface<IDXGIOutput1>();

        // Create desktop duplication
        try
        {
            _duplication = output1.DuplicateOutput(_device);
        }
        catch (SharpGenException ex)
        {
            throw new InvalidOperationException($"Failed to create desktop duplication: {ex.Message}", ex);
        }

        if (_duplication == null)
        {
            throw new InvalidOperationException("Failed to create desktop duplication: result was null");
        }

        // Create staging texture for CPU access
        var stagingDesc = new Texture2DDescription
        {
            Width = _width,
            Height = _height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        };

        _stagingTexture = _device.CreateTexture2D(stagingDesc);

        _isCapturing = true;
        _logger.LogInformation("DXGI desktop duplication initialized for {MonitorId} ({Width}x{Height})",
            _monitorId, _width, _height);

        await Task.CompletedTask;
    }

    public async Task<CapturedFrame?> CaptureFrameAsync(CancellationToken cancellationToken = default)
    {
        if (!_isCapturing || _duplication == null || _device == null || _context == null || _stagingTexture == null)
        {
            throw new InvalidOperationException("Not capturing");
        }

        try
        {
            // Try to acquire next frame (0ms timeout for non-blocking)
            var acquireResult = _duplication.AcquireNextFrame(0, out var frameInfo, out var desktopResource);

            if (acquireResult == Result.WaitTimeout || acquireResult == DxgiErrors.ErrorWaitTimeout)
            {
                // No new frame available (desktop unchanged)
                return null;
            }

            if (acquireResult == DxgiErrors.ErrorAccessLost)
            {
                _logger.LogWarning("Desktop duplication access lost, needs reinitialize");
                throw new InvalidOperationException("Desktop duplication access lost");
            }

            if (acquireResult.Failure)
            {
                _logger.LogWarning("Failed to acquire next frame: {Error}", acquireResult.Description);
                return null;
            }

            try
            {
                // Query for ID3D11Texture2D
                using var texture = desktopResource!.QueryInterface<ID3D11Texture2D>();

                // Copy to staging texture
                _context.CopyResource(_stagingTexture, texture);

                // Map staging texture to read pixels
                var mappedResource = _context.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

                try
                {
                    var stride = mappedResource.RowPitch;
                    var dataSize = _height * stride;

                    // Reuse frame buffer across frames — avoids ~8MB alloc per frame
                    if (_frameBuffer == null || _frameBuffer.Length < dataSize)
                        _frameBuffer = new byte[dataSize];

                    // Copy pixel data — single block copy when stride matches, row-by-row otherwise
                    unsafe
                    {
                        var srcPtr = (byte*)mappedResource.DataPointer;
                        var expectedStride = _width * 4; // BGRA = 4 bytes per pixel
                        if (stride == expectedStride)
                        {
                            // Stride matches width — single memcpy (fastest)
                            Marshal.Copy((IntPtr)srcPtr, _frameBuffer, 0, dataSize);
                        }
                        else
                        {
                            for (var y = 0; y < _height; y++)
                            {
                                Marshal.Copy((IntPtr)(srcPtr + y * stride), _frameBuffer, y * stride, stride);
                            }
                        }
                    }

                    return new CapturedFrame
                    {
                        Data = _frameBuffer,
                        Width = _width,
                        Height = _height,
                        Stride = stride,
                        Timestamp = DateTimeOffset.UtcNow
                    };
                }
                finally
                {
                    _context.Unmap(_stagingTexture, 0);
                }
            }
            finally
            {
                desktopResource?.Dispose();
                _duplication.ReleaseFrame();
            }
        }
        catch (SharpGen.Runtime.SharpGenException ex) when (ex.HResult == DxgiErrors.ErrorAccessLost.Code)
        {
            _logger.LogWarning("Desktop duplication access lost");
            throw new InvalidOperationException("Desktop duplication access lost", ex);
        }
    }

    private static (uint AdapterIndex, uint OutputIndex) ParseMonitorId(uint monitorId)
    {
        // For IDs >= 100, decode adapter and output index (from EnumerateMonitors)
        // For IDs < 100, treat as output index on adapter 0
        if (monitorId >= 100)
        {
            return (monitorId / 100, monitorId % 100);
        }
        return (0, monitorId);
    }

    #endregion

    #region Helpers

    private static ImageCodecInfo? GetJpegEncoder()
    {
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
        {
            if (codec.FormatID == ImageFormat.Jpeg.Guid)
                return codec;
        }
        return null;
    }

    #endregion

    #region Cursor Compositing (P/Invoke)

    private const int CURSOR_SHOWING = 0x00000001;
    private const int DI_NORMAL = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
        int cxWidth, int cyHeight, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>
    /// Draw the current mouse cursor onto the bitmap at its correct position
    /// relative to the captured monitor. Uses GDI GetCursorInfo + DrawIconEx
    /// (simpler and more reliable than parsing DXGI pointer shape types).
    /// </summary>
    private void DrawCursorOnBitmap(Bitmap bitmap)
    {
        var ci = new CURSORINFO();
        ci.cbSize = Marshal.SizeOf<CURSORINFO>();

        if (!GetCursorInfo(ref ci))
            return;

        if ((ci.flags & CURSOR_SHOWING) == 0)
            return; // Cursor hidden

        // Convert screen coords to monitor-relative coords
        var cursorX = ci.ptScreenPos.x - _monitorX;
        var cursorY = ci.ptScreenPos.y - _monitorY;

        // Skip if cursor is outside our captured monitor
        if (cursorX < -64 || cursorX > _width + 64 || cursorY < -64 || cursorY > _height + 64)
            return;

        // Get hotspot offset so cursor tip aligns correctly
        if (GetIconInfo(ci.hCursor, out var iconInfo))
        {
            cursorX -= iconInfo.xHotspot;
            cursorY -= iconInfo.yHotspot;

            // Clean up GDI bitmaps from GetIconInfo
            if (iconInfo.hbmMask != IntPtr.Zero)
                DeleteObject(iconInfo.hbmMask);
            if (iconInfo.hbmColor != IntPtr.Zero)
                DeleteObject(iconInfo.hbmColor);
        }

        // Draw cursor onto the bitmap via GDI HDC
        using var graphics = Graphics.FromImage(bitmap);
        var hdc = graphics.GetHdc();
        try
        {
            DrawIconEx(hdc, cursorX, cursorY, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }
    }

    #endregion

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        StopCaptureLoop();
        ReleaseResources();

        _logger.LogDebug("DXGI screen capture disposed");
    }
}

/// <summary>
/// DXGI error codes
/// </summary>
internal static class DxgiErrors
{
    public static readonly Result ErrorAccessLost = new Result(unchecked((int)0x887A0026));
    public static readonly Result ErrorWaitTimeout = new Result(unchecked((int)0x887A0027));
}
