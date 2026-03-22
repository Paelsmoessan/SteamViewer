using System.Text.Json.Serialization;

namespace SteamViewer.Common.Protocol;

/// <summary>
/// Connection state machine states.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConnectionState>))]
public enum ConnectionState
{
    Idle,
    Registering,
    Registered,
    Connecting,
    Connected,
    Reconnecting,
    Disconnected,
    Error
}

/// <summary>
/// Session role (host shares screen, viewer watches).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<Role>))]
public enum Role
{
    Host,
    Viewer
}

/// <summary>
/// Extension methods for Role.
/// </summary>
public static class RoleExtensions
{
    public static Role Swap(this Role role) => role switch
    {
        Role.Host => Role.Viewer,
        Role.Viewer => Role.Host,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };
}
