using System.Diagnostics;

namespace PolyPilot.Services;

public class NotificationManagerService : INotificationManagerService
{
    private bool _hasNotifySend;

    public bool HasPermission => _hasNotifySend;

    public event EventHandler<NotificationTappedEventArgs>? NotificationTapped
    {
        add { }
        remove { }
    }

    public Task InitializeAsync()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "which",
                Arguments = "notify-send",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            proc?.WaitForExit(3000);
            _hasNotifySend = proc?.ExitCode == 0;
        }
        catch
        {
            _hasNotifySend = false;
        }
        return Task.CompletedTask;
    }

    public Task SendNotificationAsync(string title, string body, string? sessionId = null)
    {
        if (!_hasNotifySend) return Task.CompletedTask;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "notify-send",
                ArgumentList = { "--app-name=PolyPilot", title, body },
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch
        {
            // notify-send unavailable or failed — swallow silently
        }
        return Task.CompletedTask;
    }
}
