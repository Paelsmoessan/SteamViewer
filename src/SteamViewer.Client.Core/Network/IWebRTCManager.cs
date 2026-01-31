namespace SteamViewer.Client.Core.Network;

/// <summary>
/// Interface for WebRTC peer connection management.
/// </summary>
public interface IWebRTCManager
{
    /// <summary>
    /// Gets the current connection state.
    /// </summary>
    string ConnectionState { get; }

    /// <summary>
    /// Gets whether the data channel is open.
    /// </summary>
    bool IsDataChannelOpen { get; }

    /// <summary>
    /// Fired when the connection state changes.
    /// </summary>
    event EventHandler<string>? ConnectionStateChanged;

    /// <summary>
    /// Fired when the data channel opens.
    /// </summary>
    event EventHandler? DataChannelOpened;

    /// <summary>
    /// Fired when the data channel closes.
    /// </summary>
    event EventHandler? DataChannelClosed;

    /// <summary>
    /// Fired when video data is received.
    /// </summary>
    event EventHandler<byte[]>? VideoDataReceived;

    /// <summary>
    /// Fired when input data is received.
    /// </summary>
    event EventHandler<byte[]>? InputDataReceived;

    /// <summary>
    /// Initializes WebRTC as the host (creates offer).
    /// </summary>
    Task InitializeAsHostAsync(string peerId);

    /// <summary>
    /// Initializes WebRTC as the viewer (waits for offer).
    /// </summary>
    Task InitializeAsViewerAsync();

    /// <summary>
    /// Handles an incoming SDP offer.
    /// </summary>
    Task HandleOfferAsync(string sdp, string peerId);

    /// <summary>
    /// Handles an incoming SDP answer.
    /// </summary>
    Task HandleAnswerAsync(string sdp);

    /// <summary>
    /// Handles an incoming ICE candidate.
    /// </summary>
    Task HandleIceCandidateAsync(string candidate, string? sdpMid, int? sdpMLineIndex);

    /// <summary>
    /// Sends video data over the video channel.
    /// </summary>
    Task SendVideoDataAsync(byte[] data);

    /// <summary>
    /// Sends input data over the input channel.
    /// </summary>
    Task SendInputDataAsync(byte[] data);

    /// <summary>
    /// Closes the WebRTC connection.
    /// </summary>
    Task CloseAsync();
}
