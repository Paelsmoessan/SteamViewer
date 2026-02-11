using Microsoft.Extensions.Logging;
using SteamViewer.Client.Core.Elevation;

namespace SteamViewer.Platform.Windows.Elevation;

/// <summary>
/// Windows implementation of IElevationService.
/// Owns the lifecycle of ElevatedHelperClient (admin) and SystemHelperClient (SYSTEM).
/// Routes input through the highest available elevation tier.
/// Forwards Secure Desktop events from SystemHelperClient to consumers (HostSession).
/// </summary>
public sealed class WindowsElevationService : IElevationService
{
    private readonly ILogger<WindowsElevationService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private ElevatedHelperClient? _adminHelper;
    private SystemHelperClient? _systemHelper;
    private bool _disposed;

    public bool IsAdminConnected => _adminHelper?.IsConnected ?? false;
    public bool IsSystemConnected => _systemHelper?.IsConnected ?? false;
    public bool IsSecureDesktopActive => _systemHelper?.IsSecureDesktopActive ?? false;

    public event Action<byte[], int, int>? OnSecureDesktopFrame;
    public event Action<bool>? OnSecureDesktopStateChanged;
    public event Action<bool>? OnAdminStateChanged;
    public event Action<bool>? OnSystemStateChanged;

    public WindowsElevationService(ILogger<WindowsElevationService> logger, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public async Task<bool> RequestAdminElevationAsync()
    {
        if (IsAdminConnected)
        {
            _logger.LogInformation("Admin helper already connected");
            return true;
        }

        try
        {
            var helperLogger = _loggerFactory.CreateLogger<ElevatedHelperClient>();
            _adminHelper = new ElevatedHelperClient(helperLogger);
            var success = await _adminHelper.LaunchAndConnectAsync();

            if (success)
            {
                _logger.LogInformation("Admin helper connected — admin features enabled");
                OnAdminStateChanged?.Invoke(true);

                // Auto-launch SYSTEM helper in background for Secure Desktop capture.
                // Fire-and-forget — admin features work even if SYSTEM fails.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var systemSuccess = await RequestSystemElevationAsync();
                        if (!systemSuccess)
                            _logger.LogWarning("Auto SYSTEM launch failed (non-fatal)");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Auto SYSTEM launch failed (non-fatal)");
                    }
                });

                return true;
            }

            _logger.LogWarning("Admin helper failed to connect (UAC denied or error)");
            await _adminHelper.DisposeAsync();
            _adminHelper = null;
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch admin helper");
            if (_adminHelper != null)
            {
                await _adminHelper.DisposeAsync();
                _adminHelper = null;
            }
            return false;
        }
    }

    public async Task<bool> RequestSystemElevationAsync()
    {
        if (IsSystemConnected)
        {
            _logger.LogInformation("SYSTEM helper already connected");
            return true;
        }

        if (!IsAdminConnected)
        {
            _logger.LogWarning("Cannot launch SYSTEM helper: admin helper not connected");
            return false;
        }

        try
        {
            var helperLogger = _loggerFactory.CreateLogger<SystemHelperClient>();
            _systemHelper = new SystemHelperClient(helperLogger, _adminHelper!);

            // Subscribe to Secure Desktop events before connecting
            _systemHelper.OnSecureDesktopFrame += HandleSecureDesktopFrame;
            _systemHelper.OnSecureDesktopStateChanged += HandleSecureDesktopStateChanged;

            var success = await _systemHelper.LaunchAndConnectAsync();

            if (success)
            {
                _logger.LogInformation("SYSTEM helper connected — SYSTEM features enabled");
                OnSystemStateChanged?.Invoke(true);
                return true;
            }

            _logger.LogWarning("SYSTEM helper failed to connect");
            UnsubscribeSystemHelper();
            await _systemHelper.DisposeAsync();
            _systemHelper = null;
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch SYSTEM helper");
            if (_systemHelper != null)
            {
                UnsubscribeSystemHelper();
                await _systemHelper.DisposeAsync();
                _systemHelper = null;
            }
            return false;
        }
    }

    private int _inputRouteCount;

    public async Task<bool> InjectInputAsync(string inputJson, int screenWidth, int screenHeight)
    {
        // Route input through the highest available elevation tier:
        // 1. If Secure Desktop active + SYSTEM connected → SYSTEM helper (only way to reach Winlogon)
        // 2. If SYSTEM connected → SYSTEM helper (can inject on both desktops)
        // 3. If admin connected → admin helper (UIPI bypass)
        // 4. Return false → caller falls back to local injection

        _inputRouteCount++;

        if (_systemHelper?.IsConnected == true)
        {
            if (_inputRouteCount <= 3 || _inputRouteCount % 500 == 0)
                _logger.LogInformation("Input route #{Count}: SYSTEM helper", _inputRouteCount);
            await _systemHelper.SendInputEventAsync(inputJson, screenWidth, screenHeight);
            return true;
        }

        if (_adminHelper?.IsConnected == true)
        {
            if (_inputRouteCount <= 3 || _inputRouteCount % 500 == 0)
                _logger.LogInformation("Input route #{Count}: admin helper", _inputRouteCount);
            await _adminHelper.SendInputEventAsync(inputJson, screenWidth, screenHeight);
            return true;
        }

        _logger.LogWarning("Input route #{Count}: NO helper connected — input dropped", _inputRouteCount);
        return false;
    }

    public async Task<bool> SendSASAsync()
    {
        _logger.LogInformation("SendSAS requested. SYSTEM connected: {System}, Admin connected: {Admin}",
            _systemHelper?.IsConnected == true, _adminHelper?.IsConnected == true);

        // Prefer SYSTEM helper for SAS — SendSAS(false) requires SeTcbPrivilege which only SYSTEM has.
        // From admin context, the P/Invoke succeeds but Windows silently ignores it.
        if (_systemHelper?.IsConnected == true)
        {
            _logger.LogInformation("Routing SAS through SYSTEM helper");
            var result = await _systemHelper.SendSASAsync();
            if (result) return true;
            _logger.LogWarning("SYSTEM helper SendSAS failed, falling through to admin");
        }

        if (_adminHelper?.IsConnected == true)
        {
            _logger.LogWarning("Routing SAS through admin helper — may be silently ignored by Windows (SeTcbPrivilege required)");
            return await _adminHelper.SendSASAsync();
        }

        _logger.LogWarning("SendSAS failed: no elevated helper connected");
        return false;
    }

    public async Task<bool> RebootAsync(string clientId, string passwordHash, string viewerPeerId)
    {
        if (_adminHelper?.IsConnected != true)
        {
            _logger.LogWarning("Reboot failed: admin helper not connected");
            return false;
        }

        return await _adminHelper.RebootAsync(clientId, passwordHash, viewerPeerId);
    }

    public async Task<bool> RunElevatedAsync(string path, string? args)
    {
        if (_adminHelper?.IsConnected != true)
        {
            _logger.LogWarning("RunElevated failed: admin helper not connected");
            return false;
        }

        return await _adminHelper.RunElevatedAsync(path, args);
    }

    public async Task<bool> RunAsSystemAsync(string path, string? args)
    {
        if (_systemHelper?.IsConnected != true)
        {
            _logger.LogWarning("RunAsSystem failed: SYSTEM helper not connected");
            return false;
        }

        return await _systemHelper.RunAsSystemAsync(path, args);
    }

    #region Secure Desktop event forwarding

    private int _sdFrameForwardCount;

    private void HandleSecureDesktopFrame(byte[] jpegData, int width, int height)
    {
        _sdFrameForwardCount++;
        if (_sdFrameForwardCount <= 3 || _sdFrameForwardCount % 100 == 0)
            _logger.LogInformation("Forwarding SD frame #{Count}: {Bytes}b {W}x{H}, subscribers={Sub}",
                _sdFrameForwardCount, jpegData.Length, width, height,
                OnSecureDesktopFrame?.GetInvocationList().Length ?? 0);
        OnSecureDesktopFrame?.Invoke(jpegData, width, height);
    }

    private void HandleSecureDesktopStateChanged(bool active)
    {
        _logger.LogInformation("Secure Desktop state changed: {Active}, subscribers={Sub}",
            active, OnSecureDesktopStateChanged?.GetInvocationList().Length ?? 0);
        OnSecureDesktopStateChanged?.Invoke(active);
    }

    private void UnsubscribeSystemHelper()
    {
        if (_systemHelper != null)
        {
            _systemHelper.OnSecureDesktopFrame -= HandleSecureDesktopFrame;
            _systemHelper.OnSecureDesktopStateChanged -= HandleSecureDesktopStateChanged;
        }
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_systemHelper != null)
        {
            UnsubscribeSystemHelper();
            await _systemHelper.DisposeAsync();
            _systemHelper = null;
            OnSystemStateChanged?.Invoke(false);
        }

        if (_adminHelper != null)
        {
            await _adminHelper.DisposeAsync();
            _adminHelper = null;
            OnAdminStateChanged?.Invoke(false);
        }
    }
}
