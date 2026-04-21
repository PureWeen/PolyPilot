using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the TurnEnd→Idle fallback timer (PR #305 / issue #299).
///
/// The fallback fires CompleteResponse when SessionIdleEvent never arrives after
/// AssistantTurnEndEvent. It is cancelled by SessionIdleEvent (normal path),
/// AssistantTurnStartEvent (new round starting), or session cleanup.
///
/// Since SessionState is private to CopilotService and SDK events cannot be
/// injected from tests, these tests verify:
///   1. The fallback delay constant is a reasonable value.
///   2. The CTS cancel+dispose pattern used by CancelTurnEndFallback works correctly.
///   3. A Task.Delay timer DOES fire when its CTS is not cancelled.
///   4. A Task.Delay timer does NOT fire when its CTS is cancelled before the delay.
/// </summary>
public class TurnEndFallbackTests
{
    // ===== Constant value =====

    [Fact]
    public void TurnEndFallbackMs_IsReasonable()
    {
        // Must be long enough for the SDK to reliably send session.idle after turn_end
        // (network latency, slow tools) but short enough to recover within a few seconds.
        Assert.InRange(CopilotService.TurnEndIdleFallbackMs, 1000, 30_000);
    }

    // ===== CTS cancel + dispose pattern (mirrors CancelTurnEndFallback) =====

    [Fact]
    public void CancelTurnEndFallback_Pattern_CancelsToken()
    {
        // Arrange: simulate the CTS stored in state.TurnEndIdleCts
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        Assert.False(token.IsCancellationRequested, "Token should not be cancelled before cancel");

        // Act: simulate Interlocked.Exchange(ref state.TurnEndIdleCts, null) + Cancel + Dispose
        var prev = cts;
        prev.Cancel();

        // Assert: token reports cancelled after Cancel()
        Assert.True(token.IsCancellationRequested, "Token must be cancelled after Cancel()");

        prev.Dispose();
        // Token should remain cancelled after Dispose()
        Assert.True(token.IsCancellationRequested, "Token must remain cancelled after Dispose()");
    }

    [Fact]
    public void CancelTurnEndFallback_Pattern_NullSafe()
    {
        // CancelTurnEndFallback uses prev?.Cancel() — null CTS must not throw.
        CancellationTokenSource? prev = null;
        prev?.Cancel();
        prev?.Dispose();
        // No exception == pass
    }

    // ===== Timer fires / does not fire =====

    [Fact]
    public async Task FallbackTimer_NotCancelled_FiresAfterDelay()
    {
        // Verify the Task.Run+Task.Delay pattern fires its completion action
        // when the CTS is never cancelled. Uses 50ms to keep the test fast.
        // Uses TaskCompletionSource instead of a bool field to avoid memory-ordering
        // issues and to provide a reliable, load-tolerant signal mechanism.
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(50, token);
                if (!token.IsCancellationRequested)
                    tcs.TrySetResult(true);
                else
                    tcs.TrySetResult(false);
            }
            catch (OperationCanceledException) { tcs.TrySetResult(false); }
        });

        // Wait up to 5s — robust against thread-pool starvation under heavy parallel load
        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        Assert.True(completedTask == tcs.Task, "Fallback timer task should complete within 5s");
        Assert.True(tcs.Task.Result, "Fallback timer should fire when CTS is not cancelled");
    }

    [Fact]
    public async Task FallbackTimer_CancelledBeforeDelay_DoesNotFire()
    {
        // Verify the Task.Run+Task.Delay pattern does NOT fire when CTS is cancelled
        // before the delay elapses — simulating CancelTurnEndFallback() being called.
        var tcs = new TaskCompletionSource<bool>();
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(100, token);
                tcs.TrySetResult(true); // fired
            }
            catch (OperationCanceledException) { tcs.TrySetResult(false); }
        });

        // Cancel before the 100ms delay elapses
        cts.Cancel();
        var fired = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(fired, "Fallback timer must not fire when CTS is cancelled");
    }

    [Fact]
    public async Task FallbackTimer_CancelledAfterIsCancellationRequestedCheck_DoesNotFire()
    {
        // Verify the explicit IsCancellationRequested guard inside the fallback closure.
        // Use a synchronization primitive to ensure cancel happens before the guard check.
        var tcs = new TaskCompletionSource<bool>();
        var readyToCheck = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            await Task.Delay(50); // unlinked delay — always completes
            // Signal that we're about to check the guard
            readyToCheck.TrySetResult();
            // Small yield to let the main thread cancel
            await Task.Delay(10);
            // Guard: explicit check mirrors the code in the real fallback
            if (token.IsCancellationRequested) { tcs.TrySetResult(false); return; }
            tcs.TrySetResult(true);
        });

        // Wait for the task to reach the guard area, then cancel
        await readyToCheck.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        var fired = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(fired, "Explicit IsCancellationRequested guard must prevent firing after cancel");
    }

    // ===== Tool session extended fallback =====

    [Fact]
    public void TurnEndIdleToolFallbackAdditionalMs_IsReasonable()
    {
        // Must be long enough that a normal LLM inter-round pause (reasoning, >4s up to ~30s)
        // won't trigger a premature CompleteResponse, but short enough to rescue a stuck session
        // faster than the watchdog (600s).
        Assert.InRange(CopilotService.TurnEndIdleToolFallbackAdditionalMs, 10_000, 120_000);
        Assert.True(
            CopilotService.TurnEndIdleToolFallbackAdditionalMs > CopilotService.TurnEndIdleFallbackMs,
            "Tool session additional delay must be longer than the base 4s fallback");
    }

    [Fact]
    public void TurnEndIdleToolFallback_TotalDelay_IsLessThanWatchdog()
    {
        // The combined fallback delay must be shorter than the watchdog tool timeout so
        // we rescue stuck sessions before the watchdog fires the error message.
        var totalMs = CopilotService.TurnEndIdleFallbackMs + CopilotService.TurnEndIdleToolFallbackAdditionalMs;
        var watchdogMs = CopilotService.WatchdogToolExecutionTimeoutSeconds * 1000;
        Assert.True(totalMs < watchdogMs,
            $"Total fallback ({totalMs}ms) must be less than watchdog tool timeout ({watchdogMs}ms)");
    }

    [Fact]
    public async Task ToolFallback_CancelledByTurnStart_DoesNotFire()
    {
        // Simulates: TurnEnd (tools used) starts timer -> TurnStart arrives -> cancels -> no fire
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var fallbackTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(50, token); // accelerated base delay (mirrors other tests in this file)
                if (token.IsCancellationRequested)
                {
                    completion.TrySetResult(false);
                    return;
                }
                await Task.Delay(100, token); // accelerated extended delay
                if (token.IsCancellationRequested)
                {
                    completion.TrySetResult(false);
                    return;
                }
                completion.TrySetResult(true);
            }
            catch (OperationCanceledException)
            {
                completion.TrySetResult(false);
            }
        });

        // Cancel well before the first delay completes.
        await Task.Delay(30);
        cts.Cancel();
        await fallbackTask;
        var completedTask = await Task.WhenAny(completion.Task, Task.Delay(5000));
        Assert.True(completedTask == completion.Task, "Cancelled fallback task should complete within 5s");
        Assert.False(await completion.Task, "Fallback must not fire when cancelled by TurnStart");
    }

    [Fact]
    public async Task ToolFallback_NoTurnStart_EventuallyFires()
    {
        // Simulates: TurnEnd (tools used) + no TurnStart + no SessionIdle -> fallback fires
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var fallbackTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(50, token); // accelerated base delay
                if (token.IsCancellationRequested)
                {
                    completion.TrySetResult(false);
                    return;
                }
                await Task.Delay(100, token); // accelerated extended delay
                if (token.IsCancellationRequested)
                {
                    completion.TrySetResult(false);
                    return;
                }
                completion.TrySetResult(true);
            }
            catch (OperationCanceledException)
            {
                completion.TrySetResult(false);
            }
        });

        var completedTask = await Task.WhenAny(completion.Task, Task.Delay(5000));
        Assert.True(completedTask == completion.Task, "Fallback must complete within 5s when not cancelled");
        await fallbackTask;
        Assert.True(await completion.Task, "Fallback must fire when no TurnStart or SessionIdle arrives");
    }

    // ===== Events.jsonl freshness guard constants =====

    [Fact]
    public void TurnEndFallbackFreshnessSeconds_IsReasonable()
    {
        // Must be tight enough to avoid false positives from prior-turn writes
        // but wide enough to catch genuinely active tools.
        Assert.InRange(CopilotService.TurnEndFallbackFreshnessSeconds, 5, 60);
    }

    [Fact]
    public void TurnEndFallbackRecheckMs_IsReasonable()
    {
        // Must be shorter than the watchdog check interval (15s) to provide
        // faster recovery than deferring entirely to the watchdog.
        Assert.InRange(CopilotService.TurnEndFallbackRecheckMs, 5_000, 30_000);
        Assert.True(CopilotService.TurnEndFallbackRecheckMs <=
            CopilotService.WatchdogCheckIntervalSeconds * 1000,
            "Recheck delay should be at most one watchdog interval");
    }

    [Fact]
    public void TurnEndFallbackFreshnessSeconds_IsSeparateFromWatchdog()
    {
        // The TurnEnd fallback freshness threshold must NOT be the same constant
        // as the watchdog's Case B freshness (300s). They serve different purposes:
        // the fallback needs a tight window, the watchdog needs a wide one.
        Assert.NotEqual(
            CopilotService.TurnEndFallbackFreshnessSeconds,
            CopilotService.WatchdogCaseBFreshnessSeconds);
        Assert.True(
            CopilotService.TurnEndFallbackFreshnessSeconds < CopilotService.WatchdogCaseBFreshnessSeconds,
            "Fallback freshness must be tighter than watchdog Case B freshness");
    }

    // ===== Events.jsonl freshness guard behavior =====

    [Fact]
    public void TurnEndFallback_FreshnessGuard_HasTurnStartAnchor()
    {
        // The freshness check must use a turn-start anchor to avoid false positives
        // from prior-turn writes. Verify the code captures freshnessCheckStart before
        // the delay and uses it in the comparison.
        var source = File.ReadAllText("../../../../PolyPilot/Services/CopilotService.Events.cs");

        // Must capture timestamp before the extended delay
        var captureIdx = source.IndexOf("var freshnessCheckStart = DateTime.UtcNow;", StringComparison.Ordinal);
        var delayIdx = source.IndexOf("await Task.Delay(TurnEndIdleToolFallbackAdditionalMs", StringComparison.Ordinal);
        Assert.True(captureIdx >= 0, "freshnessCheckStart must be captured");
        Assert.True(delayIdx >= 0, "Extended delay must exist");
        Assert.True(captureIdx < delayIdx, "freshnessCheckStart must be captured BEFORE the delay");

        // Must use the anchor in the comparison (not just fileAge)
        Assert.Contains("lastWrite > freshnessCheckStart", source);
    }

    [Fact]
    public void TurnEndFallback_FreshnessGuard_HasRecheckLoop()
    {
        // After the first freshness skip, the fallback must recheck rather than
        // permanently returning (which would leave the session stuck until the watchdog).
        var source = File.ReadAllText("../../../../PolyPilot/Services/CopilotService.Events.cs");
        Assert.Contains("TurnEndFallbackRecheckMs", source);
        Assert.Contains("rescheduling", source); // Debug log mentions rescheduling
        Assert.Contains("deferring to watchdog", source); // Eventual defer after recheck
    }

    [Fact]
    public void TurnEndFallback_FreshnessGuard_HasClockSkewProtection()
    {
        // fileAge must use Math.Max(0.0, ...) to prevent negative values from clock skew.
        var source = File.ReadAllText("../../../../PolyPilot/Services/CopilotService.Events.cs");
        // The freshness guard uses Math.Max for both initial and recheck age
        Assert.Contains("Math.Max(0.0, (DateTime.UtcNow - lastWrite).TotalSeconds)", source);
        Assert.Contains("Math.Max(0.0, (DateTime.UtcNow - File.GetLastWriteTimeUtc(ep)).TotalSeconds)", source);
    }

    [Fact]
    public void TurnEndFallback_FreshnessGuard_ChecksLastEventType()
    {
        // The fallback must check GetLastEventType for tool.execution_start to handle
        // long-running tools where events.jsonl was written before the wait started.
        var source = File.ReadAllText("../../../../PolyPilot/Services/CopilotService.Events.cs");
        // GetLastEventType is called in the IDLE-FALLBACK context
        Assert.Contains("var lastEventType = GetLastEventType(ep);", source);
        Assert.Contains("lastEventType == \"tool.execution_start\"", source);
        // Must defer to watchdog when tool detected
        Assert.Contains("tool running without live event", source);
    }

    [Fact]
    public void TurnEndFallback_SkipsDemoAndRemoteMode()
    {
        // The events.jsonl check must be bypassed in Demo and Remote modes.
        var source = File.ReadAllText("../../../../PolyPilot/Services/CopilotService.Events.cs");
        var guardIdx = source.IndexOf("check if the CLI has an in-flight", StringComparison.Ordinal);
        Assert.True(guardIdx >= 0, "Freshness guard comment must exist");
        var afterComment = source.Substring(guardIdx, Math.Min(800, source.Length - guardIdx));
        Assert.Contains("IsDemoMode", afterComment);
        Assert.Contains("IsRemoteMode", afterComment);
    }

    [Fact]
    public void WatchdogCaseB_PreCheck_HasServerLivenessGuard()
    {
        // The watchdog Case B pre-check must verify the server is alive.
        var source = File.ReadAllText("../../../../PolyPilot/Services/CopilotService.Events.cs");
        var preCheckIdx = source.IndexOf("Case B skipped", StringComparison.Ordinal);
        Assert.True(preCheckIdx >= 0, "Case B pre-check debug message must exist");
        var beforeDebug = source.Substring(Math.Max(0, preCheckIdx - 800), 800);
        Assert.Contains("_serverManager.IsServerRunning", beforeDebug);
    }

    // ===== Behavioral tests for GetLastEventType (PR #619 review follow-up) =====

    [Fact]
    public void GetLastEventType_ReturnsToolExecutionStart_WhenLastEvent()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile,
                "{\"type\":\"assistant.turn_end\"}\n" +
                "{\"type\":\"assistant.message\"}\n" +
                "{\"type\":\"tool.execution_start\",\"data\":{\"name\":\"bash\"}}\n");
            var result = CopilotService.GetLastEventType(tempFile);
            Assert.Equal("tool.execution_start", result);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void GetLastEventType_ReturnsSessionIdle_WhenTerminalEvent()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile,
                "{\"type\":\"assistant.turn_end\"}\n" +
                "{\"type\":\"session.idle\"}\n");
            var result = CopilotService.GetLastEventType(tempFile);
            Assert.Equal("session.idle", result);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void GetLastEventType_ReturnsNull_WhenFileDoesNotExist()
    {
        var result = CopilotService.GetLastEventType("/nonexistent/path/events.jsonl");
        Assert.Null(result);
    }

    [Fact]
    public void GetLastEventType_ReturnsNull_WhenFileIsEmpty()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "");
            var result = CopilotService.GetLastEventType(tempFile);
            Assert.Null(result);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void GetLastEventType_ReturnsNull_WhenInvalidJson()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "not json at all\n");
            var result = CopilotService.GetLastEventType(tempFile);
            Assert.Null(result);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void GetLastEventType_IgnoresTrailingBlankLines()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile,
                "{\"type\":\"tool.execution_complete\"}\n\n\n");
            var result = CopilotService.GetLastEventType(tempFile);
            Assert.Equal("tool.execution_complete", result);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void GetLastEventType_ReadsWithFileShareReadWrite()
    {
        // Verifies the file can be read while another process is writing
        var tempFile = Path.GetTempFileName();
        try
        {
            using (var writer = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"assistant.turn_start\"}\n");
                writer.Write(bytes);
                writer.Flush();
                // Read while writer is still open
                var result = CopilotService.GetLastEventType(tempFile);
                Assert.Equal("assistant.turn_start", result);
            }
        }
        finally { File.Delete(tempFile); }
    }
}
