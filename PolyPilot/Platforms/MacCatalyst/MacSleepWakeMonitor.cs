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

    // Cache the notification center used during Register() so Unregister() uses the same one.
    // (V4-M4: GetWorkspaceNotificationCenter() could return null at teardown time and fall back
    // to DefaultCenter, leaving observers permanently subscribed in the workspace center.)
    private static NSNotificationCenter? _registeredCenter;

    // Debounce: a single wake/unlock emits DidWake + ScreensDidWake + SessionDidBecomeActive
    // within ~200ms. Guard with a 5-second window so we only run one recovery per event cluster.
    // (V4-M1)
    private static DateTime _lastRecovery = DateTime.MinValue;
    private static readonly object _recoveryGate = new();

    private const int RecoveryDebounceSecs = 5;
    private const int BroadcastDelayMs = 2000;

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
        // V4-M2: guard against double-registration (CreateWindow() can be called more than once)
        if (_wakeObserver != null)
        {
            Console.WriteLine("[SleepWake] Already registered — skipping duplicate Register() call");
            return;
        }

        _copilotService = copilotService;
        _bridgeServer = bridgeServer;

        // Cache the center so Unregister() uses the exact same instance (V4-M4)
        _registeredCenter = GetWorkspaceNotificationCenter() ?? NSNotificationCenter.DefaultCenter;
        var notifCenter = _registeredCenter;

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
        // V4-M4: use cached center so we remove from the same one we registered on
        var notifCenter = _registeredCenter ?? NSNotificationCenter.DefaultCenter;
        foreach (var obs in new[] { _wakeObserver, _sleepObserver, _screensWakeObserver,
                                    _screensSleepObserver, _sessionActiveObserver, _sessionResignObserver })
        {
            if (obs != null)
                try { notifCenter.RemoveObserver(obs); } catch { }
        }
        _wakeObserver = _sleepObserver = _screensWakeObserver =
            _screensSleepObserver = _sessionActiveObserver = _sessionResignObserver = null;
        _registeredCenter = null;
    }

    // ----- Sleep -----

    private static void OnWillSleep(NSNotification notification)
    {
        Console.WriteLine("[SleepWake] Mac going to sleep — connection may drop");
    }

    private static void OnDidWake(NSNotification notification)
    {
        Console.WriteLine("[SleepWake] Mac woke from sleep — triggering connection health check");
        TriggerRecovery("DidWake");
    }

    // ----- Screen lock / screensaver -----

    private static void OnScreensDidSleep(NSNotification notification)
    {
        Console.WriteLine("[SleepWake] Screens turned off (lock/screensaver) — connection may drop");
    }

    private static void OnScreensDidWake(NSNotification notification)
    {
        Console.WriteLine("[SleepWake] Screens turned on (unlock/screensaver dismissed) — triggering connection health check");
        TriggerRecovery("ScreensDidWake");
    }

    // ----- Fast user switching / session lock -----

    private static void OnSessionDidResignActive(NSNotification notification)
    {
        Console.WriteLine("[SleepWake] Session became inactive (screen locked or fast-user-switched)");
    }

    private static void OnSessionDidBecomeActive(NSNotification notification)
    {
        Console.WriteLine("[SleepWake] Session became active (screen unlocked or fast-user-switch back)");
        TriggerRecovery("SessionDidBecomeActive");
    }

    // ----- Shared recovery logic -----

    private static void TriggerRecovery(string source)
    {
        // V4-M1: debounce — DidWake + ScreensDidWake + SessionDidBecomeActive all fire
        // within ~200ms of a single unlock. Only run recovery once per 5-second window.
        lock (_recoveryGate)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastRecovery).TotalSeconds < RecoveryDebounceSecs)
            {
                Console.WriteLine($"[SleepWake] Recovery suppressed (debounce, source={source})");
                return;
            }
            _lastRecovery = now;
        }

        Console.WriteLine($"[SleepWake] Running recovery (source={source})");

        var svc = _copilotService;
        var bridge = _bridgeServer;

        if (svc == null) return;

        Task.Run(async () =>
        {
            try
            {
                await svc.CheckConnectionHealthAsync();

                // V4-C1: BroadcastOrganizationState reads Organization (UI-thread-only List<T>).
                // Must marshal to UI thread to avoid concurrent modification with Add/Remove calls.
                if (bridge != null)
                {
                    await Task.Delay(BroadcastDelayMs); // give mobile client time to reconnect first
                    await svc.InvokeOnUIAsync(() => bridge.BroadcastStateToClients());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SleepWake] Recovery failed: {ex.Message}");
            }
        });
    }
}
