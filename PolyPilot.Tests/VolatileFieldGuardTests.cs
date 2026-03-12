using System.Reflection;
using System.Runtime.CompilerServices;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Guards that fields accessed from multiple threads are declared volatile.
/// Prevents regressions where someone removes the volatile modifier.
/// </summary>
public class VolatileFieldGuardTests
{
    [Fact]
    public void ActiveSessionName_IsDeclaredVolatile_OnCopilotService()
    {
        // _activeSessionName is read by WsBridge background threads (SyncRemoteSessions),
        // restore background threads, and written by UI thread (SetActiveSession, CloseSession).
        // Must be volatile for cross-thread visibility on ARM (iOS/Android).
        var field = typeof(CopilotService)
            .GetField("_activeSessionName", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.NotNull(field);
        Assert.True(
            field.GetRequiredCustomModifiers().Any(m => m == typeof(IsVolatile)),
            "_activeSessionName must be declared volatile for cross-thread visibility");
    }

    [Fact]
    public void ActiveSessionName_IsDeclaredVolatile_OnDemoService()
    {
        var field = typeof(DemoService)
            .GetField("_activeSessionName", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.NotNull(field);
        Assert.True(
            field.GetRequiredCustomModifiers().Any(m => m == typeof(IsVolatile)),
            "DemoService._activeSessionName must be declared volatile for consistency");
    }
}
