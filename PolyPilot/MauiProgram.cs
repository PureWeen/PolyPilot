using PolyPilot.Services;
using Microsoft.Extensions.Logging;
using ZXing.Net.Maui.Controls;
using MauiDevFlow.Agent;
using MauiDevFlow.Blazor;
#if MACCATALYST
using Microsoft.Maui.LifecycleEvents;
using UIKit;
#endif

namespace PolyPilot;

public static class MauiProgram
{
	private static string? _crashLogPath;
	private static string CrashLogPath => _crashLogPath ??= GetCrashLogPath();

	private static string GetCrashLogPath()
	{
		try
		{
#if ANDROID || IOS
			return Path.Combine(FileSystem.AppDataDirectory, ".polypilot", "crash.log");
#else
			var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			if (string.IsNullOrEmpty(home))
				home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			return Path.Combine(home, ".polypilot", "crash.log");
#endif
		}
		catch
		{
			return Path.Combine(Path.GetTempPath(), ".polypilot", "crash.log");
		}
	}

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

#if MACCATALYST
		builder.ConfigureLifecycleEvents(events =>
		{
			events.AddiOS(ios =>
			{
				ios.SceneWillConnect((scene, session, options) =>
				{
					if (scene is UIWindowScene windowScene)
					{
						var titlebar = windowScene.Titlebar;
						if (titlebar != null)
						{
							titlebar.TitleVisibility = UITitlebarTitleVisibility.Hidden;
							titlebar.Toolbar = null;
						}
					}
				});
				ios.OnActivated(app =>
				{
					// Clear dock badge when app becomes active
					if (OperatingSystem.IsIOSVersionAtLeast(16) || OperatingSystem.IsMacCatalystVersionAtLeast(16))
						try { UserNotifications.UNUserNotificationCenter.Current.SetBadgeCount(0, null); } catch { }
				});
			});
		});
#endif

		builder.Services.AddMauiBlazorWebView();
		
		// Register CopilotService as singleton so state is shared across components
		builder.Services.AddSingleton<CopilotService>();
		builder.Services.AddSingleton<ChatDatabase>();
		builder.Services.AddSingleton<ServerManager>();
		builder.Services.AddSingleton<DevTunnelService>();
		builder.Services.AddSingleton<WsBridgeServer>();
		builder.Services.AddSingleton<TailscaleService>();
		builder.Services.AddSingleton<WsBridgeClient>();
		builder.Services.AddSingleton<FiestaDiscoveryService>();
		builder.Services.AddSingleton<FiestaCoordinatorService>();
		builder.Services.AddSingleton<QrScannerService>();
		builder.Services.AddSingleton<KeyCommandService>();
		builder.Services.AddSingleton<GitAutoUpdateService>();
		builder.Services.AddSingleton<RepoManager>();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
		builder.AddMauiDevFlowAgent();
		builder.AddMauiBlazorDevFlowTools();
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
