using Foundation;
using UserNotifications;

namespace PolyPilot.Services;

public class NotificationManagerService : INotificationManagerService
{
    private bool _hasPermission;
    
    public bool HasPermission => _hasPermission;
    
    public event EventHandler<NotificationTappedEventArgs>? NotificationTapped;

    public async Task InitializeAsync()
    {
        var center = UNUserNotificationCenter.Current;
        center.Delegate = new NotificationDelegate(this);
        
        var settings = await center.GetNotificationSettingsAsync();
        _hasPermission = settings.AuthorizationStatus == UNAuthorizationStatus.Authorized;
        
        if (settings.AuthorizationStatus == UNAuthorizationStatus.NotDetermined)
        {
            var (granted, _) = await center.RequestAuthorizationAsync(
                UNAuthorizationOptions.Alert | UNAuthorizationOptions.Badge | UNAuthorizationOptions.Sound);
            _hasPermission = granted;
        }
    }

    public async Task SendNotificationAsync(string title, string body, string? sessionId = null)
    {
        if (!_hasPermission)
        {
            var center = UNUserNotificationCenter.Current;
            var (granted, _) = await center.RequestAuthorizationAsync(
                UNAuthorizationOptions.Alert | UNAuthorizationOptions.Badge | UNAuthorizationOptions.Sound);
            _hasPermission = granted;
            if (!_hasPermission)
                return;
        }

        var content = new UNMutableNotificationContent
        {
            Title = title,
            Body = body,
            Sound = UNNotificationSound.Default
        };

        if (sessionId != null)
        {
            content.UserInfo = NSDictionary.FromObjectAndKey(
                new NSString(sessionId), 
                new NSString("sessionId"));

            // Write to sidecar so the running instance can navigate even if the OS launches
            // a second instance (which activates us via AppleScript but can't fire the delegate).
            WritePendingNavigation(sessionId);
        }

        var trigger = UNTimeIntervalNotificationTrigger.CreateTrigger(0.1, false);
        var request = UNNotificationRequest.FromIdentifier(
            Guid.NewGuid().ToString(), 
            content, 
            trigger);

        await UNUserNotificationCenter.Current.AddNotificationRequestAsync(request);
    }

    internal void OnNotificationTapped(string? sessionId)
    {
        // Clear the sidecar: the delegate fired normally in the running instance so
        // App.OnWindowActivated doesn't need to re-navigate via file.
        if (sessionId != null)
            DeletePendingNavigation();

        NotificationTapped?.Invoke(this, new NotificationTappedEventArgs { SessionId = sessionId });
    }

    private static string PendingNavigationPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".polypilot", "pending-navigation.json");

    private static void WritePendingNavigation(string sessionId)
    {
        try
        {
            var dir = Path.GetDirectoryName(PendingNavigationPath)!;
            Directory.CreateDirectory(dir);
            // writtenAt enables the consumer to discard stale sidecars (e.g. user ignored
            // the notification and later brings the app to the foreground by other means).
            File.WriteAllText(PendingNavigationPath,
                System.Text.Json.JsonSerializer.Serialize(new { sessionId, writtenAt = DateTime.UtcNow }));
        }
        catch { /* Best effort */ }
    }

    internal static void DeletePendingNavigation()
    {
        try { File.Delete(PendingNavigationPath); }
        catch { /* Best effort */ }
    }

    private class NotificationDelegate : UNUserNotificationCenterDelegate
    {
        private readonly NotificationManagerService _service;

        public NotificationDelegate(NotificationManagerService service)
        {
            _service = service;
        }

        public override void WillPresentNotification(
            UNUserNotificationCenter center, 
            UNNotification notification, 
            Action<UNNotificationPresentationOptions> completionHandler)
        {
            // Show notification even when app is in foreground
            completionHandler(UNNotificationPresentationOptions.Banner | 
                              UNNotificationPresentationOptions.Sound);
        }

        public override void DidReceiveNotificationResponse(
            UNUserNotificationCenter center, 
            UNNotificationResponse response, 
            Action completionHandler)
        {
            var userInfo = response.Notification.Request.Content.UserInfo;
            var sessionId = userInfo.ObjectForKey(new NSString("sessionId"))?.ToString();
            
            _service.OnNotificationTapped(sessionId);
            completionHandler();
        }
    }
}
