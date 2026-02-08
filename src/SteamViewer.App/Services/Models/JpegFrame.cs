namespace SteamViewer.App.Services;

/// <summary>
/// JPEG frame data for cross-window video relay.
/// </summary>
public record JpegFrame(string Base64Data, int Width, int Height);
