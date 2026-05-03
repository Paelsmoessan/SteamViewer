using FFmpeg.AutoGen;

namespace SteamViewer.Client.Core.Video;

/// <summary>
/// One-time FFmpeg library initialization.
/// Sets the DLL search path so FFmpeg.AutoGen can find native libraries.
/// </summary>
public static class FFmpegInit
{
    private static bool _initialized;
    private static readonly object _lock = new();

    /// <summary>
    /// Ensure FFmpeg native libraries are loadable.
    /// Call before any FFmpeg.AutoGen API usage.
    /// </summary>
    public static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;

            // Look for FFmpeg DLLs in a 'ffmpeg' subfolder next to the exe
            var ffmpegDir = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
            if (Directory.Exists(ffmpegDir))
            {
                ffmpeg.RootPath = ffmpegDir;
            }
            // else: rely on system PATH or side-by-side DLLs

            _initialized = true;
        }
    }
}
