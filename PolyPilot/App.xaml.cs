using PolyPilot.Services;

namespace PolyPilot;

public partial class App : Application
{
	private readonly CopilotService _copilotService;
	private readonly MinimizedModeService _minimizedModeService;

	public App(INotificationManagerService notificationService, CopilotService copilotService, MinimizedModeService minimizedModeService)
	{
		_copilotService = copilotService;
		_minimizedModeService = minimizedModeService;
		InitializeComponent();
		_ = notificationService.InitializeAsync();

		// Navigate to session when user taps a notification
		notificationService.NotificationTapped += (_, e) =>
		{
			if (e.SessionId != null)
			{
				MainThread.BeginInvokeOnMainThread(() =>
				{
					copilotService.SwitchToSessionById(e.SessionId);
				});
			}
		};
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new MainPage()) { Title = "" };

		// Track whether the main window is focused for minimized mode popup logic
		window.Activated += (_, _) =>
		{
			_minimizedModeService.IsMainWindowFocused = true;
			CheckPendingNavigation();
		};
		window.Deactivated += (_, _) => _minimizedModeService.IsMainWindowFocused = false;

		if (OperatingSystem.IsLinux())
		{
			window.Width = 1400;
			window.Height = 900;
		}

#if MACCATALYST
		// Register Mac sleep/wake observer so we reconnect immediately after the Mac wakes,
		// even if the user doesn't click on PolyPilot (OnResume only fires on app activation).
		PolyPilot.Platforms.MacCatalyst.MacSleepWakeMonitor.Register(_copilotService);
#endif

		return window;
	}

	protected override void OnResume()
	{
		base.OnResume();
		// Belt-and-suspenders for mobile / platforms where Activated may not fire.
		CheckPendingNavigation();
		// The Mac may have been locked or slept, during which the headless server may have
		// stopped. Trigger a lightweight ping so sessions reconnect immediately on unlock.
		_ = _copilotService.CheckConnectionHealthAsync();
	}

	private void CheckPendingNavigation()
	{
		try
		{
			var navPath = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				".polypilot", "pending-navigation.json");

			if (!File.Exists(navPath))
				return;

			var json = File.ReadAllText(navPath);
			File.Delete(navPath);

			using var doc = System.Text.Json.JsonDocument.Parse(json);

			// Discard sidecars older than 30 seconds: the notification was sent long enough ago
			// that any second-instance race would have resolved. Consuming a stale sidecar would
			// navigate the user to an unintended session just because they Cmd+Tabbed back.
			if (doc.RootElement.TryGetProperty("writtenAt", out var ts))
			{
				if (DateTime.UtcNow - ts.GetDateTime() > TimeSpan.FromSeconds(30))
					return;
			}

			if (doc.RootElement.TryGetProperty("sessionId", out var prop))
			{
				var sessionId = prop.GetString();
				if (sessionId != null)
				{
					MainThread.BeginInvokeOnMainThread(() =>
					{
						_copilotService.SwitchToSessionById(sessionId);
					});
				}
			}
		}
		catch
		{
			// Best effort — never crash the running instance over a sidecar read failure
		}
	}
}
