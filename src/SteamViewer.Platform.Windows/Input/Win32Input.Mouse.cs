using System.Runtime.InteropServices;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Platform.Windows.Input;

// Mouse injection partial: move, button, wheel. All entries go through
// ConvertToAbsoluteCoordinates (Display partial) for cursor placement and
// SendInputWithRetry (entry partial) for transmission.
public static partial class Win32Input
{
    // Scroll accumulation — accumulates sub-tick deltas, only sends full WHEEL_DELTA (120) multiples.
    // Prevents phantom scroll events from high-precision trackpads.
    // Source: Sunshine high-resolution scroll accumulation (research.md lines 1124-1139)
    private static int _accumulatedVScroll;
    private static int _accumulatedHScroll;

    internal static void InjectMouseMove(double x, double y, int screenWidth, int screenHeight)
    {
        var (absX, absY) = ConvertToAbsoluteCoordinates(x, y, screenWidth, screenHeight);

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            union = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = absX,
                    dy = absY,
                    mouseData = 0,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInputWithRetry(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    internal static void InjectMouseButton(MouseButton button, double x, double y, int screenWidth, int screenHeight, bool isDown)
    {
        var (absX, absY) = ConvertToAbsoluteCoordinates(x, y, screenWidth, screenHeight);

        // Split move + click into separate SendInput calls.
        // Windows may not process the position before the click if combined.
        // Source: FreeRDP, Sunshine
        var moveInput = new INPUT
        {
            type = INPUT_MOUSE,
            union = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = absX,
                    dy = absY,
                    mouseData = 0,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInputWithRetry(1, new[] { moveInput }, Marshal.SizeOf<INPUT>());

        // Button event — no MOVE flag, no position. Cursor is already at the right spot.
        // Source: Sunshine button_mouse() (research.md lines 1183-1195)
        uint buttonFlags = 0;
        uint mouseData = 0;
        switch (button)
        {
            case MouseButton.Left:
                buttonFlags = isDown ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP;
                break;
            case MouseButton.Right:
                buttonFlags = isDown ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP;
                break;
            case MouseButton.Middle:
                buttonFlags = isDown ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP;
                break;
            case MouseButton.XButton1:
                buttonFlags = isDown ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP;
                mouseData = XBUTTON1;
                break;
            case MouseButton.XButton2:
                buttonFlags = isDown ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP;
                mouseData = XBUTTON2;
                break;
        }

        var buttonInput = new INPUT
        {
            type = INPUT_MOUSE,
            union = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = mouseData,
                    dwFlags = buttonFlags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInputWithRetry(1, new[] { buttonInput }, Marshal.SizeOf<INPUT>());
    }

    internal static void InjectMouseWheel(double deltaX, double deltaY)
    {
        _accumulatedVScroll += (int)(-deltaY * WHEEL_DELTA / 100.0);
        _accumulatedHScroll += (int)(deltaX * WHEEL_DELTA / 100.0);

        var inputs = new List<INPUT>();

        var vTicks = _accumulatedVScroll / WHEEL_DELTA;
        if (vTicks != 0)
        {
            inputs.Add(MakeWheelInput(MOUSEEVENTF_WHEEL, vTicks * WHEEL_DELTA));
            _accumulatedVScroll -= vTicks * WHEEL_DELTA;
        }

        var hTicks = _accumulatedHScroll / WHEEL_DELTA;
        if (hTicks != 0)
        {
            inputs.Add(MakeWheelInput(MOUSEEVENTF_HWHEEL, hTicks * WHEEL_DELTA));
            _accumulatedHScroll -= hTicks * WHEEL_DELTA;
        }

        if (inputs.Count > 0)
        {
            SendInputWithRetry((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        }
    }

    private static INPUT MakeWheelInput(uint flags, int wheelDelta)
    {
        return new INPUT
        {
            type = INPUT_MOUSE,
            union = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0, dy = 0,
                    mouseData = (uint)wheelDelta,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }
}
