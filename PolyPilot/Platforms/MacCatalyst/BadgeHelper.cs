using UIKit;
using UserNotifications;

namespace PolyPilot.Platforms.MacCatalyst;

/// <summary>
/// Sets the PolyPilot dock icon badge count on Mac Catalyst.
/// Requires notification permission with the Badge option (requested by NotificationManagerService).
/// </summary>
internal static class BadgeHelper
{
    /// <summary>
    /// Sets the dock icon badge to the given count. Pass 0 to clear.
    /// Safe to call from any thread.
    /// </summary>
    internal static void SetBadge(int count)
    {
        var n = Math.Max(0, count);
        if (OperatingSystem.IsMacCatalystVersionAtLeast(16))
        {
            // Modern API (Mac Catalyst 16+ / macOS Ventura+) — no deprecated warning
#pragma warning disable CA1416
            _ = UNUserNotificationCenter.Current.SetBadgeCountAsync((nint)n)
                .ContinueWith(t =>
                {
                    if (t.Exception != null)
                        Console.WriteLine($"[Badge] SetBadgeCountAsync failed: {t.Exception.InnerException?.Message}");
                }, TaskContinuationOptions.OnlyOnFaulted);
#pragma warning restore CA1416
        }
        else
        {
            // Fallback for Mac Catalyst 15.x
            MainThread.BeginInvokeOnMainThread(() =>
            {
#pragma warning disable CA1422
                UIApplication.SharedApplication.ApplicationIconBadgeNumber = n;
#pragma warning restore CA1422
            });
        }
    }
}
