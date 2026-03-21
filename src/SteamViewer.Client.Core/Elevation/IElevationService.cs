namespace SteamViewer.Client.Core.Elevation;

/// <summary>
/// Cross-platform interface for elevated privilege operations.
/// On Windows, wraps the admin (ElevatedHelperClient) and SYSTEM (SystemHelperClient) pipes.
/// On macOS, stub implementation (macOS uses accessibility permissions, not elevation pipes).
/// </summary>
public interface IElevationService : IAsyncDisposable
{
    #region State

    /// <summary>Whether the admin-level helper pipe is connected.</summary>
    bool IsAdminConnected { get; }

    /// <summary>Whether the SYSTEM-level helper pipe is connected.</summary>
    bool IsSystemConnected { get; }

    /// <summary>Whether the Secure Desktop (Winlogon) is currently active.</summary>
    bool IsSecureDesktopActive { get; }

    #endregion

    #region Elevation Lifecycle

    /// <summary>
    /// Request admin-level elevation. On Windows, this launches the elevated helper
    /// process (triggers UAC) and connects via named pipe.
    /// </summary>
    /// <returns>True if admin helper connected successfully.</returns>
    Task<bool> RequestAdminElevationAsync();

    /// <summary>
    /// Request SYSTEM-level elevation. Requires admin elevation first.
    /// On Windows, creates a scheduled task running as SYSTEM, which starts
    /// a helper process and connects via a second named pipe.
    /// </summary>
    /// <returns>True if SYSTEM helper connected successfully.</returns>
    Task<bool> RequestSystemElevationAsync();

    #endregion

    #region Input Injection

    /// <summary>
    /// Inject input through the highest available elevated pipe.
    /// Routes to SYSTEM helper (if Secure Desktop active + SYSTEM connected),
    /// else to admin helper (if connected), else returns false for local fallback.
    /// Fire-and-forget semantics — does not await pipe response.
    /// </summary>
    /// <param name="inputJson">Raw JSON input event from the viewer.</param>
    /// <param name="screenWidth">Capture screen width for coordinate scaling.</param>
    /// <param name="screenHeight">Capture screen height for coordinate scaling.</param>
    /// <returns>True if input was sent via an elevated pipe; false if caller should fall back to local injection.</returns>
    Task<bool> InjectInputAsync(string inputJson, int screenWidth, int screenHeight);

    #endregion

    #region Admin Features

    /// <summary>
    /// Lock the workstation. Uses LockWorkStation() API — Win+L is blocked by Windows from SendInput.
    /// Does not require elevation.
    /// </summary>
    Task<bool> LockWorkStationAsync();

    /// <summary>
    /// Send the Secure Attention Sequence (Ctrl+Alt+Del) via elevated helper.
    /// NOTE: Dead end without Windows service or uiAccess=true manifest (Authenticode + Program Files).
    /// Kept for future use if we convert to a service or sign the exe.
    /// </summary>
    Task<bool> SendSASAsync();

    /// <summary>
    /// Reboot the host with auto-restart credentials saved for reconnection.
    /// On Windows, writes RunOnceEx registry key and boot relay schtask via admin helper.
    /// Server URL + STUN/TURN config saved for boot relay WebRTC reconnection.
    /// </summary>
    Task<bool> RebootAsync(string clientId, string passwordHash, string viewerPeerId,
        string? serverUrl = null, string[]? stunUrls = null,
        string[]? turnUrls = null, string? turnUsername = null, string? turnCredential = null);

    /// <summary>
    /// Run a process elevated (as admin) without an additional UAC prompt.
    /// The process inherits the admin helper's elevation token.
    /// </summary>
    Task<bool> RunElevatedAsync(string path, string? args);

    #endregion

    #region SYSTEM Features

    /// <summary>
    /// Run a process as NT AUTHORITY\SYSTEM in the user's desktop session.
    /// Requires SYSTEM elevation. Uses the native ServiceUI technique (P/Invoke).
    /// </summary>
    Task<bool> RunAsSystemAsync(string path, string? args);

    #endregion

    #region Secure Desktop (Phase 2)

    /// <summary>
    /// Raised when a raw BGRA frame is captured from the Secure Desktop (Winlogon).
    /// Parameters: (bgraData, width, height, stride).
    /// </summary>
    event Action<byte[], int, int, int>? OnSecureDesktopFrame;

    /// <summary>
    /// Raised when the Secure Desktop state changes.
    /// Parameter: true = Secure Desktop active (UAC visible), false = returned to normal desktop.
    /// </summary>
    event Action<bool>? OnSecureDesktopStateChanged;

    #endregion

    #region Status Events

    /// <summary>
    /// Raised when the admin helper connection state changes.
    /// Parameter: true = connected, false = disconnected.
    /// </summary>
    event Action<bool>? OnAdminStateChanged;

    /// <summary>
    /// Raised when the SYSTEM helper connection state changes.
    /// Parameter: true = connected, false = disconnected.
    /// </summary>
    event Action<bool>? OnSystemStateChanged;

    #endregion
}
