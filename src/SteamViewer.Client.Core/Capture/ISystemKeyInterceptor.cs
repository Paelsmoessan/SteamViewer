using SteamViewer.Common.Protocol;

namespace SteamViewer.Client.Core.Capture;

/// <summary>
/// Intercepts OS-level system keys (Win, Alt+Tab) that don't reach WebView2.
/// Platform-specific: WH_KEYBOARD_LL on Windows, no-op on macOS.
/// </summary>
public interface ISystemKeyInterceptor : IDisposable
{
    /// <summary>Start intercepting system keys. Called when input is locked.</summary>
    void Install();

    /// <summary>Stop intercepting system keys. Called when input is unlocked.</summary>
    void Uninstall();

    /// <summary>Whether the hook is currently installed.</summary>
    bool IsInstalled { get; }

    /// <summary>
    /// Fired when a system key is intercepted.
    /// Parameters: (key string matching JS e.key format, isKeyDown)
    /// </summary>
    event Action<string, bool, bool>? SystemKeyIntercepted; // (key, isDown, isAltHeld)

    event Action<ushort, ushort, bool, bool, KeyModifiers>? KeyEventCaptured; // (scanCode, vkCode, isDown, isExtended, modifiers)

    bool FullCapture { get; set; }

    void SetViewerHwnd(IntPtr hwnd);
}
