using PolyPilot.Services;

namespace PolyPilot;

public partial class App : Application
{
	public App(INotificationManagerService notificationService, ScheduledPromptService scheduledPrompts)
	{
		InitializeComponent();
		_ = notificationService.InitializeAsync();
		scheduledPrompts.Start();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new MainPage()) { Title = "" };
	}
}
