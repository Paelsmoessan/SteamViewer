using System.Runtime.InteropServices;
using System.Text.Json;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Platform.Windows.Input;

/// <summary>
/// Shared static Win32 input injection logic.
/// Used by both WindowsInputInjector (non-elevated) and ElevatedHelperServer (elevated).
/// </summary>
// Made public for SteamViewer.App.HostSession to call IsKnownInputType. All other methods
// on this class are `internal static` so they remain assembly-private; only the public
// methods explicitly marked here are exposed across the project boundary.
//
// Partial-class split (Stage D #2): this file holds the entry points and the canonical
// SendInputWithRetry wrapper used by every cluster.
//   Win32Input.Display.cs   — virtual-screen state, monitor enumeration, coord conversion
//   Win32Input.Mouse.cs     — InjectMouse{Move,Button,Wheel}
//   Win32Input.Keyboard.cs  — InjectKey, InjectScanCode, InjectHybridKey, InjectUnicodeChar,
//                             KeyToVirtualKey/CodeToVirtualKey lookups (intrinsic-complexity
//                             exemptions), layout activation, modifier release
//   Win32Input.Constants.cs — consts, structs, P/Invoke DllImports (the Win32 Interop region)
public static partial class Win32Input
{
    // Desktop sync retry: last-known desktop handle per thread (value only, for comparison).
    // When SendInput fails (returns 0), the thread's desktop may be stale.
    // Re-open the input desktop and retry. Source: Sunshine send_input() + syncThreadDesktop().
    [ThreadStatic]
    private static IntPtr _lastKnownDesktop;

    /// <summary>
    /// SendInput with desktop sync retry. If SendInput returns 0, re-opens the
    /// current input desktop and retries once. Handles desktop transitions
    /// (UAC return, fast user switch) without requiring the SYSTEM helper.
    /// </summary>
    private static uint SendInputWithRetry(uint nInputs, INPUT[] pInputs, int cbSize)
    {
        var sent = SendInput(nInputs, pInputs, cbSize);
        if (sent == nInputs)
            return sent;

        // SendInput failed — try to re-attach to the current input desktop
        var hDesk = OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP);
        if (hDesk == IntPtr.Zero)
            return sent; // Can't open desktop — give up

        if (hDesk != _lastKnownDesktop)
        {
            SetThreadDesktop(hDesk);
            _lastKnownDesktop = hDesk;
            sent = SendInput(nInputs, pInputs, cbSize); // retry
        }

        // Close our handle — SetThreadDesktop already gave the thread its own reference.
        // We keep _lastKnownDesktop as a stale sentinel for change detection (Sunshine pattern).
        CloseDesktop(hDesk);
        return sent;
    }

    /// <summary>
    /// Inject an input event using Win32 SendInput.
    /// </summary>
    public static void InjectInputEvent(InputEvent inputEvent, int screenWidth, int screenHeight)
    {
        RefreshDisplayState();

        switch (inputEvent)
        {
            case InputEvent.MouseMove move:
                InjectMouseMove(move.X, move.Y, screenWidth, screenHeight);
                break;
            case InputEvent.MouseDown down:
                InjectMouseButton(down.Button, down.X, down.Y, screenWidth, screenHeight, isDown: true);
                break;
            case InputEvent.MouseUp up:
                InjectMouseButton(up.Button, up.X, up.Y, screenWidth, screenHeight, isDown: false);
                break;
            case InputEvent.MouseWheel wheel:
                InjectMouseWheel(wheel.DeltaX, wheel.DeltaY);
                break;
            case InputEvent.KeyDown keyDown:
                InjectKey(keyDown.Key, keyDown.Modifiers, isDown: true, keyDown.Code, keyDown.AltGr);
                break;
            case InputEvent.KeyUp keyUp:
                InjectKey(keyUp.Key, keyUp.Modifiers, isDown: false, keyUp.Code, keyUp.AltGr);
                break;
            case InputEvent.KeyDownScan scanDown:
                InjectHybridKey(scanDown.ScanCode, scanDown.VkCode, isDown: true,
                    scanDown.IsExtended, scanDown.UnicodeChar);
                break;
            case InputEvent.KeyUpScan scanUp:
                InjectHybridKey(scanUp.ScanCode, scanUp.VkCode, isDown: false,
                    scanUp.IsExtended, scanUp.UnicodeChar);
                break;
        }
    }

    /// <summary>
    /// JSON twin of InjectInputEvent — single canonical dispatch for the JSON wire format.
    /// Replaces the duplicated switches in ElevatedHelperServer, SystemHelperServer, and
    /// SecureDesktopCapture. Reads sw/sh from the JSON if present, else falls back to
    /// defaults. Calls logUnknownType for any case the switch can't handle so future
    /// additions to InputEvent are loud instead of silent.
    /// </summary>
    internal static void InjectInputFromJson(JsonElement root, int defaultSw, int defaultSh, Action<string>? logUnknownType = null)
    {
        var sw = root.TryGetProperty("sw", out var swProp) ? swProp.GetInt32() : defaultSw;
        var sh = root.TryGetProperty("sh", out var shProp) ? shProp.GetInt32() : defaultSh;
        var type = root.GetProperty("type").GetString();

        switch (type)
        {
            case "mouse_move":
                InjectMouseMove(
                    root.GetProperty("x").GetDouble(),
                    root.GetProperty("y").GetDouble(), sw, sh);
                break;
            case "mouse_down":
                InjectMouseButton(
                    ParseMouseButtonFromJson(root.GetProperty("button").GetString()),
                    root.GetProperty("x").GetDouble(),
                    root.GetProperty("y").GetDouble(), sw, sh, isDown: true);
                break;
            case "mouse_up":
                InjectMouseButton(
                    ParseMouseButtonFromJson(root.GetProperty("button").GetString()),
                    root.GetProperty("x").GetDouble(),
                    root.GetProperty("y").GetDouble(), sw, sh, isDown: false);
                break;
            case "mouse_wheel":
                InjectMouseWheel(
                    root.GetProperty("delta_x").GetDouble(),
                    root.GetProperty("delta_y").GetDouble());
                break;
            case "key_down":
                InjectKey(
                    root.GetProperty("key").GetString()!,
                    ParseModifiersFromJson(root), isDown: true);
                break;
            case "key_up":
                InjectKey(
                    root.GetProperty("key").GetString()!,
                    ParseModifiersFromJson(root), isDown: false);
                break;
            case "key_down_scan":
                InjectHybridKey(
                    root.GetProperty("scan").GetUInt16(),
                    root.GetProperty("vk").GetUInt16(),
                    isDown: true,
                    root.TryGetProperty("ext", out var extD) && extD.GetBoolean(),
                    root.TryGetProperty("uc", out var ucD) ? ucD.GetUInt32() : 0u);
                break;
            case "key_up_scan":
                InjectHybridKey(
                    root.GetProperty("scan").GetUInt16(),
                    root.GetProperty("vk").GetUInt16(),
                    isDown: false,
                    root.TryGetProperty("ext", out var extU) && extU.GetBoolean(),
                    root.TryGetProperty("uc", out var ucU) ? ucU.GetUInt32() : 0u);
                break;
            default:
                logUnknownType?.Invoke($"Unknown input type \"{type}\" — dropped (no switch case)");
                break;
        }
    }

    /// <summary>
    /// True if <paramref name="type"/> is one of the 8 input event message types handled by
    /// <see cref="InjectInputFromJson"/>'s switch (mouse_*, key_*). Used by HostSession's
    /// HandleControlMessage onNoHandler to distinguish input events (legitimately routed to
    /// HandleInputMessage) from truly-unknown control message types (warn-logged).
    /// </summary>
    /// <remarks>
    /// Mirrors the case labels in InjectInputFromJson. Single source of truth -
    /// adding a new input event type here AND there keeps the routing decision in lock-step.
    /// </remarks>
    public static bool IsKnownInputType(string? type) => type switch
    {
        "mouse_move" or "mouse_down" or "mouse_up" or "mouse_wheel"
            or "key_down" or "key_up" or "key_down_scan" or "key_up_scan" => true,
        _ => false
    };

    private static MouseButton ParseMouseButtonFromJson(string? button) => button switch
    {
        "Left" => MouseButton.Left,
        "Right" => MouseButton.Right,
        "Middle" => MouseButton.Middle,
        _ => MouseButton.Left
    };

    private static KeyModifiers ParseModifiersFromJson(JsonElement root)
    {
        if (!root.TryGetProperty("modifiers", out var mods))
            return KeyModifiers.None;

        return new KeyModifiers(
            mods.TryGetProperty("ctrl", out var c) && c.GetBoolean(),
            mods.TryGetProperty("shift", out var s) && s.GetBoolean(),
            mods.TryGetProperty("alt", out var a) && a.GetBoolean(),
            mods.TryGetProperty("meta", out var m) && m.GetBoolean());
    }
}
