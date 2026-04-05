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

		// Disable App Nap and prevent Maintenance Sleep — keeps network I/O, timers, and
		// background threads running while the Mac is idle or the lock screen is active.
		// Without this, macOS enters Maintenance Sleep (even with PreventUserIdleSystemSleep
		// held) and suspends the WebSocket bridge, causing mobile clients to see connection
		// refused for the duration of the sleep window.
		//
		// Flags (per Apple NSProcessInfo.h, not in .NET Catalyst bindings):
		//   0x00FFFFFF = NSActivityUserInitiated (prevents App Nap + user idle sleep)
		//   0xFF00000000 = NSActivityLatencyCritical (prevents Maintenance Sleep / deep idle)
		// Combined: 0xFF00FFFFFF
		_activityToken = NSProcessInfo.ProcessInfo.BeginActivity(
			(NSActivityOptions)0xFF00FFFFFF,
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
