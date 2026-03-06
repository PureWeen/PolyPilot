using PolyPilot.Services;

namespace PolyPilot;

public partial class App : Application
{
	private readonly CopilotService _copilotService;

	public App(INotificationManagerService notificationService, CopilotService copilotService)
	{
		InitializeComponent();
		_copilotService = copilotService;
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
		window.Destroying += OnWindowDestroying;
		return window;
	}

	private void OnWindowDestroying(object? sender, EventArgs e)
	{
		// Flush pending debounced writes to disk before the app exits.
		// This is synchronous because the Destroying event doesn't support async.
		_copilotService.FlushPendingSaves();
	}
}
