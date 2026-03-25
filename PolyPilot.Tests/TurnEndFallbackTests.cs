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
        var fired = false;
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(50, token);
                if (token.IsCancellationRequested) return;
                fired = true;
            }
            catch (OperationCanceledException) { }
        });

        await Task.Delay(500);
        Assert.True(fired, "Fallback timer should fire when CTS is not cancelled");
    }

    [Fact]
    public async Task FallbackTimer_CancelledBeforeDelay_DoesNotFire()
    {
        // Verify the Task.Run+Task.Delay pattern does NOT fire when CTS is cancelled
        // before the delay elapses — simulating CancelTurnEndFallback() being called.
        var fired = false;
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(100, token);
                fired = true;
            }
            catch (OperationCanceledException) { /* expected */ }
        });

        // Cancel before the 100ms delay elapses
        cts.Cancel();
        await Task.Delay(200);

        Assert.False(fired, "Fallback timer must not fire when CTS is cancelled");
    }

    [Fact]
    public async Task FallbackTimer_CancelledAfterIsCancellationRequestedCheck_DoesNotFire()
    {
        // Verify the explicit IsCancellationRequested guard inside the fallback closure.
        // Simulates Task.Delay completing but the token being cancelled just before the guard.
        var fired = false;
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            await Task.Delay(50); // unlinked delay — always completes
            // Guard: explicit check mirrors the code in the real fallback
            if (token.IsCancellationRequested) return;
            fired = true;
        });

        cts.Cancel(); // cancel before the guard runs
        await Task.Delay(150);

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
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        bool completeResponseFired = false;

        var fallbackTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(50, token); // accelerated base delay (mirrors other tests in this file)
                if (token.IsCancellationRequested) return;
                await Task.Delay(100, token); // accelerated extended delay
                if (token.IsCancellationRequested) return;
                completeResponseFired = true;
            }
            catch (OperationCanceledException) { }
        });

        await Task.Delay(50);
        cts.Cancel();
        cts.Dispose();
        await fallbackTask;
        Assert.False(completeResponseFired, "Fallback must not fire when cancelled by TurnStart");
    }

    [Fact]
    public async Task ToolFallback_NoTurnStart_EventuallyFires()
    {
        // Simulates: TurnEnd (tools used) + no TurnStart + no SessionIdle -> fallback fires
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        bool completeResponseFired = false;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(50, token); // accelerated base delay
                if (token.IsCancellationRequested) return;
                await Task.Delay(100, token); // accelerated extended delay
                if (token.IsCancellationRequested) return;
                completeResponseFired = true;
            }
            catch (OperationCanceledException) { }
        });

        await Task.Delay(800);
        Assert.True(completeResponseFired, "Fallback must fire when no TurnStart or SessionIdle arrives");
    }
}