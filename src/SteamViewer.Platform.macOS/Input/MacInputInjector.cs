using Microsoft.Extensions.Logging;
using SteamViewer.Client.Core.Capture;
using SteamViewer.Common.Protocol;
using CoreGraphics;
using ObjCRuntime;

namespace SteamViewer.Platform.macOS.Input;

/// <summary>
/// macOS input injection using CGEvent API.
/// </summary>
/// <remarks>
/// Note: This requires Accessibility permission in System Preferences.
/// The app must be added to Privacy &amp; Security → Accessibility.
/// </remarks>
public sealed class MacInputInjector : IInputInjector
{
    private readonly ILogger<MacInputInjector> _logger;
    private bool _disposed;
    private CGPoint _lastMousePosition;

    public MacInputInjector(ILogger<MacInputInjector> logger)
    {
        _logger = logger;
        _lastMousePosition = CGPoint.Empty;
    }

    public bool IsAvailable => !_disposed;

    public void InjectInput(InputEvent inputEvent, int screenWidth, int screenHeight)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MacInputInjector));
        }

        try
        {
            switch (inputEvent)
            {
                case InputEvent.MouseMove move:
                    InjectMouseMove(move, screenWidth, screenHeight);
                    break;
                case InputEvent.MouseDown down:
                    InjectMouseButton(down.Button, down.X, down.Y, screenWidth, screenHeight, isDown: true);
                    break;
                case InputEvent.MouseUp up:
                    InjectMouseButton(up.Button, up.X, up.Y, screenWidth, screenHeight, isDown: false);
                    break;
                case InputEvent.MouseWheel wheel:
                    InjectMouseWheel(wheel);
                    break;
                case InputEvent.KeyDown keyDown:
                    InjectKey(keyDown.Key, keyDown.Modifiers, isDown: true);
                    break;
                case InputEvent.KeyUp keyUp:
                    InjectKey(keyUp.Key, keyUp.Modifiers, isDown: false);
                    break;
                default:
                    _logger.LogWarning("Unknown input event type: {Type}", inputEvent.GetType().Name);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to inject input event");
        }
    }

    private void InjectMouseMove(InputEvent.MouseMove move, int screenWidth, int screenHeight)
    {
        var point = ConvertToScreenCoordinates(move.X, move.Y, screenWidth, screenHeight);
        _lastMousePosition = point;

        using var evt = CGEvent.CreateMouseEvent(
            null,
            CGEventType.MouseMoved,
            point,
            CGMouseButton.Left);

        evt?.Post(CGEventTapLocation.HID);
    }

    private void InjectMouseButton(MouseButton button, double x, double y, int screenWidth, int screenHeight, bool isDown)
    {
        var point = ConvertToScreenCoordinates(x, y, screenWidth, screenHeight);
        _lastMousePosition = point;

        var (eventType, cgButton) = GetMouseButtonEventType(button, isDown);

        using var evt = CGEvent.CreateMouseEvent(null, eventType, point, cgButton);
        evt?.Post(CGEventTapLocation.HID);
    }

    private void InjectMouseWheel(InputEvent.MouseWheel wheel)
    {
        // CGEvent scroll wheel uses integer units
        var deltaY = (int)(-wheel.DeltaY / 10.0);
        var deltaX = (int)(wheel.DeltaX / 10.0);

        if (deltaY != 0 || deltaX != 0)
        {
            using var evt = CGEvent.CreateScrollWheelEvent2(
                null,
                CGScrollEventUnit.Pixel,
                2, // axis count
                deltaY,
                deltaX);

            evt?.Post(CGEventTapLocation.HID);
        }
    }

    private void InjectKey(string key, KeyModifiers modifiers, bool isDown)
    {
        var keyCode = KeyToMacKeyCode(key);
        if (keyCode == ushort.MaxValue)
        {
            _logger.LogWarning("Unknown key: {Key}", key);
            return;
        }

        using var evt = CGEvent.CreateKeyboardEvent(null, keyCode, isDown);
        if (evt == null) return;

        // Apply modifiers
        var flags = CGEventFlags.None;
        if (modifiers.Ctrl) flags |= CGEventFlags.Control;
        if (modifiers.Alt) flags |= CGEventFlags.Alternate;
        if (modifiers.Shift) flags |= CGEventFlags.Shift;
        if (modifiers.Meta) flags |= CGEventFlags.Command;

        evt.Flags = flags;
        evt.Post(CGEventTapLocation.HID);
    }

    private CGPoint ConvertToScreenCoordinates(double x, double y, int screenWidth, int screenHeight)
    {
        // Get main display bounds
        var mainDisplay = CGDisplay.MainDisplayID;
        var bounds = CGDisplay.GetBounds(mainDisplay);

        // Scale coordinates from remote screen to local screen
        var scaledX = x * bounds.Width / screenWidth;
        var scaledY = y * bounds.Height / screenHeight;

        return new CGPoint(scaledX, scaledY);
    }

    private static (CGEventType EventType, CGMouseButton Button) GetMouseButtonEventType(MouseButton button, bool isDown)
    {
        return button switch
        {
            MouseButton.Left => isDown
                ? (CGEventType.LeftMouseDown, CGMouseButton.Left)
                : (CGEventType.LeftMouseUp, CGMouseButton.Left),
            MouseButton.Right => isDown
                ? (CGEventType.RightMouseDown, CGMouseButton.Right)
                : (CGEventType.RightMouseUp, CGMouseButton.Right),
            MouseButton.Middle => isDown
                ? (CGEventType.OtherMouseDown, CGMouseButton.Center)
                : (CGEventType.OtherMouseUp, CGMouseButton.Center),
            _ => (CGEventType.LeftMouseDown, CGMouseButton.Left)
        };
    }

    /// <summary>
    /// Convert JavaScript key names to macOS key codes.
    /// </summary>
    private static ushort KeyToMacKeyCode(string key)
    {
        return key.ToLowerInvariant() switch
        {
            // Letters (QWERTY layout)
            "a" => 0x00, "s" => 0x01, "d" => 0x02, "f" => 0x03, "h" => 0x04,
            "g" => 0x05, "z" => 0x06, "x" => 0x07, "c" => 0x08, "v" => 0x09,
            "b" => 0x0B, "q" => 0x0C, "w" => 0x0D, "e" => 0x0E, "r" => 0x0F,
            "y" => 0x10, "t" => 0x11, "1" or "!" => 0x12, "2" or "@" => 0x13,
            "3" or "#" => 0x14, "4" or "$" => 0x15, "6" or "^" => 0x16,
            "5" or "%" => 0x17, "=" or "+" => 0x18, "9" or "(" => 0x19,
            "7" or "&" => 0x1A, "-" or "_" => 0x1B, "8" or "*" => 0x1C,
            "0" or ")" => 0x1D, "]" or "}" => 0x1E, "o" => 0x1F, "u" => 0x20,
            "[" or "{" => 0x21, "i" => 0x22, "p" => 0x23, "l" => 0x25,
            "j" => 0x26, "'" or "\"" => 0x27, "k" => 0x28, ";" or ":" => 0x29,
            "\\" or "|" => 0x2A, "," or "<" => 0x2B, "/" or "?" => 0x2C,
            "n" => 0x2D, "m" => 0x2E, "." or ">" => 0x2F, "`" or "~" => 0x32,

            // Special keys
            "enter" => 0x24,
            "tab" => 0x30,
            " " or "space" => 0x31,
            "backspace" => 0x33,
            "escape" => 0x35,
            "delete" => 0x75,

            // Arrow keys
            "arrowleft" => 0x7B,
            "arrowright" => 0x7C,
            "arrowdown" => 0x7D,
            "arrowup" => 0x7E,

            // Function keys
            "f1" => 0x7A, "f2" => 0x78, "f3" => 0x63, "f4" => 0x76,
            "f5" => 0x60, "f6" => 0x61, "f7" => 0x62, "f8" => 0x64,
            "f9" => 0x65, "f10" => 0x6D, "f11" => 0x67, "f12" => 0x6F,

            // Navigation
            "home" => 0x73,
            "end" => 0x77,
            "pageup" => 0x74,
            "pagedown" => 0x79,

            // Modifiers
            "shift" => 0x38,
            "control" => 0x3B,
            "alt" => 0x3A,
            "meta" or "command" => 0x37,
            "capslock" => 0x39,

            _ => ushort.MaxValue // Unknown key
        };
    }

    public void Dispose()
    {
        _disposed = true;
        _logger.LogDebug("macOS input injector disposed");
    }
}
