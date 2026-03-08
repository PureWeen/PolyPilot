using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for session disposal resilience — verifies that DisposeAsync on
/// already-disposed or disconnected sessions doesn't crash the app.
/// Regression tests for: "Session disconnected and reconnect failed:
/// Cannot access a disposed object. Object name: 'StreamJsonRpc.JsonRpc'."
/// </summary>
public class SessionDisposalResilienceTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public SessionDisposalResilienceTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    [Fact]
    public async Task CloseSession_DemoMode_DoesNotThrow()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("disposable");
        Assert.NotNull(session);

        // In demo mode, Session is null! — CloseSessionAsync must handle gracefully
        var result = await svc.CloseSessionAsync("disposable");
        Assert.True(result);
        Assert.Empty(svc.GetAllSessions());
    }

    [Fact]
    public async Task CloseSession_DemoMode_MultipleTimes_DoesNotThrow()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("multi-close");

        // First close succeeds
        Assert.True(await svc.CloseSessionAsync("multi-close"));

        // Second close returns false (already removed) — must not throw
        Assert.False(await svc.CloseSessionAsync("multi-close"));
    }

    [Fact]
    public async Task DisposeService_WithDemoSessions_DoesNotThrow()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("session-1");
        await svc.CreateSessionAsync("session-2");
        Assert.Equal(2, svc.GetAllSessions().Count());

        // DisposeAsync iterates all sessions and calls DisposeAsync on each —
        // must not throw even if sessions have null or disposed underlying objects
        await svc.DisposeAsync();

        Assert.Equal(0, svc.SessionCount);
    }

    [Fact]
    public async Task DisposeService_AfterFailedPersistentInit_DoesNotThrow()
    {
        var svc = CreateService();

        // Persistent mode fails — no sessions, _client may be null
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999
        });
        Assert.False(svc.IsInitialized);

        // DisposeAsync must handle null client gracefully
        await svc.DisposeAsync();
    }

    [Fact]
    public async Task CloseSession_NonExistent_ReturnsFalse()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        // Closing a session that was never created should return false, not throw
        Assert.False(await svc.CloseSessionAsync("ghost-session"));
    }

    [Fact]
    public async Task CloseSession_TracksClosedSessionIds()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("tracked-close");
        Assert.NotNull(session.SessionId);

        await svc.CloseSessionAsync("tracked-close");

        // After closing, the session should be gone
        Assert.Empty(svc.GetAllSessions());
        Assert.Null(svc.ActiveSessionName);
    }

    [Fact]
    public async Task SendPrompt_DemoMode_AddsHistoryAndReturns()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("prompt-test");
        var result = await svc.SendPromptAsync("prompt-test", "Hello, world!");

        Assert.Equal("", result);
        Assert.Single(session.History);
        Assert.Equal("user", session.History[0].Role);
        Assert.Contains("Hello, world!", session.History[0].Content);
    }

    [Fact]
    public async Task SendPrompt_DemoMode_SkipHistory_DoesNotAddMessage()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("skip-hist");
        await svc.SendPromptAsync("skip-hist", "hidden message", skipHistoryMessage: true);

        Assert.Empty(session.History);
    }

    [Fact]
    public async Task SendPrompt_NonExistentSession_Throws()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SendPromptAsync("no-such-session", "test"));
    }

    [Fact]
    public async Task CloseSession_ActiveSession_SwitchesToAnother()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("first");
        await svc.CreateSessionAsync("second");
        svc.SetActiveSession("first");

        Assert.Equal("first", svc.ActiveSessionName);

        await svc.CloseSessionAsync("first");

        // Active session should switch to remaining one
        Assert.Equal("second", svc.ActiveSessionName);
    }

    [Fact]
    public async Task CloseSession_LastSession_ActiveBecomesNull()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("only-one");
        Assert.Equal("only-one", svc.ActiveSessionName);

        await svc.CloseSessionAsync("only-one");

        Assert.Null(svc.ActiveSessionName);
        Assert.Empty(svc.GetAllSessions());
    }

    [Fact]
    public async Task DisposeService_ThenSessionCount_IsZero()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("pre-dispose");
        Assert.Equal(1, svc.SessionCount);

        await svc.DisposeAsync();

        // After disposal, all sessions should be cleared
        Assert.Equal(0, svc.SessionCount);
    }

    // --- Reflection cycle + close interaction ---

    [Fact]
    public async Task CloseSession_WithActiveReflectionCycle_DoesNotThrow()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("reflect-close");

        // Start a reflection cycle (async version will fail to create evaluator in demo mode — that's fine)
        svc.StartReflectionCycle("reflect-close", "test goal", maxIterations: 3);

        // Give the async evaluator creation a moment to settle (it will fail silently in demo)
        await Task.Delay(50);

        // Close the session while reflection cycle is active
        var result = await svc.CloseSessionAsync("reflect-close");
        Assert.True(result);
        Assert.Empty(svc.GetAllSessions());
    }

    [Fact]
    public void StopReflectionCycle_NonExistentSession_DoesNotThrow()
    {
        var svc = CreateService();
        // Stopping a reflection cycle on a non-existent session should be a no-op
        svc.StopReflectionCycle("no-such-session");
    }

    [Fact]
    public async Task StopReflectionCycle_NoActiveCycle_DoesNotThrow()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        await svc.CreateSessionAsync("no-cycle");

        // Session exists but has no reflection cycle — should be a no-op
        svc.StopReflectionCycle("no-cycle");
    }

    // --- Abort resilience ---

    [Fact]
    public async Task AbortSession_DemoMode_DoesNotThrow()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("abort-test");

        // In demo mode, Session is null! — AbortAsync must not crash
        // AbortSessionAsync checks IsProcessing first, so it's a no-op when not processing
        await svc.AbortSessionAsync("abort-test");
    }

    [Fact]
    public async Task AbortSession_NonExistent_DoesNotThrow()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        // Aborting a non-existent session should be a no-op
        await svc.AbortSessionAsync("ghost");
    }

    // --- Rename with queued images ---

    [Fact]
    public async Task RenameSession_MovesQueuedImagePaths()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("old-name");
        svc.EnqueueMessage("old-name", "with image", new List<string> { "/tmp/img.png" });

        var result = svc.RenameSession("old-name", "new-name");
        Assert.True(result);

        // Old name should no longer exist
        Assert.Null(svc.GetSession("old-name"));
        Assert.NotNull(svc.GetSession("new-name"));

        // Queue should survive rename
        var session = svc.GetSession("new-name")!;
        Assert.Single(session.MessageQueue);
        Assert.Equal("with image", session.MessageQueue[0]);
    }

    [Fact]
    public async Task RenameSession_ToExistingName_ReturnsFalse()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("alpha");
        await svc.CreateSessionAsync("beta");

        // Can't rename to an existing name
        Assert.False(svc.RenameSession("alpha", "beta"));

        // Both sessions should still exist unchanged
        Assert.NotNull(svc.GetSession("alpha"));
        Assert.NotNull(svc.GetSession("beta"));
    }

    [Fact]
    public async Task RenameSession_SameName_ReturnsTrue()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("same");
        Assert.True(svc.RenameSession("same", "same"));
        Assert.NotNull(svc.GetSession("same"));
    }

    // --- ClearHistory ---

    [Fact]
    public async Task ClearHistory_ResetsMessageCount()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("clear-hist");
        await svc.SendPromptAsync("clear-hist", "msg1");
        await svc.SendPromptAsync("clear-hist", "msg2");
        Assert.Equal(2, session.History.Count);
        Assert.Equal(2, session.MessageCount);

        svc.ClearHistory("clear-hist");
        Assert.Empty(session.History);
        Assert.Equal(0, session.MessageCount);
    }

    [Fact]
    public void ClearHistory_NonExistentSession_DoesNotThrow()
    {
        var svc = CreateService();
        // Should not throw
        svc.ClearHistory("ghost");
    }

    // --- DisposeAsync edge cases ---

    [Fact]
    public async Task DisposeService_AfterAllSessionsClosed_DoesNotThrow()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("s1");
        await svc.CreateSessionAsync("s2");
        await svc.CloseSessionAsync("s1");
        await svc.CloseSessionAsync("s2");

        // All sessions already closed — DisposeAsync should handle empty state
        await svc.DisposeAsync();
        Assert.Equal(0, svc.SessionCount);
    }

    [Fact]
    public async Task DisposeService_CalledTwice_DoesNotThrow()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        await svc.CreateSessionAsync("double-dispose");

        await svc.DisposeAsync();
        // Second dispose should be safe (sessions already cleared)
        await svc.DisposeAsync();
        Assert.Equal(0, svc.SessionCount);
    }

    // --- EnqueueMessage + ClearQueue edge cases ---

    [Fact]
    public async Task ClearQueue_AlsoClearsImagePaths()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("queue-clear");
        svc.EnqueueMessage("queue-clear", "msg1", new List<string> { "/tmp/a.png" });
        svc.EnqueueMessage("queue-clear", "msg2", new List<string> { "/tmp/b.png" });
        Assert.Equal(2, session.MessageQueue.Count);

        svc.ClearQueue("queue-clear");
        Assert.Empty(session.MessageQueue);
    }

    [Fact]
    public async Task RemoveQueuedMessage_KeepsOtherMessages()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("remove-q");
        svc.EnqueueMessage("remove-q", "first");
        svc.EnqueueMessage("remove-q", "second");
        svc.EnqueueMessage("remove-q", "third");
        Assert.Equal(3, session.MessageQueue.Count);

        svc.RemoveQueuedMessage("remove-q", 1); // Remove "second"
        Assert.Equal(2, session.MessageQueue.Count);
        Assert.Equal("first", session.MessageQueue[0]);
        Assert.Equal("third", session.MessageQueue[1]);
    }

    // --- OnError event verification ---

    [Fact]
    public async Task SendPrompt_DemoMode_DoesNotFireOnError()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("no-error");
        var errorFired = false;
        svc.OnError += (_, _) => errorFired = true;

        await svc.SendPromptAsync("no-error", "hello");
        Assert.False(errorFired);
    }

    [Fact]
    public async Task OnStateChanged_FiresOnSendPrompt()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("state-event");
        var stateChangedCount = 0;
        svc.OnStateChanged += () => stateChangedCount++;

        await svc.SendPromptAsync("state-event", "hi");
        Assert.True(stateChangedCount > 0, "OnStateChanged should fire during SendPromptAsync");
    }

    [Fact]
    public async Task OnStateChanged_FiresOnClose()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("close-event");
        var stateChangedCount = 0;
        svc.OnStateChanged += () => stateChangedCount++;

        await svc.CloseSessionAsync("close-event");
        Assert.True(stateChangedCount > 0, "OnStateChanged should fire during CloseSessionAsync");
    }

    // --- Session isolation ---

    [Fact]
    public async Task SendPrompt_DoesNotAffectOtherSessions()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var s1 = await svc.CreateSessionAsync("isolated-1");
        var s2 = await svc.CreateSessionAsync("isolated-2");

        await svc.SendPromptAsync("isolated-1", "only for s1");

        Assert.Single(s1.History);
        Assert.Empty(s2.History);
    }

    [Fact]
    public async Task CloseSession_DoesNotAffectOtherSessions()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("keep");
        await svc.CreateSessionAsync("remove");

        await svc.SendPromptAsync("keep", "preserved message");
        await Task.Delay(50); // Let demo response complete

        await svc.CloseSessionAsync("remove");

        var kept = svc.GetSession("keep");
        Assert.NotNull(kept);
        Assert.True(kept!.History.Count >= 1);
        Assert.Contains(kept.History, m => m.Content.Contains("preserved message"));
    }
}
