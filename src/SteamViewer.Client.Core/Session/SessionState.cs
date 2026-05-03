using SteamViewer.Common.Protocol;

namespace SteamViewer.Client.Core.Session;

/// <summary>
/// Represents the current state of a session.
/// </summary>
public sealed class SessionState
{
    /// <summary>
    /// Current connection state.
    /// </summary>
    public ConnectionState ConnectionState { get; private set; } = ConnectionState.Idle;

    /// <summary>
    /// Current role (host or viewer).
    /// </summary>
    public Role Role { get; private set; } = Role.Host;

    /// <summary>
    /// Our client credentials.
    /// </summary>
    public ClientCredentials Credentials { get; }

    /// <summary>
    /// Peer's client ID (if connected).
    /// </summary>
    public string? PeerId { get; private set; }

    /// <summary>
    /// Peer's role (opposite of ours).
    /// </summary>
    public Role? PeerRole { get; private set; }

    /// <summary>
    /// Event fired when connection state changes.
    /// </summary>
    public event EventHandler<ConnectionState>? ConnectionStateChanged;

    /// <summary>
    /// Event fired when role changes.
    /// </summary>
    public event EventHandler<Role>? RoleChanged;

    /// <summary>
    /// Creates a new session state as host.
    /// </summary>
    public static SessionState NewHost(ClientCredentials credentials)
    {
        return new SessionState(credentials, Role.Host);
    }

    /// <summary>
    /// Creates a new session state as viewer.
    /// </summary>
    public static SessionState NewViewer(ClientCredentials credentials)
    {
        return new SessionState(credentials, Role.Viewer);
    }

    private SessionState(ClientCredentials credentials, Role role)
    {
        Credentials = credentials;
        Role = role;
    }

    /// <summary>
    /// Sets the connection state.
    /// </summary>
    public void SetConnectionState(ConnectionState state)
    {
        if (ConnectionState != state)
        {
            ConnectionState = state;
            ConnectionStateChanged?.Invoke(this, state);
        }
    }

    /// <summary>
    /// Connects to a peer.
    /// </summary>
    public void ConnectToPeer(string peerId)
    {
        PeerId = peerId;
        PeerRole = Role.Swap();
        SetConnectionState(ConnectionState.Connecting);
    }

    /// <summary>
    /// Marks the connection as established.
    /// </summary>
    public void MarkConnected()
    {
        SetConnectionState(ConnectionState.Connected);
    }

    /// <summary>
    /// Disconnects from the current peer.
    /// </summary>
    public void Disconnect()
    {
        PeerId = null;
        PeerRole = null;
        SetConnectionState(ConnectionState.Disconnected);
    }

    /// <summary>
    /// Swaps roles with the peer.
    /// </summary>
    public void SwapRoles()
    {
        Role = Role.Swap();
        if (PeerRole.HasValue)
        {
            PeerRole = PeerRole.Value.Swap();
        }
        RoleChanged?.Invoke(this, Role);
    }

    /// <summary>
    /// Sets the role directly.
    /// </summary>
    public void SetRole(Role role)
    {
        if (Role != role)
        {
            Role = role;
            RoleChanged?.Invoke(this, role);
        }
    }

    /// <summary>
    /// Checks if currently hosting.
    /// </summary>
    public bool IsHost => Role == Role.Host;

    /// <summary>
    /// Checks if currently viewing.
    /// </summary>
    public bool IsViewer => Role == Role.Viewer;

    /// <summary>
    /// Checks if connected to a peer.
    /// </summary>
    public bool IsConnected => ConnectionState == ConnectionState.Connected && PeerId != null;
}
