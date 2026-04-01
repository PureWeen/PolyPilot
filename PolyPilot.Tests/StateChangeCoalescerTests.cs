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
        await using var svc = CreateService();
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
        await using var svc = CreateService();
        int fireCount = 0;
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.OnStateChanged += () =>
        {
            Interlocked.Increment(ref fireCount);
            fired.TrySetResult();
        };

        svc.NotifyStateChangedCoalesced();
        var completedTask = await Task.WhenAny(fired.Task, Task.Delay(5000));
        Assert.True(completedTask == fired.Task, "Coalesced notification should fire within 5s");
        await Task.Delay(200);

        Assert.Equal(1, fireCount);
    }

    [Fact]
    public async Task SeparateBursts_FireSeparately()
    {
        await using var svc = CreateService();
        int fireCount = 0;
        var firstBurst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBurst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.OnStateChanged += () =>
        {
            var count = Interlocked.Increment(ref fireCount);
            if (count >= 1) firstBurst.TrySetResult();
            if (count >= 2) secondBurst.TrySetResult();
        };

        // First burst
        for (int i = 0; i < 10; i++)
            svc.NotifyStateChangedCoalesced();
        var firstCompleted = await Task.WhenAny(firstBurst.Task, Task.Delay(5000));
        Assert.True(firstCompleted == firstBurst.Task, "First coalesced burst should fire within 5s");
        await Task.Delay(200);

        // Second burst after timer has fired
        for (int i = 0; i < 10; i++)
            svc.NotifyStateChangedCoalesced();
        var secondCompleted = await Task.WhenAny(secondBurst.Task, Task.Delay(5000));
        Assert.True(secondCompleted == secondBurst.Task, "Second coalesced burst should fire within 5s");
        await Task.Delay(200);

        // Each burst should produce ~1 notification
        Assert.InRange(fireCount, 2, 4);
    }

    [Fact]
    public void ImmediateNotify_StillWorks()
    {
        var svc = CreateService();
        try
        {
            int fireCount = 0;
            svc.OnStateChanged += () => Interlocked.Increment(ref fireCount);

            // Direct OnStateChanged (not coalesced) should fire immediately
            svc.NotifyStateChanged();
            Assert.Equal(1, fireCount);

            svc.NotifyStateChanged();
            Assert.Equal(2, fireCount);
        }
        finally
        {
            svc.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
