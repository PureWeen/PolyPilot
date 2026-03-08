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

    [Fact]
    public void CompletionRace_OnStateChangedThrottledButHandleCompleteRenders()
    {
        // Documents the race condition that caused stuck "Thinking" indicators:
        // 1. Streaming events (AssistantTurnEndEvent) fire OnStateChanged rapidly
        // 2. CompleteResponse fires OnStateChanged (throttled — dropped!)
        // 3. CompleteResponse fires OnSessionComplete → HandleComplete
        //
        // The fix: HandleComplete calls StateHasChanged() directly instead of
        // going through ScheduleRender(), guaranteeing the completion renders.
        //
        // This test verifies the throttle DOES drop the OnStateChanged from step 2:
        var throttle = new RenderThrottle(500);

        // Step 1: Streaming event refresh passes (first in window)
        Assert.True(throttle.ShouldRefresh(isSessionSwitch: false, hasCompletedSessions: false));

        // Step 2: CompleteResponse's OnStateChanged arrives <500ms later — THROTTLED
        // completedSessions is empty at this point because HandleComplete hasn't run yet
        Assert.False(throttle.ShouldRefresh(isSessionSwitch: false, hasCompletedSessions: false));

        // Step 3: HandleComplete runs, adds to completedSessions, calls StateHasChanged directly
        // (bypasses RefreshState/throttle entirely — this is the fix)
    }

    [Fact]
    public void HandleComplete_MustNotUseInvokeAsync_RegressionGuard()
    {
        // Regression guard: HandleComplete is called from CompleteResponse which is
        // already marshaled to the UI thread via Invoke(SynchronizationContext.Post).
        // Wrapping the body in InvokeAsync DEFERS execution, causing StateHasChanged()
        // to run too late — after other RefreshState calls consume the dirty flag.
        // The DOM then never updates with IsProcessing=false ("stuck Thinking" bug).
        //
        // Only the delayed 10-second cleanup callback should use InvokeAsync
        // (because Task.Delay.ContinueWith runs on the thread pool).
        var dashboardPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..",
            "PolyPilot", "Components", "Pages", "Dashboard.razor");
        Assert.True(File.Exists(dashboardPath), $"Dashboard.razor not found at {dashboardPath}");

        var source = File.ReadAllText(dashboardPath);

        // Extract the HandleComplete method body (from signature to next "private " or "protected ")
        var methodStart = source.IndexOf("private void HandleComplete(", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "HandleComplete method not found in Dashboard.razor");

        // Find the next method definition after HandleComplete
        var afterSignature = source.IndexOf('\n', methodStart) + 1;
        var nextMethod = source.IndexOf("\n    private ", afterSignature, StringComparison.Ordinal);
        if (nextMethod < 0) nextMethod = source.Length;
        var methodBody = source.Substring(methodStart, nextMethod - methodStart);

        // Count InvokeAsync calls — should have exactly 1 (the delayed cleanup only)
        var invokeAsyncCount = 0;
        var searchFrom = 0;
        while (true)
        {
            var idx = methodBody.IndexOf("InvokeAsync(", searchFrom, StringComparison.Ordinal);
            if (idx < 0) break;
            invokeAsyncCount++;
            searchFrom = idx + 1;
        }

        Assert.True(invokeAsyncCount <= 1,
            $"HandleComplete has {invokeAsyncCount} InvokeAsync calls — expected at most 1 (the delayed cleanup). " +
            "The synchronous body must NOT use InvokeAsync because HandleComplete is already on the UI thread " +
            "(called from CompleteResponse via Invoke/SynchronizationContext.Post). " +
            "InvokeAsync defers execution and causes stale renders (stuck 'Thinking' indicators).");

        // The one allowed InvokeAsync must be inside a Task.Delay continuation
        if (invokeAsyncCount == 1)
        {
            var invokeIdx = methodBody.IndexOf("InvokeAsync(", StringComparison.Ordinal);
            var precedingContext = methodBody.Substring(Math.Max(0, invokeIdx - 100), Math.Min(100, invokeIdx));
            Assert.Contains("Task.Delay", precedingContext,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CompleteResponse_OnSessionComplete_FiresBeforeOnStateChanged()
    {
        // Regression guard: In the main completion path of CompleteResponse (after
        // IsProcessing=false), OnSessionComplete must fire BEFORE OnStateChanged.
        // HandleComplete (OnSessionComplete handler) populates completedSessions;
        // RefreshState (OnStateChanged handler) checks completedSessions.Count > 0
        // to bypass throttle. If order is reversed, throttle drops the render.
        var eventsPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..",
            "PolyPilot", "Services", "CopilotService.Events.cs");
        Assert.True(File.Exists(eventsPath), $"CopilotService.Events.cs not found at {eventsPath}");

        var source = File.ReadAllText(eventsPath);

        // Find CompleteResponse method
        var methodStart = source.IndexOf("private void CompleteResponse(", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "CompleteResponse method not found");

        // The critical ordering is AFTER IsProcessing = false (the main completion path).
        // Use the preceding comment as anchor to find the right occurrence.
        var anchor = source.IndexOf("// Clear IsProcessing BEFORE completing the TCS", methodStart, StringComparison.Ordinal);
        Assert.True(anchor >= 0, "CompleteResponse TCS comment not found");
        var isProcessingFalse = source.IndexOf("state.Info.IsProcessing = false;", anchor, StringComparison.Ordinal);
        Assert.True(isProcessingFalse >= 0, "IsProcessing = false not found in CompleteResponse main path");

        var afterProcessing = source.Substring(isProcessingFalse, 1000);
        var completeIdx = afterProcessing.IndexOf("OnSessionComplete?", StringComparison.Ordinal);
        var stateIdx = afterProcessing.IndexOf("OnStateChanged?", StringComparison.Ordinal);
        Assert.True(completeIdx >= 0, "OnSessionComplete not found after IsProcessing=false in CompleteResponse");
        Assert.True(stateIdx >= 0, "OnStateChanged not found after IsProcessing=false in CompleteResponse");
        Assert.True(completeIdx < stateIdx,
            "OnSessionComplete must fire BEFORE OnStateChanged in CompleteResponse. " +
            "HandleComplete populates completedSessions which RefreshState checks to bypass throttle.");
    }
}
