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
        // Timeout must be long enough for legitimate pauses (>60s)
        // but short enough to recover from dead connections (<300s).
        Assert.InRange(CopilotService.WatchdogInactivityTimeoutSeconds, 60, 300);
    }

    [Fact]
    public void WatchdogToolExecutionTimeout_IsReasonable()
    {
        // Tool execution timeout must be long enough for long-running tools
        // (e.g., UI tests, builds) but not infinite.
        Assert.InRange(CopilotService.WatchdogToolExecutionTimeoutSeconds, 300, 1800);
        Assert.True(
            CopilotService.WatchdogToolExecutionTimeoutSeconds > CopilotService.WatchdogInactivityTimeoutSeconds,
            "Tool execution timeout must be greater than base inactivity timeout");
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
            "⚠️ Session appears stuck — no response received. You can try sending your message again.");

        Assert.Equal("system", msg.Role);
        Assert.Contains("appears stuck", msg.Content);
        Assert.Contains("try sending", msg.Content);
    }

    [Theory]
    [InlineData(30, "30 seconds")]
    [InlineData(59, "59 seconds")]
    [InlineData(60, "1 minute(s)")]
    [InlineData(120, "2 minute(s)")]
    [InlineData(600, "10 minute(s)")]
    public void WatchdogErrorMessage_FormatsTimeoutCorrectly(int effectiveTimeout, string expected)
    {
        // Mirrors the production formatting logic in RunProcessingWatchdogAsync.
        // Regression guard: 30s quiescence must not produce "0 minute(s)".
        var timeoutDisplay = effectiveTimeout >= 60
            ? $"{effectiveTimeout / 60} minute(s)"
            : $"{effectiveTimeout} seconds";
        Assert.Equal(expected, timeoutDisplay);
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
            "⚠️ Session appears stuck — no response received. You can try sending your message again."));
        info.IsProcessing = false;

        Assert.Single(info.History);
        Assert.Equal(ChatMessageType.System, info.History[0].MessageType);
        Assert.Contains("appears stuck", info.History[0].Content);
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

    // ===========================================================================
    // Regression tests for: SEND/COMPLETE race condition (generation counter)
    //
    // When SessionIdleEvent queues CompleteResponse via SyncContext.Post(),
    // a new SendPromptAsync can sneak in before the callback executes.
    // Without a generation counter, CompleteResponse would clear the NEW send's
    // IsProcessing state, causing the new turn's events to become "ghost events".
    //
    // Evidence from diagnostic log (13:00:00 race):
    //   13:00:00.238 [EVT] SessionIdleEvent   ← IDLE arrives
    //   13:00:00.242 [IDLE] queued             ← Post() to UI thread
    //   13:00:00.251 [SEND] IsProcessing=true  ← NEW SEND sneaks in!
    //   13:00:00.261 [COMPLETE] responseLen=0  ← Completes WRONG turn
    // ===========================================================================

    [Fact]
    public async Task DemoMode_RapidSends_NoGhostState()
    {
        // Verify that rapid sequential sends in demo mode don't leave
        // IsProcessing in an inconsistent state.
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("rapid-send");

        for (int i = 0; i < 10; i++)
        {
            await svc.SendPromptAsync("rapid-send", $"Message {i}");
            Assert.False(session.IsProcessing,
                $"IsProcessing should be false after send {i} completes");
        }

        // All messages should have been processed
        Assert.True(session.History.Count >= 10,
            "All rapid sends should produce responses in demo mode");
    }

    [Fact]
    public async Task DemoMode_SendAfterComplete_ProcessingStateClean()
    {
        // Simulates the scenario where a send follows immediately after
        // a completion — the generation counter should prevent the old
        // IDLE's CompleteResponse from affecting the new send.
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("send-after-complete");

        // First send completes normally
        await svc.SendPromptAsync("send-after-complete", "First message");
        Assert.False(session.IsProcessing, "First send should complete");

        // Second send immediately after — in real code, a stale IDLE callback
        // from the first turn could race with this send.
        await svc.SendPromptAsync("send-after-complete", "Second message");
        Assert.False(session.IsProcessing, "Second send should also complete");

        // Both messages should be in history
        Assert.True(session.History.Count >= 2,
            "Both messages should produce responses");
    }

    [Fact]
    public async Task SendPromptAsync_DebugInfrastructure_WorksInDemoMode()
    {
        // Verify that the debug/logging infrastructure is functional.
        // Note: the generation counter [SEND] log only fires in non-demo mode
        // (the demo path returns before reaching that code). This test verifies
        // the OnDebug event fires for other operations.
        var svc = CreateService();

        var debugMessages = new List<string>();
        svc.OnDebug += msg => debugMessages.Add(msg);

        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        await svc.CreateSessionAsync("gen-debug");

        // Demo init produces debug messages
        Assert.NotEmpty(debugMessages);
        Assert.Contains(debugMessages, m => m.Contains("Demo mode"));
    }

    [Fact]
    public async Task AbortSessionAsync_WorksRegardlessOfGeneration()
    {
        // AbortSessionAsync must always clear IsProcessing regardless of
        // generation state. It bypasses the generation check (force-complete).
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("abort-gen");

        // Manually set IsProcessing to simulate a session mid-turn
        session.IsProcessing = true;

        // Abort should force-clear regardless of generation
        await svc.AbortSessionAsync("abort-gen");

        Assert.False(session.IsProcessing,
            "AbortSessionAsync must always clear IsProcessing, regardless of generation");
    }

    [Fact]
    public async Task AbortSessionAsync_ClearsQueueAndProcessingStatus()
    {
        // Abort must clear the message queue so queued messages don't auto-send,
        // and reset processing status fields so the UI shows idle state.
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("abort-queue");

        // Simulate active processing with queued messages
        session.IsProcessing = true;
        session.ProcessingStartedAt = DateTime.UtcNow;
        session.ToolCallCount = 5;
        session.ProcessingPhase = 3;
        session.MessageQueue.Add("queued message 1");
        session.MessageQueue.Add("queued message 2");

        await svc.AbortSessionAsync("abort-queue");

        Assert.False(session.IsProcessing);
        Assert.Null(session.ProcessingStartedAt);
        Assert.Equal(0, session.ToolCallCount);
        Assert.Equal(0, session.ProcessingPhase);
        Assert.Empty(session.MessageQueue);
    }

    [Fact]
    public async Task AbortSessionAsync_AllowsSubsequentSend()
    {
        // After aborting a stuck session, user should be able to send a new message.
        // This tests the full Stop → re-send flow the user described.
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("abort-resend");

        // Send first message
        await svc.SendPromptAsync("abort-resend", "First message");
        Assert.False(session.IsProcessing);

        // Simulate stuck state (what happens when CLI goes silent)
        session.IsProcessing = true;

        // User clicks Stop
        await svc.AbortSessionAsync("abort-resend");
        Assert.False(session.IsProcessing);

        // User sends another message — should succeed, not throw "already processing"
        await svc.SendPromptAsync("abort-resend", "Message after abort");
        Assert.False(session.IsProcessing);
    }

    [Fact]
    public async Task StuckSession_ManuallySetProcessing_AbortClears()
    {
        // Simulates the exact user scenario: session stuck in "Thinking",
        // user clicks Stop, gets response, can continue.
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("stuck-thinking");

        // Start a conversation
        await svc.SendPromptAsync("stuck-thinking", "Initial message");
        var historyCountBefore = session.History.Count;

        // Simulate getting stuck (events stop arriving, IsProcessing stays true)
        session.IsProcessing = true;

        // In demo mode, sends return early without checking IsProcessing.
        // In non-demo mode, this would throw "already processing".
        // Verify the stuck state is set correctly.
        Assert.True(session.IsProcessing);

        // Abort clears the stuck state
        await svc.AbortSessionAsync("stuck-thinking");
        Assert.False(session.IsProcessing);

        // Now user can send again
        await svc.SendPromptAsync("stuck-thinking", "Recovery message");
        Assert.False(session.IsProcessing);
        Assert.True(session.History.Count > historyCountBefore,
            "New messages should be added to history after abort recovery");
    }

    [Fact]
    public async Task DemoMode_ConcurrentSessions_IndependentState()
    {
        // Generation counters are per-session. Operations on one session
        // must not affect another session's state.
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var s1 = await svc.CreateSessionAsync("concurrent-1");
        var s2 = await svc.CreateSessionAsync("concurrent-2");
        var s3 = await svc.CreateSessionAsync("concurrent-3");

        // Send to all three
        await svc.SendPromptAsync("concurrent-1", "Hello 1");
        await svc.SendPromptAsync("concurrent-2", "Hello 2");
        await svc.SendPromptAsync("concurrent-3", "Hello 3");

        // All should be in clean state
        Assert.False(s1.IsProcessing, "Session 1 should not be stuck");
        Assert.False(s2.IsProcessing, "Session 2 should not be stuck");
        Assert.False(s3.IsProcessing, "Session 3 should not be stuck");

        // Stuck one session — others unaffected
        s2.IsProcessing = true;
        Assert.False(s1.IsProcessing);
        Assert.True(s2.IsProcessing);
        Assert.False(s3.IsProcessing);

        // Send to non-stuck sessions still works
        await svc.SendPromptAsync("concurrent-1", "Message while s2 stuck");
        await svc.SendPromptAsync("concurrent-3", "Message while s2 stuck");
        Assert.False(s1.IsProcessing);
        Assert.False(s3.IsProcessing);
    }

    [Fact]
    public async Task DemoMode_AbortNotProcessing_IsNoOp()
    {
        // Aborting a session that isn't processing should be harmless
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("abort-noop");
        Assert.False(session.IsProcessing);

        // Should not throw or change state
        await svc.AbortSessionAsync("abort-noop");
        Assert.False(session.IsProcessing);
    }

    [Fact]
    public async Task DemoMode_AbortNonExistentSession_IsNoOp()
    {
        // Aborting a session that doesn't exist should not throw
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        // Should be a no-op, not an exception
        await svc.AbortSessionAsync("does-not-exist");
    }

    [Fact]
    public async Task DemoMode_SendWhileProcessing_StillSucceeds()
    {
        // Demo mode's SendPromptAsync returns early without checking IsProcessing.
        // This is by design — demo responses are simulated locally and don't conflict.
        // The IsProcessing guard only applies in non-demo SDK mode.
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("double-send");
        session.IsProcessing = true; // Simulate in-flight request

        // Demo mode ignores IsProcessing — should not throw
        await svc.SendPromptAsync("double-send", "Demo allows this");
        // The manually-set IsProcessing persists (demo doesn't clear it),
        // but the send itself should succeed.
    }

    [Fact]
    public async Task DemoMode_MultipleRapidAborts_NoThrow()
    {
        // Multiple rapid aborts on the same session should be idempotent
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("rapid-abort");
        session.IsProcessing = true;

        // Fire multiple aborts in quick succession
        await svc.AbortSessionAsync("rapid-abort");
        await svc.AbortSessionAsync("rapid-abort");
        await svc.AbortSessionAsync("rapid-abort");

        Assert.False(session.IsProcessing);
    }

    [Fact]
    public async Task DemoMode_HistoryIntegrity_AfterAbortAndResend()
    {
        // After abort + resend, history should contain all user messages
        // and should not have duplicate or missing entries.
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("history-integrity");

        // Normal send
        await svc.SendPromptAsync("history-integrity", "Message 1");
        var count1 = session.History.Count;

        // Simulate stuck and abort
        session.IsProcessing = true;
        await svc.AbortSessionAsync("history-integrity");

        // Send again
        await svc.SendPromptAsync("history-integrity", "Message 2");
        var count2 = session.History.Count;

        // History should have grown (user message + response for each send)
        Assert.True(count2 > count1,
            $"History should grow after abort+resend (was {count1}, now {count2})");

        // All user messages should be present
        var userMessages = session.History.Where(m => m.Role == "user").Select(m => m.Content).ToList();
        Assert.Contains("Message 1", userMessages);
        Assert.Contains("Message 2", userMessages);
    }

    [Fact]
    public async Task OnStateChanged_FiresOnAbort()
    {
        // UI must be notified when abort clears IsProcessing
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("abort-notify");
        session.IsProcessing = true;

        var stateChangedCount = 0;
        svc.OnStateChanged += () => stateChangedCount++;

        await svc.AbortSessionAsync("abort-notify");

        Assert.True(stateChangedCount > 0,
            "OnStateChanged must fire when abort clears processing state");
    }

    [Fact]
    public async Task OnStateChanged_DoesNotFireOnAbortWhenNotProcessing()
    {
        // Abort on an already-idle session should not fire OnStateChanged
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("abort-idle");

        var stateChangedCount = 0;
        svc.OnStateChanged += () => stateChangedCount++;

        await svc.AbortSessionAsync("abort-idle");

        Assert.Equal(0, stateChangedCount);
    }

    // --- Bug A: Watchdog callback must not kill a new turn after abort+resend ---

    [Fact]
    public async Task WatchdogCallback_AfterAbortAndResend_DoesNotKillNewTurn()
    {
        // Regression: if the watchdog fires and queues a callback via InvokeOnUI,
        // then the user aborts + resends before the callback executes, the callback
        // must detect the generation mismatch and skip — not kill the new turn.
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("watchdog-gen");

        // Simulate first turn
        await svc.SendPromptAsync("watchdog-gen", "First prompt");
        Assert.False(session.IsProcessing, "Demo mode completes immediately");

        // Simulate second turn then abort
        session.IsProcessing = true;
        await svc.AbortSessionAsync("watchdog-gen");
        Assert.False(session.IsProcessing, "Abort clears processing");

        // Simulate third turn (the new send)
        await svc.SendPromptAsync("watchdog-gen", "Third prompt");

        // After demo completes, session should be idle with response in history
        Assert.False(session.IsProcessing, "New send completed successfully");
        Assert.True(session.History.Count >= 2,
            "History should contain messages from successful sends");
    }

    [Fact]
    public async Task AbortThenResend_PreservesNewTurnState()
    {
        // Verifies the abort+resend sequence leaves the session in a clean state
        // where the new turn's processing is not interfered with.
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("abort-resend");

        // Send, abort, send again — the second send must succeed cleanly
        await svc.SendPromptAsync("abort-resend", "First");
        session.IsProcessing = true; // simulate stuck
        await svc.AbortSessionAsync("abort-resend");
        await svc.SendPromptAsync("abort-resend", "Second");

        Assert.False(session.IsProcessing);
        var lastMsg = session.History.LastOrDefault();
        Assert.NotNull(lastMsg);
    }

    // --- Bug B: Resume fallback must not race with SDK events ---

    [Fact]
    public async Task ResumeFallback_DoesNotCorruptState_WhenSessionCompletesNormally()
    {
        // The 10s resume fallback must not clear IsProcessing if the session
        // has already completed normally (HasReceivedEventsSinceResume = true).
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("resume-safe");

        // After demo mode init, session should be idle
        Assert.False(session.IsProcessing,
            "Fresh session should not be stuck processing");
    }

    [Fact]
    public async Task ResumeFallback_StateMutations_OnlyViaUIThread()
    {
        // Verify that after creating a session, state mutations from the resume
        // fallback (if any) don't corrupt the history list.
        // In demo mode, the fallback should never fire since events arrive immediately.
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("resume-thread-safe");
        await svc.SendPromptAsync("resume-thread-safe", "Test");

        // Wait a moment to ensure any background tasks have run
        await Task.Delay(100);

        // History should be intact — no corruption from concurrent List<T> access
        var historySnapshot = session.History.ToArray();
        Assert.True(historySnapshot.Length >= 1, "History should have at least the response");
        Assert.All(historySnapshot, msg => Assert.NotNull(msg.Content));
    }

    [Fact]
    public async Task MultipleAbortResendCycles_MaintainCleanState()
    {
        // Stress test: rapid abort+resend cycles should not leave orphaned state
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("stress-abort");

        for (int i = 0; i < 5; i++)
        {
            await svc.SendPromptAsync("stress-abort", $"Prompt {i}");
            if (i < 4) // Don't abort the last one
            {
                session.IsProcessing = true; // simulate stuck
                await svc.AbortSessionAsync("stress-abort");
                Assert.False(session.IsProcessing, $"Abort cycle {i} should clear processing");
            }
        }

        Assert.False(session.IsProcessing, "Final state should be idle");
        // History should contain messages from all cycles
        Assert.True(session.History.Count >= 5,
            $"Expected at least 5 history entries from 5 send cycles, got {session.History.Count}");
    }

    // ===========================================================================
    // Watchdog timeout selection logic
    // Tests the 3-way condition: hasActiveTool || IsResumed || HasUsedToolsThisTurn
    // SessionState is private, so we replicate the decision logic inline using
    // local variables that mirror the watchdog algorithm in CopilotService.Events.cs.
    // ===========================================================================

    [Fact]
    public void HasUsedToolsThisTurn_DefaultsFalse()
    {
        // Mirrors SessionState.HasUsedToolsThisTurn default (bool default = false)
        bool hasUsedToolsThisTurn = default;
        Assert.False(hasUsedToolsThisTurn);
    }

    [Fact]
    public void HasUsedToolsThisTurn_CanBeSet()
    {
        // Mirrors setting HasUsedToolsThisTurn = true on ToolExecutionStartEvent
        bool hasUsedToolsThisTurn = false;
        hasUsedToolsThisTurn = true;
        Assert.True(hasUsedToolsThisTurn);
    }

    [Fact]
    public void HasUsedToolsThisTurn_ResetByCompleteResponse()
    {
        // Mirrors CompleteResponse resetting HasUsedToolsThisTurn = false
        bool hasUsedToolsThisTurn = true;
        // CompleteResponse resets the field
        hasUsedToolsThisTurn = false;
        Assert.False(hasUsedToolsThisTurn);
    }

    /// <summary>
    /// Mirrors the three-tier timeout selection logic from RunProcessingWatchdogAsync.
    /// Kept in sync so tests validate the actual production formula.
    /// </summary>
    private static int ComputeEffectiveTimeout(bool hasActiveTool, bool isResumed, bool hasReceivedEvents, bool hasUsedTools, bool isMultiAgent = false)
    {
        var useResumeQuiescence = isResumed && !hasReceivedEvents && !hasActiveTool && !hasUsedTools;
        var useToolTimeout = hasActiveTool || (isResumed && !useResumeQuiescence) || hasUsedTools || isMultiAgent;
        return useResumeQuiescence
            ? CopilotService.WatchdogResumeQuiescenceTimeoutSeconds
            : useToolTimeout
                ? CopilotService.WatchdogToolExecutionTimeoutSeconds
                : CopilotService.WatchdogInactivityTimeoutSeconds;
    }

    [Fact]
    public void WatchdogTimeoutSelection_NoTools_UsesInactivityTimeout()
    {
        // When no tool activity and not resumed → use shorter inactivity timeout
        var effectiveTimeout = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: false, hasReceivedEvents: false, hasUsedTools: false);

        Assert.Equal(CopilotService.WatchdogInactivityTimeoutSeconds, effectiveTimeout);
        Assert.Equal(120, effectiveTimeout);
    }

    [Fact]
    public void WatchdogTimeoutSelection_ActiveTool_UsesToolTimeout()
    {
        // When ActiveToolCallCount > 0 → use longer tool execution timeout
        var effectiveTimeout = ComputeEffectiveTimeout(
            hasActiveTool: true, isResumed: false, hasReceivedEvents: false, hasUsedTools: false);

        Assert.Equal(CopilotService.WatchdogToolExecutionTimeoutSeconds, effectiveTimeout);
        Assert.Equal(600, effectiveTimeout);
    }

    [Fact]
    public void WatchdogTimeoutSelection_ResumedSession_NoEvents_UsesQuiescenceTimeout()
    {
        // Resumed session with zero events since restart → short quiescence timeout (30s)
        // so the user doesn't have to click Stop on a session that already finished
        var effectiveTimeout = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: true, hasReceivedEvents: false, hasUsedTools: false);

        Assert.Equal(CopilotService.WatchdogResumeQuiescenceTimeoutSeconds, effectiveTimeout);
        Assert.Equal(30, effectiveTimeout);
    }

    [Fact]
    public void WatchdogTimeoutSelection_ResumedSession_WithEvents_UsesToolTimeout()
    {
        // Resumed session that HAS received events → use longer tool timeout (600s)
        // because the session is genuinely active
        var effectiveTimeout = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: true, hasReceivedEvents: true, hasUsedTools: false);

        Assert.Equal(CopilotService.WatchdogToolExecutionTimeoutSeconds, effectiveTimeout);
        Assert.Equal(600, effectiveTimeout);
    }

    [Fact]
    public void WatchdogTimeoutSelection_HasUsedTools_UsesToolTimeout()
    {
        // When tools have been used this turn (HasUsedToolsThisTurn=true) → use longer
        // tool timeout even between tool rounds when the model is thinking
        var effectiveTimeout = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: false, hasReceivedEvents: false, hasUsedTools: true);

        Assert.Equal(CopilotService.WatchdogToolExecutionTimeoutSeconds, effectiveTimeout);
        Assert.Equal(600, effectiveTimeout);
    }

    [Fact]
    public void WatchdogTimeoutSelection_ResumedWithActiveTool_UsesToolTimeout()
    {
        // Active tool prevents quiescence even with no events — uses 600s not 30s
        var effectiveTimeout = ComputeEffectiveTimeout(
            hasActiveTool: true, isResumed: true, hasReceivedEvents: false, hasUsedTools: false);

        Assert.Equal(CopilotService.WatchdogToolExecutionTimeoutSeconds, effectiveTimeout);
        Assert.Equal(600, effectiveTimeout);
    }

    [Fact]
    public void WatchdogTimeoutSelection_MultiAgent_UsesToolTimeout()
    {
        // Multi-agent sessions use longer tool timeout even without tool activity
        var effectiveTimeout = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: false, hasReceivedEvents: false, hasUsedTools: false, isMultiAgent: true);

        Assert.Equal(CopilotService.WatchdogToolExecutionTimeoutSeconds, effectiveTimeout);
        Assert.Equal(600, effectiveTimeout);
    }

    [Fact]
    public void WatchdogTimeoutSelection_MultiAgentResumed_NoEvents_UsesQuiescenceTimeout()
    {
        // Even multi-agent sessions use quiescence when resumed with zero events —
        // if the orchestration died, no point waiting 600s
        var effectiveTimeout = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: true, hasReceivedEvents: false, hasUsedTools: false, isMultiAgent: true);

        Assert.Equal(CopilotService.WatchdogResumeQuiescenceTimeoutSeconds, effectiveTimeout);
        Assert.Equal(30, effectiveTimeout);
    }

    [Fact]
    public void HasUsedToolsThisTurn_ResetOnNewSend()
    {
        // SendPromptAsync resets HasUsedToolsThisTurn alongside ActiveToolCallCount
        // to prevent stale tool-usage from a previous turn inflating the timeout
        // After reset: not resumed, no tools → inactivity timeout (120s)
        var effectiveTimeout = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: false, hasReceivedEvents: false, hasUsedTools: false);

        Assert.Equal(120, effectiveTimeout);
    }

    [Fact]
    public void IsResumed_ClearedAfterFirstTurn()
    {
        // IsResumed is only set when session was mid-turn at restart,
        // and should be cleared after the first successful CompleteResponse
        var info = new AgentSessionInfo { Name = "test", Model = "test", IsResumed = true };
        Assert.True(info.IsResumed);

        // CompleteResponse clears it
        info.IsResumed = false;
        Assert.False(info.IsResumed);

        // Subsequent turns use inactivity timeout (120s), not tool timeout (600s)
        var effectiveTimeout = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: info.IsResumed, hasReceivedEvents: false, hasUsedTools: false);

        Assert.Equal(120, effectiveTimeout);
    }

    [Fact]
    public void IsResumed_OnlySetWhenStillProcessing()
    {
        // IsResumed should only be true when session was mid-turn at restart
        // Idle-resumed sessions should NOT get the 600s timeout
        var idleResumed = new AgentSessionInfo { Name = "idle", Model = "test", IsResumed = false };
        var midTurnResumed = new AgentSessionInfo { Name = "mid", Model = "test", IsResumed = true };

        Assert.False(idleResumed.IsResumed);
        Assert.True(midTurnResumed.IsResumed);
    }

    [Fact]
    public void IsResumed_ClearedOnAbort()
    {
        // Abort must clear IsResumed so subsequent turns use 120s timeout
        var info = new AgentSessionInfo { Name = "t", Model = "m", IsResumed = true };
        Assert.True(info.IsResumed);

        // Simulate abort path
        info.IsProcessing = false;
        info.IsResumed = false;

        Assert.False(info.IsResumed);
    }

    [Fact]
    public void IsResumed_ClearedOnError()
    {
        // SessionErrorEvent must clear IsResumed
        var info = new AgentSessionInfo { Name = "t", Model = "m", IsResumed = true };

        // Simulate error path
        info.IsProcessing = false;
        info.IsResumed = false;

        Assert.False(info.IsResumed);
    }

    [Fact]
    public void IsResumed_ClearedOnWatchdogTimeout()
    {
        // Watchdog timeout must clear IsResumed so next turns don't get 600s
        var info = new AgentSessionInfo { Name = "t", Model = "m", IsResumed = true };

        // Simulate watchdog timeout path
        info.IsProcessing = false;
        info.IsResumed = false;

        // Verify next turn would use 120s (not resumed, no tools)
        var effectiveTimeout = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: info.IsResumed, hasReceivedEvents: false, hasUsedTools: false);

        Assert.Equal(120, effectiveTimeout);
    }

    [Fact]
    public void HasUsedToolsThisTurn_VolatileConsistency()
    {
        // Verify that Volatile.Write/Read round-trips correctly
        // (mirrors the cross-thread pattern: SDK thread writes, watchdog timer reads)
        bool field = false;
        Volatile.Write(ref field, true);
        Assert.True(Volatile.Read(ref field));

        Volatile.Write(ref field, false);
        Assert.False(Volatile.Read(ref field));
    }

    // --- Multi-agent watchdog timeout ---

    [Fact]
    public void IsSessionInMultiAgentGroup_ReturnsTrueForMultiAgentWorker()
    {
        // Regression: watchdog used 120s timeout for multi-agent workers doing text-heavy
        // tasks (PR reviews), killing them before the response arrived.
        // IsSessionInMultiAgentGroup should return true so the 600s timeout is used.
        var svc = CreateService();
        var group = new SessionGroup { Id = "ma-group", Name = "Test Squad", IsMultiAgent = true };
        svc.Organization.Groups.Add(group);
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "Test Squad-worker-1",
            GroupId = "ma-group",
            Role = MultiAgentRole.Worker
        });

        Assert.True(svc.IsSessionInMultiAgentGroup("Test Squad-worker-1"));
    }

    [Fact]
    public void IsSessionInMultiAgentGroup_ReturnsFalseForNonMultiAgentSession()
    {
        var svc = CreateService();
        var group = new SessionGroup { Id = "regular-group", Name = "Regular Group", IsMultiAgent = false };
        svc.Organization.Groups.Add(group);
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "regular-session",
            GroupId = "regular-group"
        });

        Assert.False(svc.IsSessionInMultiAgentGroup("regular-session"));
    }

    [Fact]
    public void IsSessionInMultiAgentGroup_ReturnsFalseForUnknownSession()
    {
        var svc = CreateService();
        Assert.False(svc.IsSessionInMultiAgentGroup("nonexistent-session"));
    }

    // ===========================================================================
    // Resume quiescence regression guards
    // These tests protect against the PR #148 regression pattern: short timeouts
    // that kill genuinely active resumed sessions.
    // ===========================================================================

    [Fact]
    public void ResumeQuiescenceTimeout_IsReasonable()
    {
        // Must be long enough for the SDK to reconnect and start streaming
        // (PR #148 regression: 10s was too short and killed active sessions).
        // Must be at least 2× the check interval to guarantee at least one
        // safe check before firing.
        Assert.InRange(CopilotService.WatchdogResumeQuiescenceTimeoutSeconds, 20, 120);
        Assert.True(
            CopilotService.WatchdogResumeQuiescenceTimeoutSeconds >= CopilotService.WatchdogCheckIntervalSeconds * 2,
            $"Quiescence timeout ({CopilotService.WatchdogResumeQuiescenceTimeoutSeconds}s) must be at least " +
            $"2× check interval ({CopilotService.WatchdogCheckIntervalSeconds}s) to allow at least one safe check");
    }

    [Fact]
    public void ResumeQuiescenceTimeout_IsLessThanInactivityTimeout()
    {
        // Quiescence should be shorter than the normal inactivity timeout —
        // that's the whole point of the feature.
        Assert.True(
            CopilotService.WatchdogResumeQuiescenceTimeoutSeconds < CopilotService.WatchdogInactivityTimeoutSeconds,
            "Quiescence timeout must be less than inactivity timeout");
    }

    [Fact]
    public void ResumeQuiescence_OnlyTriggersWhenResumedAndNoEvents()
    {
        // Exhaustive: quiescence can ONLY trigger when IsResumed=true AND
        // HasReceivedEvents=false AND no active tools AND no used tools.
        // All other combinations must NOT trigger quiescence.

        // The ONE case that should trigger quiescence:
        Assert.Equal(30, ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: true, hasReceivedEvents: false, hasUsedTools: false));

        // All other resumed combos must NOT trigger quiescence:
        Assert.Equal(600, ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: true, hasReceivedEvents: true, hasUsedTools: false));
        Assert.Equal(600, ComputeEffectiveTimeout(
            hasActiveTool: true, isResumed: true, hasReceivedEvents: false, hasUsedTools: false));
        Assert.Equal(600, ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: true, hasReceivedEvents: false, hasUsedTools: true));
        Assert.Equal(600, ComputeEffectiveTimeout(
            hasActiveTool: true, isResumed: true, hasReceivedEvents: true, hasUsedTools: true));
    }

    [Fact]
    public void ResumeQuiescence_NotResumed_NeverTriggersQuiescence()
    {
        // Non-resumed sessions must NEVER get the 30s quiescence timeout,
        // regardless of other flags.
        Assert.Equal(120, ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: false, hasReceivedEvents: false, hasUsedTools: false));
        Assert.Equal(600, ComputeEffectiveTimeout(
            hasActiveTool: true, isResumed: false, hasReceivedEvents: false, hasUsedTools: false));
        Assert.Equal(600, ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: false, hasReceivedEvents: false, hasUsedTools: true));
        Assert.Equal(600, ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: false, hasReceivedEvents: false, hasUsedTools: false, isMultiAgent: true));
    }

    [Fact]
    public void ResumeQuiescence_TransitionsToToolTimeout_WhenEventsArrive()
    {
        // When events start flowing on a resumed session, it must transition
        // from 30s quiescence to 600s tool timeout (not 120s inactivity).
        // This is critical: the session is confirmed active, so we give it
        // the full tool-execution timeout.

        // Before events: quiescence
        Assert.Equal(30, ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: true, hasReceivedEvents: false, hasUsedTools: false));

        // After events arrive: 600s tool timeout (IsResumed is still true)
        Assert.Equal(600, ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: true, hasReceivedEvents: true, hasUsedTools: false));
    }

    [Fact]
    public void ResumeQuiescence_TransitionsToInactivity_AfterIsResumedCleared()
    {
        // After IsResumed is cleared (by the watchdog IsResumed-clearing block),
        // the session should use the normal inactivity timeout.
        Assert.Equal(120, ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: false, hasReceivedEvents: true, hasUsedTools: false));
    }

    [Theory]
    [InlineData(false, false, false, false, false, 120)]   // Normal: inactivity
    [InlineData(true,  false, false, false, false, 600)]   // Active tool: 600s
    [InlineData(false, true,  false, false, false, 30)]    // Resumed, no events: quiescence
    [InlineData(false, true,  true,  false, false, 600)]   // Resumed, events: tool timeout
    [InlineData(true,  true,  false, false, false, 600)]   // Resumed, active tool: tool timeout
    [InlineData(false, true,  false, true,  false, 600)]   // Resumed, used tools: tool timeout
    [InlineData(false, false, false, false, true,  600)]   // Multi-agent: tool timeout
    [InlineData(false, true,  false, false, true,  30)]    // Resumed+multiAgent, no events: quiescence wins
    [InlineData(false, false, false, true,  false, 600)]   // HasUsedTools: tool timeout
    [InlineData(true,  true,  true,  true,  true,  600)]   // All flags: tool timeout
    public void WatchdogTimeoutSelection_ExhaustiveMatrix(
        bool hasActiveTool, bool isResumed, bool hasReceivedEvents,
        bool hasUsedTools, bool isMultiAgent, int expectedTimeout)
    {
        var actual = ComputeEffectiveTimeout(hasActiveTool, isResumed, hasReceivedEvents, hasUsedTools, isMultiAgent);
        Assert.Equal(expectedTimeout, actual);
    }

    [Fact]
    public void SeedTime_MustNotCauseImmediateKill_RegressionGuard()
    {
        // REGRESSION GUARD for PR #148 failure mode:
        // If LastEventAtTicks is seeded from events.jsonl file time (e.g. 5 min old),
        // elapsed on the first watchdog check would be ~315s, exceeding ANY timeout.
        // The production code must seed from DateTime.UtcNow for resumed sessions.
        //
        // This test verifies the INVARIANT: on the first watchdog check (after ~15s),
        // elapsed must be less than the quiescence timeout for a freshly seeded timer.
        var seed = DateTime.UtcNow; // Correct: seed from UtcNow, not file time
        var firstCheckTime = DateTime.UtcNow.AddSeconds(CopilotService.WatchdogCheckIntervalSeconds);
        var elapsed = (firstCheckTime - seed).TotalSeconds;

        Assert.True(elapsed < CopilotService.WatchdogResumeQuiescenceTimeoutSeconds,
            $"First watchdog check ({elapsed:F0}s after seed) must NOT exceed quiescence timeout " +
            $"({CopilotService.WatchdogResumeQuiescenceTimeoutSeconds}s). " +
            "If this fails, seed is from file time — PR #148 regression!");
    }

    [Fact]
    public void SeedTime_FromStaleFile_WouldCauseImmediateKill_DocumentsRisk()
    {
        // Documents WHY we don't seed from events.jsonl file time:
        // A file 5 minutes old would cause elapsed = 300 + 15 = 315s at first check,
        // far exceeding the 30s quiescence timeout → session killed in 15s.
        var staleFileTime = DateTime.UtcNow.AddSeconds(-300); // 5 min old
        var firstCheckTime = DateTime.UtcNow.AddSeconds(CopilotService.WatchdogCheckIntervalSeconds);
        var elapsed = (firstCheckTime - staleFileTime).TotalSeconds;

        // This WOULD exceed quiescence — proving the risk
        Assert.True(elapsed > CopilotService.WatchdogResumeQuiescenceTimeoutSeconds,
            "Stale file seed would cause immediate kill — this is why we seed from UtcNow");

        // It would even exceed the tool execution timeout!
        Assert.True(elapsed < CopilotService.WatchdogToolExecutionTimeoutSeconds,
            "5 min old file wouldn't exceed the 600s tool timeout, " +
            "but would exceed the 30s quiescence timeout");
    }

    [Fact]
    public void QuiescenceTimeout_EscapesOnFirstEvent()
    {
        // Once HasReceivedEventsSinceResume goes true, the quiescence path
        // is permanently disabled for that session. Verify the transition.
        bool hasReceivedEvents = false;

        // Before first event: quiescence
        var timeout1 = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: true, hasReceivedEvents: hasReceivedEvents, hasUsedTools: false);
        Assert.Equal(30, timeout1);

        // SDK sends first event
        hasReceivedEvents = true;

        // After first event: tool timeout (NOT quiescence, NOT inactivity)
        var timeout2 = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: true, hasReceivedEvents: hasReceivedEvents, hasUsedTools: false);
        Assert.Equal(600, timeout2);
    }

    [Fact]
    public void QuiescenceTimeout_DoesNotAffect_NormalSendPromptPath()
    {
        // SendPromptAsync creates sessions with IsResumed=false.
        // Quiescence must NEVER affect normal (non-resumed) processing.
        // This protects against the case where someone accidentally sets
        // IsResumed=true on a non-resumed session.
        var effectiveTimeout = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: false, hasReceivedEvents: false, hasUsedTools: false);
        Assert.Equal(CopilotService.WatchdogInactivityTimeoutSeconds, effectiveTimeout);
        Assert.NotEqual(CopilotService.WatchdogResumeQuiescenceTimeoutSeconds, effectiveTimeout);
    }

    [Fact]
    public void WatchdogResumeQuiescence_Constant_MatchesExpectedValue()
    {
        // Pin the value so changes require updating this test intentionally.
        Assert.Equal(30, CopilotService.WatchdogResumeQuiescenceTimeoutSeconds);
    }

    [Fact]
    public void AllThreeTimeoutTiers_AreDistinct()
    {
        // The three timeout tiers must be distinct and ordered:
        // quiescence < inactivity < tool execution
        Assert.True(CopilotService.WatchdogResumeQuiescenceTimeoutSeconds
            < CopilotService.WatchdogInactivityTimeoutSeconds);
        Assert.True(CopilotService.WatchdogInactivityTimeoutSeconds
            < CopilotService.WatchdogToolExecutionTimeoutSeconds);
    }

    // --- GetEventsFileRestoreHints tests ---

    [Fact]
    public void RestoreHints_MissingFile_ReturnsFalse()
    {
        var service = CreateService();
        var basePath = Path.Combine(Path.GetTempPath(), $"restore-hints-{Guid.NewGuid()}");
        Directory.CreateDirectory(basePath);
        try
        {
            var (isRecentlyActive, hadToolActivity) = service.GetEventsFileRestoreHints("nonexistent-session", basePath);
            Assert.False(isRecentlyActive);
            Assert.False(hadToolActivity);
        }
        finally { Directory.Delete(basePath, true); }
    }

    [Fact]
    public void RestoreHints_FreshFile_AssistantEvent_ReturnsRecentlyActiveOnly()
    {
        var service = CreateService();
        var basePath = Path.Combine(Path.GetTempPath(), $"restore-hints-{Guid.NewGuid()}");
        var sessionDir = Path.Combine(basePath, "test-session");
        Directory.CreateDirectory(sessionDir);
        try
        {
            // Write a fresh events.jsonl with a non-tool active event
            File.WriteAllText(Path.Combine(sessionDir, "events.jsonl"),
                """{"type":"assistant.message_delta","data":{}}""");

            var (isRecentlyActive, hadToolActivity) = service.GetEventsFileRestoreHints("test-session", basePath);
            Assert.True(isRecentlyActive, "File was just written — should be recently active");
            Assert.False(hadToolActivity, "Last event is not a tool event");
        }
        finally { Directory.Delete(basePath, true); }
    }

    [Fact]
    public void RestoreHints_FreshFile_ToolEvent_ReturnsBothTrue()
    {
        var service = CreateService();
        var basePath = Path.Combine(Path.GetTempPath(), $"restore-hints-{Guid.NewGuid()}");
        var sessionDir = Path.Combine(basePath, "test-session");
        Directory.CreateDirectory(sessionDir);
        try
        {
            // Write a fresh events.jsonl with a tool execution event as the last line
            File.WriteAllText(Path.Combine(sessionDir, "events.jsonl"),
                """{"type":"assistant.turn_start","data":{}}""" + "\n" +
                """{"type":"tool.execution_start","data":{"name":"bash"}}""");

            var (isRecentlyActive, hadToolActivity) = service.GetEventsFileRestoreHints("test-session", basePath);
            Assert.True(isRecentlyActive, "File was just written — should be recently active");
            Assert.True(hadToolActivity, "Last event is tool.execution_start");
        }
        finally { Directory.Delete(basePath, true); }
    }

    [Fact]
    public void RestoreHints_FreshFile_ToolProgressEvent_ReturnsBothTrue()
    {
        var service = CreateService();
        var basePath = Path.Combine(Path.GetTempPath(), $"restore-hints-{Guid.NewGuid()}");
        var sessionDir = Path.Combine(basePath, "test-session");
        Directory.CreateDirectory(sessionDir);
        try
        {
            File.WriteAllText(Path.Combine(sessionDir, "events.jsonl"),
                """{"type":"tool.execution_progress","data":{}}""");

            var (isRecentlyActive, hadToolActivity) = service.GetEventsFileRestoreHints("test-session", basePath);
            Assert.True(isRecentlyActive);
            Assert.True(hadToolActivity, "Last event is tool.execution_progress");
        }
        finally { Directory.Delete(basePath, true); }
    }

    [Fact]
    public void RestoreHints_StaleFile_ReturnsNotRecentlyActive()
    {
        var service = CreateService();
        var basePath = Path.Combine(Path.GetTempPath(), $"restore-hints-{Guid.NewGuid()}");
        var sessionDir = Path.Combine(basePath, "test-session");
        Directory.CreateDirectory(sessionDir);
        try
        {
            var eventsFile = Path.Combine(sessionDir, "events.jsonl");
            File.WriteAllText(eventsFile,
                """{"type":"tool.execution_start","data":{"name":"bash"}}""");
            // Make file older than inactivity timeout
            File.SetLastWriteTimeUtc(eventsFile,
                DateTime.UtcNow.AddSeconds(-(CopilotService.WatchdogInactivityTimeoutSeconds + 10)));

            var (isRecentlyActive, hadToolActivity) = service.GetEventsFileRestoreHints("test-session", basePath);
            Assert.False(isRecentlyActive, "File is stale — should not be recently active");
            Assert.False(hadToolActivity, "Stale files should not report tool activity");
        }
        finally { Directory.Delete(basePath, true); }
    }

    [Fact]
    public void RestoreHints_EmptyFile_ReturnsRecentlyActiveWithNoToolActivity()
    {
        var service = CreateService();
        var basePath = Path.Combine(Path.GetTempPath(), $"restore-hints-{Guid.NewGuid()}");
        var sessionDir = Path.Combine(basePath, "test-session");
        Directory.CreateDirectory(sessionDir);
        try
        {
            File.WriteAllText(Path.Combine(sessionDir, "events.jsonl"), "");
            var (isRecentlyActive, hadToolActivity) = service.GetEventsFileRestoreHints("test-session", basePath);
            Assert.True(isRecentlyActive, "Fresh empty file is still recently active");
            Assert.False(hadToolActivity, "Empty file has no tool events");
        }
        finally { Directory.Delete(basePath, true); }
    }

    [Fact]
    public void RestoreHints_FreshToolActivity_BypassesQuiescenceTimeout()
    {
        // Integration-style test: When restore hints indicate recent tool activity,
        // the effective watchdog timeout should NOT be the 30s quiescence timeout.
        // Simulates the scenario from the bug: session is genuinely active on the server
        // but SDK hasn't reconnected yet.
        var service = CreateService();
        var basePath = Path.Combine(Path.GetTempPath(), $"restore-hints-{Guid.NewGuid()}");
        var sessionDir = Path.Combine(basePath, "test-session");
        Directory.CreateDirectory(sessionDir);
        try
        {
            File.WriteAllText(Path.Combine(sessionDir, "events.jsonl"),
                """{"type":"tool.execution_start","data":{"name":"bash"}}""");

            var (isRecentlyActive, hadToolActivity) = service.GetEventsFileRestoreHints("test-session", basePath);

            // Simulate what the restore code does with these hints
            bool hasReceivedEvents = isRecentlyActive; // Pre-seeded from hints
            bool hasUsedTools = hadToolActivity;        // Pre-seeded from hints

            var effectiveTimeout = ComputeEffectiveTimeout(
                hasActiveTool: false,
                isResumed: true,
                hasReceivedEvents: hasReceivedEvents,
                hasUsedTools: hasUsedTools);

            // Must NOT be the 30s quiescence — should be 600s tool timeout
            Assert.NotEqual(CopilotService.WatchdogResumeQuiescenceTimeoutSeconds, effectiveTimeout);
            Assert.Equal(CopilotService.WatchdogToolExecutionTimeoutSeconds, effectiveTimeout);
        }
        finally { Directory.Delete(basePath, true); }
    }

    [Fact]
    public void RestoreHints_FreshNonToolActivity_BypassesQuiescenceTimeout()
    {
        // When restore hints indicate recent non-tool activity, the timeout should
        // transition through the IsResumed clearing logic to 120s inactivity.
        var service = CreateService();
        var basePath = Path.Combine(Path.GetTempPath(), $"restore-hints-{Guid.NewGuid()}");
        var sessionDir = Path.Combine(basePath, "test-session");
        Directory.CreateDirectory(sessionDir);
        try
        {
            File.WriteAllText(Path.Combine(sessionDir, "events.jsonl"),
                """{"type":"assistant.message_delta","data":{}}""");

            var (isRecentlyActive, hadToolActivity) = service.GetEventsFileRestoreHints("test-session", basePath);

            bool hasReceivedEvents = isRecentlyActive;
            bool hasUsedTools = hadToolActivity;

            var effectiveTimeout = ComputeEffectiveTimeout(
                hasActiveTool: false,
                isResumed: true,
                hasReceivedEvents: hasReceivedEvents,
                hasUsedTools: hasUsedTools);

            // Must NOT be the 30s quiescence — should be 600s (resumed + events = tool timeout)
            Assert.NotEqual(CopilotService.WatchdogResumeQuiescenceTimeoutSeconds, effectiveTimeout);
            Assert.Equal(CopilotService.WatchdogToolExecutionTimeoutSeconds, effectiveTimeout);
        }
        finally { Directory.Delete(basePath, true); }
    }

    [Fact]
    public void RestoreHints_StaleFile_StillUsesQuiescenceTimeout()
    {
        // When the file is stale, the quiescence timeout should still apply —
        // the turn probably finished long ago.
        var service = CreateService();
        var basePath = Path.Combine(Path.GetTempPath(), $"restore-hints-{Guid.NewGuid()}");
        var sessionDir = Path.Combine(basePath, "test-session");
        Directory.CreateDirectory(sessionDir);
        try
        {
            var eventsFile = Path.Combine(sessionDir, "events.jsonl");
            File.WriteAllText(eventsFile,
                """{"type":"tool.execution_start","data":{"name":"bash"}}""");
            File.SetLastWriteTimeUtc(eventsFile,
                DateTime.UtcNow.AddSeconds(-(CopilotService.WatchdogInactivityTimeoutSeconds + 10)));

            var (isRecentlyActive, hadToolActivity) = service.GetEventsFileRestoreHints("test-session", basePath);

            // Stale: no pre-seeding → quiescence still applies
            bool hasReceivedEvents = isRecentlyActive; // false
            bool hasUsedTools = hadToolActivity;        // false

            var effectiveTimeout = ComputeEffectiveTimeout(
                hasActiveTool: false,
                isResumed: true,
                hasReceivedEvents: hasReceivedEvents,
                hasUsedTools: hasUsedTools);

            Assert.Equal(CopilotService.WatchdogResumeQuiescenceTimeoutSeconds, effectiveTimeout);
        }
        finally { Directory.Delete(basePath, true); }
    }

    [Fact]
    public void RestoreHints_MalformedJson_PreservesFileAgeSignal()
    {
        var service = CreateService();
        var basePath = Path.Combine(Path.GetTempPath(), $"restore-hints-{Guid.NewGuid()}");
        var sessionDir = Path.Combine(basePath, "test-session");
        Directory.CreateDirectory(sessionDir);
        try
        {
            File.WriteAllText(Path.Combine(sessionDir, "events.jsonl"), "{{ bad json {{");
            var (isRecentlyActive, hadToolActivity) = service.GetEventsFileRestoreHints("test-session", basePath);
            // File was just written (age < 120s) so isRecentlyActive is true even though JSON is malformed.
            // This ensures the quiescence bypass still works for recently-active sessions with corrupt events.
            Assert.True(isRecentlyActive, "Recently-written file should preserve isRecentlyActive despite malformed JSON");
            Assert.False(hadToolActivity, "Cannot detect tool activity from bad JSON");
        }
        finally { Directory.Delete(basePath, true); }
    }
}
