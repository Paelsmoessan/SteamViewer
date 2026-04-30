using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SteamViewer.Client.Core.Network;

public sealed class TurnConfigService
{
    private readonly ILogger<TurnConfigService> _logger;
    private readonly string _serverBaseUrl;
    private TurnConfig? _cached;

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

    public async Task<TurnConfig> GetConfigAsync()
    {
        if (_cached != null)
            return _cached;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await http.GetFromJsonAsync<TurnConfigResponse>(
                $"{_serverBaseUrl}/api/turn-config");

            if (response is { Enabled: true, Urls.Length: > 0, Username: not null, Credential: not null })
            {
                _cached = new TurnConfig(true, response.Urls, response.Username, response.Credential);
                _logger.LogInformation("TURN config fetched from server ({UrlCount} URLs)", response.Urls.Length);
            }
            else
            {
                _cached = TurnConfig.Disabled;
                _logger.LogInformation("TURN not enabled on server");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch TURN config from server, TURN relay disabled");
            _cached = TurnConfig.Disabled;
        }

        return _cached;
    }

    public void InvalidateCache() => _cached = null;

    private record TurnConfigResponse(bool Enabled, string[] Urls, string? Username, string? Credential);
}

public record TurnConfig(bool Enabled, string[] Urls, string? Username, string? Credential)
{
    public static readonly TurnConfig Disabled = new(false, Array.Empty<string>(), null, null);
}
