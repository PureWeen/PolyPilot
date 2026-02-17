using PolyPilot.Models;

namespace PolyPilot.Tests;

public class RenderThrottleTests
{
    [Fact]
    public void SessionSwitch_AlwaysAllowed()
    {
        var throttle = new RenderThrottle(500);
        // Even if called rapidly, session switches always pass
        Assert.True(throttle.ShouldRefresh(isSessionSwitch: true, hasCompletedSessions: false));
        Assert.True(throttle.ShouldRefresh(isSessionSwitch: true, hasCompletedSessions: false));
        Assert.True(throttle.ShouldRefresh(isSessionSwitch: true, hasCompletedSessions: false));
    }

    [Fact]
    public void CompletedSession_BypassesThrottle()
    {
        var throttle = new RenderThrottle(500);
        // First call goes through normally
        Assert.True(throttle.ShouldRefresh(isSessionSwitch: false, hasCompletedSessions: false));

        // Immediately after, normal refresh is throttled
        Assert.False(throttle.ShouldRefresh(isSessionSwitch: false, hasCompletedSessions: false));

        // But a completed session always gets through — this is the critical behavior
        Assert.True(throttle.ShouldRefresh(isSessionSwitch: false, hasCompletedSessions: true));
    }

    [Fact]
    public void NormalRefresh_ThrottledWithin500ms()
    {
        var throttle = new RenderThrottle(500);
        Assert.True(throttle.ShouldRefresh(isSessionSwitch: false, hasCompletedSessions: false));
        // Second call within throttle window should be blocked
        Assert.False(throttle.ShouldRefresh(isSessionSwitch: false, hasCompletedSessions: false));
    }

    [Fact]
    public void NormalRefresh_AllowedAfterThrottleExpires()
    {
        var throttle = new RenderThrottle(500);
        Assert.True(throttle.ShouldRefresh(isSessionSwitch: false, hasCompletedSessions: false));

        // Simulate time passing beyond throttle window
        throttle.SetLastRefresh(DateTime.UtcNow.AddMilliseconds(-600));
        Assert.True(throttle.ShouldRefresh(isSessionSwitch: false, hasCompletedSessions: false));
    }

    [Fact]
    public void CompletedSession_UpdatesLastRefreshTime()
    {
        var throttle = new RenderThrottle(500);
        // Set last refresh to long ago
        throttle.SetLastRefresh(DateTime.UtcNow.AddSeconds(-10));

        // Completed session bypass updates the timestamp
        Assert.True(throttle.ShouldRefresh(isSessionSwitch: false, hasCompletedSessions: true));
        var afterCompleted = throttle.LastRefresh;

        // So a normal refresh immediately after is still throttled
        Assert.False(throttle.ShouldRefresh(isSessionSwitch: false, hasCompletedSessions: false));
    }

    [Fact]
    public void MultipleCompletedSessions_AllBypassThrottle()
    {
        var throttle = new RenderThrottle(500);
        // Rapid completed-session refreshes should all pass through
        Assert.True(throttle.ShouldRefresh(isSessionSwitch: false, hasCompletedSessions: true));
        Assert.True(throttle.ShouldRefresh(isSessionSwitch: false, hasCompletedSessions: true));
        Assert.True(throttle.ShouldRefresh(isSessionSwitch: false, hasCompletedSessions: true));
    }

    [Fact]
    public void CustomThrottleInterval_Respected()
    {
        var throttle = new RenderThrottle(1000);
        Assert.True(throttle.ShouldRefresh(isSessionSwitch: false, hasCompletedSessions: false));

        // 600ms later — within 1000ms throttle, should be blocked
        throttle.SetLastRefresh(DateTime.UtcNow.AddMilliseconds(-600));
        Assert.False(throttle.ShouldRefresh(isSessionSwitch: false, hasCompletedSessions: false));

        // 1100ms later — past throttle, should pass
        throttle.SetLastRefresh(DateTime.UtcNow.AddMilliseconds(-1100));
        Assert.True(throttle.ShouldRefresh(isSessionSwitch: false, hasCompletedSessions: false));
    }
}
