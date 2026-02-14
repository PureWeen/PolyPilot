using PolyPilot.Services;

namespace PolyPilot;

public partial class App : Application
{
	public App(INotificationManagerService notificationService)
	{
		InitializeComponent();
		_ = notificationService.InitializeAsync();
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
