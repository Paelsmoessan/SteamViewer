using Microsoft.Extensions.Logging;
using SharpGen.Runtime;
using SteamViewer.Client.Core.Capture;
using SteamViewer.Common.Protocol;
using System.Drawing.Imaging;
using Vortice.Direct3D11;
using Vortice.DXGI;
using DXGIFactory = Vortice.DXGI.DXGI;

namespace SteamViewer.Platform.Windows.ScreenCapture;

/// <summary>
/// Windows screen capture using DXGI Desktop Duplication API. GPU-accelerated.
/// Provides both single-frame capture (IScreenCapture) and a continuous capture
/// loop with JPEG encoding for the canvas bridge pipeline.
///
/// Canvas bridge flow:
///   DXGI AcquireNextFrame -> BGRA staging texture -> JPEG encode
///   -> OnFrameCaptured event -> JSInterop -> hidden canvas -> captureStream -> WebRTC
///
/// Partial-class layout (concerns split per file):
///   DxgiScreenCapture.cs (this file): lifecycle, capture-loop orchestration,
///       monitor enumeration, Dispose, fields, public surface
///   .Init.cs:    DXGI/D3D11 init + reinit + desktop sync + ReleaseResources
///   .Frame.cs:   single-frame capture (CaptureFrameAsync/Core), pixel copy,
///                ParseMonitorId
///   .Capture.cs: JPEG/raw encode dispatch (EncodeAndFireFrame), ACCESS_LOST
///                recovery (HandleAccessLost), DxgiAccessLostException
///   .Cursor.cs:  cursor compositing + shape detection (Win32 GDI P/Invoke)
/// </summary>
public sealed partial class DxgiScreenCapture : IScreenCapture
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

    // Signal to wake DXGI from SD polling sleep (fired when SD exits)
    private readonly ManualResetEventSlim _desktopAvailableSignal = new(false);

    // Reusable frame buffer - avoids ~8MB allocation per frame (240MB/s GC pressure at 30fps)
    private byte[]? _frameBuffer;

    // Last captured raw frame - exposed for lossless settle snapshot
    private byte[]? _lastRawFrame;
    private int _lastRawWidth;
    private int _lastRawHeight;
    private int _lastRawStride;

    // Cursor shape tracking
    private IntPtr _lastCursorHandle;
    private Dictionary<IntPtr, string>? _standardCursors;

    // Access-lost log de-flooding. AcquireNextFrame returning ACCESS_LOST during Phase 1
    // SD-wait fires at ~250ms cadence; without this counter the log flood is one WARN per
    // attempt for the full SD duration. First fire stays WARN (boundary marker); subsequent
    // fires demote to LogDebug. Resets to 0 when AcquireNextFrame next succeeds (success
    // path in Frame.cs sets it back to 0). Per the gate-logging rule the boundary stays
    // visible while the noise doesn't drown the log.
    private int _consecutiveAccessLostLogs;

    public DxgiScreenCapture(ILogger<DxgiScreenCapture> logger)
    {
        _logger = logger;
    }

    public (int Width, int Height) Resolution => (_width, _height);

    public bool IsCapturing => _isCapturing;

    /// <summary>Whether to composite the host cursor onto captured frames. Default true.</summary>
    public bool ShowCursor { get; set; } = true;

    /// <summary>Last captured BGRA frame data. Null if no frame captured yet.</summary>
    public byte[]? LastRawFrame => _lastRawFrame;
    public int LastRawWidth => _lastRawWidth;
    public int LastRawHeight => _lastRawHeight;
    public int LastRawStride => _lastRawStride;

    /// <summary>Raised when a JPEG frame is captured. Parameters: (jpegData, width, height).</summary>
    public event Action<byte[], int, int>? OnFrameCaptured;

    /// <summary>Raised with raw BGRA pixel data (no JPEG encode). Parameters: (bgraData, width, height, stride).</summary>
    public event Action<byte[], int, int, int>? OnRawFrameCaptured;

    /// <summary>Raised when the host cursor shape changes. Parameter: CSS cursor value (e.g. "default", "text", "pointer").</summary>
    public event Action<string>? OnCursorShapeChanged;

    /// <summary>Raised when AcquireNextFrame reports no desktop change (screen is static).</summary>
    public event Action? OnFrameUnchanged;

    /// <summary>
    /// Signal that the Secure Desktop has exited - wake the DXGI retry loop immediately.
    /// Called from HostSession when OnSecureDesktopStateChanged(false) fires.
    /// </summary>
    public void NotifyDesktopAvailable()
    {
        _desktopAvailableSignal.Set();
    }

    /// <summary>
    /// Start continuous DXGI capture on a dedicated thread. Captures at ~30 FPS,
    /// JPEG-encodes each frame, fires OnFrameCaptured. Auto-recovers on
    /// DXGI_ERROR_ACCESS_LOST (lock screen, display mode change).
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
    /// Main capture loop. Initializes DXGI, captures frames, JPEG-encodes, fires
    /// events. Handles ACCESS_LOST by reinitializing. Pattern modeled after
    /// SecureDesktopCapture (proven).
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

        // JPEG encoder setup (quality 75% - higher than SD's 65% since this is primary desktop).
        // Pattern from SecureDesktopCapture.cs.
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
        var reinitStartTicks = 0L; // Timestamp when reinit attempts began (reset when SD exits)
        const int targetIntervalMs = 33; // ~30 FPS target
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var idleSw = System.Diagnostics.Stopwatch.StartNew(); // Tracks time since last frame fired
        byte[]? lastJpegData = null;
        int lastWidth = 0, lastHeight = 0;
        byte[]? lastRawData = null;
        int lastRawStride = 0;

        // Build HCURSOR -> CSS cursor lookup table (once)
        InitCursorShapeTable();

        try
        {
            while (!_stopRequested)
            {
                sw.Restart();

                try
                {
                    CapturedFrame? frame;
                    try
                    {
                        frame = CaptureFrameCore();
                    }
                    catch (Exception trapEx)
                    {
                        // For DxgiAccessLostException: reuse the _consecutiveAccessLostLogs counter
                        // (incremented by Frame.cs BEFORE rethrowing, so it reflects the current fire).
                        // First fire stays WARN as the boundary marker; subsequent fires demote to
                        // LogDebug to avoid the per-attempt WARN flood during Phase 1 SD-wait.
                        // Other exception types (rare) keep WARN-per-attempt so they're not lost.
                        if (trapEx is DxgiAccessLostException && _consecutiveAccessLostLogs > 1)
                            _logger.LogDebug("[DXGI-TRAP] CaptureFrameCore threw {Type}: {Message} (fire #{Count})",
                                trapEx.GetType().Name, trapEx.Message, _consecutiveAccessLostLogs);
                        else
                            _logger.LogWarning("[DXGI-TRAP] CaptureFrameCore threw {Type}: {Message}",
                                trapEx.GetType().Name, trapEx.Message);
                        throw; // Re-throw for the outer catch blocks
                    }

                    if (frame == null)
                    {
                        // Cursor shape can change even when screen is static (hovering over UI)
                        DetectCursorShapeChange();

                        // Notify listeners that screen is unchanged (for lossless settle detection)
                        OnFrameUnchanged?.Invoke();

                        // Screen static - no need to re-encode identical frames.
                        // Lossless settle (QOI) handles pixel-perfect quality on idle.
                        Thread.Sleep(5);
                        continue;
                    }

                    consecutiveErrors = 0;

                    // Detect cursor shape changes (fires event only when HCURSOR changes)
                    DetectCursorShapeChange();

                    // JPEG encode BGRA frame and fire event
                    using (frame)
                    {
                        EncodeAndFireFrame(frame, jpegEncoder, encoderParams, jpegStream, ref frameCount);

                        // Cache last frame for idle keepalive re-fires
                        lastWidth = frame.Width;
                        lastHeight = frame.Height;
                        if (OnRawFrameCaptured != null)
                        {
                            lastRawData = frame.Data; // frame.Data is a byte[] we can hold
                            lastRawStride = frame.Stride;
                        }
                        else if (OnFrameCaptured != null)
                        {
                            lastJpegData = jpegStream.ToArray();
                        }

                        // Expose last frame for lossless settle snapshot
                        _lastRawFrame = frame.Data;
                        _lastRawWidth = frame.Width;
                        _lastRawHeight = frame.Height;
                        _lastRawStride = frame.Stride;
                    }
                    idleSw.Restart();

                    // Adaptive sleep - maintain ~30 FPS regardless of encode time
                    var elapsed = (int)sw.ElapsedMilliseconds;
                    var sleepMs = targetIntervalMs - elapsed;
                    if (sleepMs > 1)
                        Thread.Sleep(sleepMs);
                }
                catch (DxgiAccessLostException ex)
                {
                    HandleAccessLost(ex, ref consecutiveErrors, ref reinitStartTicks);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[DXGI-TRAP] Caught generic Exception: {Type}: {Message}", ex.GetType().Name, ex.Message);
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
    /// Enumerate all DXGI outputs (monitors) with names and bounds. Uses
    /// IDXGIOutput.Description.DesktopCoordinates for exact monitor geometry.
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        StopCaptureLoop();
        _desktopAvailableSignal.Dispose();
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
