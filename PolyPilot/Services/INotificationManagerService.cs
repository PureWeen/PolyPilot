namespace PolyPilot.Services;

/// <summary>
/// Cross-platform service for sending local notifications.
/// </summary>
public interface INotificationManagerService
{
    /// <summary>
    /// Initializes the notification system and requests permissions if needed.
    /// </summary>
    Task InitializeAsync();
    
    /// <summary>
    /// Sends a local notification immediately.
    /// </summary>
    /// <param name="title">Notification title (e.g., session name)</param>
    /// <param name="body">Notification body (e.g., status message)</param>
    /// <param name="sessionId">Optional session ID for deep linking</param>
    Task SendNotificationAsync(string title, string body, string? sessionId = null);
    
    /// <summary>
    /// Whether the user has granted notification permissions.
    /// </summary>
    bool HasPermission { get; }
    
    /// <summary>
    /// Event raised when the user taps a notification.
    /// </summary>
    event EventHandler<NotificationTappedEventArgs>? NotificationTapped;
}

public class NotificationTappedEventArgs : EventArgs
{
    public string? SessionId { get; init; }
}
