using AutoPilot.App.Services;
using Microsoft.Extensions.Logging;

namespace AutoPilot.App;

public static class MauiProgram
{
	private static readonly string CrashLogPath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
		".copilot", "autopilot-crash.log");

	public static MauiApp CreateMauiApp()
	{
		// Set up global exception handlers
		AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
		{
			LogException("AppDomain.UnhandledException", args.ExceptionObject as Exception);
		};

		TaskScheduler.UnobservedTaskException += (sender, args) =>
		{
			LogException("TaskScheduler.UnobservedTaskException", args.Exception);
			args.SetObserved(); // Prevent crash
		};

		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();
		
		// Register CopilotService as singleton so state is shared across components
		builder.Services.AddSingleton<CopilotService>();
		builder.Services.AddSingleton<ServerManager>();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}

	private static void LogException(string source, Exception? ex)
	{
		if (ex == null) return;
		try
		{
			var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
			var logEntry = $"\n=== {timestamp} [{source}] ===\n{ex}\n";
			File.AppendAllText(CrashLogPath, logEntry);
			Console.WriteLine($"[CRASH] {source}: {ex.Message}");
		}
		catch { /* Don't throw in exception handler */ }
	}
}
