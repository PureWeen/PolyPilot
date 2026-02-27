using System.Collections.Concurrent;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the PendingReasoningMessages dedup mechanism that prevents
/// duplicate reasoning ChatMessages when rapid deltas arrive before
/// InvokeOnUI fires.
/// </summary>
public class ReasoningDedupTests
{
    [Fact]
    public void PendingReasoningMessages_PreventsDuplicateCreation()
    {
        // Simulates the race: two rapid deltas for the same reasoningId
        // arrive before InvokeOnUI adds the first to History.
        var pending = new ConcurrentDictionary<string, ChatMessage>();
        var history = new List<ChatMessage>();
        var reasoningId = "reason-1";

        // First delta: not in pending, not in history → create new
        var msg1 = pending.GetValueOrDefault(reasoningId)
            ?? history.LastOrDefault(m => m.MessageType == ChatMessageType.Reasoning && m.ReasoningId == reasoningId);

        Assert.Null(msg1); // nothing found → would create new

        var newMsg = ChatMessage.ReasoningMessage(reasoningId);
        pending[reasoningId] = newMsg;

        // Second delta: found in pending map → reuse existing
        var msg2 = pending.GetValueOrDefault(reasoningId)
            ?? history.LastOrDefault(m => m.MessageType == ChatMessageType.Reasoning && m.ReasoningId == reasoningId);

        Assert.NotNull(msg2);
        Assert.Same(newMsg, msg2); // Same object, no duplicate

        // Simulate InvokeOnUI completing: move to history, remove from pending
        history.Add(newMsg);
        pending.TryRemove(reasoningId, out _);

        // Third delta: now found in history → still no duplicate
        var msg3 = pending.GetValueOrDefault(reasoningId)
            ?? history.LastOrDefault(m => m.MessageType == ChatMessageType.Reasoning && m.ReasoningId == reasoningId);

        Assert.NotNull(msg3);
        Assert.Same(newMsg, msg3);
        Assert.Single(history); // Only one message ever created
    }

    [Fact]
    public void PendingReasoningMessages_DifferentIds_CreateSeparateMessages()
    {
        var pending = new ConcurrentDictionary<string, ChatMessage>();

        var msg1 = ChatMessage.ReasoningMessage("reason-1");
        var msg2 = ChatMessage.ReasoningMessage("reason-2");
        pending["reason-1"] = msg1;
        pending["reason-2"] = msg2;

        Assert.Equal(2, pending.Count);
        Assert.NotSame(pending["reason-1"], pending["reason-2"]);
    }

    [Fact]
    public void PendingReasoningMessages_ClearedOnNewTurn()
    {
        // Verify that pending map is properly clearable (as done in SendPromptAsync/CompleteResponse)
        var pending = new ConcurrentDictionary<string, ChatMessage>();
        pending["reason-1"] = ChatMessage.ReasoningMessage("reason-1");
        pending["reason-2"] = ChatMessage.ReasoningMessage("reason-2");

        Assert.Equal(2, pending.Count);

        pending.Clear();

        Assert.Empty(pending);
    }

    [Fact]
    public void PendingReasoningMessages_ConcurrentAccess_IsThreadSafe()
    {
        // ConcurrentDictionary should handle simultaneous reads/writes without corruption
        var pending = new ConcurrentDictionary<string, ChatMessage>();
        var reasoningId = "reason-concurrent";

        // Simulate 10 concurrent deltas for the same reasoningId
        var results = new ConcurrentBag<ChatMessage>();
        Parallel.For(0, 10, _ =>
        {
            var existing = pending.GetOrAdd(reasoningId, _ => ChatMessage.ReasoningMessage(reasoningId));
            results.Add(existing);
        });

        // Only one entry in the dictionary
        Assert.Single(pending);
        // All 10 callers got the same object reference
        var first = results.First();
        Assert.All(results, r => Assert.Same(first, r));
    }
}
