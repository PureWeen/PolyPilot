namespace PolyPilot.Services;

public class NotificationManagerService : INotificationManagerService
{
    public bool HasPermission => false;

    public event EventHandler<NotificationTappedEventArgs>? NotificationTapped
    {
        add { }
        remove { }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task SendNotificationAsync(string title, string body, string? sessionId = null)
        => Task.CompletedTask;
}
