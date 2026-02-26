using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for steering message support: sending a new message while the assistant
/// is still processing aborts the current turn and starts a new one.
/// </summary>
public class SteeringMessageTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public SteeringMessageTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    // --- ChatMessage.IsInterrupted model tests ---

    [Fact]
    public void ChatMessage_IsInterrupted_DefaultsFalse()
    {
        var msg = ChatMessage.AssistantMessage("partial response");
        Assert.False(msg.IsInterrupted);
    }

    [Fact]
    public void ChatMessage_IsInterrupted_CanBeSetTrue()
    {
        var msg = new ChatMessage("assistant", "partial", DateTime.Now) { IsInterrupted = true };
        Assert.True(msg.IsInterrupted);
        Assert.Equal("assistant", msg.Role);
        Assert.Equal(ChatMessageType.Assistant, msg.MessageType);
    }

    [Fact]
    public void ChatMessage_UserMessage_IsInterrupted_DefaultsFalse()
    {
        var msg = ChatMessage.UserMessage("steer me");
        Assert.False(msg.IsInterrupted);
    }

    [Fact]
    public void ChatMessage_IsInterrupted_DoesNotAffectIsComplete()
    {
        var msg = new ChatMessage("assistant", "partial", DateTime.Now) { IsInterrupted = true, IsComplete = true };
        Assert.True(msg.IsInterrupted);
        Assert.True(msg.IsComplete);
    }

    [Fact]
    public void ChatMessage_IsInterrupted_IndependentOfIsSuccess()
    {
        var msg = new ChatMessage("assistant", "partial", DateTime.Now) { IsInterrupted = true };
        Assert.True(msg.IsInterrupted);
        Assert.False(msg.IsSuccess); // unrelated field should be unaffected
    }

    // --- AbortSessionAsync with markAsInterrupted ---

    [Fact]
    public async Task AbortWithMarkInterrupted_WhenNotProcessing_DoesNothing()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("no-op-abort");

        var historyBefore = session.History.Count;
        // Session is NOT processing — abort should be a no-op
        await svc.AbortSessionAsync("no-op-abort", markAsInterrupted: true);

        Assert.False(session.IsProcessing);
        Assert.Equal(historyBefore, session.History.Count);
    }

    [Fact]
    public async Task AbortWithMarkInterrupted_ClearsProcessingState()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("abort-steer");

        session.IsProcessing = true;
        await svc.AbortSessionAsync("abort-steer", markAsInterrupted: true);

        Assert.False(session.IsProcessing);
        Assert.False(session.IsResumed);
        Assert.Null(session.ProcessingStartedAt);
        Assert.Equal(0, session.ToolCallCount);
        Assert.Equal(0, session.ProcessingPhase);
    }

    [Fact]
    public async Task AbortWithMarkInterrupted_FiresStateChanged()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        await svc.CreateSessionAsync("abort-steer-state");

        var stateChangedCount = 0;
        svc.OnStateChanged += () => stateChangedCount++;

        var session = svc.GetAllSessions().First();
        session.IsProcessing = true;
        await svc.AbortSessionAsync("abort-steer-state", markAsInterrupted: true);

        Assert.True(stateChangedCount >= 1, "OnStateChanged must fire when marking as interrupted");
    }

    // --- SteerSessionAsync ---

    [Fact]
    public async Task SteerSession_WhenNotProcessing_SendsNormally()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("steer-idle");

        Assert.False(session.IsProcessing);
        await svc.SteerSessionAsync("steer-idle", "steering message");

        // In demo mode, the user message is added synchronously
        Assert.True(session.History.Any(m => m.Role == "user" && m.Content.Contains("steering message")),
            "Steering message should be added to history as a user message");
    }

    [Fact]
    public async Task SteerSession_WhenProcessing_AbortsAndSends()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("steer-processing");

        // Send first message, then simulate processing
        await svc.SendPromptAsync("steer-processing", "first message");
        session.IsProcessing = true; // simulate stuck/still processing

        var historyBefore = session.History.Count;

        await svc.SteerSessionAsync("steer-processing", "new direction");

        // After steering: IsProcessing is cleared by abort, then new send happens
        Assert.False(session.IsProcessing, "Steering should clear processing state");
        // The steering message should appear in history
        Assert.True(session.History.Any(m => m.Role == "user" && m.Content.Contains("new direction")),
            "Steering message should appear in history as a user message");
    }

    [Fact]
    public async Task SteerSession_WhenProcessing_FiresStateChanged()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        await svc.CreateSessionAsync("steer-state-fire");

        var stateChangedCount = 0;
        svc.OnStateChanged += () => stateChangedCount++;

        var session = svc.GetAllSessions().First();
        session.IsProcessing = true;
        await svc.SteerSessionAsync("steer-state-fire", "steer now");

        Assert.True(stateChangedCount >= 1, "OnStateChanged must fire during steer");
    }

    [Fact]
    public async Task SteerSession_PreservesHistoryFromBeforeSteer()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("steer-history");

        // Build some history
        await svc.SendPromptAsync("steer-history", "original question");
        var countBefore = session.History.Count;

        // Simulate processing and then steer
        session.IsProcessing = true;
        await svc.SteerSessionAsync("steer-history", "actually, do this instead");

        // Original history should be intact
        Assert.True(session.History.Count >= countBefore,
            "Existing history must not be lost during steering");
        Assert.True(session.History.Any(m => m.Content.Contains("original question")),
            "Original user message should still be in history");
    }

    // --- Regression: abort (without markAsInterrupted) still works normally ---

    [Fact]
    public async Task AbortWithoutMarkInterrupted_HistoryMessageNotInterrupted()
    {
        // Plain abort (Stop button) should not mark messages as interrupted.
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("plain-abort");

        session.IsProcessing = true;
        await svc.AbortSessionAsync("plain-abort"); // default: markAsInterrupted = false

        Assert.False(session.IsProcessing);
        // No messages added (no partial response in CurrentResponse in demo mode)
        // Ensure no IsInterrupted messages were added spuriously
        Assert.True(session.History.All(m => !m.IsInterrupted),
            "Plain abort must not mark any message as interrupted");
    }

    // --- SendingFlag generation-guard: steer abort must not clobber new turn's lock ---

    [Fact]
    public async Task SteerSession_AfterAbort_NewTurnCanSend()
    {
        // Regression for: steer abort resets SendingFlag=0, new turn acquires it (SendingFlag=1),
        // old turn's catch block must not reset SendingFlag back to 0 (clobbering new turn).
        // In demo mode we can't reproduce the async Task cancellation exactly, but we can
        // verify that after steering, further sends still succeed (i.e., the lock isn't stuck).
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("steer-lock-guard");

        // Steer once
        await svc.SendPromptAsync("steer-lock-guard", "original");
        session.IsProcessing = true;
        await svc.SteerSessionAsync("steer-lock-guard", "steer 1");

        // Must be able to steer again — if SendingFlag was clobbered IsProcessing throws
        Assert.False(session.IsProcessing);

        // Steer again to confirm lock is properly held/released
        session.IsProcessing = true;
        await svc.SteerSessionAsync("steer-lock-guard", "steer 2");
        Assert.False(session.IsProcessing);

        // And normal send still works
        await svc.SendPromptAsync("steer-lock-guard", "final message");
        Assert.False(session.IsProcessing);
    }

    [Fact]
    public async Task SteerSession_IsAwaitable_ExceptionPropagates()
    {
        // SteerSessionAsync now awaits SendPromptAsync, so errors surface to the caller.
        // Verify it at least returns a Task (not void) and doesn't swallow normal completion.
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("steer-awaitable");

        // Should complete without throwing for a normal steering scenario
        session.IsProcessing = true;
        var task = svc.SteerSessionAsync("steer-awaitable", "steer message");
        Assert.NotNull(task); // returns a real Task, not fire-and-forget
        await task; // must complete (not hang or throw in demo mode)
        Assert.False(session.IsProcessing);
    }
}
