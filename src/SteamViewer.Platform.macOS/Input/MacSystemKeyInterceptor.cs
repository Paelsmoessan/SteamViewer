using SteamViewer.Client.Core.Capture;

namespace SteamViewer.Platform.macOS.Input;

/// <summary>
/// No-op stub for macOS. System key interception not yet implemented.
/// </summary>
public sealed class MacSystemKeyInterceptor : ISystemKeyInterceptor
{
    public bool IsInstalled => false;
    public event Action<string, bool, bool>? SystemKeyIntercepted;
    public void Install() { }
    public void Uninstall() { }
    public void Dispose() { }
}
