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
        NotificationTapped?.Invoke(this, new NotificationTappedEventArgs { SessionId = sessionId });
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
