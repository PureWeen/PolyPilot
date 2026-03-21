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
        int fireCount = 0;
        svc.OnStateChanged += () => Interlocked.Increment(ref fireCount);

        // Fire 20 rapid coalesced notifications
        for (int i = 0; i < 20; i++)
            svc.NotifyStateChangedCoalesced();

        // Wait for the coalesce timer to fire (150ms + margin)
        await Task.Delay(300);

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
        svc.OnStateChanged += () => Interlocked.Increment(ref fireCount);

        // First burst
        for (int i = 0; i < 10; i++)
            svc.NotifyStateChangedCoalesced();
        await Task.Delay(300);

        // Second burst after timer has fired
        for (int i = 0; i < 10; i++)
            svc.NotifyStateChangedCoalesced();
        await Task.Delay(300);

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
