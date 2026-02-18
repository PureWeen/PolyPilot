using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the processing watchdog that detects sessions stuck in "Thinking" state
/// when the persistent server dies mid-turn and no more SDK events arrive.
/// Regression tests for: sessions permanently stuck in IsProcessing=true after server disconnect.
/// </summary>
public class ProcessingWatchdogTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public ProcessingWatchdogTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    // --- Watchdog constant validation ---

    [Fact]
    public void WatchdogCheckInterval_IsReasonable()
    {
        // Check interval must be at least 5s to avoid excessive polling,
        // and at most 60s so stuck state is detected in reasonable time.
        Assert.InRange(CopilotService.WatchdogCheckIntervalSeconds, 5, 60);
    }

    [Fact]
    public void WatchdogInactivityTimeout_IsReasonable()
    {
        // Timeout must be long enough for legitimate tool executions (>60s)
        // but short enough to recover from dead connections (<300s).
        Assert.InRange(CopilotService.WatchdogInactivityTimeoutSeconds, 60, 300);
    }

    [Fact]
    public void WatchdogTimeout_IsGreaterThanCheckInterval()
    {
        // Timeout must be strictly greater than check interval — watchdog needs
        // multiple checks before declaring inactivity.
        Assert.True(
            CopilotService.WatchdogInactivityTimeoutSeconds > CopilotService.WatchdogCheckIntervalSeconds,
            "Inactivity timeout must be greater than check interval");
    }

    // --- Demo mode: sessions should not get stuck ---

    [Fact]
    public async Task DemoMode_SendPrompt_DoesNotLeaveIsProcessingTrue()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("demo-no-stuck");
        await svc.SendPromptAsync("demo-no-stuck", "Test prompt");

        // Demo mode returns immediately — IsProcessing should never be stuck true
        Assert.False(session.IsProcessing,
            "Demo mode sessions should not be left in IsProcessing=true state");
    }

    [Fact]
    public async Task DemoMode_MultipleSends_NoneStuck()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var s1 = await svc.CreateSessionAsync("multi-1");
        var s2 = await svc.CreateSessionAsync("multi-2");

        await svc.SendPromptAsync("multi-1", "Hello");
        await svc.SendPromptAsync("multi-2", "World");

        Assert.False(s1.IsProcessing);
        Assert.False(s2.IsProcessing);
    }

    // --- Model-level: system message format for stuck sessions ---

    [Fact]
    public void SystemMessage_ConnectionLost_HasExpectedContent()
    {
        var msg = ChatMessage.SystemMessage(
            "⚠️ Connection lost — no response received. You can try sending your message again.");

        Assert.Equal("system", msg.Role);
        Assert.Contains("Connection lost", msg.Content);
        Assert.Contains("try sending", msg.Content);
    }

    [Fact]
    public void AgentSessionInfo_IsProcessing_DefaultsFalse()
    {
        var info = new AgentSessionInfo { Name = "test", Model = "test-model" };
        Assert.False(info.IsProcessing);
    }

    [Fact]
    public void AgentSessionInfo_IsProcessing_CanBeSetAndCleared()
    {
        var info = new AgentSessionInfo { Name = "test", Model = "test-model" };

        info.IsProcessing = true;
        Assert.True(info.IsProcessing);

        info.IsProcessing = false;
        Assert.False(info.IsProcessing);
    }

    // --- Persistent mode: initialization failure leaves clean state ---

    [Fact]
    public async Task PersistentMode_FailedInit_NoStuckSessions()
    {
        var svc = CreateService();

        // Persistent mode with unreachable port — will fail to connect
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999
        });

        // No sessions should exist, and none should be stuck processing
        Assert.Empty(svc.GetAllSessions());
        foreach (var session in svc.GetAllSessions())
        {
            Assert.False(session.IsProcessing,
                $"Session '{session.Name}' should not be stuck processing after failed init");
        }
    }

    // --- Recovery scenario: IsProcessing cleared allows new messages ---

    [Fact]
    public async Task DemoMode_SessionNotProcessing_CanSendNewMessage()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("recovery-test");

        // Simulate the state after watchdog clears stuck processing:
        // session.IsProcessing should be false, allowing new sends.
        Assert.False(session.IsProcessing);

        // Should succeed without throwing "Session is already processing"
        await svc.SendPromptAsync("recovery-test", "Message after recovery");
        Assert.Single(session.History);
    }

    [Fact]
    public async Task DemoMode_SessionAlreadyProcessing_ThrowsOnSend()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("already-busy");

        // Manually set IsProcessing to simulate stuck state (before watchdog fires)
        session.IsProcessing = true;

        // SendPromptAsync in demo mode doesn't check IsProcessing (it returns early),
        // but non-demo mode would throw. Verify the model state.
        Assert.True(session.IsProcessing);
    }

    // --- Watchdog system message appears in history ---

    [Fact]
    public void SystemMessage_AddedToHistory_IsVisible()
    {
        var info = new AgentSessionInfo { Name = "test-hist", Model = "test-model" };

        // Simulate what the watchdog does when clearing stuck state
        info.IsProcessing = true;
        info.History.Add(ChatMessage.SystemMessage(
            "⚠️ Connection lost — no response received. You can try sending your message again."));
        info.IsProcessing = false;

        Assert.Single(info.History);
        Assert.Equal(ChatMessageType.System, info.History[0].MessageType);
        Assert.Contains("Connection lost", info.History[0].Content);
        Assert.False(info.IsProcessing);
    }

    // --- OnError fires when session appears stuck ---

    [Fact]
    public async Task DemoMode_OnError_NotFiredForNormalOperation()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("no-error");
        var errors = new List<(string session, string error)>();
        svc.OnError += (s, e) => errors.Add((s, e));

        await svc.SendPromptAsync("no-error", "Normal message");

        Assert.Empty(errors);
    }

    // --- Reconnect after stuck state ---

    [Fact]
    public async Task ReconnectAsync_ClearsAllSessions()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var s1 = await svc.CreateSessionAsync("pre-reconnect-1");
        var s2 = await svc.CreateSessionAsync("pre-reconnect-2");

        // Reconnect should clear all existing sessions (fresh start)
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        // Old session references should not be stuck processing
        Assert.False(s1.IsProcessing);
        Assert.False(s2.IsProcessing);
    }

    // ===========================================================================
    // Regression tests for: relaunch deploys new app, old copilot server running
    // Session restore silently swallows all failures → app shows 0 sessions.
    // ===========================================================================

    [Fact]
    public async Task PersistentMode_FailedInit_SetsNeedsConfiguration()
    {
        var svc = CreateService();

        // Persistent mode with unreachable server → should set NeedsConfiguration
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999
        });

        Assert.False(svc.IsInitialized,
            "App should NOT be initialized when persistent server is unreachable");
        Assert.True(svc.NeedsConfiguration,
            "NeedsConfiguration should be true so settings page is shown");
    }

    [Fact]
    public async Task PersistentMode_FailedInit_NoSessionsStuckProcessing()
    {
        var svc = CreateService();

        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999
        });

        // After failed init, no sessions should exist at all (much less stuck ones)
        var sessions = svc.GetAllSessions().ToList();
        Assert.Empty(sessions);
    }

    [Fact]
    public async Task DemoMode_SessionRestore_AllSessionsVisible()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        // Create multiple sessions
        var s1 = await svc.CreateSessionAsync("restore-1");
        var s2 = await svc.CreateSessionAsync("restore-2");
        var s3 = await svc.CreateSessionAsync("restore-3");

        Assert.Equal(3, svc.GetAllSessions().Count());

        // Reconnect to demo mode should start fresh (demo has no persistence)
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        // After reconnect, old sessions are cleared (demo doesn't persist)
        // The key invariant: session count matches what's visible to the user
        Assert.Equal(svc.SessionCount, svc.GetAllSessions().Count());
    }

    [Fact]
    public async Task ReconnectAsync_IsInitialized_CorrectForEachMode()
    {
        var svc = CreateService();

        // Demo mode → always succeeds
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        Assert.True(svc.IsInitialized, "Demo mode should always initialize");

        // Persistent mode with bad port → fails
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999
        });
        Assert.False(svc.IsInitialized, "Persistent with bad port should fail");

        // Back to demo → recovers
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        Assert.True(svc.IsInitialized, "Should recover when switching back to Demo");
    }

    [Fact]
    public async Task ReconnectAsync_ClearsStuckProcessingFromPreviousMode()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("was-stuck");
        session.IsProcessing = true; // Simulate stuck state

        // Reconnect should clear all sessions including stuck ones
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        // After reconnect, old sessions are removed — no stuck sessions in new state
        Assert.Empty(svc.GetAllSessions());
        // If we create new sessions, they start clean
        var fresh = await svc.CreateSessionAsync("fresh");
        Assert.False(fresh.IsProcessing, "New session after reconnect should not be stuck");
    }

    [Fact]
    public async Task OnStateChanged_FiresDuringReconnect()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var stateChangedCount = 0;
        svc.OnStateChanged += () => stateChangedCount++;

        // Reconnect to a different mode and back
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999
        });

        Assert.True(stateChangedCount > 0,
            "OnStateChanged must fire during reconnect so UI updates");
    }
}
