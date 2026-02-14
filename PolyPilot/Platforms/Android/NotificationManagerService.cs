using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;

namespace PolyPilot.Services;

public class NotificationManagerService : INotificationManagerService
{
    private const string ChannelId = "polypilot_session_alerts";
    private const string ChannelName = "Session Alerts";
    private const string ChannelDescription = "Notifications when sessions need attention";
    private const int NotificationIdBase = 10000;
    
    private static int _notificationCounter;
    private bool _hasPermission;
    private NotificationManager? _notificationManager;
    
    public bool HasPermission => _hasPermission;
    
    public event EventHandler<NotificationTappedEventArgs>? NotificationTapped;

    public Task InitializeAsync()
    {
        var context = Platform.AppContext;
        _notificationManager = context.GetSystemService(Context.NotificationService) as NotificationManager;
        
        CreateNotificationChannel();
        CheckPermission();
        
        return Task.CompletedTask;
    }

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O || _notificationManager == null)
            return;

        var channel = new NotificationChannel(ChannelId, ChannelName, NotificationImportance.High)
        {
            Description = ChannelDescription
        };
        channel.EnableVibration(true);
        channel.SetShowBadge(true);
        
        _notificationManager.CreateNotificationChannel(channel);
    }

    private void CheckPermission()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            _hasPermission = ContextCompat.CheckSelfPermission(
                Platform.AppContext, 
                Manifest.Permission.PostNotifications) == Permission.Granted;
        }
        else
        {
            // Pre-Android 13 doesn't require runtime permission
            _hasPermission = true;
        }
    }

    public async Task SendNotificationAsync(string title, string body, string? sessionId = null)
    {
        if (_notificationManager == null)
            return;

        // Request permission if needed (Android 13+)
        if (!_hasPermission && Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            var status = await Permissions.RequestAsync<NotificationPermission>();
            _hasPermission = status == PermissionStatus.Granted;
            if (!_hasPermission)
                return;
        }

        var context = Platform.AppContext;
        var intent = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName ?? "");
        
        if (intent != null && sessionId != null)
        {
            intent.PutExtra("sessionId", sessionId);
            intent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        }

        var pendingIntent = PendingIntent.GetActivity(
            context,
            Interlocked.Increment(ref _notificationCounter),
            intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var builder = new NotificationCompat.Builder(context, ChannelId)
            .SetSmallIcon(Resource.Mipmap.appicon_foreground)
            .SetContentTitle(title)
            .SetContentText(body)
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(body))
            .SetAutoCancel(true)
            .SetContentIntent(pendingIntent)
            .SetPriority(NotificationCompat.PriorityHigh)
            .SetDefaults((int)NotificationDefaults.Vibrate);

        _notificationManager.Notify(
            NotificationIdBase + Interlocked.Increment(ref _notificationCounter), 
            builder.Build());
    }

    internal void OnNotificationTapped(string? sessionId)
    {
        NotificationTapped?.Invoke(this, new NotificationTappedEventArgs { SessionId = sessionId });
    }
}

/// <summary>
/// Custom permission for POST_NOTIFICATIONS (Android 13+)
/// </summary>
public class NotificationPermission : Permissions.BasePlatformPermission
{
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions
    {
        get
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            {
                return new[] { (Manifest.Permission.PostNotifications, true) };
            }
            return Array.Empty<(string, bool)>();
        }
    }
}
