namespace PolyPilot.Services;

/// <summary>
/// Manages the minimized popup window that appears when a session completes while
/// the main PolyPilot window is not focused.
/// </summary>
public interface IMinimizedModeService
{
    /// <summary>
    /// Whether the main PolyPilot window is currently focused.
    /// </summary>
    bool IsMainWindowFocused { get; set; }

    /// <summary>
    /// Called when a session completes while the main window is not focused.
    /// Queues the popup and opens it if none is currently active.
    /// </summary>
    void OnSessionCompleted(string sessionName, string sessionId, string? lastResponse);
}
