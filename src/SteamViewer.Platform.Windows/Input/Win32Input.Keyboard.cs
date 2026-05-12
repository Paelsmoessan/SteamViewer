using System.Runtime.InteropServices;
using System.Text;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Platform.Windows.Input;

// Keyboard injection partial: virtual-key dispatch, scan-code/Unicode/hybrid
// paths, AltGr resolution, layout activation, and modifier release.
public static partial class Win32Input
{
    // Extended keys require KEYEVENTF_EXTENDEDKEY flag — without it, SendInput ignores or
    // misinterprets them (e.g. NumLock toggle, navigation keys, right-side modifiers).
    private static readonly HashSet<ushort> _extendedKeys = new()
    {
        0x2D, // VK_INSERT
        0x2E, // VK_DELETE
        0x24, // VK_HOME
        0x23, // VK_END
        0x21, // VK_PRIOR (PageUp)
        0x22, // VK_NEXT (PageDown)
        0x26, // VK_UP
        0x28, // VK_DOWN
        0x25, // VK_LEFT
        0x27, // VK_RIGHT
        0x90, // VK_NUMLOCK
        0x2C, // VK_SNAPSHOT (PrintScreen)
        0xA3, // VK_RCONTROL
        0xA5, // VK_RMENU (Right Alt)
        0x5B, // VK_LWIN
        0x5C, // VK_RWIN
    };

    private static IntPtr _savedHostLayout;
    private static string? _activeHostKlid;

    internal static string? ActiveHostKlid => _activeHostKlid;

    internal static void InjectKey(string key, KeyModifiers modifiers, bool isDown,
        string? code = null, bool altGr = false)
    {
        var inputs = new List<INPUT>();

        // AltGr workaround: WebView2/WinUI bug (microsoft-ui-xaml#10284) strips AltGr composition.
        // JS tracks AltGr via code:"AltRight" and sends altGr:true + e.code.
        // We resolve the character via ToUnicodeEx using the host's keyboard layout.
        if (altGr && code != null)
        {
            var resolved = ResolveAltGrCharacter(code);
            if (resolved != null)
            {
                var flags = KEYEVENTF_UNICODE | (isDown ? 0u : KEYEVENTF_KEYUP);
                inputs.Add(new INPUT
                {
                    type = INPUT_KEYBOARD,
                    union = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = 0,
                            wScan = (ushort)resolved[0],
                            dwFlags = flags,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    }
                });
                SendInputWithRetry((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
                return;
            }
        }

        // Legacy AltGr detection (in case WebView2 fixes the bug and sends proper modifiers)
        var isAltGrLegacy = key.Length == 1 && modifiers.Ctrl && modifiers.Alt && !modifiers.Meta;
        if (isAltGrLegacy)
        {
            var flags = KEYEVENTF_UNICODE | (isDown ? 0u : KEYEVENTF_KEYUP);
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                union = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = (ushort)key[0],
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            });
        }
        else
        {
            if (isDown)
            {
                if (modifiers.Ctrl) AddKeyInput(inputs, VK_CONTROL, isDown: true);
                if (modifiers.Alt) AddKeyInput(inputs, VK_MENU, isDown: true);
                if (modifiers.Shift) AddKeyInput(inputs, VK_SHIFT, isDown: true);
                if (modifiers.Meta) AddKeyInput(inputs, VK_LWIN, isDown: true);
            }

            var vk = KeyToVirtualKey(key);
            if (vk != 0)
            {
                AddKeyInput(inputs, vk, isDown);
            }
            else if (key.Length == 1)
            {
                // Unknown VK but single character — use KEYEVENTF_UNICODE to type it directly.
                // This handles non-US layout characters (æ, ø, å, ö, etc.) that have no VK mapping.
                // JS e.key already resolves the viewer's keyboard layout for us.
                var flags = KEYEVENTF_UNICODE | (isDown ? 0u : KEYEVENTF_KEYUP);
                inputs.Add(new INPUT
                {
                    type = INPUT_KEYBOARD,
                    union = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = 0,
                            wScan = (ushort)key[0],
                            dwFlags = flags,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    }
                });
            }

            if (!isDown)
            {
                if (modifiers.Meta) AddKeyInput(inputs, VK_LWIN, isDown: false);
                if (modifiers.Shift) AddKeyInput(inputs, VK_SHIFT, isDown: false);
                if (modifiers.Alt) AddKeyInput(inputs, VK_MENU, isDown: false);
                if (modifiers.Ctrl) AddKeyInput(inputs, VK_CONTROL, isDown: false);
            }
        }

        if (inputs.Count > 0)
        {
            SendInputWithRetry((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        }
    }

    private static void AddKeyInput(List<INPUT> inputs, ushort vk, bool isDown)
    {
        var flags = isDown ? 0u : KEYEVENTF_KEYUP;
        if (_extendedKeys.Contains(vk)) flags |= KEYEVENTF_EXTENDEDKEY;

        inputs.Add(new INPUT
        {
            type = INPUT_KEYBOARD,
            union = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        });
    }

    public static ushort KeyToVirtualKey(string key)
    {
        return key.ToLowerInvariant() switch
        {
            // Letters
            "a" => 0x41, "b" => 0x42, "c" => 0x43, "d" => 0x44, "e" => 0x45,
            "f" => 0x46, "g" => 0x47, "h" => 0x48, "i" => 0x49, "j" => 0x4A,
            "k" => 0x4B, "l" => 0x4C, "m" => 0x4D, "n" => 0x4E, "o" => 0x4F,
            "p" => 0x50, "q" => 0x51, "r" => 0x52, "s" => 0x53, "t" => 0x54,
            "u" => 0x55, "v" => 0x56, "w" => 0x57, "x" => 0x58, "y" => 0x59,
            "z" => 0x5A,

            // Numbers
            "0" or ")" => 0x30, "1" or "!" => 0x31, "2" or "@" => 0x32,
            "3" or "#" => 0x33, "4" or "$" => 0x34, "5" or "%" => 0x35,
            "6" or "^" => 0x36, "7" or "&" => 0x37, "8" or "*" => 0x38,
            "9" or "(" => 0x39,

            // Function keys
            "f1" => VK_F1, "f2" => VK_F2, "f3" => VK_F3, "f4" => VK_F4,
            "f5" => VK_F5, "f6" => VK_F6, "f7" => VK_F7, "f8" => VK_F8,
            "f9" => VK_F9, "f10" => VK_F10, "f11" => VK_F11, "f12" => VK_F12,

            // Special keys
            "enter" => VK_RETURN,
            "escape" => VK_ESCAPE,
            "backspace" => VK_BACK,
            "tab" => VK_TAB,
            " " or "space" => VK_SPACE,
            "delete" => VK_DELETE,
            "insert" => VK_INSERT,
            "home" => VK_HOME,
            "end" => VK_END,
            "pageup" => VK_PRIOR,
            "pagedown" => VK_NEXT,

            // Arrow keys
            "arrowup" => VK_UP,
            "arrowdown" => VK_DOWN,
            "arrowleft" => VK_LEFT,
            "arrowright" => VK_RIGHT,

            // Modifiers
            "shift" => VK_SHIFT,
            "control" => VK_CONTROL,
            "alt" => VK_MENU,
            "meta" => VK_LWIN,
            "capslock" => VK_CAPITAL,
            "numlock" => VK_NUMLOCK,
            "scrolllock" => VK_SCROLL,

            // Punctuation
            ";" or ":" => VK_OEM_1,
            "=" or "+" => VK_OEM_PLUS,
            "," or "<" => VK_OEM_COMMA,
            "-" or "_" => VK_OEM_MINUS,
            "." or ">" => VK_OEM_PERIOD,
            "/" or "?" => VK_OEM_2,
            "`" or "~" => VK_OEM_3,
            "[" or "{" => VK_OEM_4,
            "\\" or "|" => VK_OEM_5,
            "]" or "}" => VK_OEM_6,
            "'" or "\"" => VK_OEM_7,

            _ => 0
        };
    }

    /// <summary>
    /// Map JS KeyboardEvent.code (physical key position) to Win32 virtual key code.
    /// Used for AltGr workaround where e.key is broken by WebView2.
    /// </summary>
    private static ushort CodeToVirtualKey(string code) => code switch
    {
        "Digit0" => 0x30, "Digit1" => 0x31, "Digit2" => 0x32, "Digit3" => 0x33,
        "Digit4" => 0x34, "Digit5" => 0x35, "Digit6" => 0x36, "Digit7" => 0x37,
        "Digit8" => 0x38, "Digit9" => 0x39,
        "KeyA" => 0x41, "KeyB" => 0x42, "KeyC" => 0x43, "KeyD" => 0x44,
        "KeyE" => 0x45, "KeyF" => 0x46, "KeyG" => 0x47, "KeyH" => 0x48,
        "KeyI" => 0x49, "KeyJ" => 0x4A, "KeyK" => 0x4B, "KeyL" => 0x4C,
        "KeyM" => 0x4D, "KeyN" => 0x4E, "KeyO" => 0x4F, "KeyP" => 0x50,
        "KeyQ" => 0x51, "KeyR" => 0x52, "KeyS" => 0x53, "KeyT" => 0x54,
        "KeyU" => 0x55, "KeyV" => 0x56, "KeyW" => 0x57, "KeyX" => 0x58,
        "KeyY" => 0x59, "KeyZ" => 0x5A,
        "Minus" => VK_OEM_MINUS, "Equal" => VK_OEM_PLUS,
        "BracketLeft" => VK_OEM_4, "BracketRight" => VK_OEM_6,
        "Backslash" => VK_OEM_5, "IntlBackslash" => VK_OEM_102,
        "Semicolon" => VK_OEM_1, "Quote" => VK_OEM_7,
        "Backquote" => VK_OEM_3,
        "Comma" => VK_OEM_COMMA, "Period" => VK_OEM_PERIOD,
        "Slash" => VK_OEM_2,
        _ => 0
    };

    /// <summary>
    /// Resolve what character AltGr + physical key produces on the host's keyboard layout.
    /// Uses ToUnicodeEx with a synthetic Ctrl+Alt keyboard state.
    /// </summary>
    private static string? ResolveAltGrCharacter(string code)
    {
        var vk = CodeToVirtualKey(code);
        if (vk == 0) return null;

        // Get the active keyboard layout from the foreground window's thread
        var hwnd = GetForegroundWindow();
        var threadId = GetWindowThreadProcessId(hwnd, out _);
        var hkl = GetKeyboardLayout(threadId);

        // Build keyboard state with Ctrl+Alt pressed (= AltGr)
        var keyState = new byte[256];
        keyState[VK_CONTROL] = 0x80;   // VK_CONTROL (generic)
        keyState[0xA2] = 0x80;         // VK_LCONTROL
        keyState[VK_MENU] = 0x80;      // VK_MENU (generic Alt)
        keyState[0xA5] = 0x80;         // VK_RMENU (Right Alt = AltGr)

        var scanCode = MapVirtualKeyEx(vk, MAPVK_VK_TO_VSC, hkl);
        var buffer = new StringBuilder(8);
        var result = ToUnicodeEx(vk, scanCode, keyState, buffer, buffer.Capacity, 0, hkl);

        if (result >= 1)
            return buffer.ToString(0, result);

        return null;
    }

    internal static void InjectScanCode(ushort scanCode, bool isDown, bool isExtended)
    {
        var flags = KEYEVENTF_SCANCODE | (isDown ? 0u : KEYEVENTF_KEYUP);
        if (isExtended) flags |= KEYEVENTF_EXTENDEDKEY;

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            union = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = scanCode,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInputWithRetry(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Hybrid injection: use Unicode for text keys with a resolved character,
    /// scan codes for everything else (modifiers, function keys, navigation).
    /// This is the AnyDesk "Auto" mode pattern - correct text across layouts,
    /// correct shortcuts/modifiers via scan codes.
    /// </summary>
    internal static void InjectHybridKey(ushort scanCode, ushort vkCode, bool isDown,
        bool isExtended, uint unicodeChar)
    {
        if (unicodeChar >= 0x20 && IsTextVk(vkCode))
        {
            InjectUnicodeChar(unicodeChar, isDown);
            return;
        }

        InjectScanCode(scanCode, isDown, isExtended);
    }

    /// <summary>
    /// Inject a Unicode character via KEYEVENTF_UNICODE. The character arrives at
    /// apps as WM_CHAR regardless of the host's active keyboard layout.
    /// Handles supplementary characters (emoji, CJK Extension B) via surrogate pairs.
    /// </summary>
    internal static void InjectUnicodeChar(uint codepoint, bool isDown)
    {
        if (codepoint <= 0xFFFF)
        {
            var flags = KEYEVENTF_UNICODE | (isDown ? 0u : KEYEVENTF_KEYUP);
            var input = new INPUT
            {
                type = INPUT_KEYBOARD,
                union = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = (ushort)codepoint,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
            SendInputWithRetry(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }
        else
        {
            // Supplementary character (U+10000 and above): surrogate pair.
            // Keydown: high surrogate first, then low. Keyup: low first, then high.
            var high = (ushort)(0xD800 + ((codepoint - 0x10000) >> 10));
            var low = (ushort)(0xDC00 + ((codepoint - 0x10000) & 0x3FF));

            var flags = KEYEVENTF_UNICODE | (isDown ? 0u : KEYEVENTF_KEYUP);
            INPUT[] inputs;

            if (isDown)
            {
                inputs = new[]
                {
                    MakeUnicodeInput(high, flags),
                    MakeUnicodeInput(low, flags),
                };
            }
            else
            {
                inputs = new[]
                {
                    MakeUnicodeInput(low, flags),
                    MakeUnicodeInput(high, flags),
                };
            }

            SendInputWithRetry((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }
    }

    private static INPUT MakeUnicodeInput(ushort scan, uint flags)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            union = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = scan,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    internal static bool IsTextVk(ushort vk) => vk is
        (>= 0x30 and <= 0x39) or  // 0-9
        (>= 0x41 and <= 0x5A) or  // A-Z
        (>= 0xBA and <= 0xC0) or  // OEM_1 through OEM_3 (punctuation)
        (>= 0xDB and <= 0xDF) or  // OEM_4 through OEM_8
        0xE2 or                     // OEM_102 (ISO backslash)
        0x20;                       // Space

    /// <summary>
    /// Activate a keyboard layout on the host by KLID string. Returns true if
    /// the layout was loaded. Saves the original layout for RestoreKeyboardLayout.
    /// Note: LoadKeyboardLayout with KLF_ACTIVATE is system-wide on Win8+.
    /// </summary>
    internal static bool ActivateKeyboardLayout(string klid)
    {
        if (_savedHostLayout == IntPtr.Zero)
        {
            var fgHwnd = GetForegroundWindow();
            var fgThread = GetWindowThreadProcessId(fgHwnd, out _);
            _savedHostLayout = GetKeyboardLayout(fgThread);
        }

        var hkl = LoadKeyboardLayout(klid, KLF_ACTIVATE | KLF_SUBSTITUTE_OK);
        if (hkl == IntPtr.Zero) return false;

        _activeHostKlid = klid;
        return true;
    }

    /// <summary>
    /// Restore the host's original keyboard layout (call on session disconnect).
    /// </summary>
    internal static void RestoreKeyboardLayout()
    {
        if (_savedHostLayout != IntPtr.Zero)
        {
            ActivateKeyboardLayoutApi(_savedHostLayout, 0);
            _savedHostLayout = IntPtr.Zero;
            _activeHostKlid = null;
        }
    }

    internal static void ReleaseAllModifiers()
    {
        ushort[] modifierVks =
        [
            VK_SHIFT, 0xA0, 0xA1,       // VK_SHIFT, VK_LSHIFT, VK_RSHIFT
            VK_CONTROL, 0xA2, 0xA3,     // VK_CONTROL, VK_LCONTROL, VK_RCONTROL
            VK_MENU, 0xA4, 0xA5,        // VK_MENU, VK_LMENU, VK_RMENU
            VK_LWIN, 0x5C               // VK_LWIN, VK_RWIN
        ];

        var inputs = new List<INPUT>();
        foreach (var vk in modifierVks)
            AddKeyInput(inputs, vk, isDown: false);

        if (inputs.Count > 0)
            SendInputWithRetry((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }
}
