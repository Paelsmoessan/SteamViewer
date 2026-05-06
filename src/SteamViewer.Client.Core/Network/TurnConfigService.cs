using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SteamViewer.Client.Core.Network;

public sealed class TurnConfigService
{
    private readonly ILogger<TurnConfigService> _logger;
    private readonly string _serverBaseUrl;
    private TurnConfig? _cached;
    private string? _cachedForClientId;
    private DateTimeOffset _cacheExpiry;

    public TurnConfigService(IConfiguration configuration, ILogger<TurnConfigService> logger)
    {
        _logger = logger;
        var wsUrl = configuration["SignalingServer"]
                    ?? throw new InvalidOperationException("SignalingServer not configured");

        _serverBaseUrl = wsUrl
            .Replace("wss://", "https://")
            .Replace("ws://", "http://")
            .TrimEnd('/');

        if (_serverBaseUrl.EndsWith("/ws"))
            _serverBaseUrl = _serverBaseUrl[..^3];
    }

    /// <summary>
    /// Fetch TURN credentials for the given registered clientId. Server issues ephemeral
    /// HMAC-signed creds bound to this clientId (10-minute expiry); we refresh on cache miss.
    /// Cache is per-clientId so a viewer requesting creds for two different sessions does
    /// not accidentally reuse another session's creds.
    /// </summary>
    public async Task<TurnConfig> GetConfigAsync(string clientId)
    {
        if (string.IsNullOrEmpty(clientId))
            return TurnConfig.Disabled;

        // Reuse cache only if it's for the same clientId AND not close to expiry.
        // 8-minute reuse window leaves 2 minutes margin against the server's 10-minute TTL.
        if (_cached != null && _cachedForClientId == clientId && DateTimeOffset.UtcNow < _cacheExpiry)
            return _cached;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var url = $"{_serverBaseUrl}/api/turn-config?clientId={Uri.EscapeDataString(clientId)}";
            var response = await http.GetFromJsonAsync<TurnConfigResponse>(url);

            if (response is { Enabled: true, Urls.Length: > 0, Username: not null, Credential: not null })
            {
                _cached = new TurnConfig(true, response.Urls, response.Username, response.Credential);
                _cachedForClientId = clientId;
                _cacheExpiry = DateTimeOffset.UtcNow.AddMinutes(8);
                _logger.LogInformation("TURN config fetched from server for {ClientId} ({UrlCount} URLs)",
                    clientId, response.Urls.Length);
            }
            else
            {
                _cached = TurnConfig.Disabled;
                _cachedForClientId = clientId;
                _cacheExpiry = DateTimeOffset.UtcNow.AddMinutes(8);
                _logger.LogInformation("TURN not enabled on server (or refused for clientId {ClientId})", clientId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch TURN config for {ClientId}, TURN relay disabled", clientId);
            _cached = TurnConfig.Disabled;
            _cachedForClientId = clientId;
            _cacheExpiry = DateTimeOffset.UtcNow.AddMinutes(1); // Short cache on failure - retry sooner
        }

        return _cached;
    }

    public void InvalidateCache()
    {
        _cached = null;
        _cachedForClientId = null;
    }

    private record TurnConfigResponse(bool Enabled, string[] Urls, string? Username, string? Credential);
}

public record TurnConfig(bool Enabled, string[] Urls, string? Username, string? Credential)
{
    public static readonly TurnConfig Disabled = new(false, Array.Empty<string>(), null, null);
}
