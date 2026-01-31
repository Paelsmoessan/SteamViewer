namespace SteamViewer.Client.Core.Session;

/// <summary>
/// Configuration for a remote desktop session.
/// </summary>
public sealed class SessionConfig
{
    /// <summary>
    /// Signaling server WebSocket URL.
    /// </summary>
    public string SignalingServerUrl { get; set; } = "ws://localhost:8080/ws";

    /// <summary>
    /// STUN servers for NAT traversal.
    /// </summary>
    public List<string> StunServers { get; set; } = new()
    {
        "stun:stun.l.google.com:19302",
        "stun:stun1.l.google.com:19302"
    };

    /// <summary>
    /// Enable verbose logging.
    /// </summary>
    public bool Verbose { get; set; }

    /// <summary>
    /// Target frame rate for screen capture.
    /// </summary>
    public int TargetFps { get; set; } = 30;

    /// <summary>
    /// Video bitrate in bits per second.
    /// </summary>
    public int VideoBitrate { get; set; } = 4_000_000;
}
