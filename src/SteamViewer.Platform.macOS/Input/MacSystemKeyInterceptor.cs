using SteamViewer.Client.Core.Capture;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Platform.macOS.Input;

/// <summary>
/// No-op stub for macOS. System key interception not yet implemented.
/// </summary>
public sealed class MacSystemKeyInterceptor : ISystemKeyInterceptor
{
    public bool IsInstalled => false;
    public bool FullCapture { get; set; }
    public event Action<string, bool, bool>? SystemKeyIntercepted;
    public event Action<ushort, ushort, bool, bool, KeyModifiers, uint>? KeyEventCaptured;
    public event Action<string>? LayoutChanged;
    public void Install() { }
    public void Uninstall() { }
    public void SetViewerHwnd(IntPtr hwnd) { }
    public string? GetCurrentKeyboardLayoutId() => null;
    public void Dispose() { }
}
