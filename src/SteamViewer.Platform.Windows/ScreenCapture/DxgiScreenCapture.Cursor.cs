using Microsoft.Extensions.Logging;
using System.Drawing;
using System.Runtime.InteropServices;

namespace SteamViewer.Platform.Windows.ScreenCapture;

public sealed partial class DxgiScreenCapture
{
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

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

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

    /// <summary>
    /// Build the HCURSOR handle to CSS cursor name lookup table.
    /// Called once on first capture frame. LoadCursor with NULL hInstance
    /// returns the same handle for the process lifetime.
    /// </summary>
    private void InitCursorShapeTable()
    {
        _standardCursors = new Dictionary<IntPtr, string>();

        // IDC_ constants -> CSS cursor values
        var mapping = new (int idcConstant, string cssValue)[]
        {
            (32512, "default"),      // IDC_ARROW
            (32513, "text"),         // IDC_IBEAM
            (32514, "wait"),         // IDC_WAIT
            (32515, "crosshair"),    // IDC_CROSS
            (32516, "default"),      // IDC_UPARROW (no CSS equivalent)
            (32642, "nwse-resize"),  // IDC_SIZENWSE
            (32643, "nesw-resize"),  // IDC_SIZENESW
            (32644, "ew-resize"),    // IDC_SIZEWE
            (32645, "ns-resize"),    // IDC_SIZENS
            (32646, "move"),         // IDC_SIZEALL
            (32648, "not-allowed"),  // IDC_NO
            (32649, "pointer"),      // IDC_HAND
            (32650, "progress"),     // IDC_APPSTARTING
            (32651, "help"),         // IDC_HELP
        };

        foreach (var (idc, css) in mapping)
        {
            var handle = LoadCursor(IntPtr.Zero, idc);
            if (handle != IntPtr.Zero)
                _standardCursors[handle] = css;
        }

        _logger.LogInformation("Cursor shape table initialized: {Count} standard cursors mapped", _standardCursors.Count);
    }

    /// <summary>
    /// Detect cursor shape changes and fire OnCursorShapeChanged. Called from the
    /// capture loop on every frame - only fires the event when the HCURSOR handle
    /// actually changes (typically a few times/sec).
    /// </summary>
    private void DetectCursorShapeChange()
    {
        if (OnCursorShapeChanged == null) return;

        var ci = new CURSORINFO();
        ci.cbSize = Marshal.SizeOf<CURSORINFO>();
        if (!GetCursorInfo(ref ci)) return;

        if (ci.hCursor == _lastCursorHandle) return;
        _lastCursorHandle = ci.hCursor;

        if (_standardCursors == null) return;

        var cssValue = _standardCursors.TryGetValue(ci.hCursor, out var name) ? name : "default";
        OnCursorShapeChanged.Invoke(cssValue);
    }
}
