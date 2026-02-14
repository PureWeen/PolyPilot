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
		return new Window(new MainPage()) { Title = "" };
	}
}
