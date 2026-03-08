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

    // ===== Verify TurnEndIdleFallbackMs constant is accessible and correct =====

    [Fact]
    public void TurnEndIdleFallbackMs_Is4000()
    {
        Assert.Equal(4000, CopilotService.TurnEndIdleFallbackMs);
    }
}
