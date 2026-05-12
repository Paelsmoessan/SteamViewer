using System.Runtime.InteropServices;

namespace SteamViewer.Platform.Windows.Input;

// Display geometry partial: virtual-screen tracking, monitor enumeration,
// captured-monitor caching, and coordinate conversion to Win32 absolute space.
public static partial class Win32Input
{
    // Virtual screen dimensions — refreshed on every call (sub-microsecond GetSystemMetrics)
    private static int _vsLeft, _vsTop, _vsWidth, _vsHeight;

    // Multi-monitor support: cached monitor rectangles, re-enumerated when virtual screen changes
    private record struct MonitorRect(int X, int Y, int Width, int Height, bool IsPrimary, string DeviceName);
    private static MonitorRect[]? _monitors;

    // Cached captured monitor bounds — set once at capture start, used as fast-path in ConvertToAbsoluteCoordinates.
    // Source: Sunshine — match once at capture start, reuse stored bounds. (.claude/research/mouse-input/research.md)
    private static int _cachedCaptureW, _cachedCaptureH;
    private static MonitorRect? _cachedTarget;

    private static List<MonitorRect>? _monitorCollector;

    private static void RefreshDisplayState()
    {
        // Process-level PMv2 DPI awareness is set in Program.Main() — all threads
        // get physical pixel dimensions from GetSystemMetrics/EnumDisplayMonitors.
        var left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        // Re-enumerate monitors only when virtual screen dimensions change (hot-plug, settings change)
        if (width != _vsWidth || height != _vsHeight || left != _vsLeft || top != _vsTop || _monitors == null)
        {
            EnumerateMonitors();
        }

        _vsLeft = left;
        _vsTop = top;
        _vsWidth = width;
        _vsHeight = height;
    }

    private static void EnumerateMonitors()
    {
        _monitorCollector = new List<MonitorRect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, MonitorEnumCallback, IntPtr.Zero);
        _monitors = _monitorCollector.ToArray();
        _monitorCollector = null;
    }

    private static bool MonitorEnumCallback(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData)
    {
        var mi = new MONITORINFOEXW { cbSize = Marshal.SizeOf<MONITORINFOEXW>() };
        if (GetMonitorInfoW(hMonitor, ref mi))
        {
            _monitorCollector?.Add(new MonitorRect(
                mi.rcMonitor.left,
                mi.rcMonitor.top,
                mi.rcMonitor.right - mi.rcMonitor.left,
                mi.rcMonitor.bottom - mi.rcMonitor.top,
                (mi.dwFlags & MONITORINFOF_PRIMARY) != 0,
                mi.szDevice ?? ""));
        }
        return true;
    }

    /// <summary>
    /// Get virtual screen dimensions. Call after EnsureInitialized.
    /// </summary>
    public static (int Left, int Top, int Width, int Height) GetVirtualScreen()
    {
        RefreshDisplayState();
        return (_vsLeft, _vsTop, _vsWidth, _vsHeight);
    }

    /// <summary>
    /// Get the primary monitor's physical pixel dimensions.
    /// Uses SM_CXSCREEN/SM_CYSCREEN (always returns primary monitor size).
    /// Use this instead of hardcoding 1920x1080.
    /// </summary>
    public static (int Width, int Height) GetPrimaryMonitorSize()
    {
        return (GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
    }

    /// <summary>
    /// Get cached monitor list with physical pixel dimensions.
    /// </summary>
    public static IReadOnlyList<(int X, int Y, int Width, int Height, bool IsPrimary, string DeviceName)> GetMonitors()
    {
        RefreshDisplayState();
        if (_monitors == null) return Array.Empty<(int, int, int, int, bool, string)>();
        return _monitors.Select(m => (m.X, m.Y, m.Width, m.Height, m.IsPrimary, m.DeviceName)).ToArray();
    }

    /// <summary>
    /// Cache the target monitor bounds for the current capture session.
    /// Called once when screen sharing starts. Matches capture dimensions to a monitor.
    /// If capture matches full virtual desktop, clears the cache (no offset needed).
    /// Source: Sunshine — match once, use stored bounds.
    /// </summary>
    public static void SetCapturedMonitor(int captureWidth, int captureHeight)
    {
        RefreshDisplayState();
        _cachedCaptureW = captureWidth;
        _cachedCaptureH = captureHeight;

        if (captureWidth == _vsWidth && captureHeight == _vsHeight)
        {
            _cachedTarget = null; // Full virtual desktop — no offset needed
        }
        else
        {
            _cachedTarget = FindMonitorByResolution(captureWidth, captureHeight)
                           ?? _monitors?.FirstOrDefault(m => m.IsPrimary);
        }
    }

    /// <summary>
    /// Explicitly set the target monitor bounds (when monitor identity is known, e.g., from picker UI).
    /// Bypasses resolution matching entirely — no ambiguity possible.
    /// </summary>
    public static void SetCapturedMonitorExplicit(int x, int y, int w, int h)
    {
        _cachedTarget = new MonitorRect(x, y, w, h, false, "");
        _cachedCaptureW = w;
        _cachedCaptureH = h;
    }

    /// <summary>
    /// Clear the cached target monitor (call when capture stops).
    /// </summary>
    public static void ClearCapturedMonitor()
    {
        _cachedTarget = null;
        _cachedCaptureW = 0;
        _cachedCaptureH = 0;
    }

    /// <summary>
    /// Convert remote capture coordinates to Win32 absolute coordinates (0-65535).
    /// When capturing a single monitor on a multi-monitor setup, maps coords to that
    /// monitor's area rather than stretching across the full virtual desktop.
    /// Uses cached monitor bounds (set via SetCapturedMonitor) as fast path.
    /// </summary>
    public static (int AbsX, int AbsY) ConvertToAbsoluteCoordinates(double x, double y, int screenWidth, int screenHeight)
    {
        RefreshDisplayState();

        // Determine which area of the virtual desktop the capture represents
        int targetX = _vsLeft, targetY = _vsTop, targetW = _vsWidth, targetH = _vsHeight;

        // Fast path: use cached target if capture dimensions match what was cached
        if (_cachedTarget != null && screenWidth == _cachedCaptureW && screenHeight == _cachedCaptureH)
        {
            targetX = _cachedTarget.Value.X;
            targetY = _cachedTarget.Value.Y;
            targetW = _cachedTarget.Value.Width;
            targetH = _cachedTarget.Value.Height;
        }
        // Slow path: capture dims changed (rare) or cache not set — re-match
        else if (screenWidth != _vsWidth || screenHeight != _vsHeight)
        {
            var match = FindMonitorByResolution(screenWidth, screenHeight)
                        ?? _monitors?.FirstOrDefault(m => m.IsPrimary);
            if (match != null)
            {
                targetX = match.Value.X;
                targetY = match.Value.Y;
                targetW = match.Value.Width;
                targetH = match.Value.Height;
            }
        }

        // Map capture pixel coords to virtual screen position
        var localX = targetX + x * targetW / screenWidth;
        var localY = targetY + y * targetH / screenHeight;

        // Convert to absolute 0-65535 range across full virtual desktop
        var absX = (int)Math.Round((localX - _vsLeft) * 65535.0 / _vsWidth);
        var absY = (int)Math.Round((localY - _vsTop) * 65535.0 / _vsHeight);

        return (Math.Clamp(absX, 0, 65535), Math.Clamp(absY, 0, 65535));
    }

    /// <summary>
    /// Match capture dimensions to a specific monitor.
    /// Returns null if capture matches full virtual desktop or no monitor matches.
    /// </summary>
    private static MonitorRect? FindMonitorByResolution(int width, int height)
    {
        if (_monitors == null || _monitors.Length <= 1) return null;

        MonitorRect? firstMatch = null;
        var matchCount = 0;

        foreach (var mon in _monitors)
        {
            if (mon.Width == width && mon.Height == height)
            {
                firstMatch ??= mon;
                matchCount++;
            }
        }

        if (matchCount == 1) return firstMatch;

        // Multiple monitors with same resolution — prefer primary
        if (matchCount > 1)
        {
            foreach (var mon in _monitors)
            {
                if (mon.Width == width && mon.Height == height && mon.IsPrimary)
                    return mon;
            }
            return firstMatch; // None is primary, use first match
        }

        return null; // No match — fall back to full virtual desktop
    }
}
