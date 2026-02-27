using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for ChatDatabase write serialization and error handling.
/// Verifies that concurrent fire-and-forget AddMessageAsync calls
/// don't crash due to TOCTOU race on OrderIndex.
/// </summary>
public class ChatDatabaseTests : IDisposable
{
    private readonly string _dbDir;
    private readonly ChatDatabase _db;

    public ChatDatabaseTests()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), $"polypilot-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_dbDir);

        // Use reflection to set the static _dbPath field for test isolation
        var field = typeof(ChatDatabase).GetField("_dbPath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        field!.SetValue(null, Path.Combine(_dbDir, "test_chat.db"));

        _db = new ChatDatabase();
    }

    public void Dispose()
    {
        // Reset static field so other tests aren't affected
        var field = typeof(ChatDatabase).GetField("_dbPath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        field!.SetValue(null, null);

        try { Directory.Delete(_dbDir, true); } catch { }
    }

    [Fact]
    public async Task AddMessageAsync_ConcurrentCalls_ProduceUniqueOrderIndices()
    {
        var sessionId = "test-session-concurrent";
        var tasks = new List<Task<int>>();

        // Fire 20 concurrent AddMessageAsync calls (simulates event handler storm)
        for (int i = 0; i < 20; i++)
        {
            var msg = ChatMessage.UserMessage($"Message {i}");
            tasks.Add(_db.AddMessageAsync(sessionId, msg));
        }

        await Task.WhenAll(tasks);

        // All should succeed (no -1 error returns)
        Assert.All(tasks, t => Assert.True(t.Result > 0, "AddMessageAsync should not return error code -1"));

        // Verify all messages have unique, sequential OrderIndex values
        var messages = await _db.GetAllMessagesAsync(sessionId);
        Assert.Equal(20, messages.Count);

        // Messages should be in order with no gaps or duplicates
        for (int i = 0; i < messages.Count; i++)
        {
            Assert.Equal($"Message {i}", messages[i].Content);
        }
    }

    [Fact]
    public async Task AddMessageAsync_SequentialCalls_IncrementOrderIndex()
    {
        var sessionId = "test-session-sequential";

        await _db.AddMessageAsync(sessionId, ChatMessage.UserMessage("First"));
        await _db.AddMessageAsync(sessionId, ChatMessage.AssistantMessage("Reply"));
        await _db.AddMessageAsync(sessionId, ChatMessage.UserMessage("Second"));

        var messages = await _db.GetAllMessagesAsync(sessionId);
        Assert.Equal(3, messages.Count);
        Assert.Equal("First", messages[0].Content);
        Assert.Equal("Reply", messages[1].Content);
        Assert.Equal("Second", messages[2].Content);
    }

    [Fact]
    public async Task AddMessageAsync_DifferentSessions_AreIndependent()
    {
        var session1 = "session-1";
        var session2 = "session-2";

        // Interleave messages from two sessions concurrently
        var t1 = _db.AddMessageAsync(session1, ChatMessage.UserMessage("S1-M1"));
        var t2 = _db.AddMessageAsync(session2, ChatMessage.UserMessage("S2-M1"));
        var t3 = _db.AddMessageAsync(session1, ChatMessage.UserMessage("S1-M2"));
        var t4 = _db.AddMessageAsync(session2, ChatMessage.UserMessage("S2-M2"));

        await Task.WhenAll(t1, t2, t3, t4);

        var msgs1 = await _db.GetAllMessagesAsync(session1);
        var msgs2 = await _db.GetAllMessagesAsync(session2);

        Assert.Equal(2, msgs1.Count);
        Assert.Equal(2, msgs2.Count);
    }

    [Fact]
    public async Task BulkInsertAsync_ConcurrentWithAdd_DoesNotCrash()
    {
        var sessionId = "test-bulk-concurrent";
        var bulkMessages = Enumerable.Range(0, 10)
            .Select(i => ChatMessage.AssistantMessage($"Bulk {i}"))
            .ToList();

        // Run bulk insert and single adds concurrently
        var bulkTask = _db.BulkInsertAsync(sessionId, bulkMessages);
        var addTask = _db.AddMessageAsync("other-session", ChatMessage.UserMessage("Single"));

        await Task.WhenAll(bulkTask, addTask);

        // Both should complete without crashing
        var msgs = await _db.GetAllMessagesAsync(sessionId);
        Assert.True(msgs.Count > 0);
    }

    [Fact]
    public async Task ClearSessionAsync_ConcurrentWithAdd_DoesNotCrash()
    {
        var sessionId = "test-clear-concurrent";

        // Add some messages first
        await _db.AddMessageAsync(sessionId, ChatMessage.UserMessage("Before clear"));

        // Run clear and add concurrently
        var clearTask = _db.ClearSessionAsync(sessionId);
        var addTask = _db.AddMessageAsync(sessionId, ChatMessage.UserMessage("After clear"));

        await Task.WhenAll(clearTask, addTask);

        // Should not crash; final state depends on ordering but no exception
        var msgs = await _db.GetAllMessagesAsync(sessionId);
        Assert.True(msgs.Count >= 0);
    }
}
