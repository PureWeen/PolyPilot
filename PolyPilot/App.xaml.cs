using PolyPilot.Services;

namespace PolyPilot;

public partial class App : Application
{
	public App(INotificationManagerService notificationService, CopilotService copilotService)
	{
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
		if (OperatingSystem.IsLinux())
		{
			window.Width = 1400;
			window.Height = 900;
		}
		return window;
	}
}
