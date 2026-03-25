using System.Runtime.InteropServices;
using Foundation;
using ObjCRuntime;
using PolyPilot.Services;

namespace PolyPilot.Platforms.MacCatalyst;

/// <summary>
/// Subscribes to NSWorkspace sleep/wake and screen-lock/unlock notifications so PolyPilot
/// can proactively recover the copilot connection and re-sync mobile clients.
///
/// Events handled:
///   NSWorkspaceWillSleepNotification       — Mac going to sleep
///   NSWorkspaceDidWakeNotification         — Mac woke from sleep
///   NSWorkspaceScreensDidSleepNotification — displays turned off (lock screen / screensaver)
///   NSWorkspaceScreensDidWakeNotification  — displays turned back on (unlock / screensaver dismissed)
///   NSWorkspaceSessionDidResignActiveNotification — user session became inactive (screen locked)
///   NSWorkspaceSessionDidBecomeActiveNotification — user session became active again (screen unlocked)
///
/// On Mac Catalyst, App.OnResume() fires when the *app* is re-activated by the user but
/// NOT when the Mac wakes from sleep or unlocks without the user clicking PolyPilot first.
/// All these notifications fire regardless of which app has focus.
///
/// All notifications must use NSWorkspace.sharedWorkspace.notificationCenter, not
/// NSNotificationCenter.defaultCenter. NSWorkspace has no direct .NET binding on Mac Catalyst,
/// so we access it via ObjC messaging.
/// </summary>
public static class MacSleepWakeMonitor
{
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr IntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);

    private static NSObject? _wakeObserver;
    private static NSObject? _sleepObserver;
    private static NSObject? _screensWakeObserver;
    private static NSObject? _screensSleepObserver;
    private static NSObject? _sessionActiveObserver;
    private static NSObject? _sessionResignObserver;
    private static CopilotService? _copilotService;
    private static WsBridgeServer? _bridgeServer;

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

    /// <param name="bridgeServer">Optional — when provided, a full state broadcast is sent to
    /// mobile clients after unlock so they re-sync any state missed during the lock.</param>
    public static void Register(CopilotService copilotService, WsBridgeServer? bridgeServer = null)
    {
        _copilotService = copilotService;
        _bridgeServer = bridgeServer;

        var notifCenter = GetWorkspaceNotificationCenter() ?? NSNotificationCenter.DefaultCenter;

        // --- Sleep / Wake (system sleep, not just screen off) ---
        _sleepObserver = notifCenter.AddObserver(
            new NSString("NSWorkspaceWillSleepNotification"),
            null, NSOperationQueue.MainQueue, OnWillSleep);

        _wakeObserver = notifCenter.AddObserver(
            new NSString("NSWorkspaceDidWakeNotification"),
            null, NSOperationQueue.MainQueue, OnDidWake);

        // --- Screen lock / unlock (lock screen, screensaver) ---
        // NSWorkspaceScreensDidWakeNotification fires whenever the display turns on —
        // covers the common "lock screen then unlock" path without requiring the user
        // to click on PolyPilot.
        _screensWakeObserver = notifCenter.AddObserver(
            new NSString("NSWorkspaceScreensDidWakeNotification"),
            null, NSOperationQueue.MainQueue, OnScreensDidWake);

        _screensSleepObserver = notifCenter.AddObserver(
            new NSString("NSWorkspaceScreensDidSleepNotification"),
            null, NSOperationQueue.MainQueue, OnScreensDidSleep);

        // --- Fast user switching / session lock ---
        // NSWorkspaceSessionDidBecomeActiveNotification: this session's user logged back in.
        _sessionActiveObserver = notifCenter.AddObserver(
            new NSString("NSWorkspaceSessionDidBecomeActiveNotification"),
            null, NSOperationQueue.MainQueue, OnSessionDidBecomeActive);

        _sessionResignObserver = notifCenter.AddObserver(
            new NSString("NSWorkspaceSessionDidResignActiveNotification"),
            null, NSOperationQueue.MainQueue, OnSessionDidResignActive);

        Console.WriteLine("[SleepWake] NSWorkspace sleep/wake + lock/unlock observers registered");
    }

    public static void Unregister()
    {
        var notifCenter = GetWorkspaceNotificationCenter() ?? NSNotificationCenter.DefaultCenter;
        foreach (var obs in new[] { _wakeObserver, _sleepObserver, _screensWakeObserver,
                                    _screensSleepObserver, _sessionActiveObserver, _sessionResignObserver })
        {
            if (obs != null)
                try { notifCenter.RemoveObserver(obs); } catch { }
        }
        _wakeObserver = _sleepObserver = _screensWakeObserver =
            _screensSleepObserver = _sessionActiveObserver = _sessionResignObserver = null;
    }

    // ----- Sleep -----

    private static void OnWillSleep(NSNotification notification)
    {
        Console.WriteLine("[SleepWake] Mac going to sleep — connection may drop");
    }

    private static void OnDidWake(NSNotification notification)
    {
        Console.WriteLine("[SleepWake] Mac woke from sleep — triggering connection health check");
        TriggerRecovery();
    }

    // ----- Screen lock / screensaver -----

    private static void OnScreensDidSleep(NSNotification notification)
    {
        Console.WriteLine("[SleepWake] Screens turned off (lock/screensaver) — connection may drop");
    }

    private static void OnScreensDidWake(NSNotification notification)
    {
        Console.WriteLine("[SleepWake] Screens turned on (unlock/screensaver dismissed) — triggering connection health check");
        TriggerRecovery();
    }

    // ----- Fast user switching / session lock -----

    private static void OnSessionDidResignActive(NSNotification notification)
    {
        Console.WriteLine("[SleepWake] Session became inactive (screen locked or fast-user-switched)");
    }

    private static void OnSessionDidBecomeActive(NSNotification notification)
    {
        Console.WriteLine("[SleepWake] Session became active (screen unlocked or fast-user-switch back)");
        TriggerRecovery();
    }

    // ----- Shared recovery logic -----

    private static void TriggerRecovery()
    {
        var svc = _copilotService;
        var bridge = _bridgeServer;

        if (svc != null)
        {
            Task.Run(async () =>
            {
                await svc.CheckConnectionHealthAsync();
                // After connection is confirmed healthy, broadcast state to re-sync mobile clients
                // that may have reconnected during the lock but missed state updates.
                if (bridge != null)
                {
                    await Task.Delay(2000); // give mobile client time to reconnect first
                    bridge.BroadcastStateToClients();
                }
            }).ContinueWith(_ => { });
        }
    }
}
