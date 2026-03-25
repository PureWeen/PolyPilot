using System.Runtime.InteropServices;
using Foundation;
using ObjCRuntime;
using PolyPilot.Services;

namespace PolyPilot.Platforms.MacCatalyst;

/// <summary>
/// Subscribes to NSWorkspace sleep/wake AND screen lock/unlock notifications so
/// PolyPilot can proactively recover all connections after the Mac wakes or unlocks.
/// 
/// On Mac Catalyst, App.OnResume() fires when the *app* is re-activated but
/// NOT reliably when the Mac wakes from system sleep without the user first
/// clicking on PolyPilot. NSWorkspace DidWake fires immediately after the Mac
/// wakes, regardless of which app has focus.
///
/// Similarly, NSWorkspaceScreensDidWakeNotification fires when the screen unlocks
/// (or the display turns on after being locked), which is the more common scenario
/// for mobile users whose desktop goes idle.
///
/// Recovery covers three layers:
/// 1. Copilot headless server (ping → restart if needed)
/// 2. WsBridge HttpListener (check IsRunning → AcceptLoopAsync handles restart)
/// 3. DevTunnel process (check tunnel URL reachability → restart if stale)
///
/// NSWorkspace notifications must be observed via NSWorkspace.sharedWorkspace.notificationCenter,
/// NOT NSNotificationCenter.defaultCenter. NSWorkspace is not in the Mac Catalyst .NET binding,
/// so we access it via ObjC messaging and wrap the result as a managed NSNotificationCenter.
/// </summary>
public static class MacSleepWakeMonitor
{
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr IntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);

    private static readonly List<NSObject> _observers = new();
    private static CopilotService? _copilotService;
    private static WsBridgeServer? _bridgeServer;
    private static DevTunnelService? _devTunnelService;

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

    public static void Register(CopilotService copilotService, WsBridgeServer? bridgeServer = null, DevTunnelService? devTunnelService = null)
    {
        _copilotService = copilotService;
        _bridgeServer = bridgeServer;
        _devTunnelService = devTunnelService;

        var notifCenter = GetWorkspaceNotificationCenter() ?? NSNotificationCenter.DefaultCenter;

        // System sleep/wake
        _observers.Add(notifCenter.AddObserver(
            new NSString("NSWorkspaceDidWakeNotification"),
            null, NSOperationQueue.MainQueue, OnDidWake));
        _observers.Add(notifCenter.AddObserver(
            new NSString("NSWorkspaceWillSleepNotification"),
            null, NSOperationQueue.MainQueue, OnWillSleep));

        // Screen lock/unlock (display sleep/wake) — fires when the user locks the screen
        // or closes/opens the lid, without necessarily putting the Mac to full system sleep.
        _observers.Add(notifCenter.AddObserver(
            new NSString("NSWorkspaceScreensDidWakeNotification"),
            null, NSOperationQueue.MainQueue, OnScreenDidWake));
        _observers.Add(notifCenter.AddObserver(
            new NSString("NSWorkspaceScreensDidSleepNotification"),
            null, NSOperationQueue.MainQueue, OnScreenDidSleep));

        Console.WriteLine("[SleepWake] NSWorkspace sleep/wake + screen lock/unlock observers registered");
    }

    public static void Unregister()
    {
        var notifCenter = GetWorkspaceNotificationCenter() ?? NSNotificationCenter.DefaultCenter;
        foreach (var obs in _observers)
            notifCenter.RemoveObserver(obs);
        _observers.Clear();
    }

    private static void OnWillSleep(NSNotification notification)
    {
        Console.WriteLine("[SleepWake] Mac going to sleep — connections may drop");
    }

    private static void OnScreenDidSleep(NSNotification notification)
    {
        Console.WriteLine("[SleepWake] Screen locked/display off");
    }

    private static void OnDidWake(NSNotification notification)
    {
        Console.WriteLine("[SleepWake] Mac woke from sleep — triggering full recovery");
        RecoverAll();
    }

    private static void OnScreenDidWake(NSNotification notification)
    {
        Console.WriteLine("[SleepWake] Screen unlocked/display on — triggering full recovery");
        RecoverAll();
    }

    /// <summary>
    /// Recover all three connection layers: copilot server, WsBridge, and DevTunnel.
    /// </summary>
    private static void RecoverAll()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Layer 1: Copilot headless server
                var svc = _copilotService;
                if (svc != null)
                {
                    try { await svc.CheckConnectionHealthAsync(); }
                    catch (Exception ex) { Console.WriteLine($"[SleepWake] Copilot health check failed: {ex.Message}"); }
                }

                // Layer 2: WsBridge — if the listener died, the AcceptLoopAsync restart logic
                // handles it, but we log the state so it's visible in diagnostics.
                var bridge = _bridgeServer;
                if (bridge != null && !bridge.IsRunning)
                    Console.WriteLine("[SleepWake] WsBridge listener is not running — AcceptLoopAsync will restart it");

                // Layer 3: DevTunnel — the `devtunnel host` process may still be alive but
                // the tunnel connection is stale (TCP keepalive lost during sleep/lock).
                // Ping the tunnel URL; if unreachable, restart the tunnel.
                var tunnel = _devTunnelService;
                if (tunnel != null && tunnel.State == TunnelState.Running)
                {
                    var healthy = await CheckTunnelHealthAsync(tunnel);
                    if (!healthy)
                    {
                        Console.WriteLine("[SleepWake] DevTunnel stale after wake — restarting");
                        await tunnel.RestartAsync();
                    }
                    else
                    {
                        Console.WriteLine("[SleepWake] DevTunnel healthy after wake");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SleepWake] Recovery error: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Ping the tunnel URL to verify the DevTunnel connection is still alive.
    /// Returns false if the tunnel is unreachable (stale after sleep/lock).
    /// </summary>
    private static async Task<bool> CheckTunnelHealthAsync(DevTunnelService tunnel)
    {
        var url = tunnel.TunnelUrl;
        if (string.IsNullOrEmpty(url)) return false;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var response = await http.GetAsync(url);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
