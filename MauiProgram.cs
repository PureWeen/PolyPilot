using AutoPilot.App.Services;
using Microsoft.Extensions.Logging;
using ZXing.Net.Maui.Controls;
using MauiDevFlow.Agent;
using MauiDevFlow.Blazor;

namespace AutoPilot.App;

public static class MauiProgram
{
	private static readonly string CrashLogPath = Path.Combine(
		string.IsNullOrEmpty(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
			? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
			: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
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
			.UseBarcodeReader()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();
		
		// Register CopilotService as singleton so state is shared across components
		builder.Services.AddSingleton<CopilotService>();
		builder.Services.AddSingleton<ChatDatabase>();
		builder.Services.AddSingleton<ServerManager>();
		builder.Services.AddSingleton<DevTunnelService>();
		builder.Services.AddSingleton<WsBridgeServer>();
		builder.Services.AddSingleton<WsBridgeClient>();
		builder.Services.AddSingleton<QrScannerService>();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#if MACCATALYST
		// Mac server app: Agent=9233, CDP=9232
		builder.AddMauiDevFlowAgent(options => { options.Port = 9233; });
		builder.AddMauiBlazorDevFlowTools();
#else
		// Mobile client apps: Agent=9243, CDP=9242
		builder.AddMauiDevFlowAgent(options => { options.Port = 9243; });
		builder.AddMauiBlazorDevFlowTools();
#endif
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
