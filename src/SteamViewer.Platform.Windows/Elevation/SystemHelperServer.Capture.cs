using System.IO.Pipes;
using System.Text.Json;

namespace SteamViewer.Platform.Windows.Elevation;

// Secure Desktop capture + video pipe + notify pipe concerns for SystemHelperServer.
// Capture and Notify were merged into one partition - both pipes are created
// sequentially inside Run, torn down by sibling Cleanup* calls on every exit
// path, and SendNotification (Notify side) is only ever called from capture
// event handlers.
public static partial class SystemHelperServer
{
    private static SecureDesktopCapture? _capture;

    // Video pipe for binary BGRA frames (server -> client)
    private static NamedPipeServerStream? _videoPipeServer;
    private static BinaryWriter? _videoWriter;
    private static readonly object _videoWriteLock = new();
    private static volatile bool _videoConnected;

    // Notify pipe for server-push notifications (server -> client)
    private static NamedPipeServerStream? _notifyPipeServer;
    private static StreamWriter? _notifyWriter;
    private static readonly object _notifyWriteLock = new();
    private static volatile bool _notifyConnected;

    private static int _frameCount;

    private static void CleanupCapture()
    {
        if (_capture != null)
        {
            _capture.OnSecureDesktopActive -= OnCaptureSecureDesktopActive;
            _capture.OnSecureDesktopInactive -= OnCaptureSecureDesktopInactive;
            _capture.OnFrameCaptured -= OnCaptureFrameCaptured;
            _capture.Dispose();
            _capture = null;
            DebugLog("Secure Desktop capture disposed");
        }
    }

    private static void CleanupVideoPipe()
    {
        _videoConnected = false;
        try { _videoWriter?.Dispose(); } catch { }
        _videoWriter = null;
        try { _videoPipeServer?.Dispose(); } catch { }
        _videoPipeServer = null;
    }

    private static void CleanupNotifyPipe()
    {
        _notifyConnected = false;
        try { _notifyWriter?.Dispose(); } catch { }
        _notifyWriter = null;
        try { _notifyPipeServer?.Dispose(); } catch { }
        _notifyPipeServer = null;
    }

    private static void OnCaptureSecureDesktopActive(int width, int height)
    {
        DebugLog($"Secure Desktop active notification → notify pipe ({width}x{height})");
        SendNotification(new { notification = "secureDesktopActive", width, height });
    }

    private static void OnCaptureSecureDesktopInactive()
    {
        DebugLog("Secure Desktop inactive notification → notify pipe");
        SendNotification(new { notification = "secureDesktopInactive" });
    }

    private static void OnCaptureFrameCaptured(byte[] bgraData, int width, int height, int stride)
    {
        if (!_videoConnected || _videoWriter == null) return;

        lock (_videoWriteLock)
        {
            try
            {
                _frameCount++;
                var dataSize = stride * height;
                if (_frameCount <= 3 || _frameCount % 100 == 0)
                    DebugLog($"Video frame #{_frameCount}: {dataSize}b BGRA ({width}x{height}, stride={stride}), writing to pipe...");

                // Binary frame protocol: [uint32 width][uint32 height][uint32 stride][BGRA pixels]
                _videoWriter.Write((uint)width);
                _videoWriter.Write((uint)height);
                _videoWriter.Write((uint)stride);
                _videoWriter.Write(bgraData, 0, dataSize);
                _videoWriter.Flush();

                if (_frameCount <= 3)
                    DebugLog($"Video frame #{_frameCount}: pipe write complete ({12 + dataSize}b total)");
            }
            catch (IOException)
            {
                // Video pipe disconnected
                DebugLog("Video pipe write failed (disconnected)");
                _videoConnected = false;
            }
            catch (Exception ex)
            {
                DebugLog($"Video frame write error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Send a server-initiated notification over the dedicated notify pipe.
    /// Uses a separate pipe from the control pipe to avoid synchronous I/O deadlocks.
    /// </summary>
    private static void SendNotification(object notification)
    {
        if (!_notifyConnected || _notifyWriter == null) return;

        lock (_notifyWriteLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(notification);
                _notifyWriter.WriteLine(json);
            }
            catch (Exception ex)
            {
                DebugLog($"Notification write error: {ex.Message}");
                _notifyConnected = false;
            }
        }
    }
}
