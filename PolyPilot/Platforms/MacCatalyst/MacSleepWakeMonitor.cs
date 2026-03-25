using System.Runtime.InteropServices;
using Foundation;
using ObjCRuntime;
using PolyPilot.Services;

namespace PolyPilot.Platforms.MacCatalyst;

/// <summary>
/// Subscribes to NSWorkspace sleep/wake notifications so PolyPilot can
/// proactively recover the copilot connection after the Mac wakes from sleep.
/// 
/// On Mac Catalyst, App.OnResume() fires when the *app* is re-activated but
/// NOT reliably when the Mac wakes from system sleep without the user first
/// clicking on PolyPilot. NSWorkspace DidWake fires immediately after the Mac
/// wakes, regardless of which app has focus.
///
/// NSWorkspaceDidWakeNotification must be observed via NSWorkspace.sharedWorkspace.notificationCenter,
/// NOT NSNotificationCenter.defaultCenter. NSWorkspace is not in the Mac Catalyst .NET binding,
/// so we access it via ObjC messaging and wrap the result as a managed NSNotificationCenter.
/// </summary>
public static class MacSleepWakeMonitor
{
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr IntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);

    private static NSObject? _wakeObserver;
    private static NSObject? _sleepObserver;
    private static CopilotService? _copilotService;

    /// <summary>
    /// Returns NSWorkspace.sharedWorkspace.notificationCenter as a managed NSNotificationCenter.
    /// NSWorkspace is AppKit-only and has no direct .NET binding on Mac Catalyst.
    /// </summary>
    private static NSNotificationCenter? GetWorkspaceNotificationCenter()
    {
        try
        {
            var nsWorkspaceClass = Class.GetHandle("NSWorkspace");
            if (nsWorkspaceClass == IntPtr.Zero) return null;
            var shared = IntPtr_objc_msgSend(nsWorkspaceClass, Selector.GetHandle("sharedWorkspace"));
            if (shared == IntPtr.Zero) return null;
            var centerPtr = IntPtr_objc_msgSend(shared, Selector.GetHandle("notificationCenter"));
            if (centerPtr == IntPtr.Zero) return null;
            return Runtime.GetNSObject<NSNotificationCenter>(centerPtr);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SleepWake] Failed to get NSWorkspace notificationCenter: {ex.Message}");
            return null;
        }
    }

    public static void Register(CopilotService copilotService)
    {
        _copilotService = copilotService;

        // NSWorkspaceDidWakeNotification and NSWorkspaceWillSleepNotification must be observed
        // via NSWorkspace.sharedWorkspace.notificationCenter — not NSNotificationCenter.defaultCenter.
        var notifCenter = GetWorkspaceNotificationCenter() ?? NSNotificationCenter.DefaultCenter;

        // Wake: Mac has just woken from sleep — reconnect immediately
        _wakeObserver = notifCenter.AddObserver(
            new NSString("NSWorkspaceDidWakeNotification"),
            null,
            NSOperationQueue.MainQueue,
            OnDidWake);

        // Sleep (optional): log so we can correlate with subsequent wake
        _sleepObserver = notifCenter.AddObserver(
            new NSString("NSWorkspaceWillSleepNotification"),
            null,
            NSOperationQueue.MainQueue,
            OnWillSleep);

        Console.WriteLine("[SleepWake] NSWorkspace sleep/wake observer registered");
    }

    public static void Unregister()
    {
        var notifCenter = GetWorkspaceNotificationCenter() ?? NSNotificationCenter.DefaultCenter;
        if (_wakeObserver != null)
        {
            notifCenter.RemoveObserver(_wakeObserver);
            _wakeObserver = null;
        }
        if (_sleepObserver != null)
        {
            notifCenter.RemoveObserver(_sleepObserver);
            _sleepObserver = null;
        }
    }

    private static void OnWillSleep(NSNotification notification)
    {
        Console.WriteLine("[SleepWake] Mac going to sleep — connection may drop");
    }

    private static void OnDidWake(NSNotification notification)
    {
        Console.WriteLine("[SleepWake] Mac woke from sleep — triggering connection health check");
        var svc = _copilotService;
        if (svc != null)
            Task.Run(async () => await svc.CheckConnectionHealthAsync()).ContinueWith(_ => { });
    }
}
