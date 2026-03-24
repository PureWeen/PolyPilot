using Foundation;

namespace PolyPilot;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	private NSObject? _activityToken;

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	public override bool FinishedLaunching(UIKit.UIApplication application, NSDictionary? launchOptions)
	{
		var result = base.FinishedLaunching(application, launchOptions);

		// Disable App Nap — keeps timers, network I/O, and background threads running
		// while the Mac lock screen is active. Without this, WsBridge debounce timers
		// freeze, keepalive pings never fire, and clients see the app as dead.
		// NSActivityOptions.UserInitiated = 0x00FFFFFF (per Apple NSProcessInfo.h)
		// More aggressive than AllowingIdleSystemSleep (0x00EFFFFF) but necessary
		// to keep WebSocket bridge alive during idle. Not in .NET Catalyst bindings.
		_activityToken = NSProcessInfo.ProcessInfo.BeginActivity(
			(NSActivityOptions)0x00FFFFFF,
			"PolyPilot manages Copilot CLI sessions and serves remote clients via WebSocket");

		return result;
	}

	public override void WillTerminate(UIKit.UIApplication application)
	{
		if (_activityToken != null)
			NSProcessInfo.ProcessInfo.EndActivity(_activityToken);
		base.WillTerminate(application);
	}
}
