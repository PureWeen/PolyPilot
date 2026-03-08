using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the TurnEnd→Idle fallback timer behavior (SDK bug #299 / PR #305).
/// Since SessionState is private to CopilotService, these tests verify the
/// CancelTurnEndFallback pattern and fallback timing using the same
/// Interlocked.Exchange + CancellationTokenSource pattern used in production.
/// </summary>
public class TurnEndIdleFallbackTests
{
    // ===== CancelTurnEndFallback pattern: cancel + dispose =====

    [Fact]
    public void CancelTurnEndFallback_CancelsAndDisposesCts()
    {
        // Replicate the exact pattern from CancelTurnEndFallback(SessionState):
        //   var prev = Interlocked.Exchange(ref field, null);
        //   prev?.Cancel();
        //   prev?.Dispose();
        CancellationTokenSource? field = new CancellationTokenSource();
        var token = field.Token;

        // Act: simulate CancelTurnEndFallback
        var prev = Interlocked.Exchange(ref field, null);
        prev?.Cancel();
        prev?.Dispose();

        // Assert
        Assert.Null(field);
        Assert.True(token.IsCancellationRequested);
        // Disposed CTS throws ObjectDisposedException on .Token access
        Assert.Throws<ObjectDisposedException>(() => { _ = prev!.Token; });
    }

    [Fact]
    public void CancelTurnEndFallback_NullField_DoesNotThrow()
    {
        CancellationTokenSource? field = null;

        // Should be safe with null (no-op)
        var prev = Interlocked.Exchange(ref field, null);
        prev?.Cancel();
        prev?.Dispose();

        Assert.Null(field);
    }

    // ===== Fallback does NOT fire when cancelled within the delay =====

    [Fact]
    public async Task Fallback_DoesNotFireWhenCancelledBySessionIdle()
    {
        // Simulates: TurnEnd starts 4s timer → SessionIdle arrives at ~50ms → cancels timer
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        bool completeResponseFired = false;

        var fallbackTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(CopilotService.TurnEndIdleFallbackMs, token);
                completeResponseFired = true;
            }
            catch (OperationCanceledException) { }
        });

        // Simulate SessionIdle arriving quickly (cancel within delay)
        await Task.Delay(50);
        cts.Cancel();
        cts.Dispose();

        await fallbackTask;
        Assert.False(completeResponseFired, "CompleteResponse should NOT fire when cancelled by SessionIdle");
    }

    // ===== Fallback DOES fire when no SessionIdle arrives =====

    [Fact]
    public async Task Fallback_FiresWhenNoSessionIdleArrives()
    {
        // Simulates: TurnEnd starts timer → no SessionIdle → timer fires CompleteResponse
        // Use a short delay to keep tests fast
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        bool completeResponseFired = false;

        var fallbackTask = Task.Run(async () =>
        {
            try
            {
                // Use a short delay for test speed (real code uses TurnEndIdleFallbackMs = 4000)
                await Task.Delay(50, token);
                if (token.IsCancellationRequested) return;
                completeResponseFired = true;
            }
            catch (OperationCanceledException) { }
        });

        // Don't cancel — simulating missing SessionIdle
        await fallbackTask;
        Assert.True(completeResponseFired, "CompleteResponse SHOULD fire when no SessionIdle arrives");
        cts.Dispose();
    }

    // ===== HasUsedToolsThisTurn guard: fallback does NOT fire after tool use =====

    [Fact]
    public async Task Fallback_DoesNotFire_WhenToolsWereUsedThisTurn()
    {
        // Simulates the HasUsedToolsThisTurn guard in the fallback closure:
        //   if (Volatile.Read(ref state.HasUsedToolsThisTurn)) return;
        // When tools were used, a new TurnStart is expected — skip CompleteResponse.
        bool hasUsedTools = true; // simulates state.HasUsedToolsThisTurn = true
        bool completeResponseFired = false;

        var fallbackTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(50); // short delay (no CTS, always completes)
                // Guard: mirrors the production guard
                if (hasUsedTools) return;
                completeResponseFired = true;
            }
            catch (OperationCanceledException) { }
        });

        await fallbackTask;
        Assert.False(completeResponseFired,
            "Fallback must NOT fire CompleteResponse when tools were used this turn (TurnStart is coming)");
    }

    [Fact]
    public async Task Fallback_DoesFire_WhenNoToolsUsedAndNoSessionIdle()
    {
        // Simulates a first turn (no tools) where SessionIdle never arrives.
        // HasUsedToolsThisTurn = false → guard passes → CompleteResponse fires.
        bool hasUsedTools = false; // simulates first turn, no tools executed
        bool completeResponseFired = false;

        var fallbackTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(50);
                if (hasUsedTools) return;
                completeResponseFired = true;
            }
            catch (OperationCanceledException) { }
        });

        await fallbackTask;
        Assert.True(completeResponseFired,
            "Fallback SHOULD fire CompleteResponse when no tools were used and no SessionIdle arrived");
    }

    // ===== Multiple rapid TurnEnd events: no CTS leak =====

    [Fact]
    public void MultipleRapidTurnEnds_NoCtsLeak()
    {
        // Simulates multiple rapid AssistantTurnEnd events each creating a new CTS.
        // Each install cancels and disposes the previous one (Interlocked.Exchange pattern).
        // Verifies that all previous CTS instances are cancelled+disposed (no leak).
        CancellationTokenSource? field = null;
        var cancellations = new List<CancellationTokenSource>();

        for (int i = 0; i < 10; i++)
        {
            var newCts = new CancellationTokenSource();
            cancellations.Add(newCts);

            // Replicate the TurnEnd CTS install pattern:
            //   var prevCts = Interlocked.Exchange(ref state.TurnEndIdleCts, newCts);
            //   prevCts?.Cancel(); prevCts?.Dispose();
            var prev = Interlocked.Exchange(ref field, newCts);
            prev?.Cancel();
            prev?.Dispose();
        }

        // All CTS except the last should be cancelled
        for (int i = 0; i < cancellations.Count - 1; i++)
        {
            Assert.True(cancellations[i].IsCancellationRequested,
                $"CTS[{i}] should be cancelled after being replaced by a newer TurnEnd");
        }

        // The last CTS is still installed (not cancelled)
        Assert.False(cancellations[^1].IsCancellationRequested,
            "The most recently installed CTS must NOT be cancelled — it's still active");
        Assert.Same(field, cancellations[^1]);

        // Cleanup
        var last = Interlocked.Exchange(ref field, null);
        last?.Cancel();
        last?.Dispose();
    }

    // ===== Verify TurnEndIdleFallbackMs constant is accessible and correct =====

    [Fact]
    public void TurnEndIdleFallbackMs_Is4000()
    {
        Assert.Equal(4000, CopilotService.TurnEndIdleFallbackMs);
    }
}
