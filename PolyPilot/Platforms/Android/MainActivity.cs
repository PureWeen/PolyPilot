using Android.App;
using Android.Content;
using Android.Content.PM;
using PolyPilot.Services;

namespace PolyPilot;

[Activity(
    Theme = "@style/Maui.SplashTheme", 
    MainLauncher = true, 
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleNotificationIntent(intent);
    }

    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        HandleNotificationIntent(Intent);
    }

    private void HandleNotificationIntent(Intent? intent)
    {
        if (intent == null) return;
        
        var sessionId = intent.GetStringExtra("sessionId");
        if (!string.IsNullOrEmpty(sessionId))
        {
            var service = IPlatformApplication.Current?.Services.GetService<INotificationManagerService>();
            if (service is NotificationManagerService androidService)
            {
                androidService.OnNotificationTapped(sessionId);
            }
        }
    }
}
