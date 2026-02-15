#if WINDOWS
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;

namespace SteamViewer.App.Services;

/// <summary>
/// Zero-copy frame transfer bridge from C# DXGI capture to JS WebRTC pipeline.
/// Uses WebView2 SharedBuffer to bypass base64+JSInterop bottleneck.
///
/// Flow: C# writes JPEG bytes to shared memory → PostSharedBufferToScript →
///       JS receives ArrayBuffer directly (no base64, no string alloc, no IPC marshal).
///
/// Source: WebView2 SharedBuffer API (stable since Edge 107, 2022)
/// Research: .claude/research/binary-frame-transfer/research.md
/// </summary>
public sealed class NativeFrameBridge : IDisposable
{
    private readonly ILogger<NativeFrameBridge> _logger;
    private CoreWebView2? _coreWebView2;
    private DispatcherQueue? _dispatcherQueue;
    private CoreWebView2SharedBuffer? _bufferA;
    private CoreWebView2SharedBuffer? _bufferB;
    private bool _useBufferA = true;
    private bool _initialized;
    private bool _disposed;
    private volatile int _frameInFlight; // 1 = UI thread still processing previous frame

    // 16MB per buffer — enough for raw BGRA up to 2560x1440 (14.7MB)
    private const ulong BufferSize = 16 * 1024 * 1024;

    public NativeFrameBridge(ILogger<NativeFrameBridge> logger)
    {
        _logger = logger;
    }

    public bool IsInitialized => _initialized;

    /// <summary>
    /// Initialize with CoreWebView2 from the host window's BlazorWebView.
    /// Must be called after CoreWebView2 is ready (from BlazorWebViewInitialized handler).
    /// </summary>
    public void Initialize(CoreWebView2 coreWebView2)
    {
        if (_initialized) return;

        try
        {
            _coreWebView2 = coreWebView2;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            var env = coreWebView2.Environment;
            _bufferA = env.CreateSharedBuffer(BufferSize);
            _bufferB = env.CreateSharedBuffer(BufferSize);
            _initialized = true;
            _logger.LogInformation("NativeFrameBridge initialized (2x {Size}KB shared buffers)",
                BufferSize / 1024);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize NativeFrameBridge SharedBuffers");
        }
    }

    /// <summary>
    /// Push a JPEG frame to JS via SharedBuffer. Zero-copy — no base64, no string alloc.
    /// Called from the DXGI capture thread. Thread-safe via buffer alternation.
    /// </summary>
    public void PushFrame(byte[] jpegData, int width, int height, string sessionId)
    {
        if (!_initialized || _coreWebView2 == null || _dispatcherQueue == null) return;

        // Drop frame if UI thread hasn't consumed the previous one yet — prevents queue buildup
        if (Interlocked.CompareExchange(ref _frameInFlight, 1, 0) != 0)
            return;

        var buffer = _useBufferA ? _bufferA : _bufferB;
        if (buffer == null) { Interlocked.Exchange(ref _frameInFlight, 0); return; }

        // Alternate buffers BEFORE dispatching — next frame uses the other buffer
        _useBufferA = !_useBufferA;

        try
        {
            // Write JPEG bytes into shared memory on capture thread (safe — buffer not being read yet)
            using (var winrtStream = buffer.OpenStream())
            using (var stream = winrtStream.AsStreamForWrite())
            {
                stream.Position = 0;
                stream.Write(jpegData, 0, jpegData.Length);
                stream.Flush();
            }

            // PostSharedBufferToScript MUST run on UI thread (CoreWebView2 COM affinity)
            var len = jpegData.Length;
            var w = width;
            var h = height;
            var sid = sessionId;
            var buf = buffer;
            var webview = _coreWebView2;

            _dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var metadata = $"{{\"len\":{len},\"w\":{w},\"h\":{h},\"sid\":\"{sid}\"}}";
                    webview.PostSharedBufferToScript(
                        buf,
                        CoreWebView2SharedBufferAccess.ReadOnly,
                        metadata);
                }
                catch (Exception ex)
                {
                    if (_logCounter++ % 300 == 0)
                    {
                        _logger.LogWarning(ex, "PostSharedBufferToScript error (sample)");
                    }
                }
                finally
                {
                    // Allow next frame from capture thread
                    Interlocked.Exchange(ref _frameInFlight, 0);
                }
            });
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _frameInFlight, 0);
            if (_logCounter++ % 300 == 0)
            {
                _logger.LogWarning(ex, "NativeFrameBridge.PushFrame error (sample)");
            }
        }
    }

    /// <summary>
    /// Push a raw BGRA frame to JS via SharedBuffer. Eliminates JPEG encode+decode entirely.
    /// JS creates VideoFrame directly from BGRA pixels — saves 20-40ms per frame.
    /// </summary>
    public void PushRawFrame(byte[] bgraData, int width, int height, int stride, string sessionId)
    {
        if (!_initialized || _coreWebView2 == null || _dispatcherQueue == null) return;

        // Drop frame if UI thread hasn't consumed the previous one yet
        if (Interlocked.CompareExchange(ref _frameInFlight, 1, 0) != 0)
            return;

        var buffer = _useBufferA ? _bufferA : _bufferB;
        if (buffer == null) { Interlocked.Exchange(ref _frameInFlight, 0); return; }

        _useBufferA = !_useBufferA;

        try
        {
            int rowBytes = width * 4;
            int dataLen = width * height * 4; // Tightly packed size for JS VideoFrame

            using (var winrtStream = buffer.OpenStream())
            using (var stream = winrtStream.AsStreamForWrite())
            {
                stream.Position = 0;
                if (stride == rowBytes)
                {
                    // Fast path: no padding, write entire block
                    stream.Write(bgraData, 0, dataLen);
                }
                else
                {
                    // Slow path: strip stride padding row by row
                    for (int y = 0; y < height; y++)
                        stream.Write(bgraData, y * stride, rowBytes);
                }
                stream.Flush();
            }

            var len = dataLen;
            var w = width;
            var h = height;
            var sid = sessionId;
            var buf = buffer;
            var webview = _coreWebView2;

            _dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var metadata = $"{{\"len\":{len},\"w\":{w},\"h\":{h},\"raw\":true,\"sid\":\"{sid}\"}}";
                    webview.PostSharedBufferToScript(
                        buf,
                        CoreWebView2SharedBufferAccess.ReadOnly,
                        metadata);
                }
                catch (Exception ex)
                {
                    if (_logCounter++ % 300 == 0)
                    {
                        _logger.LogWarning(ex, "PostSharedBufferToScript raw error (sample)");
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _frameInFlight, 0);
                }
            });
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _frameInFlight, 0);
            if (_logCounter++ % 300 == 0)
            {
                _logger.LogWarning(ex, "NativeFrameBridge.PushRawFrame error (sample)");
            }
        }
    }

    private int _logCounter;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _initialized = false;

        _bufferA?.Dispose();
        _bufferB?.Dispose();
        _bufferA = null;
        _bufferB = null;
        _coreWebView2 = null;

        _logger.LogDebug("NativeFrameBridge disposed");
    }
}
#endif
