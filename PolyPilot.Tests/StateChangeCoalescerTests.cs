using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for NotifyStateChangedCoalesced — verifies that rapid-fire calls
/// coalesce into fewer OnStateChanged invocations.
/// </summary>
public class StateChangeCoalescerTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public StateChangeCoalescerTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    [Fact]
    public async Task RapidCalls_CoalesceIntoSingleNotification()
    {
        var svc = CreateService();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int fireCount = 0;
        svc.OnStateChanged += () =>
        {
            Interlocked.Increment(ref fireCount);
            tcs.TrySetResult();
        };

        // Fire 20 rapid coalesced notifications
        for (int i = 0; i < 20; i++)
            svc.NotifyStateChangedCoalesced();

        // Wait for at least one notification to fire (with generous timeout for CI)
        await Task.WhenAny(tcs.Task, Task.Delay(2000));
        // Small additional window for any extra coalesced fires
        await Task.Delay(100);

        // Should have coalesced into 1 notification (not 20)
        Assert.InRange(fireCount, 1, 3);
    }

    [Fact]
    public async Task SingleCall_FiresExactlyOnce()
    {
        var svc = CreateService();
        int fireCount = 0;
        svc.OnStateChanged += () => Interlocked.Increment(ref fireCount);

        svc.NotifyStateChangedCoalesced();
        // Wait well beyond the coalesce window (150ms) to ensure the timer has fired,
        // even under heavy CI load. Single call can only ever produce exactly 1 fire.
        await Task.Delay(600);

        Assert.Equal(1, fireCount);
    }

    [Fact]
    public async Task SeparateBursts_FireSeparately()
    {
        var svc = CreateService();
        int fireCount = 0;

        // First burst — wait via TCS so we don't depend on wall-clock timing.
        // Under heavy parallel test load, fixed delays like 800ms can be shorter
        // than the threadpool-delayed timer callback, causing the pending CAS flag
        // to remain set when burst 2 starts — burst 2 then silently merges into burst 1
        // and only one notification fires instead of two.
        var tcs1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.OnStateChanged += () =>
        {
            Interlocked.Increment(ref fireCount);
            tcs1.TrySetResult();
        };

        for (int i = 0; i < 10; i++)
            svc.NotifyStateChangedCoalesced();
        // Wait for first burst to actually fire (with generous 5s timeout)
        await Task.WhenAny(tcs1.Task, Task.Delay(5000));
        Assert.True(tcs1.Task.IsCompleted, "First burst should have fired within 5s");

        // Second burst after first has fired
        var tcs2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.OnStateChanged += () => tcs2.TrySetResult();
        for (int i = 0; i < 10; i++)
            svc.NotifyStateChangedCoalesced();
        await Task.WhenAny(tcs2.Task, Task.Delay(5000));
        Assert.True(tcs2.Task.IsCompleted, "Second burst should have fired within 5s");

        // Each burst produced at least one notification — total should be 2 or slightly more
        Assert.InRange(fireCount, 2, 6);
    }

    [Fact]
    public void ImmediateNotify_StillWorks()
    {
        var svc = CreateService();
        int fireCount = 0;
        svc.OnStateChanged += () => Interlocked.Increment(ref fireCount);

        // Direct OnStateChanged (not coalesced) should fire immediately
        svc.NotifyStateChanged();
        Assert.Equal(1, fireCount);

        svc.NotifyStateChanged();
        Assert.Equal(2, fireCount);
    }
}
