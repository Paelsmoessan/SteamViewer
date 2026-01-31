using SteamViewer.Common.Protocol;

namespace SteamViewer.Client.Core.Capture;

/// <summary>
/// Platform-agnostic interface for input injection.
/// </summary>
public interface IInputInjector : IDisposable
{
    /// <summary>
    /// Inject an input event into the system.
    /// </summary>
    /// <param name="inputEvent">The input event to inject</param>
    /// <param name="screenWidth">The remote screen width for coordinate scaling</param>
    /// <param name="screenHeight">The remote screen height for coordinate scaling</param>
    void InjectInput(InputEvent inputEvent, int screenWidth, int screenHeight);

    /// <summary>
    /// Check if input injection is available/permitted.
    /// </summary>
    bool IsAvailable { get; }
}
