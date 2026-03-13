using SteamViewer.Client.Core.Elevation;

namespace SteamViewer.Platform.macOS.Elevation;

/// <summary>
/// No-op stub for macOS. macOS uses accessibility permissions (not elevation pipes).
/// Can be fleshed out later with osascript/Authorization Services if needed.
/// </summary>
public sealed class MacElevationService : IElevationService
{
    public bool IsAdminConnected => false;
    public bool IsSystemConnected => false;
    public bool IsSecureDesktopActive => false;

    public event Action<byte[], int, int>? OnSecureDesktopFrame;
    public event Action<bool>? OnSecureDesktopStateChanged;
    public event Action<bool>? OnAdminStateChanged;
    public event Action<bool>? OnSystemStateChanged;

    public Task<bool> RequestAdminElevationAsync() => Task.FromResult(false);
    public Task<bool> RequestSystemElevationAsync() => Task.FromResult(false);
    public Task<bool> InjectInputAsync(string inputJson, int screenWidth, int screenHeight) => Task.FromResult(false);
    public Task<bool> LockWorkStationAsync() => Task.FromResult(false);
    public Task<bool> SendSASAsync() => Task.FromResult(false);
    public Task<bool> RebootAsync(string clientId, string passwordHash, string viewerPeerId,
        string? serverUrl = null, string[]? stunUrls = null,
        string[]? turnUrls = null, string? turnUsername = null, string? turnCredential = null) => Task.FromResult(false);
    public Task<bool> RunElevatedAsync(string path, string? args) => Task.FromResult(false);
    public Task<bool> RunAsSystemAsync(string path, string? args) => Task.FromResult(false);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
