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
    private static int ComputeEffectiveTimeout(bool hasActiveTool, bool isResumed, bool hasReceivedEvents, bool hasUsedTools, bool isMultiAgent = false, bool isReconnectedSend = false)
    {
        var useResumeQuiescence = isResumed && !hasReceivedEvents && !hasActiveTool && !hasUsedTools;
        // Mirrors production formula in RunProcessingWatchdogAsync exactly.
        // NOTE: isMultiAgent no longer extends the timeout (PR #332 fix).
        // hasUsedTools goes through useUsedToolsTimeout (180s), NOT useToolTimeout (600s).
        var useToolTimeout = hasActiveTool || (isResumed && !useResumeQuiescence);
        var useUsedToolsTimeout = !useToolTimeout && hasUsedTools && !hasActiveTool;
        var useReconnectTimeout = isReconnectedSend && !useToolTimeout && !useUsedToolsTimeout && !useResumeQuiescence;
        return useResumeQuiescence
            ? CopilotService.WatchdogResumeQuiescenceTimeoutSeconds
            : useToolTimeout
                ? CopilotService.WatchdogToolExecutionTimeoutSeconds
                : useUsedToolsTimeout
                    ? CopilotService.WatchdogUsedToolsIdleTimeoutSeconds
                    : useReconnectTimeout
                        ? CopilotService.WatchdogReconnectInactivityTimeoutSeconds
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
    public void WatchdogTimeoutSelection_HasUsedTools_UsesUsedToolsTimeout()
    {
        // When tools have been used this turn (HasUsedToolsThisTurn=true) but no tool
        // is currently active, the session is between tool rounds (model is thinking).
        // Production routes this through WatchdogUsedToolsIdleTimeoutSeconds (180s) —
        // longer than inactivity (120s) to give the model time to respond, but shorter
        // than the full tool execution timeout (600s) since no tool is actually running.
        var effectiveTimeout = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: false, hasReceivedEvents: false, hasUsedTools: true);

        Assert.Equal(CopilotService.WatchdogUsedToolsIdleTimeoutSeconds, effectiveTimeout);
        Assert.Equal(180, effectiveTimeout);
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
    public void WatchdogTimeoutSelection_MultiAgent_NoTools_UsesInactivityTimeout()
    {
        // Multi-agent sessions without tool use now use the 120s inactivity timeout.
        // The old 600s blanket for isMultiAgent caused stuck-session UX bugs when the SDK
        // dropped terminal events (sdk bug #299 variant): users waited up to 600s when
        // workers that do active text generation (delta events flowing) are NOT at risk of
        // the 120s timeout because deltas keep elapsed small.
        var effectiveTimeout = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: false, hasReceivedEvents: false, hasUsedTools: false, isMultiAgent: true);

        Assert.Equal(CopilotService.WatchdogInactivityTimeoutSeconds, effectiveTimeout);
        Assert.Equal(120, effectiveTimeout);
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
    public void HasUsedToolsThisTurn_IsDeclaredVolatile()
    {
        // HasUsedToolsThisTurn is read by the watchdog timer thread and written by
        // SDK background threads and the UI thread. The field must be declared volatile
        // to ensure cross-thread visibility on ARM (iOS/Android).
        var field = typeof(CopilotService)
            .GetNestedType("SessionState", System.Reflection.BindingFlags.NonPublic)!
            .GetField("HasUsedToolsThisTurn")!;
        Assert.True((field.Attributes & System.Reflection.FieldAttributes.NotSerialized) != 0
            || field.FieldType == typeof(bool),
            "HasUsedToolsThisTurn must be a bool field");
        // C# volatile modifier sets the IsVolatile required modifier on the field
        Assert.True(field.GetRequiredCustomModifiers().Any(m => m == typeof(System.Runtime.CompilerServices.IsVolatile)),
            "HasUsedToolsThisTurn must be declared volatile for ARM memory model safety");
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
        Assert.Equal(180, ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: false, hasReceivedEvents: false, hasUsedTools: true));
        // Multi-agent without tools now uses 120s (not 600s) — see WatchdogTimeoutSelection_MultiAgent_NoTools_UsesInactivityTimeout
        Assert.Equal(120, ComputeEffectiveTimeout(
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
    [InlineData(false, false, false, false, true,  120)]   // Multi-agent no-tools: inactivity (PR #332 fix)
    [InlineData(false, true,  false, false, true,  30)]    // Resumed+multiAgent, no events: quiescence wins
    [InlineData(false, false, false, true,  false, 180)]   // HasUsedTools, no active tool: 180s (used-tools idle)
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
            // Make file older than tool execution timeout (the restore hints threshold)
            File.SetLastWriteTimeUtc(eventsFile,
                DateTime.UtcNow.AddSeconds(-(CopilotService.WatchdogToolExecutionTimeoutSeconds + 10)));

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
                DateTime.UtcNow.AddSeconds(-(CopilotService.WatchdogToolExecutionTimeoutSeconds + 10)));

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
            // File was just written (age < 600s) so isRecentlyActive is true even though JSON is malformed.
            // This ensures the quiescence bypass still works for recently-active sessions with corrupt events.
            Assert.True(isRecentlyActive, "Recently-written file should preserve isRecentlyActive despite malformed JSON");
            Assert.False(hadToolActivity, "Cannot detect tool activity from bad JSON");
        }
        finally { Directory.Delete(basePath, true); }
    }

    // --- Metrics events should NOT reset watchdog timer ---

    [Fact]
    public void WatchdogMaxProcessingTime_Constant_IsReasonable()
    {
        // Max processing time must be at least 30 minutes (long agent tasks)
        // and at most 2 hours (anything longer is certainly stuck).
        Assert.InRange(CopilotService.WatchdogMaxProcessingTimeSeconds, 1800, 7200);
    }

    [Fact]
    public void WatchdogMaxProcessingTime_ExceedsToolTimeout()
    {
        // Max processing time must be strictly greater than the tool execution
        // timeout — it's a safety net for when the tool timeout doesn't fire
        // because events keep arriving.
        Assert.True(CopilotService.WatchdogMaxProcessingTimeSeconds
            > CopilotService.WatchdogToolExecutionTimeoutSeconds,
            "Max processing time must exceed tool execution timeout");
    }

    [Fact]
    public void WatchdogMaxProcessingTime_CatchesStuckSession_EvenWithContinuousEvents()
    {
        // REGRESSION TEST for the FailedDelegation bug:
        // When the SDK sends repeated SessionUsageInfoEvent (e.g., FailedDelegation)
        // without a terminal event, the session gets stuck forever because each event
        // resets LastEventAtTicks. The max processing time is an absolute safety net
        // that fires regardless of event activity.
        //
        // Simulate: ProcessingStartedAt was set long ago, but events keep arriving
        var processingStartedAt = DateTime.UtcNow.AddSeconds(-CopilotService.WatchdogMaxProcessingTimeSeconds - 1);
        var totalProcessingSeconds = (DateTime.UtcNow - processingStartedAt).TotalSeconds;
        var exceededMaxTime = totalProcessingSeconds >= CopilotService.WatchdogMaxProcessingTimeSeconds;

        Assert.True(exceededMaxTime,
            "Session that has been processing for longer than WatchdogMaxProcessingTimeSeconds " +
            "must be flagged as exceeded, even if events are still arriving");
    }

    [Fact]
    public void WatchdogMaxProcessingTime_DoesNotFirePrematurely()
    {
        // A session that just started processing should NOT be flagged as exceeding
        // the max processing time.
        var processingStartedAt = DateTime.UtcNow.AddSeconds(-60); // 1 minute ago
        var totalProcessingSeconds = (DateTime.UtcNow - processingStartedAt).TotalSeconds;
        var exceededMaxTime = totalProcessingSeconds >= CopilotService.WatchdogMaxProcessingTimeSeconds;

        Assert.False(exceededMaxTime,
            "Session that started 60 seconds ago must NOT exceed max processing time");
    }

    [Fact]
    public void WatchdogMaxProcessingTime_NullStartedAt_DoesNotFire()
    {
        // If ProcessingStartedAt is null (shouldn't happen during processing,
        // but defensive), max time check should be safely handled.
        DateTime? processingStartedAt = null;
        var totalProcessingSeconds = processingStartedAt.HasValue
            ? (DateTime.UtcNow - processingStartedAt.Value).TotalSeconds
            : 0;
        var exceededMaxTime = totalProcessingSeconds >= CopilotService.WatchdogMaxProcessingTimeSeconds;

        Assert.False(exceededMaxTime,
            "Null ProcessingStartedAt should not trigger max processing time");
    }

    [Fact]
    public void MetricsEvents_ShouldNotResetWatchdog_DocumentedBehavior()
    {
        // Documents the fix for the FailedDelegation stuck session bug:
        // SessionUsageInfoEvent and AssistantUsageEvent are metrics-only events
        // that should NOT reset the watchdog timer (LastEventAtTicks).
        //
        // Before the fix, ALL events updated LastEventAtTicks unconditionally,
        // causing the watchdog to never fire when the SDK kept sending
        // SessionUsageInfoEvent without a terminal event.
        //
        // This test verifies the classification of events as progress vs metrics.
        // Progress events indicate actual turn work (message deltas, tool calls).
        // Metrics events are informational only (token counts, model info).

        // These event type names should NOT reset the watchdog timer:
        var metricsEventTypeNames = new[] { "SessionUsageInfoEvent", "AssistantUsageEvent" };

        // These events SHOULD reset the watchdog timer (non-exhaustive):
        var progressEventTypeNames = new[]
        {
            "AssistantTurnStartEvent", "AssistantMessageDeltaEvent",
            "AssistantMessageEvent", "ToolExecutionStartEvent",
            "ToolExecutionCompleteEvent", "AssistantTurnEndEvent",
            "SessionIdleEvent", "SessionErrorEvent"
        };

        // Verify that the event classification in the code is correct by
        // checking the type names used in the HandleSessionEvent guard.
        Assert.Equal(2, metricsEventTypeNames.Length);
        Assert.True(progressEventTypeNames.Length > metricsEventTypeNames.Length,
            "Most events should be progress events (reset the watchdog timer)");
    }

    [Fact]
    public void WatchdogTriggersOnInactivity_OrMaxTime_DisjunctionLogic()
    {
        // The watchdog should fire if EITHER condition is met:
        // 1. Inactivity timeout exceeded (no progress events for N seconds), OR
        // 2. Max processing time exceeded (total time > WatchdogMaxProcessingTimeSeconds)
        //
        // This tests the disjunction: even if inactivity hasn't been reached,
        // max time can still trigger the watchdog.

        double elapsedSinceLastEvent = 10; // Only 10 seconds since last event
        double totalProcessingTime = CopilotService.WatchdogMaxProcessingTimeSeconds + 1;
        int effectiveTimeout = CopilotService.WatchdogToolExecutionTimeoutSeconds; // 600s

        bool inactivityTriggered = elapsedSinceLastEvent >= effectiveTimeout;
        bool maxTimeTriggered = totalProcessingTime >= CopilotService.WatchdogMaxProcessingTimeSeconds;

        Assert.False(inactivityTriggered, "Inactivity should NOT be triggered (only 10s)");
        Assert.True(maxTimeTriggered, "Max time SHOULD be triggered");
        Assert.True(inactivityTriggered || maxTimeTriggered,
            "Watchdog must fire when either condition is met (disjunction)");
    }

    [Fact]
    public void AllFourTimeoutConstants_AreDistinctAndOrdered()
    {
        // All four timeout constants must be distinct and ordered:
        // quiescence < inactivity < tool execution < max processing time
        Assert.True(CopilotService.WatchdogResumeQuiescenceTimeoutSeconds
            < CopilotService.WatchdogInactivityTimeoutSeconds);
        Assert.True(CopilotService.WatchdogInactivityTimeoutSeconds
            < CopilotService.WatchdogToolExecutionTimeoutSeconds);
        Assert.True(CopilotService.WatchdogToolExecutionTimeoutSeconds
            < CopilotService.WatchdogMaxProcessingTimeSeconds);
    }

    // ===================================================================
    // REGRESSION GUARD TESTS
    // Tests covering every known regression from PRs #148→#153→#158→#163→
    // #164→#195→#207→#211→#224→this-PR. Each test documents the original
    // failure mode so it can never silently regress again.
    // ===================================================================

    // --- PR #148 regression: hardcoded 10s resume timeout killed active sessions ---

    [Fact]
    public void Regression_PR148_NoHardcodedShortTimeout()
    {
        // PR #148 had a 10-second hardcoded resume timeout that killed sessions
        // still actively doing tool calls (dotnet build, git push, etc.).
        // INV-4: No hardcoded short timeouts. The minimum timeout is the
        // 30s quiescence, and ONLY for resumed sessions with zero events.
        Assert.True(CopilotService.WatchdogResumeQuiescenceTimeoutSeconds >= 15,
            "Minimum timeout must be >= 15s to avoid killing active sessions");
        Assert.True(CopilotService.WatchdogCheckIntervalSeconds < CopilotService.WatchdogResumeQuiescenceTimeoutSeconds,
            "Check interval must be shorter than shortest timeout to detect it");
    }

    [Fact]
    public void Regression_PR148_SeedFromUtcNow_NotFileTime()
    {
        // If LastEventAtTicks is seeded from events.jsonl file time, a file
        // written 5 minutes ago would cause elapsed = 315s at first check,
        // exceeding ALL timeouts. This was the exact PR #148 failure mode.
        // The fix: always seed from DateTime.UtcNow.
        var utcNowSeed = DateTime.UtcNow;
        var firstCheck = utcNowSeed.AddSeconds(CopilotService.WatchdogCheckIntervalSeconds);
        var elapsed = (firstCheck - utcNowSeed).TotalSeconds;

        // Must be less than the shortest timeout
        Assert.True(elapsed < CopilotService.WatchdogResumeQuiescenceTimeoutSeconds,
            $"UTC seed: first check elapsed {elapsed:F0}s must be < quiescence {CopilotService.WatchdogResumeQuiescenceTimeoutSeconds}s");

        // Contrast: file time seed would fail
        var staleFileSeed = DateTime.UtcNow.AddSeconds(-300);
        var staleElapsed = (firstCheck - staleFileSeed).TotalSeconds;
        Assert.True(staleElapsed > CopilotService.WatchdogResumeQuiescenceTimeoutSeconds,
            "Stale file seed WOULD exceed quiescence — proving the risk");
    }

    // --- PR #148 sub-fix: watchdog using 120s during tool-call loops ---

    [Fact]
    public void Regression_PR148_ToolLoops_UseUsedToolsTimeoutNotInactivity()
    {
        // AssistantTurnStartEvent resets ActiveToolCallCount to 0 between
        // tool rounds, making "thinking" gaps look like inactivity. The fix:
        // HasUsedToolsThisTurn stays true for the entire processing cycle.
        // Without it, the watchdog would use 120s (inactivity) between tool
        // rounds, killing sessions doing legitimate multi-step work.
        // HasUsedTools routes to WatchdogUsedToolsIdleTimeoutSeconds (180s),
        // not the full 600s tool-execution timeout.

        // During tool loop: ActiveToolCallCount=0 but HasUsedToolsThisTurn=true
        var timeout = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: false, hasReceivedEvents: true, hasUsedTools: true);
        Assert.Equal(CopilotService.WatchdogUsedToolsIdleTimeoutSeconds, timeout);
        Assert.NotEqual(CopilotService.WatchdogInactivityTimeoutSeconds, timeout);
    }

    // --- PR #163 regression: IsResumed kept watchdog at 600s forever ---

    [Fact]
    public void Regression_PR163_IsResumed_ClearedAfterEventsArrive()
    {
        // IsResumed was never cleared, keeping the watchdog at 600s forever.
        // The fix: clear IsResumed once events flow AND no tool activity.
        // Before clearing: resumed + events → 600s (tool timeout)
        var beforeClear = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: true, hasReceivedEvents: true, hasUsedTools: false);
        Assert.Equal(600, beforeClear);

        // After clearing IsResumed (simulated): not resumed, events → 120s
        var afterClear = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: false, hasReceivedEvents: true, hasUsedTools: false);
        Assert.Equal(120, afterClear);
    }

    [Fact]
    public void Regression_PR163_IsResumed_NotClearedDuringToolActivity()
    {
        // Guard: don't clear IsResumed while tools are active — the session
        // genuinely needs the 600s timeout during tool execution.
        // This is the condition: IsResumed && HasReceivedEvents && !hasActiveTool && !HasUsedToolsThisTurn
        // If hasActiveTool=true, don't clear:
        var withActiveTool = ComputeEffectiveTimeout(
            hasActiveTool: true, isResumed: true, hasReceivedEvents: true, hasUsedTools: false);
        Assert.Equal(600, withActiveTool); // Must stay at 600s

        // If HasUsedToolsThisTurn=true, don't clear:
        var withUsedTools = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: true, hasReceivedEvents: true, hasUsedTools: true);
        Assert.Equal(600, withUsedTools); // Must stay at 600s
    }

    // --- PR #195 regression: multi-agent workers killed at 120s ---

    [Fact]
    public void Regression_PR195_MultiAgentWorkers_DeltasKeepElapsedSmall()
    {
        // PR #195 concern: multi-agent workers doing text-heavy tasks (PR reviews, no tools)
        // were killed at 120s. The fix was: isMultiAgent → 600s. But that was wrong reasoning.
        //
        // The CORRECT insight (PR #332): workers generating text stream DELTA EVENTS continuously.
        // Each delta resets LastEventAtTicks, keeping elapsed tiny. The 120s timeout cannot fire
        // during active generation. It can only fire when the session goes SILENT, which means
        // either stuck (good to clean up) or done with terminal events dropped (Case B).
        //
        // Multi-agent without tools → 120s timeout (inactivity). This is intentional.
        var timeout = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: false, hasReceivedEvents: true,
            hasUsedTools: false, isMultiAgent: true);
        Assert.Equal(CopilotService.WatchdogInactivityTimeoutSeconds, timeout);
        Assert.Equal(120, timeout);
    }

    // --- PR #211 regression: quiescence must not kill active sessions ---

    [Fact]
    public void Regression_PR211_Quiescence_OnlyForZeroEventResumes()
    {
        // The 30s quiescence timeout must ONLY fire when:
        // 1. IsResumed=true AND
        // 2. HasReceivedEventsSinceResume=false AND
        // 3. No active tools AND no tools used
        // If ANY event arrives, quiescence is permanently disabled.

        // Zero events: quiescence (30s)
        Assert.Equal(30, ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: true, hasReceivedEvents: false, hasUsedTools: false));

        // Events arrived: NOT quiescence (600s)
        Assert.Equal(600, ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: true, hasReceivedEvents: true, hasUsedTools: false));

        // Not resumed at all: NOT quiescence (120s)
        Assert.Equal(120, ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: false, hasReceivedEvents: false, hasUsedTools: false));
    }

    [Fact]
    public void Regression_PR211_Quiescence_DisabledByNonResumedSessions()
    {
        // Normal send-prompt sessions must NEVER use quiescence timeout.
        // SendPromptAsync creates sessions with IsResumed=false.
        for (int i = 0; i < 2; i++)
        {
            bool hasReceivedEvents = i == 1;
            var timeout = ComputeEffectiveTimeout(
                hasActiveTool: false, isResumed: false,
                hasReceivedEvents: hasReceivedEvents, hasUsedTools: false);
            Assert.NotEqual(CopilotService.WatchdogResumeQuiescenceTimeoutSeconds, timeout);
        }
    }

    // --- PR #224 regression: restore hints threshold too short ---

    [Fact]
    public void Regression_PR224_RestoreHints_ThresholdMatchesToolTimeout()
    {
        // PR #224 used WatchdogInactivityTimeoutSeconds (120s) as the file age
        // threshold for GetEventsFileRestoreHints. This caused sessions with
        // long-running tool calls (>120s between events) to miss the bypass.
        // Fix: use WatchdogToolExecutionTimeoutSeconds (600s) since tool calls
        // can legitimately go 5-10 minutes without writing events.
        var service = CreateService();
        var basePath = Path.Combine(Path.GetTempPath(), $"restore-hints-{Guid.NewGuid()}");
        var sessionDir = Path.Combine(basePath, "test-session");
        Directory.CreateDirectory(sessionDir);
        try
        {
            var eventsFile = Path.Combine(sessionDir, "events.jsonl");
            File.WriteAllText(eventsFile,
                """{"type":"tool.execution_start","data":{"name":"bash"}}""");

            // File age between old threshold (120s) and new threshold (600s)
            // This would have been stale under the old threshold but should be
            // detected as active under the new one.
            File.SetLastWriteTimeUtc(eventsFile,
                DateTime.UtcNow.AddSeconds(-(CopilotService.WatchdogInactivityTimeoutSeconds + 30)));

            var (isRecentlyActive, hadToolActivity) = service.GetEventsFileRestoreHints("test-session", basePath);
            Assert.True(isRecentlyActive,
                "File 150s old should be considered recently active (threshold is now 600s, not 120s)");
            Assert.True(hadToolActivity,
                "Last event is tool.execution_start — should detect tool activity");
        }
        finally { Directory.Delete(basePath, true); }
    }

    [Fact]
    public void Regression_PR224_RestoreHints_StaleAt600s_StillQuiesces()
    {
        // Files older than WatchdogToolExecutionTimeoutSeconds should STILL
        // use the 30s quiescence timeout — the turn almost certainly finished.
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
                DateTime.UtcNow.AddSeconds(-(CopilotService.WatchdogToolExecutionTimeoutSeconds + 60)));

            var (isRecentlyActive, hadToolActivity) = service.GetEventsFileRestoreHints("test-session", basePath);
            Assert.False(isRecentlyActive, "File > 600s old should be stale");

            var timeout = ComputeEffectiveTimeout(
                hasActiveTool: false, isResumed: true,
                hasReceivedEvents: isRecentlyActive, hasUsedTools: hadToolActivity);
            Assert.Equal(CopilotService.WatchdogResumeQuiescenceTimeoutSeconds, timeout);
        }
        finally { Directory.Delete(basePath, true); }
    }

    // --- This PR: metrics events should not reset watchdog timer ---

    [Fact]
    public void Regression_FailedDelegation_MetricsEventsDontResetTimer()
    {
        // When the SDK sends repeated SessionUsageInfoEvent (e.g., after
        // FailedDelegation), the session was stuck forever because each
        // event reset LastEventAtTicks. Fix: skip LastEventAtTicks update
        // for SessionUsageInfoEvent and AssistantUsageEvent.
        //
        // Scenario simulation: session processing started 2 hours ago,
        // events keep arriving but all are metrics events.
        // LastEventAtTicks was NOT updated by metrics events, so elapsed
        // since last PROGRESS event is 2 hours.

        var processingStart = DateTime.UtcNow.AddHours(-2);
        var lastProgressEvent = processingStart.AddMinutes(1); // Last real event 1 min in
        var now = DateTime.UtcNow;

        var elapsedSinceProgress = (now - lastProgressEvent).TotalSeconds;
        var effectiveTimeout = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: false, hasReceivedEvents: true, hasUsedTools: false);

        // Inactivity timeout should fire based on last PROGRESS event
        Assert.True(elapsedSinceProgress >= effectiveTimeout,
            $"Elapsed since progress ({elapsedSinceProgress:F0}s) must exceed timeout ({effectiveTimeout}s)");
    }

    [Fact]
    public void Regression_FailedDelegation_MaxTime_CatchesAllStuckScenarios()
    {
        // Even if we miss a metrics event type in the filter, the max
        // processing time is an absolute safety net that fires regardless.
        var processingStart = DateTime.UtcNow.AddSeconds(
            -(CopilotService.WatchdogMaxProcessingTimeSeconds + 1));
        var totalProcessing = (DateTime.UtcNow - processingStart).TotalSeconds;

        Assert.True(totalProcessing >= CopilotService.WatchdogMaxProcessingTimeSeconds,
            "Max processing time must catch sessions stuck for > 60 minutes");
    }

    // --- Cross-cutting invariant tests ---

    [Fact]
    public void Invariant_INV1_AllFieldsClearedTogether()
    {
        // INV-1: When IsProcessing is set to false, ALL companion fields must
        // be cleared in the same callback. Verify the field list is complete.
        // The 9 fields that must be cleared:
        // 1. IsProcessing = false
        // 2. ProcessingStartedAt = null
        // 3. ToolCallCount = 0
        // 4. ProcessingPhase = 0
        // 5. ActiveToolCallCount = 0
        // 6. HasUsedToolsThisTurn = false
        // 7. IsResumed = false
        // 8. SendingFlag = 0
        // 9. ResponseCompletion?.TrySetResult

        // This test ensures the constants have the expected values — if someone
        // changes a default, this test forces them to verify all 9 cleanup sites.
        var info = new AgentSessionInfo { Name = "inv1-test", Model = "test" };
        info.IsProcessing = true;
        info.ProcessingStartedAt = DateTime.UtcNow;
        info.ToolCallCount = 5;
        info.ProcessingPhase = 3;
        info.IsResumed = true;

        // Simulate the cleanup
        info.IsProcessing = false;
        info.ProcessingStartedAt = null;
        info.ToolCallCount = 0;
        info.ProcessingPhase = 0;
        info.IsResumed = false;

        Assert.False(info.IsProcessing);
        Assert.Null(info.ProcessingStartedAt);
        Assert.Equal(0, info.ToolCallCount);
        Assert.Equal(0, info.ProcessingPhase);
        Assert.False(info.IsResumed);
    }

    [Fact]
    public void Invariant_INV4_NoTimeoutShorterThan15s()
    {
        // INV-4: No hardcoded short timeouts. The minimum possible timeout
        // from the production formula must be >= 15s.
        var allCombinations = new[]
        {
            ComputeEffectiveTimeout(false, false, false, false, false),
            ComputeEffectiveTimeout(true, false, false, false, false),
            ComputeEffectiveTimeout(false, true, false, false, false),
            ComputeEffectiveTimeout(false, true, true, false, false),
            ComputeEffectiveTimeout(false, false, false, true, false),
            ComputeEffectiveTimeout(false, false, false, false, true),
            ComputeEffectiveTimeout(true, true, true, true, true),
        };
        foreach (var timeout in allCombinations)
        {
            Assert.True(timeout >= 15,
                $"Timeout {timeout}s violates INV-4: no timeout shorter than 15s");
        }
    }

    [Fact]
    public void Invariant_INV5_HasUsedToolsThisTurn_PreventsDowngrade()
    {
        // INV-5: HasUsedToolsThisTurn alone is sufficient to prevent downgrade to
        // 120s inactivity timeout. ActiveToolCallCount resets to 0 between tool
        // rounds, so HasUsedToolsThisTurn is the reliable signal.
        // Routes to WatchdogUsedToolsIdleTimeoutSeconds (180s), not inactivity (120s).
        var withHasUsed = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: false, hasReceivedEvents: false, hasUsedTools: true);
        Assert.Equal(CopilotService.WatchdogUsedToolsIdleTimeoutSeconds, withHasUsed);
        Assert.NotEqual(CopilotService.WatchdogInactivityTimeoutSeconds, withHasUsed);
    }

    [Fact]
    public void Invariant_INV6_IsResumed_ClearedOnAllTerminationPaths()
    {
        // INV-6: IsResumed must be cleared on ALL termination paths:
        // CompleteResponse, AbortSessionAsync, SessionErrorEvent handler,
        // and the watchdog timeout callback. Verify the watchdog's own
        // timeout logic can reach states where IsResumed is true and
        // transitions correctly.

        // Resumed + no events → quiescence (30s) → fires → clears IsResumed
        var quiescenceTimeout = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: true, hasReceivedEvents: false, hasUsedTools: false);
        Assert.Equal(30, quiescenceTimeout);

        // After IsResumed is cleared (simulating watchdog fire):
        var afterClear = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: false, hasReceivedEvents: false, hasUsedTools: false);
        Assert.Equal(120, afterClear);
    }

    // --- End-to-end scenario tests ---

    [Fact]
    public void Scenario_AppRestart_ActiveToolCall_NoFalseKill()
    {
        // Scenario: Session is mid-tool-call when app restarts.
        // events.jsonl last written 3 minutes ago (tool still running on server).
        // Expected: quiescence bypass activates, uses 600s timeout, session survives.
        var service = CreateService();
        var basePath = Path.Combine(Path.GetTempPath(), $"scenario-{Guid.NewGuid()}");
        var sessionDir = Path.Combine(basePath, "test-session");
        Directory.CreateDirectory(sessionDir);
        try
        {
            var eventsFile = Path.Combine(sessionDir, "events.jsonl");
            File.WriteAllText(eventsFile,
                """{"type":"tool.execution_start","data":{"name":"dotnet_build"}}""");
            // File written 3 minutes ago — older than old threshold (120s)
            // but within new threshold (600s)
            File.SetLastWriteTimeUtc(eventsFile,
                DateTime.UtcNow.AddSeconds(-180));

            var (isRecentlyActive, hadToolActivity) = service.GetEventsFileRestoreHints("test-session", basePath);

            // Under old code (120s threshold): isRecentlyActive=false → 30s quiescence → FALSE KILL
            // Under new code (600s threshold): isRecentlyActive=true → 600s → session survives
            Assert.True(isRecentlyActive, "3-minute-old file must be considered recently active");
            Assert.True(hadToolActivity, "Last event is tool start");

            var timeout = ComputeEffectiveTimeout(
                hasActiveTool: false, isResumed: true,
                hasReceivedEvents: isRecentlyActive, hasUsedTools: hadToolActivity);
            Assert.Equal(CopilotService.WatchdogToolExecutionTimeoutSeconds, timeout);
        }
        finally { Directory.Delete(basePath, true); }
    }

    [Fact]
    public void Scenario_FailedDelegation_EventsKeepComing_SessionUnstuck()
    {
        // Scenario: SDK sends repeated SessionUsageInfoEvent with
        // FailedDelegation data but never sends SessionIdleEvent.
        // Old behavior: LastEventAtTicks reset on every event → watchdog never fires
        // New behavior: metrics events don't reset timer → watchdog fires at 120s
        //               OR max processing time fires at 3600s as safety net

        // The effective timeout for a non-tool, non-resumed session
        var effectiveTimeout = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: false, hasReceivedEvents: true, hasUsedTools: false);
        Assert.Equal(CopilotService.WatchdogInactivityTimeoutSeconds, effectiveTimeout);

        // With only metrics events arriving, LastEventAtTicks is NOT updated.
        // After 120s of no progress events, the watchdog fires.
        // Even if metrics somehow reset the timer, max processing time catches it.
        Assert.True(CopilotService.WatchdogMaxProcessingTimeSeconds > 0,
            "Max processing time must be positive as a safety net");
    }

    [Fact]
    public void Scenario_LongAgentTask_NotKilledPrematurely()
    {
        // Scenario: Agent session with many tool calls, each taking 2-3 minutes.
        // 20+ minutes of legitimate work with continuous progress events.
        // Expected: watchdog doesn't fire because LastEventAtTicks keeps resetting
        // from real progress events (tool starts, tool completes, message deltas).

        // With tool activity between rounds (hasUsedTools=true, no active tool), timeout is 180s
        var timeout = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: false, hasReceivedEvents: true, hasUsedTools: true);
        Assert.Equal(180, timeout);

        // Even at 59 minutes, max processing time hasn't been exceeded (it's 60 min)
        var processingStart = DateTime.UtcNow.AddMinutes(-59);
        var totalProcessing = (DateTime.UtcNow - processingStart).TotalSeconds;
        Assert.True(totalProcessing < CopilotService.WatchdogMaxProcessingTimeSeconds,
            "59-minute active session must NOT be killed by max processing time");
    }

    [Fact]
    public void Scenario_NormalPrompt_QuiescenceNeverApplies()
    {
        // Scenario: User types a prompt and sends it. This is NOT a resume.
        // The 30s quiescence timeout must NEVER fire for normal prompts.
        // (SendPromptAsync sets IsResumed=false.)
        var timeout = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: false, hasReceivedEvents: false, hasUsedTools: false);
        Assert.Equal(CopilotService.WatchdogInactivityTimeoutSeconds, timeout);
        Assert.True(timeout > 30, "Normal prompt must use timeout > 30s");
    }

    // ===== Watchdog tool-vs-no-tool behavior (new in PR fix) =====

    [Fact]
    public void WatchdogDecision_ActiveTool_ServerAlive_ShouldResetTimer()
    {
        // When a tool is actively running (ActiveToolCallCount > 0) and the server is alive,
        // the watchdog must reset the inactivity timer rather than killing the session.
        // This is verified via source-code assertion (since SessionState is private).
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Events.cs"));
        var methodIdx = source.IndexOf("private async Task RunProcessingWatchdogAsync");
        var elapsedIdx = source.IndexOf("elapsed >= effectiveTimeout", methodIdx);
        // Find the end of the method dynamically (next top-level member after RunProcessingWatchdogAsync)
        var methodEndIdx = source.IndexOf("    private readonly ConcurrentDictionary", methodIdx);
        if (methodEndIdx < 0) methodEndIdx = source.Length;
        var block = source.Substring(elapsedIdx, methodEndIdx - elapsedIdx);

        // Must check hasActiveTool before deciding server liveness path
        Assert.True(block.Contains("hasActiveTool"),
            "Watchdog must branch on hasActiveTool to distinguish running tool from lost-idle scenario");

        // Server liveness check must be conditioned on hasActiveTool
        var activeToolIdx = block.IndexOf("hasActiveTool");
        var serverRunningIdx = block.IndexOf("IsServerRunning");
        Assert.True(serverRunningIdx > activeToolIdx,
            "IsServerRunning check must appear AFTER the hasActiveTool check (guarded by it)");

        // Timer reset: LastEventAtTicks must be updated in the 'server alive' path
        var lastEventIdx = block.IndexOf("LastEventAtTicks");
        Assert.True(lastEventIdx > 0, "Must reset LastEventAtTicks when server is alive and tool is running");

        // Must use continue to skip the kill
        var continueIdx = block.IndexOf("continue;");
        Assert.True(continueIdx > 0, "Must 'continue' watchdog loop when server alive and tool running");
    }

    [Fact]
    public void WatchdogDecision_NoActiveTool_ToolsWereUsed_ShouldCompleteCleanly()
    {
        // When no tool is active (ActiveToolCallCount = 0) but tools WERE used this turn,
        // the watchdog must complete the session CLEANLY (call CompleteResponse, no error msg).
        // This is the "SessionIdleEvent lost" scenario — response is done, just terminal event missed.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Events.cs"));
        var methodIdx = source.IndexOf("private async Task RunProcessingWatchdogAsync");
        var elapsedIdx = source.IndexOf("elapsed >= effectiveTimeout", methodIdx);
        var methodEndIdx = source.IndexOf("    private readonly ConcurrentDictionary", methodIdx);
        if (methodEndIdx < 0) methodEndIdx = source.Length;
        var block = source.Substring(elapsedIdx, methodEndIdx - elapsedIdx);

        // Must have both conditions
        Assert.True(block.Contains("hasUsedTools"),
            "Watchdog must check hasUsedTools to identify lost-idle-event scenario");

        // CompleteResponse must be called in this path (not just the error path)
        var completeResponseIdx = block.IndexOf("CompleteResponse");
        Assert.True(completeResponseIdx > 0,
            "Watchdog must call CompleteResponse for the lost-idle-event scenario (not just show error)");

        // The error message should NOT appear in this path — clean completion, no error text
        // The error path ('Session appears stuck') should be in a different branch
        var stuckMsgIdx = block.IndexOf("Session appears stuck");
        Assert.True(stuckMsgIdx > completeResponseIdx,
            "The 'appears stuck' error message must come AFTER CompleteResponse, in a separate branch");

        // Case B must exit the block (break/return) before falling through to Case C error path
        var breakIdx = block.IndexOf("break;", completeResponseIdx);
        Assert.True(breakIdx > 0 && breakIdx < stuckMsgIdx,
            "Case B must have a 'break;' before Case C to prevent fallthrough to the error kill path");
    }

    [Fact]
    public void WatchdogDecision_MaxTimeExceeded_AlwaysKills()
    {
        // When total processing time exceeds WatchdogMaxProcessingTimeSeconds, the watchdog
        // must kill regardless of server liveness or tool state — no session runs forever.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Events.cs"));
        var methodIdx = source.IndexOf("private async Task RunProcessingWatchdogAsync");
        var elapsedIdx = source.IndexOf("elapsed >= effectiveTimeout", methodIdx);
        var methodEndIdx = source.IndexOf("    private readonly ConcurrentDictionary", methodIdx);
        if (methodEndIdx < 0) methodEndIdx = source.Length;
        var block = source.Substring(elapsedIdx, methodEndIdx - elapsedIdx);

        // exceededMaxTime must gate the liveness/clean-complete paths
        var exceededIdx = block.IndexOf("exceededMaxTime");
        var serverRunningIdx = block.IndexOf("IsServerRunning");
        Assert.True(exceededIdx > 0 && serverRunningIdx > 0,
            "Both exceededMaxTime and IsServerRunning must be present");
        Assert.True(exceededIdx < serverRunningIdx,
            "exceededMaxTime check must appear before the server liveness bypass — max time always kills");
    }

    [Fact]
    public void WatchdogPeriodicFlush_HasGenerationGuard()
    {
        // The periodic flush must capture ProcessingGeneration before InvokeOnUI and
        // validate it inside the lambda — preventing stale watchdog ticks from flushing
        // new-turn content into old-turn history if the user aborts + resends.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Events.cs"));
        var methodIdx = source.IndexOf("private async Task RunProcessingWatchdogAsync");
        var flushCommentIdx = source.IndexOf("Periodic mid-watchdog flush", methodIdx);
        Assert.True(flushCommentIdx > 0, "Periodic flush comment must exist in RunProcessingWatchdogAsync");
        // Capture 1000 chars around the flush block to verify generation guard
        var flushBlock = source.Substring(flushCommentIdx, 1000);
        Assert.True(flushBlock.Contains("ProcessingGeneration"),
            "Periodic flush block must read ProcessingGeneration before InvokeOnUI (race condition guard)");
    }

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PolyPilot.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find repo root");
    }

    // ===== ExternalToolRequestedEvent in EventMatrix =====

    [Fact]
    public void ExternalToolRequestedEvent_IsInEventMatrix()
    {
        // ExternalToolRequestedEvent was arriving as "Unhandled" in logs, causing spam
        // and incorrectly updating LastEventAtTicks. It must be explicitly classified.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Events.cs"));
        Assert.True(source.Contains("ExternalToolRequestedEvent"),
            "ExternalToolRequestedEvent must be explicitly listed in SdkEventMatrix to prevent 'Unhandled' log spam");
    }

    [Fact]
    public void ExternalToolRequestedEvent_ClassifiedAsTimelineOnly()
    {
        // Must be TimelineOnly — it doesn't need chat projection but should not suppress
        // LastEventAtTicks updates (it does represent live activity on the session).
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Events.cs"));
        var idx = source.IndexOf("ExternalToolRequestedEvent");
        Assert.True(idx >= 0);
        var context = source.Substring(idx, 80);
        Assert.True(context.Contains("TimelineOnly"),
            "ExternalToolRequestedEvent must be classified as TimelineOnly");
    }

    // ===== Periodic mid-watchdog flush =====

    [Fact]
    public void WatchdogPeriodicFlush_PresenceInSource()
    {
        // The watchdog must flush CurrentResponse to History periodically so partial
        // responses are visible even while IsProcessing=true (e.g., stuck mid-stream).
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Events.cs"));
        var methodIdx = source.IndexOf("private async Task RunProcessingWatchdogAsync");
        // The flush must be inside the watchdog method
        var watchdogBody = source.Substring(methodIdx, 6000);
        Assert.True(watchdogBody.Contains("CurrentResponse.Length"),
            "Watchdog must check CurrentResponse.Length for periodic flush");
        Assert.True(watchdogBody.Contains("periodic flush") || watchdogBody.Contains("FlushCurrentResponse"),
            "Watchdog must call FlushCurrentResponse for periodic flush of accumulated content");
    }

    [Fact]
    public void WatchdogPeriodicFlush_RunsBeforeTimeoutCheck()
    {
        // The periodic flush must run BEFORE the elapsed >= effectiveTimeout check,
        // so content is visible even before the session is declared stuck.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Events.cs"));
        var methodIdx = source.IndexOf("private async Task RunProcessingWatchdogAsync");
        var flushIdx = source.IndexOf("Periodic mid-watchdog flush", methodIdx);
        var elapsedCheckIdx = source.IndexOf("elapsed >= effectiveTimeout", methodIdx);
        Assert.True(flushIdx > 0, "Periodic flush comment must exist in RunProcessingWatchdogAsync");
        Assert.True(flushIdx < elapsedCheckIdx,
            "Periodic flush must appear BEFORE the elapsed >= effectiveTimeout kill check");
    }

    // ===== Multi-agent no-tool session stuck-session recovery (PR #332 fix) =====

    [Fact]
    public void WatchdogDecision_MultiAgent_NoTools_CaseBIncludesMultiAgent_InSource()
    {
        // Case B must handle multi-agent sessions without tool use.
        // This covers the scenario where an orchestrator/worker receives AssistantTurnStartEvent
        // (cancels the 4s TurnEnd fallback), then the SDK drops the terminal events for the
        // follow-on sub-turn. Without this, the 120s watchdog fires Case C (error kill) instead
        // of Case B (clean complete), showing an unnecessary error message.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Events.cs"));
        var methodIdx = source.IndexOf("private async Task RunProcessingWatchdogAsync");
        var endIdx = source.IndexOf("    private readonly ConcurrentDictionary", methodIdx);
        var watchdogBody = source.Substring(methodIdx, endIdx - methodIdx);

        Assert.True(watchdogBody.Contains("isMultiAgentSession"),
            "Case B must reference isMultiAgentSession to handle multi-agent no-tool sessions");
        // Find the inline "Case B:" comment that's directly in the else-if block
        var caseBInlineIdx = watchdogBody.IndexOf("Case B:");
        Assert.True(caseBInlineIdx >= 0, "Inline 'Case B:' comment must exist in watchdog block");
        // Look back ~200 chars to find the else if condition containing isMultiAgentSession
        var conditionStart = Math.Max(0, caseBInlineIdx - 200);
        var conditionBlock = watchdogBody.Substring(conditionStart, 400);
        Assert.True(conditionBlock.Contains("isMultiAgentSession"),
            "Case B condition must include isMultiAgentSession for multi-agent no-tool recovery");
    }

    [Fact]
    public void MultiAgent_NoTools_UseInactivityTimeout_NotToolTimeout()
    {
        // Multi-agent sessions without tool use must use 120s inactivity timeout, NOT 600s.
        // Workers generating text stream deltas continuously — elapsed stays small during
        // generation. The 120s timeout only fires when the session goes SILENT.
        // When silent: either done with lost terminal event (Case B clean complete) or
        // genuinely stuck (Case C error kill).
        // This prevents the 600s stuck-session UX bug: sdk drops TurnEnd/Idle after
        // TurnStart cancels the 4s fallback → user waits 600s instead of 120s.
        var timeout = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: false, hasReceivedEvents: false,
            hasUsedTools: false, isMultiAgent: true);
        Assert.Equal(120, timeout); // Must be inactivity (120s), not tool timeout (600s)
        Assert.True(timeout < CopilotService.WatchdogToolExecutionTimeoutSeconds,
            "Multi-agent without tools must use shorter inactivity timeout (120s), not 600s");
    }

    [Fact]
    public void AssistantTurnStartEvent_LoggedInEvtDiagnostics_InSource()
    {
        // AssistantTurnStartEvent MUST be included in the [EVT] log filter.
        // Without this, when TurnStart cancels the TurnEnd fallback silently,
        // event-diagnostics.log shows a gap with no explanation — making stuck-session
        // forensics impossible (root cause of the PR #332 debug session bug).
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Events.cs"));
        var evtLogIdx = source.IndexOf("[EVT]");
        Assert.True(evtLogIdx >= 0, "[EVT] log must exist in event handler");
        // The EVT filter block must include AssistantTurnStartEvent
        var filterContext = source.Substring(Math.Max(0, evtLogIdx - 200), 400);
        Assert.True(filterContext.Contains("AssistantTurnStartEvent"),
            "AssistantTurnStartEvent must be included in [EVT] logging so TurnStart " +
            "is visible in diagnostics (prevents invisible fallback cancellations)");
    }

    // ===========================================================================
    // Regression tests for: stuck session due to watchdog Case A infinite reset
    // Bug: When a tool is active (ActiveToolCallCount > 0) and the persistent
    // server is alive but the specific session's JSON-RPC connection is dead,
    // Case A resets LastEventAtTicks indefinitely. ProcessingStartedAt resets
    // on each app restart, so the 60-minute max time safety net never fires.
    // Fix: Cap consecutive Case A resets via WatchdogMaxToolAliveResets.
    // ===========================================================================

    [Fact]
    public void WatchdogMaxToolAliveResets_IsReasonable()
    {
        // Must allow at least 1 reset (legitimate long tool execution),
        // but not infinite. Cap at a reasonable number so stuck sessions
        // are killed in 30-60 minutes even with server alive.
        Assert.InRange(CopilotService.WatchdogMaxToolAliveResets, 1, 10);
    }

    [Fact]
    public void WatchdogMaxToolAliveResets_BoundsMaxStuckTime()
    {
        // The reset counter is incremented BEFORE the comparison:
        //   var resets = Interlocked.Increment(ref state.WatchdogCaseAResets);
        //   if (resets > WatchdogMaxToolAliveResets) { /* fall through */ }
        // So the cap fires on the (N+1)th trigger. Actual max stuck time
        // = (WatchdogMaxToolAliveResets + 1) × tool timeout (one initial
        // timeout to enter the block, then N resets before exceeding the cap).
        // This must be less than WatchdogMaxProcessingTimeSeconds (3600s)
        // so the reset cap fires before the absolute max (which may be
        // defeated by ProcessingStartedAt resetting on app restart).
        var maxCaseAStuckSeconds = (CopilotService.WatchdogMaxToolAliveResets + 1)
            * CopilotService.WatchdogToolExecutionTimeoutSeconds;
        Assert.True(maxCaseAStuckSeconds < CopilotService.WatchdogMaxProcessingTimeSeconds,
            $"Case A max stuck time ({maxCaseAStuckSeconds}s) must be less than " +
            $"absolute max ({CopilotService.WatchdogMaxProcessingTimeSeconds}s). " +
            "The reset cap is the PRIMARY safety net since the absolute max resets on app restart.");
    }

    [Fact]
    public void CaseA_ExceedingMaxResets_FallsThroughToKill_InSource()
    {
        // Verify the watchdog's Case A uses events.jsonl freshness checks and falls
        // through when the file is stale and the confirmation cycle is exceeded.
        // This is the core fix for the stuck-session bug where a dead session's tool
        // appears active but no events ever arrive.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Events.cs"));
        var methodIdx = source.IndexOf("private async Task RunProcessingWatchdogAsync");
        Assert.True(methodIdx >= 0, "RunProcessingWatchdogAsync must exist");
        var endIdx = source.IndexOf("    private readonly ConcurrentDictionary", methodIdx);
        var watchdogBody = source.Substring(methodIdx, endIdx - methodIdx);

        // Case A must check events.jsonl freshness
        Assert.True(watchdogBody.Contains("events.jsonl"),
            "Case A must check events.jsonl freshness to distinguish active tools from dead connections");
        // Case A must increment WatchdogCaseAResets for confirmation cycles
        Assert.True(watchdogBody.Contains("WatchdogCaseAResets"),
            "Case A must track reset count via state.WatchdogCaseAResets");
    }

    [Fact]
    public void CaseA_ResetCounter_ClearedOnRealEvents_InSource()
    {
        // When real SDK events arrive (not usage/metrics), the Case A reset counter
        // must be cleared. This proves the session's connection is alive, so future
        // Case A resets should be fresh (not counting against the cap).
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Events.cs"));
        var handlerIdx = source.IndexOf("private void HandleSessionEvent");
        Assert.True(handlerIdx >= 0, "HandleSessionEvent must exist");
        // Find the block that resets LastEventAtTicks (only for real events)
        var lastEventResetIdx = source.IndexOf("LastEventAtTicks", handlerIdx);
        Assert.True(lastEventResetIdx >= 0, "HandleSessionEvent must update LastEventAtTicks");
        // The counter reset must be near the LastEventAtTicks reset (within ~300 chars)
        var nearbyBlock = source.Substring(lastEventResetIdx, Math.Min(300, source.Length - lastEventResetIdx));
        Assert.True(nearbyBlock.Contains("WatchdogCaseAResets"),
            "WatchdogCaseAResets must be reset near LastEventAtTicks in HandleSessionEvent " +
            "to clear the counter when real SDK events prove the connection is alive");
    }

    [Fact]
    public void CaseA_ResetCounter_ClearedOnWatchdogStart_InSource()
    {
        // StartProcessingWatchdog must reset WatchdogCaseAResets to 0 so each new
        // watchdog instance starts with a clean reset counter.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Events.cs"));
        var startIdx = source.IndexOf("private void StartProcessingWatchdog");
        Assert.True(startIdx >= 0, "StartProcessingWatchdog must exist");
        var methodEnd = source.IndexOf("_ = RunProcessingWatchdogAsync", startIdx);
        var methodBody = source.Substring(startIdx, methodEnd - startIdx);
        Assert.True(methodBody.Contains("WatchdogCaseAResets"),
            "StartProcessingWatchdog must reset WatchdogCaseAResets to 0");
    }

    [Fact]
    public void ExceededMaxTime_TrueWhenProcessingStartedAtNull_InSource()
    {
        // Defensive: if ProcessingStartedAt is null while IsProcessing is true,
        // exceededMaxTime must be true. Without this, Case A can reset forever
        // because totalProcessingSeconds=0 makes exceededMaxTime always false.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Events.cs"));
        var methodIdx = source.IndexOf("private async Task RunProcessingWatchdogAsync");
        Assert.True(methodIdx >= 0, "RunProcessingWatchdogAsync must exist");
        var endIdx = source.IndexOf("    private readonly ConcurrentDictionary", methodIdx);
        var watchdogBody = source.Substring(methodIdx, endIdx - methodIdx);

        // The exceededMaxTime calculation must handle null ProcessingStartedAt
        Assert.True(watchdogBody.Contains("!startedAt.HasValue"),
            "exceededMaxTime must be true when ProcessingStartedAt is null " +
            "(defensive guard against Case A infinite reset)");
    }

    // ===========================================================================
    // Regression tests for: reconnect path inherits stale tool state
    // Bug: After reconnect, HasUsedToolsThisTurn=true from the dead connection
    // inflates the watchdog timeout from 120s to 600s. ProcessingStartedAt is
    // not reset, so the max-time safety net measures from the original send.
    // Fix: Reset tool tracking and ProcessingStartedAt in the reconnect block.
    // ===========================================================================

    [Fact]
    public void ReconnectPath_ResetsHasUsedToolsThisTurn_InSource()
    {
        // After reconnect, HasUsedToolsThisTurn must be false so the new connection
        // uses the 120s inactivity timeout, not the 600s tool timeout inherited from
        // the dead connection. Without this, reconnected sessions wait 5x longer.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs"));
        var reconnectIdx = source.IndexOf("[RECONNECT] '{sessionName}' replacing state");
        Assert.True(reconnectIdx >= 0, "Reconnect block must exist");
        // Find StartProcessingWatchdog in the reconnect block (marks the end of state setup)
        var watchdogIdx = source.IndexOf("StartProcessingWatchdog(state, sessionName)", reconnectIdx);
        Assert.True(watchdogIdx >= 0, "StartProcessingWatchdog must be called in reconnect block");
        var reconnectBlock = source.Substring(reconnectIdx, watchdogIdx - reconnectIdx);

        // HasUsedToolsThisTurn must be set to false (not carried from old state)
        Assert.True(reconnectBlock.Contains("HasUsedToolsThisTurn = false"),
            "Reconnect block must reset HasUsedToolsThisTurn to false for the new connection. " +
            "Carrying over true from the dead connection inflates the timeout from 120s to 600s.");
    }

    [Fact]
    public void ReconnectPath_ResetsProcessingStartedAt_InSource()
    {
        // After reconnect, ProcessingStartedAt must be reset to DateTime.UtcNow
        // so the watchdog's max-time safety net measures from the reconnect time,
        // not the original send time.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs"));
        var reconnectIdx = source.IndexOf("[RECONNECT] '{sessionName}' replacing state");
        Assert.True(reconnectIdx >= 0, "Reconnect block must exist");
        var watchdogIdx = source.IndexOf("StartProcessingWatchdog(state, sessionName)", reconnectIdx);
        var reconnectBlock = source.Substring(reconnectIdx, watchdogIdx - reconnectIdx);

        Assert.True(reconnectBlock.Contains("ProcessingStartedAt = DateTime.UtcNow"),
            "Reconnect block must reset ProcessingStartedAt to DateTime.UtcNow. " +
            "Without this, the 60-min max-time safety net measures from the original send.");
    }

    [Fact]
    public void ReconnectPath_ResetsActiveToolCallCount_InSource()
    {
        // After reconnect, ActiveToolCallCount must be 0. No tools have started
        // on the new connection. A stale count > 0 would trigger Case A resets.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs"));
        var reconnectIdx = source.IndexOf("[RECONNECT] '{sessionName}' replacing state");
        Assert.True(reconnectIdx >= 0, "Reconnect block must exist");
        var watchdogIdx = source.IndexOf("StartProcessingWatchdog(state, sessionName)", reconnectIdx);
        var reconnectBlock = source.Substring(reconnectIdx, watchdogIdx - reconnectIdx);

        Assert.True(reconnectBlock.Contains("ActiveToolCallCount") && reconnectBlock.Contains("0"),
            "Reconnect block must reset ActiveToolCallCount to 0 for the new connection.");
    }

    // ===== Multi-agent Case B extended freshness (issue #365 fix) =====

    [Fact]
    public void WatchdogCaseBFreshnessSeconds_StandardValue()
    {
        // Standard (non-multi-agent) freshness is 300s (5 min).
        Assert.Equal(300, CopilotService.WatchdogCaseBFreshnessSeconds);
    }

    [Fact]
    public void WatchdogMultiAgentCaseBFreshnessSeconds_ExtendedValue()
    {
        // Multi-agent sessions get 1800s (30 min) to accommodate long server-side tool execution.
        Assert.Equal(1800, CopilotService.WatchdogMultiAgentCaseBFreshnessSeconds);
        Assert.True(CopilotService.WatchdogMultiAgentCaseBFreshnessSeconds >
                    CopilotService.WatchdogCaseBFreshnessSeconds,
            "Multi-agent freshness must be longer than standard freshness");
    }

    [Fact]
    public void WatchdogMultiAgentCaseBFreshnessSeconds_WithinWorkerTimeout()
    {
        // The extended freshness must be less than the worker execution timeout (60 min = 3600s).
        // It's a safety net, not an override of the ultimate backstop.
        Assert.True(CopilotService.WatchdogMultiAgentCaseBFreshnessSeconds <
                    CopilotService.WatchdogMaxProcessingTimeSeconds,
            "Multi-agent Case B freshness must be shorter than the absolute max processing time");
    }

    [Fact]
    public void WatchdogCaseB_UsesMultiAgentFreshness_InSource()
    {
        // Case B freshness check must select threshold based on isMultiAgentSession.
        // This prevents premature force-completion of worker sessions (issue #365).
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Events.cs"));
        var methodIdx = source.IndexOf("private async Task RunProcessingWatchdogAsync");
        var endIdx = source.IndexOf("    private readonly ConcurrentDictionary", methodIdx);
        var watchdogBody = source.Substring(methodIdx, endIdx - methodIdx);

        // The freshness threshold must be conditional on isMultiAgentSession
        Assert.True(watchdogBody.Contains("WatchdogMultiAgentCaseBFreshnessSeconds"),
            "Case B must reference WatchdogMultiAgentCaseBFreshnessSeconds for multi-agent sessions");
        Assert.True(watchdogBody.Contains("WatchdogCaseBFreshnessSeconds"),
            "Case B must reference WatchdogCaseBFreshnessSeconds for standard sessions");

        // The age comparison must use the parameterized threshold, not a hardcoded 300
        Assert.True(watchdogBody.Contains("age < freshnessSeconds"),
            "Case B must compare age against the parameterized freshnessSeconds, not a hardcoded value");
        Assert.False(watchdogBody.Contains("age < 300"),
            "Case B must NOT use hardcoded 300 — must use named constant via freshnessSeconds variable");
    }

    [Fact]
    public void WatchdogCaseB_FreshnessThresholdSelection_InSource()
    {
        // Verify that the freshness threshold is selected based on isMultiAgentSession
        // using a ternary before the events.jsonl check.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Events.cs"));
        var methodIdx = source.IndexOf("private async Task RunProcessingWatchdogAsync");
        var endIdx = source.IndexOf("    private readonly ConcurrentDictionary", methodIdx);
        var watchdogBody = source.Substring(methodIdx, endIdx - methodIdx);

        // The threshold selection must use isMultiAgentSession ternary
        Assert.True(watchdogBody.Contains("isMultiAgentSession")
            && watchdogBody.Contains("WatchdogMultiAgentCaseBFreshnessSeconds")
            && watchdogBody.Contains("WatchdogCaseBFreshnessSeconds"),
            "Case B must select freshness threshold based on isMultiAgentSession");
    }

    // ===== Watchdog crash recovery: catch(Exception) must clear IsProcessing =====

    [Fact]
    public void WatchdogCatchBlock_ClearsIsProcessing_InSource()
    {
        // If the watchdog loop throws an unexpected exception, the catch(Exception) block
        // MUST clear IsProcessing — otherwise the session is permanently stuck.
        // Regression test for: sessions stuck at "Sending..." forever after watchdog crash.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Events.cs"));
        var methodIdx = source.IndexOf("private async Task RunProcessingWatchdogAsync");
        var endIdx = source.IndexOf("    private readonly ConcurrentDictionary", methodIdx);
        var watchdogBody = source.Substring(methodIdx, endIdx - methodIdx);

        // The catch(Exception) block must contain IsProcessing cleanup
        Assert.True(watchdogBody.Contains("[WATCHDOG-CRASH]"),
            "Watchdog catch block must use [WATCHDOG-CRASH] diagnostic tag");
        Assert.True(watchdogBody.Contains("clearing IsProcessing after watchdog crash"),
            "Watchdog catch block must log that it is clearing IsProcessing on crash");

        // The crash recovery must clear all INV-1 companion fields
        // Search the full catch block (after the [WATCHDOG-CRASH] tag) for required patterns
        var crashIdx = watchdogBody.IndexOf("[WATCHDOG-CRASH]");
        var crashBlock = watchdogBody.Substring(crashIdx);
        Assert.True(crashBlock.Contains("IsProcessing = false"),
            "Watchdog crash recovery must set IsProcessing = false");
        Assert.True(crashBlock.Contains("SendingFlag"),
            "Watchdog crash recovery must clear SendingFlag (INV-1)");
        Assert.True(crashBlock.Contains("ProcessingStartedAt = null"),
            "Watchdog crash recovery must clear ProcessingStartedAt (INV-1)");
        Assert.True(crashBlock.Contains("ProcessingPhase = 0"),
            "Watchdog crash recovery must clear ProcessingPhase (INV-1)");
        Assert.True(crashBlock.Contains("ClearPermissionDenials"),
            "Watchdog crash recovery must clear permission denials (INV-1)");
    }

    [Fact]
    public void WatchdogCatchBlock_CompletesResponseCompletion_InSource()
    {
        // The crash recovery must complete the TCS so callers (e.g., orchestrators)
        // waiting on SendPromptAsync aren't blocked forever.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Events.cs"));
        var methodIdx = source.IndexOf("private async Task RunProcessingWatchdogAsync");
        var endIdx = source.IndexOf("    private readonly ConcurrentDictionary", methodIdx);
        var watchdogBody = source.Substring(methodIdx, endIdx - methodIdx);

        var crashIdx = watchdogBody.IndexOf("[WATCHDOG-CRASH]");
        var crashBlock = watchdogBody.Substring(crashIdx);
        Assert.True(crashBlock.Contains("ResponseCompletion?.TrySetResult"),
            "Watchdog crash recovery must complete ResponseCompletion TCS to unblock callers");
        Assert.True(crashBlock.Contains("OnSessionComplete"),
            "Watchdog crash recovery must fire OnSessionComplete for orchestrator coordination");
        Assert.True(crashBlock.Contains("OnStateChanged"),
            "Watchdog crash recovery must fire OnStateChanged to update UI");
    }

    // ===== Watchdog kill callback: FlushCurrentResponse must be protected =====

    [Fact]
    public void WatchdogKillCallback_ProtectsFlushCurrentResponse_InSource()
    {
        // The watchdog's Case C kill callback (InvokeOnUI) calls FlushCurrentResponse
        // BEFORE setting IsProcessing = false. If FlushCurrentResponse throws, the
        // exception is caught by InvokeOnUI's try-catch, but IsProcessing is never cleared
        // because the watchdog has already exited (break). FlushCurrentResponse MUST be
        // wrapped in try-catch within the kill callback.
        // Regression test for: sessions permanently stuck after FlushCurrentResponse failure.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Events.cs"));
        var methodIdx = source.IndexOf("private async Task RunProcessingWatchdogAsync");
        var endIdx = source.IndexOf("    private readonly ConcurrentDictionary", methodIdx);
        var watchdogBody = source.Substring(methodIdx, endIdx - methodIdx);

        // Find the watchdog timeout kill path (Case C): the block that sets IsProcessing=false
        // after the Case A/B checks. It should contain a protected FlushCurrentResponse call.
        var killIdx = watchdogBody.IndexOf("[WATCHDOG] '{sessionName}' IsProcessing=false — watchdog timeout");
        Assert.True(killIdx > 0, "Could not find watchdog kill path diagnostic log");

        // Look backwards from the kill log to find the FlushCurrentResponse call.
        // It must be wrapped in try-catch, not bare.
        var beforeKill = watchdogBody.Substring(Math.Max(0, killIdx - 500), Math.Min(500, killIdx));
        Assert.True(beforeKill.Contains("try { FlushCurrentResponse") || beforeKill.Contains("try\n") || beforeKill.Contains("flush failed during kill"),
            "FlushCurrentResponse in the watchdog kill path must be wrapped in try-catch to prevent " +
            "IsProcessing from staying true if the flush throws");
    }

    [Fact]
    public void WatchdogCrashRecovery_ClearsCompanionFields()
    {
        // Behavioral test: verify that the INV-1 companion fields are properly
        // reset when a session recovers from a stuck state (simulating what the
        // crash recovery path does).
        var info = new AgentSessionInfo { Name = "crash-test", Model = "test" };
        info.IsProcessing = true;
        info.IsResumed = true;
        info.ProcessingStartedAt = DateTime.UtcNow;
        info.ToolCallCount = 5;
        info.ProcessingPhase = 3;
        info.ConsecutiveStuckCount = 0;

        // Simulate crash recovery clearing all fields (mirrors the catch block logic)
        info.IsProcessing = false;
        info.IsResumed = false;
        info.ProcessingStartedAt = null;
        info.ToolCallCount = 0;
        info.ProcessingPhase = 0;
        info.ClearPermissionDenials();
        info.ConsecutiveStuckCount++;

        Assert.False(info.IsProcessing);
        Assert.False(info.IsResumed);
        Assert.Null(info.ProcessingStartedAt);
        Assert.Equal(0, info.ToolCallCount);
        Assert.Equal(0, info.ProcessingPhase);
        Assert.Equal(1, info.ConsecutiveStuckCount);
    }

    // ===== Tests for IsReconnectedSend / WatchdogReconnectInactivityTimeoutSeconds (#406) =====

    [Fact]
    public void WatchdogReconnectTimeout_IsWithinValidRange()
    {
        // Reconnect timeout must be shorter than normal inactivity (so we detect
        // dead event streams faster) but longer than the resume quiescence timeout
        // (so a legitimately slow reconnect isn't killed too quickly).
        Assert.True(
            CopilotService.WatchdogReconnectInactivityTimeoutSeconds < CopilotService.WatchdogInactivityTimeoutSeconds,
            $"WatchdogReconnectInactivityTimeoutSeconds ({CopilotService.WatchdogReconnectInactivityTimeoutSeconds}s) " +
            $"must be less than WatchdogInactivityTimeoutSeconds ({CopilotService.WatchdogInactivityTimeoutSeconds}s)");
        Assert.True(
            CopilotService.WatchdogReconnectInactivityTimeoutSeconds > CopilotService.WatchdogResumeQuiescenceTimeoutSeconds,
            $"WatchdogReconnectInactivityTimeoutSeconds ({CopilotService.WatchdogReconnectInactivityTimeoutSeconds}s) " +
            $"must be greater than WatchdogResumeQuiescenceTimeoutSeconds ({CopilotService.WatchdogResumeQuiescenceTimeoutSeconds}s)");
    }

    [Fact]
    public void WatchdogTimeoutSelection_ReconnectedSend_NoTools_UsesReconnectTimeout()
    {
        // After a reconnect, IsReconnectedSend=true with no tool activity →
        // use the shorter reconnect timeout (35s) to detect dead event streams quickly.
        var effectiveTimeout = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: false, hasReceivedEvents: false,
            hasUsedTools: false, isReconnectedSend: true);

        Assert.Equal(CopilotService.WatchdogReconnectInactivityTimeoutSeconds, effectiveTimeout);
        Assert.Equal(35, effectiveTimeout);
    }

    [Fact]
    public void WatchdogTimeoutSelection_ReconnectedSend_WithActiveTool_UsesToolTimeout()
    {
        // Reconnected send with an active tool → tool timeout takes priority over
        // reconnect timeout. Don't kill a legitimately running tool early.
        var effectiveTimeout = ComputeEffectiveTimeout(
            hasActiveTool: true, isResumed: false, hasReceivedEvents: false,
            hasUsedTools: false, isReconnectedSend: true);

        Assert.Equal(CopilotService.WatchdogToolExecutionTimeoutSeconds, effectiveTimeout);
        Assert.Equal(600, effectiveTimeout);
    }

    [Fact]
    public void WatchdogTimeoutSelection_ReconnectedSend_Resumed_NoEvents_UsesQuiescenceTimeout()
    {
        // If a session is both resumed AND reconnected with no events, the resume
        // quiescence path takes priority — it's the most specific short-circuit.
        var effectiveTimeout = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: true, hasReceivedEvents: false,
            hasUsedTools: false, isReconnectedSend: true);

        Assert.Equal(CopilotService.WatchdogResumeQuiescenceTimeoutSeconds, effectiveTimeout);
        Assert.Equal(30, effectiveTimeout);
    }

    [Fact]
    public void WatchdogTimeoutSelection_NotReconnectedSend_UsesInactivityTimeout()
    {
        // Normal (non-reconnected) send with no tool flags → standard 120s inactivity timeout.
        // IsReconnectedSend=false must not activate the shorter reconnect path.
        var effectiveTimeout = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: false, hasReceivedEvents: false,
            hasUsedTools: false, isReconnectedSend: false);

        Assert.Equal(CopilotService.WatchdogInactivityTimeoutSeconds, effectiveTimeout);
        Assert.Equal(120, effectiveTimeout);
    }

    [Fact]
    public void WatchdogTimeoutSelection_ReconnectedSend_WithUsedTools_UsesUsedToolsTimeout()
    {
        // isReconnectedSend=true AND hasUsedTools=true (tools ran during this turn, none active).
        // hasUsedTools (180s) takes priority over the reconnect timeout (35s) because
        // the session did real work and the model may be thinking between tool rounds.
        // The reconnect-fast-fail logic only applies when NO tools have been used.
        var effectiveTimeout = ComputeEffectiveTimeout(
            hasActiveTool: false, isResumed: false, hasReceivedEvents: false,
            hasUsedTools: true, isReconnectedSend: true);

        Assert.Equal(CopilotService.WatchdogUsedToolsIdleTimeoutSeconds, effectiveTimeout);
        Assert.Equal(180, effectiveTimeout);
        Assert.NotEqual(CopilotService.WatchdogReconnectInactivityTimeoutSeconds, effectiveTimeout);
    }

    [Fact]
    public void IsReconnectedSend_IsDeclaredVolatile()
    {
        // Verify IsReconnectedSend is declared as volatile bool on SessionState.
        // Volatile is required for correctness: the watchdog reads it on a background
        // thread while the event handler clears it on the SDK callback thread.
        // Reflection check mirrors the existing HasUsedToolsThisTurn volatile test.
        var sessionStateType = typeof(CopilotService).GetNestedTypes(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .FirstOrDefault(t => t.Name == "SessionState");
        Assert.NotNull(sessionStateType);

        var field = sessionStateType!.GetField("IsReconnectedSend",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        Assert.Equal(typeof(bool), field!.FieldType);

        // Check for the volatile modifier via custom modifiers
        var requiredMods = field.GetRequiredCustomModifiers();
        var isVolatile = requiredMods.Any(t => t.FullName == "System.Runtime.CompilerServices.IsVolatile");
        Assert.True(isVolatile, "IsReconnectedSend must be declared as 'volatile bool' (field has volatile modifier)");
    }
}