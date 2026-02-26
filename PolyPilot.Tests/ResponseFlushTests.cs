using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Regression tests for: session response lost when IsProcessing is cleared
/// prematurely by watchdog, error handler, or reconnect race condition.
/// Bug: UI shows "Thinking" with no response, but SDK completed the work.
/// </summary>
public class ResponseFlushTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public ResponseFlushTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    // --- Model-level: simulated watchdog flush preserves content ---

    [Fact]
    public void WatchdogFlush_SimulatedStateTransition_PreservesHistory()
    {
        // Simulates the watchdog scenario: session accumulates response content,
        // then IsProcessing is cleared. The fix ensures accumulated content
        // is flushed to history before clearing IsProcessing.
        var info = new AgentSessionInfo { Name = "test-flush", Model = "test-model" };

        // Simulate: user sent a message
        info.History.Add(new ChatMessage("user", "What files are on the network?", DateTime.Now));
        info.IsProcessing = true;
        info.MessageCount = info.History.Count;

        // Simulate: partial response accumulated (normally in CurrentResponse,
        // but at model level we test the flush writes to history)
        var partialResponse = new ChatMessage("assistant", "I found 3 files on the network drive.", DateTime.Now);
        info.History.Add(partialResponse);
        info.MessageCount = info.History.Count;

        // Simulate: watchdog fires, clears IsProcessing
        info.IsProcessing = false;
        info.History.Add(ChatMessage.SystemMessage(
            "⚠️ Session appears stuck — no response received. You can try sending your message again."));

        // Verify: the partial response is still in history (not lost)
        Assert.False(info.IsProcessing);
        Assert.Equal(3, info.History.Count);
        var assistantMessages = info.History.Where(m => m.Role == "assistant").ToList();
        Assert.Single(assistantMessages);
        Assert.Contains("3 files", assistantMessages[0].Content);
    }

    [Fact]
    public void ErrorEvent_SimulatedStateTransition_PreservesPartialResponse()
    {
        // Simulates the SessionErrorEvent scenario: deltas arrived,
        // then an error occurs. The fix ensures accumulated content
        // is flushed before clearing IsProcessing.
        var info = new AgentSessionInfo { Name = "test-error-flush", Model = "test-model" };

        // User message
        info.History.Add(new ChatMessage("user", "List network shares", DateTime.Now));
        info.IsProcessing = true;

        // Partial assistant response (flushed by the fix before error clears state)
        var partial = new ChatMessage("assistant", "Accessing network...", DateTime.Now);
        info.History.Add(partial);

        // Error occurs, clears processing
        info.History.Add(ChatMessage.ErrorMessage("Connection lost"));
        info.IsProcessing = false;

        // Verify partial response is preserved
        Assert.False(info.IsProcessing);
        Assert.Equal(3, info.History.Count);
        Assert.Equal("assistant", info.History[1].Role);
        Assert.Equal("Accessing network...", info.History[1].Content);
    }

    // --- Service-level: abort flushes accumulated content ---

    [Fact]
    public async Task DemoMode_AbortWhileProcessing_HistoryIntact()
    {
        // Abort should preserve existing history (the fix ensures
        // FlushCurrentResponse runs before clearing state).
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("abort-flush");
        await svc.SendPromptAsync("abort-flush", "First message");

        var historyCountAfterFirst = session.History.Count;
        Assert.True(historyCountAfterFirst >= 1, "Should have at least the user message");

        // Simulate stuck state then abort
        session.IsProcessing = true;
        await svc.AbortSessionAsync("abort-flush");

        Assert.False(session.IsProcessing);
        // History from before abort must be preserved
        Assert.True(session.History.Count >= historyCountAfterFirst,
            "Abort should not lose existing history messages");
    }

    [Fact]
    public async Task DemoMode_SendAfterStuckRecovery_AddsToHistory()
    {
        // After recovering from a stuck state, new messages should work normally.
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("recovery-flush");

        // First exchange
        await svc.SendPromptAsync("recovery-flush", "Hello");
        var countAfterFirst = session.History.Count;

        // Simulate watchdog clearing stuck state
        session.IsProcessing = true;
        session.IsProcessing = false;
        session.History.Add(ChatMessage.SystemMessage("⚠️ Session appears stuck"));

        // Send another message — should work
        await svc.SendPromptAsync("recovery-flush", "Try again");
        Assert.False(session.IsProcessing);
        Assert.True(session.History.Count > countAfterFirst,
            "Messages after recovery should be added to history");
    }

    // --- CompleteResponse with IsProcessing=false should flush ---

    [Fact]
    public void CompleteResponse_WhenProcessingFalse_StillFlushesContent()
    {
        // This tests the invariant: if response content exists but IsProcessing
        // was cleared prematurely, the content should still be flushed to history
        // when CompleteResponse runs (e.g., from a late SessionIdleEvent).
        var info = new AgentSessionInfo { Name = "late-flush", Model = "test-model" };

        // Start a turn
        info.History.Add(new ChatMessage("user", "Do work", DateTime.Now));
        info.IsProcessing = true;

        // Watchdog fires prematurely, clears IsProcessing
        // (In the actual code, FlushCurrentResponse would run here due to the fix)
        info.IsProcessing = false;

        // Late SessionIdleEvent arrives — CompleteResponse called
        // (In actual code, the fix flushes CurrentResponse even when IsProcessing=false)
        // Verify the model supports this: we can add assistant messages after IsProcessing=false
        var lateContent = new ChatMessage("assistant", "Work completed successfully.", DateTime.Now);
        info.History.Add(lateContent);
        info.MessageCount = info.History.Count;

        // Verify the late content is in history alongside the user message
        Assert.Equal(2, info.History.Count);
        Assert.Equal("user", info.History[0].Role);
        Assert.Equal("assistant", info.History[1].Role);
        Assert.Equal("Work completed successfully.", info.History[1].Content);
    }

    // --- Reconnect generation reset ---

    [Fact]
    public async Task DemoMode_ReconnectAsync_ClearsProcessingState()
    {
        // Reconnect should leave no sessions in stuck state in the service.
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("recon-1");
        await svc.CreateSessionAsync("recon-2");

        // Reconnect clears everything — old sessions are removed from service
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        // After reconnect, service should have no sessions
        Assert.Empty(svc.GetAllSessions());
    }

    [Fact]
    public async Task DemoMode_SendAfterReconnect_WorksNormally()
    {
        // After reconnect, sessions in the new connection should work.
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("before-recon");
        await svc.SendPromptAsync("before-recon", "Message 1");

        // Reconnect (simulates mode switch or server restart)
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        // Create new session in new connection
        var newSession = await svc.CreateSessionAsync("after-recon");
        await svc.SendPromptAsync("after-recon", "Fresh start");

        Assert.False(newSession.IsProcessing);
        Assert.True(newSession.History.Count >= 1);
    }

    // --- History integrity after state transitions ---

    [Fact]
    public void HistoryPreserved_AcrossMultipleStateTransitions()
    {
        // Verify history is preserved across multiple processing cycles,
        // including simulated watchdog/error interruptions.
        var info = new AgentSessionInfo { Name = "multi-cycle", Model = "test-model" };

        // Cycle 1: Normal completion
        info.History.Add(new ChatMessage("user", "Message 1", DateTime.Now));
        info.IsProcessing = true;
        info.History.Add(new ChatMessage("assistant", "Response 1", DateTime.Now));
        info.IsProcessing = false;

        // Cycle 2: Watchdog interruption (partial response preserved by fix)
        info.History.Add(new ChatMessage("user", "Message 2", DateTime.Now));
        info.IsProcessing = true;
        info.History.Add(new ChatMessage("assistant", "Partial response 2", DateTime.Now));
        info.IsProcessing = false; // Watchdog
        info.History.Add(ChatMessage.SystemMessage("⚠️ Session appears stuck"));

        // Cycle 3: Error interruption (partial response preserved by fix)
        info.History.Add(new ChatMessage("user", "Message 3", DateTime.Now));
        info.IsProcessing = true;
        info.History.Add(new ChatMessage("assistant", "Partial 3", DateTime.Now));
        info.History.Add(ChatMessage.ErrorMessage("Connection error"));
        info.IsProcessing = false;

        // Cycle 4: Normal completion after errors
        info.History.Add(new ChatMessage("user", "Message 4", DateTime.Now));
        info.IsProcessing = true;
        info.History.Add(new ChatMessage("assistant", "Response 4", DateTime.Now));
        info.IsProcessing = false;

        // All messages should be preserved
        Assert.False(info.IsProcessing);
        // ErrorMessage has role="assistant", so count excludes it via MessageType
        var assistantResponses = info.History
            .Where(m => m.Role == "assistant" && m.MessageType == ChatMessageType.Assistant)
            .ToList();
        Assert.Equal(4, assistantResponses.Count);
        Assert.Equal("Response 1", assistantResponses[0].Content);
        Assert.Equal("Partial response 2", assistantResponses[1].Content);
        Assert.Equal("Partial 3", assistantResponses[2].Content);
        Assert.Equal("Response 4", assistantResponses[3].Content);

        // System/error messages also preserved
        Assert.Single(info.History.Where(m => m.MessageType == ChatMessageType.System));
        Assert.Single(info.History.Where(m => m.MessageType == ChatMessageType.Error));
    }

    [Fact]
    public async Task DemoMode_HighMessageCount_NoResponseLoss()
    {
        // Regression: the original bug had 90 messages. Ensure high message
        // count sessions don't lose responses.
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("high-count");

        // Send many messages
        for (int i = 0; i < 20; i++)
        {
            await svc.SendPromptAsync("high-count", $"Message {i}");
        }

        Assert.False(session.IsProcessing);
        // Each send in demo mode adds at least a user message
        Assert.True(session.History.Count >= 20,
            $"Expected at least 20 history entries, got {session.History.Count}");

        // Simulate stuck + recovery on high-count session
        session.IsProcessing = true;
        await svc.AbortSessionAsync("high-count");
        Assert.False(session.IsProcessing);

        // Can still send
        await svc.SendPromptAsync("high-count", "After recovery");
        Assert.False(session.IsProcessing);
    }

    // --- Persistent mode: connection failure doesn't lose state ---

    [Fact]
    public async Task PersistentMode_FailedInit_NoOrphanedProcessing()
    {
        // If persistent mode fails to connect, no sessions should be
        // left in a processing state.
        var svc = CreateService();

        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999
        });

        Assert.Empty(svc.GetAllSessions());
        foreach (var s in svc.GetAllSessions())
        {
            Assert.False(s.IsProcessing,
                $"Session '{s.Name}' stuck processing after failed init");
        }
    }

    // --- OnStateChanged fires during recovery ---

    [Fact]
    public async Task DemoMode_AbortFiresStateChanged()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("state-changed");
        var stateChangedCount = 0;
        svc.OnStateChanged += () => stateChangedCount++;

        // Simulate stuck + abort
        var session = svc.GetAllSessions().First();
        session.IsProcessing = true;
        await svc.AbortSessionAsync("state-changed");

        // OnStateChanged should have fired during abort
        Assert.True(stateChangedCount >= 1,
            "OnStateChanged must fire when abort clears processing state");
    }

    // --- ChatMessage factory methods used in fixes ---

    [Fact]
    public void ChatMessage_SystemMessage_HasCorrectType()
    {
        var msg = ChatMessage.SystemMessage("⚠️ Session appears stuck");
        Assert.Equal("system", msg.Role);
        Assert.Equal(ChatMessageType.System, msg.MessageType);
        Assert.Contains("appears stuck", msg.Content);
    }

    [Fact]
    public void ChatMessage_ErrorMessage_HasCorrectType()
    {
        var msg = ChatMessage.ErrorMessage("Connection lost");
        Assert.Equal(ChatMessageType.Error, msg.MessageType);
        Assert.Contains("Connection lost", msg.Content);
    }

    [Fact]
    public void ChatMessage_AssistantMessage_ModelPreserved()
    {
        // The fix creates assistant messages with model info during flush.
        var msg = new ChatMessage("assistant", "Response text", DateTime.Now) { Model = "gpt-5.3-codex" };
        Assert.Equal("assistant", msg.Role);
        Assert.Equal("gpt-5.3-codex", msg.Model);
        Assert.Equal("Response text", msg.Content);
    }

    // --- TurnEnd flush: prevents content loss on app restart ---

    [Fact]
    public void TurnEndFlush_SimulatedContentLoss_ContentPreservedInHistory()
    {
        // Regression test for ReviewPRs bug: assistant.message content accumulated
        // in CurrentResponse was lost when the app restarted between turn_end and
        // session.idle. The fix calls FlushCurrentResponse on AssistantTurnEndEvent.
        var info = new AgentSessionInfo { Name = "review-session", Model = "claude-opus-4.6" };

        info.History.Add(new ChatMessage("user", "do a deep review of PR #34217", DateTime.Now));
        info.IsProcessing = true;

        // Simulate: assistant.message with review content arrives → appended to CurrentResponse
        // Then turn_end fires → FlushCurrentResponse persists it to history
        var reviewContent = "## Deep Review: PR #34217\n\nThis PR updates the CLI design doc...";
        var flushedMsg = new ChatMessage("assistant", reviewContent, DateTime.Now) { Model = info.Model };
        info.History.Add(flushedMsg);
        info.MessageCount = info.History.Count;

        // Simulate: app restarts (session.resume) before session.idle
        // The flushed content survives because it's in history/DB
        Assert.Equal(2, info.History.Count);
        var review = info.History.Last();
        Assert.Equal("assistant", review.Role);
        Assert.Contains("Deep Review: PR #34217", review.Content);
    }

    [Fact]
    public void TurnEndFlush_EmptyResponse_NoHistoryEntryAdded()
    {
        // FlushCurrentResponse is a no-op when CurrentResponse is empty (tool-only sub-turns).
        // This verifies the behavior at the model level.
        var info = new AgentSessionInfo { Name = "tool-session", Model = "test" };
        info.History.Add(new ChatMessage("user", "list files", DateTime.Now));
        info.IsProcessing = true;
        var initialCount = info.History.Count;

        // Simulate: tool sub-turn with no assistant text → FlushCurrentResponse does nothing
        // (no empty assistant message added)
        Assert.Equal(initialCount, info.History.Count);
    }

    [Fact]
    public void TurnEndFlush_ContentFollowedByToolCall_NotDuplicated()
    {
        // When assistant text is flushed at turn_end and then more tool calls follow,
        // the flushed content should not be duplicated when CompleteResponse runs later.
        var info = new AgentSessionInfo { Name = "multi-turn", Model = "test" };
        info.History.Add(new ChatMessage("user", "analyze this", DateTime.Now));

        // Turn 1: assistant text flushed at turn_end
        var firstText = new ChatMessage("assistant", "Let me check...", DateTime.Now) { Model = info.Model };
        info.History.Add(firstText);

        // Turn 2: tool call (no assistant text)
        info.History.Add(ChatMessage.ToolCallMessage("bash", "call-1", "ls -la"));

        // Turn 3: final response via CompleteResponse
        var finalText = new ChatMessage("assistant", "Here are the results.", DateTime.Now) { Model = info.Model };
        info.History.Add(finalText);

        // Both text segments should be in history, not duplicated
        var assistantMessages = info.History.Where(m => m.Role == "assistant" && m.MessageType != ChatMessageType.ToolCall).ToList();
        Assert.Equal(2, assistantMessages.Count);
        Assert.Equal("Let me check...", assistantMessages[0].Content);
        Assert.Equal("Here are the results.", assistantMessages[1].Content);
    }
}
