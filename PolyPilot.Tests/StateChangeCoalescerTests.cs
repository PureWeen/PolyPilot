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
        await Task.Delay(1200);

        Assert.Equal(1, fireCount);
    }

    [Fact]
    public async Task SeparateBursts_FireSeparately()
    {
        var svc = CreateService();
        var firstBurstFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBurstFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int fireCount = 0;
        int secondBurstThreshold = int.MaxValue;
        svc.OnStateChanged += () =>
        {
            var count = Interlocked.Increment(ref fireCount);
            firstBurstFired.TrySetResult();
            if (count >= Volatile.Read(ref secondBurstThreshold))
                secondBurstFired.TrySetResult();
        };

        // First burst
        for (int i = 0; i < 10; i++)
            svc.NotifyStateChangedCoalesced();
        await Task.WhenAny(firstBurstFired.Task, Task.Delay(3000));
        Assert.True(firstBurstFired.Task.IsCompleted, "Expected first burst notification to fire");

        // Give any straggling callbacks from the first burst a brief chance to drain
        // before we define the threshold for the second burst.
        await Task.Delay(250);
        Volatile.Write(ref secondBurstThreshold, Volatile.Read(ref fireCount) + 1);

        // Second burst after the first coalesced notification has actually fired
        for (int i = 0; i < 10; i++)
            svc.NotifyStateChangedCoalesced();
        await Task.WhenAny(secondBurstFired.Task, Task.Delay(3000));
        Assert.True(secondBurstFired.Task.IsCompleted, "Expected second burst notification to fire");
        await Task.Delay(100);

        // Each burst should produce ~1 notification
        Assert.InRange(fireCount, 2, 4);
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
