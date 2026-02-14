namespace PolyPilot.Services;

public class NotificationManagerService : INotificationManagerService
{
    public bool HasPermission => true;

    public event EventHandler<NotificationTappedEventArgs>? NotificationTapped;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task SendNotificationAsync(string title, string body, string? sessionId = null)
    {
        // TODO: Implement Windows toast notifications via Microsoft.Toolkit.Uwp.Notifications
        return Task.CompletedTask;
    }
}
