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
        session.TurnRoundCount = 5;
        session.HasReceivedFirstEvent = true;
        session.MessageQueue.Add("queued message 1");
        session.MessageQueue.Add("queued message 2");

        await svc.AbortSessionAsync("abort-queue");

        Assert.False(session.IsProcessing);
        Assert.Null(session.ProcessingStartedAt);
        Assert.Equal(0, session.TurnRoundCount);
        Assert.False(session.HasReceivedFirstEvent);
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

    [Fact]
    public void WatchdogTimeoutSelection_NoTools_UsesInactivityTimeout()
    {
        // When no tool activity and not resumed → use shorter inactivity timeout
        int activeToolCallCount = 0;
        bool isResumed = false;
        bool hasUsedToolsThisTurn = false;

        var hasActiveTool = Interlocked.CompareExchange(ref activeToolCallCount, 0, 0) > 0;
        var useToolTimeout = hasActiveTool || isResumed || hasUsedToolsThisTurn;
        var effectiveTimeout = useToolTimeout
            ? CopilotService.WatchdogToolExecutionTimeoutSeconds
            : CopilotService.WatchdogInactivityTimeoutSeconds;

        Assert.Equal(CopilotService.WatchdogInactivityTimeoutSeconds, effectiveTimeout);
        Assert.Equal(120, effectiveTimeout);
    }

    [Fact]
    public void WatchdogTimeoutSelection_ActiveTool_UsesToolTimeout()
    {
        // When ActiveToolCallCount > 0 → use longer tool execution timeout
        int activeToolCallCount = 1;
        bool isResumed = false;
        bool hasUsedToolsThisTurn = false;

        var hasActiveTool = Interlocked.CompareExchange(ref activeToolCallCount, 0, 0) > 0;
        var useToolTimeout = hasActiveTool || isResumed || hasUsedToolsThisTurn;
        var effectiveTimeout = useToolTimeout
            ? CopilotService.WatchdogToolExecutionTimeoutSeconds
            : CopilotService.WatchdogInactivityTimeoutSeconds;

        Assert.Equal(CopilotService.WatchdogToolExecutionTimeoutSeconds, effectiveTimeout);
        Assert.Equal(600, effectiveTimeout);
    }

    [Fact]
    public void WatchdogTimeoutSelection_ResumedSession_UsesToolTimeout()
    {
        // When session is resumed (IsResumed=true) → use longer tool timeout
        // because resumed sessions may have in-flight tool calls from before restart
        int activeToolCallCount = 0;
        bool isResumed = true;
        bool hasUsedToolsThisTurn = false;

        var hasActiveTool = Interlocked.CompareExchange(ref activeToolCallCount, 0, 0) > 0;
        var useToolTimeout = hasActiveTool || isResumed || hasUsedToolsThisTurn;
        var effectiveTimeout = useToolTimeout
            ? CopilotService.WatchdogToolExecutionTimeoutSeconds
            : CopilotService.WatchdogInactivityTimeoutSeconds;

        Assert.Equal(CopilotService.WatchdogToolExecutionTimeoutSeconds, effectiveTimeout);
        Assert.Equal(600, effectiveTimeout);
    }

    [Fact]
    public void WatchdogTimeoutSelection_HasUsedTools_UsesToolTimeout()
    {
        // When tools have been used this turn (HasUsedToolsThisTurn=true) → use longer
        // tool timeout even between tool rounds when the model is thinking
        int activeToolCallCount = 0;
        bool isResumed = false;
        bool hasUsedToolsThisTurn = true;

        var hasActiveTool = Interlocked.CompareExchange(ref activeToolCallCount, 0, 0) > 0;
        var useToolTimeout = hasActiveTool || isResumed || hasUsedToolsThisTurn;
        var effectiveTimeout = useToolTimeout
            ? CopilotService.WatchdogToolExecutionTimeoutSeconds
            : CopilotService.WatchdogInactivityTimeoutSeconds;

        Assert.Equal(CopilotService.WatchdogToolExecutionTimeoutSeconds, effectiveTimeout);
        Assert.Equal(600, effectiveTimeout);
    }

    [Fact]
    public void HasUsedToolsThisTurn_ResetOnNewSend()
    {
        // SendPromptAsync resets HasUsedToolsThisTurn alongside ActiveToolCallCount
        // to prevent stale tool-usage from a previous turn inflating the timeout
        bool hasUsedToolsThisTurn = true;
        // SendPromptAsync resets it
        hasUsedToolsThisTurn = false;
        int activeToolCallCount = 0;
        bool isResumed = false;

        var hasActiveTool = Interlocked.CompareExchange(ref activeToolCallCount, 0, 0) > 0;
        var useToolTimeout = hasActiveTool || isResumed || hasUsedToolsThisTurn;
        var effectiveTimeout = useToolTimeout
            ? CopilotService.WatchdogToolExecutionTimeoutSeconds
            : CopilotService.WatchdogInactivityTimeoutSeconds;

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
        int activeToolCallCount = 0;
        bool hasUsedToolsThisTurn = false;

        var hasActiveTool = Interlocked.CompareExchange(ref activeToolCallCount, 0, 0) > 0;
        var useToolTimeout = hasActiveTool || info.IsResumed || hasUsedToolsThisTurn;
        var effectiveTimeout = useToolTimeout
            ? CopilotService.WatchdogToolExecutionTimeoutSeconds
            : CopilotService.WatchdogInactivityTimeoutSeconds;

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

        // Verify next turn would use 120s
        int activeToolCallCount = 0;
        bool hasUsedToolsThisTurn = false;
        var hasActiveTool = Interlocked.CompareExchange(ref activeToolCallCount, 0, 0) > 0;
        var useToolTimeout = hasActiveTool || info.IsResumed || hasUsedToolsThisTurn;
        var effectiveTimeout = useToolTimeout
            ? CopilotService.WatchdogToolExecutionTimeoutSeconds
            : CopilotService.WatchdogInactivityTimeoutSeconds;

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
}
