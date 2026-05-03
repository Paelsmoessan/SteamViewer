using System.Runtime.InteropServices;

namespace SteamViewer.Launcher;

/// <summary>
/// Pure Win32 splash window — no WinForms dependency.
/// Dark themed borderless window with title, status text, progress bar, and version.
/// </summary>
sealed class SplashWindow : IDisposable
{
    private const string ClassName = "SteamViewerSplash";
    private const int Width = 420;
    private const int Height = 200;

    // Colors (BGR for Win32)
    private static readonly uint BgColor = RGB(10, 25, 41);       // Dark navy
    private static readonly uint TitleColor = RGB(255, 255, 255);  // White
    private static readonly uint StatusColor = RGB(176, 190, 210); // Light blue-gray
    private static readonly uint DetailColor = RGB(120, 140, 160); // Muted gray
    private static readonly uint VersionColor = RGB(80, 100, 120); // Dark gray
    private static readonly uint BarBgColor = RGB(30, 50, 70);    // Progress bar background
    private static readonly uint BarFillColor = RGB(50, 130, 220); // Progress bar fill

    private IntPtr _hwnd;
    private IntPtr _bgBrush;
    private IntPtr _titleFont;
    private IntPtr _statusFont;
    private IntPtr _detailFont;
    private IntPtr _versionFont;
    private WndProcDelegate? _wndProc;
    private bool _disposed;

    private string _statusText = "Preparing...";
    private string _detailText = "";
    private string _versionText = "";
    private int _progressPercent;

    // Keep delegate alive to prevent GC collection
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public SplashWindow()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        _versionText = $"v{version?.Major}.{version?.Minor}.{version?.Build}";

        _bgBrush = CreateSolidBrush(BgColor);
        _titleFont = CreateFontW(-27, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
        _statusFont = CreateFontW(-16, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
        _detailFont = CreateFontW(-13, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
        _versionFont = CreateFontW(-12, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");

        _wndProc = WndProc;

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0x0020, // CS_OWNDC
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            hCursor = LoadCursor(IntPtr.Zero, 32512), // IDC_ARROW
            hbrBackground = _bgBrush,
            lpszClassName = ClassName
        };

        RegisterClassEx(ref wc);

        // Center on screen
        int screenW = GetSystemMetrics(0); // SM_CXSCREEN
        int screenH = GetSystemMetrics(1); // SM_CYSCREEN
        int x = (screenW - Width) / 2;
        int y = (screenH - Height) / 2;

        _hwnd = CreateWindowEx(
            0x00000008 | 0x00080000, // WS_EX_TOPMOST | WS_EX_LAYERED
            ClassName,
            "SteamViewer",
            0x80000000 | 0x10000000, // WS_POPUP | WS_VISIBLE
            x, y, Width, Height,
            IntPtr.Zero, IntPtr.Zero,
            GetModuleHandle(null), IntPtr.Zero);

        // Make fully opaque (layered window for no taskbar flash)
        SetLayeredWindowAttributes(_hwnd, 0, 255, 0x00000002); // LWA_ALPHA
    }

    public void Show()
    {
        ShowWindow(_hwnd, 5); // SW_SHOW
        UpdateWindow(_hwnd);
        PumpMessages();
    }

    public void SetStatus(string text)
    {
        _statusText = text;
        InvalidateRect(_hwnd, IntPtr.Zero, false);
        PumpMessages();
    }

    public void SetProgress(int percent, string detail)
    {
        _progressPercent = Math.Clamp(percent, 0, 100);
        _detailText = detail;
        InvalidateRect(_hwnd, IntPtr.Zero, false);
        PumpMessages();
    }

    public void Close()
    {
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        PumpMessages();
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case 0x000F: // WM_PAINT
                Paint(hWnd);
                return IntPtr.Zero;

            case 0x0201: // WM_LBUTTONDOWN — drag borderless window
                ReleaseCapture();
                SendMessage(hWnd, 0x0112, (IntPtr)0xF010, IntPtr.Zero); // WM_SYSCOMMAND + SC_MOVE
                return IntPtr.Zero;

            case 0x0002: // WM_DESTROY
                return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void Paint(IntPtr hWnd)
    {
        var ps = new PAINTSTRUCT();
        IntPtr hdc = BeginPaint(hWnd, ref ps);

        // Double-buffer: paint to memory DC, then blit
        IntPtr memDC = CreateCompatibleDC(hdc);
        IntPtr memBmp = CreateCompatibleBitmap(hdc, Width, Height);
        IntPtr oldBmp = SelectObject(memDC, memBmp);

        // Background
        var bgRect = new RECT { left = 0, top = 0, right = Width, bottom = Height };
        FillRect(memDC, ref bgRect, _bgBrush);

        SetBkMode(memDC, 1); // TRANSPARENT

        // Title: "SteamViewer" at (20, 20)
        DrawText(memDC, _titleFont, TitleColor, _versionText.Length > 0 ? "SteamViewer" : "SteamViewer",
            20, 20, 380, 40, 0x00); // DT_LEFT

        // Status text at (20, 70)
        DrawText(memDC, _statusFont, StatusColor, _statusText, 20, 70, 380, 24, 0x00);

        // Progress bar at (20, 104), size 380x24
        var barBgBrush = CreateSolidBrush(BarBgColor);
        var barBgRect = new RECT { left = 20, top = 104, right = 400, bottom = 128 };
        FillRect(memDC, ref barBgRect, barBgBrush);
        DeleteObject(barBgBrush);

        if (_progressPercent > 0)
        {
            int fillWidth = (int)(380.0 * _progressPercent / 100);
            var barFillBrush = CreateSolidBrush(BarFillColor);
            var barFillRect = new RECT { left = 20, top = 104, right = 20 + fillWidth, bottom = 128 };
            FillRect(memDC, ref barFillRect, barFillBrush);
            DeleteObject(barFillBrush);
        }

        // Detail text at (20, 134)
        if (!string.IsNullOrEmpty(_detailText))
            DrawText(memDC, _detailFont, DetailColor, _detailText, 20, 134, 380, 20, 0x00);

        // Version at (20, 168), right-aligned
        DrawText(memDC, _versionFont, VersionColor, _versionText, 20, 168, 380, 18, 0x02); // DT_RIGHT

        // Blit to screen
        BitBlt(hdc, 0, 0, Width, Height, memDC, 0, 0, 0x00CC0020); // SRCCOPY

        SelectObject(memDC, oldBmp);
        DeleteObject(memBmp);
        DeleteDC(memDC);

        EndPaint(hWnd, ref ps);
    }

    private static void DrawText(IntPtr hdc, IntPtr font, uint color, string text, int x, int y, int w, int h, uint format)
    {
        var oldFont = SelectObject(hdc, font);
        SetTextColor(hdc, color);
        var rect = new RECT { left = x, top = y, right = x + w, bottom = y + h };
        DrawTextW(hdc, text, -1, ref rect, format | 0x20 | 0x04); // DT_SINGLELINE | DT_VCENTER
        SelectObject(hdc, oldFont);
    }

    private static void PumpMessages()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1)) // PM_REMOVE
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private static uint RGB(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
        if (_bgBrush != IntPtr.Zero) { DeleteObject(_bgBrush); _bgBrush = IntPtr.Zero; }
        if (_titleFont != IntPtr.Zero) { DeleteObject(_titleFont); _titleFont = IntPtr.Zero; }
        if (_statusFont != IntPtr.Zero) { DeleteObject(_statusFont); _statusFont = IntPtr.Zero; }
        if (_detailFont != IntPtr.Zero) { DeleteObject(_detailFont); _detailFont = IntPtr.Zero; }
        if (_versionFont != IntPtr.Zero) { DeleteObject(_versionFont); _versionFont = IntPtr.Zero; }
        UnregisterClass(ClassName, GetModuleHandle(null));
    }

    // Win32 interop
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int pt_x, pt_y; }
    [StructLayout(LayoutKind.Sequential)] struct PAINTSTRUCT { public IntPtr hdc; public bool fErase; public RECT rcPaint; public bool fRestore; public bool fIncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool UnregisterClass(string className, IntPtr hInstance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool UpdateWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr hWnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr hInstance, int cursor);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int DrawTextW(IntPtr hdc, string text, int count, ref RECT rect, uint format);
    [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern uint SetTextColor(IntPtr hdc, uint color);
    [DllImport("gdi32.dll")] static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateFontW(int height, int width, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dest, int xDest, int yDest, int w, int h, IntPtr src, int xSrc, int ySrc, uint rop);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}
