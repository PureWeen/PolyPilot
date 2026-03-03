using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests that ChatDatabase handles SQLite errors gracefully instead of throwing
/// unobserved task exceptions. Covers the fix for CannotOpen/corrupt DB scenarios.
/// </summary>
public class ChatDatabaseResilienceTests : IDisposable
{
    private readonly string _tempDir;

    public ChatDatabaseResilienceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"polypilot-chatdb-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task AddMessageAsync_WithValidDb_ReturnsPositiveId()
    {
        var dbPath = Path.Combine(_tempDir, "valid.db");
        ChatDatabase.SetDbPathForTesting(dbPath);
        var db = new ChatDatabase();

        var msg = ChatMessage.UserMessage("hello");
        var id = await db.AddMessageAsync("session-1", msg);

        Assert.True(id > 0, "Should return a positive ID on success");
    }

    [Fact]
    public async Task AddMessageAsync_WithInvalidPath_ReturnsNegativeOne()
    {
        // Point to a non-existent deeply nested path that can't be created
        ChatDatabase.SetDbPathForTesting("/dev/null/impossible/path/test.db");
        var db = new ChatDatabase();
        db.ResetConnection();

        var msg = ChatMessage.UserMessage("hello");
        var id = await db.AddMessageAsync("session-1", msg);

        Assert.Equal(-1, id);
    }

    [Fact]
    public async Task BulkInsertAsync_WithInvalidPath_DoesNotThrow()
    {
        ChatDatabase.SetDbPathForTesting("/dev/null/impossible/path/test.db");
        var db = new ChatDatabase();
        db.ResetConnection();

        var messages = new List<ChatMessage> { ChatMessage.UserMessage("hello") };

        // Should not throw — exception is caught internally
        await db.BulkInsertAsync("session-1", messages);
    }

    [Fact]
    public async Task UpdateToolCompleteAsync_WithInvalidPath_DoesNotThrow()
    {
        ChatDatabase.SetDbPathForTesting("/dev/null/impossible/path/test.db");
        var db = new ChatDatabase();
        db.ResetConnection();

        await db.UpdateToolCompleteAsync("session-1", "tool-1", "result", true);
    }

    [Fact]
    public async Task UpdateReasoningContentAsync_WithInvalidPath_DoesNotThrow()
    {
        ChatDatabase.SetDbPathForTesting("/dev/null/impossible/path/test.db");
        var db = new ChatDatabase();
        db.ResetConnection();

        await db.UpdateReasoningContentAsync("session-1", "reason-1", "content", true);
    }

    [Fact]
    public async Task GetConnectionAsync_DoesNotCacheBrokenConnection()
    {
        // First: use an invalid path to trigger failure
        ChatDatabase.SetDbPathForTesting("/dev/null/impossible/path/test.db");
        var db = new ChatDatabase();
        db.ResetConnection();

        var id = await db.AddMessageAsync("session-1", ChatMessage.UserMessage("fail"));
        Assert.Equal(-1, id);

        // Now: switch to a valid path — should recover
        var dbPath = Path.Combine(_tempDir, "recovery.db");
        ChatDatabase.SetDbPathForTesting(dbPath);
        // LogError already reset _db, so no need to call ResetConnection

        var id2 = await db.AddMessageAsync("session-1", ChatMessage.UserMessage("recovered"));
        Assert.True(id2 > 0, "Should recover after switching to a valid path");
    }

    [Fact]
    public async Task RoundTrip_MessagesArePersistedAndRetrievable()
    {
        var dbPath = Path.Combine(_tempDir, "roundtrip.db");
        ChatDatabase.SetDbPathForTesting(dbPath);
        var db = new ChatDatabase();

        var msg1 = ChatMessage.UserMessage("first");
        var msg2 = ChatMessage.AssistantMessage("response");

        await db.AddMessageAsync("s1", msg1);
        await db.AddMessageAsync("s1", msg2);

        var messages = await db.GetAllMessagesAsync("s1");
        Assert.Equal(2, messages.Count);
        Assert.Equal("first", messages[0].Content);
        Assert.Equal("response", messages[1].Content);
    }

    [Fact]
    public async Task GetAllMessagesAsync_WithInvalidPath_ReturnsEmptyList()
    {
        ChatDatabase.SetDbPathForTesting("/dev/null/impossible/path/test.db");
        var db = new ChatDatabase();
        db.ResetConnection();

        var result = await db.GetAllMessagesAsync("session-1");
        Assert.Empty(result);
    }

    [Fact]
    public async Task HasMessagesAsync_WithInvalidPath_ReturnsFalse()
    {
        ChatDatabase.SetDbPathForTesting("/dev/null/impossible/path/test.db");
        var db = new ChatDatabase();
        db.ResetConnection();

        var result = await db.HasMessagesAsync("session-1");
        Assert.False(result);
    }

    [Fact]
    public async Task GetMessageCountAsync_WithInvalidPath_ReturnsZero()
    {
        ChatDatabase.SetDbPathForTesting("/dev/null/impossible/path/test.db");
        var db = new ChatDatabase();
        db.ResetConnection();

        var count = await db.GetMessageCountAsync("session-1");
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ClearSessionAsync_WithInvalidPath_DoesNotThrow()
    {
        ChatDatabase.SetDbPathForTesting("/dev/null/impossible/path/test.db");
        var db = new ChatDatabase();
        db.ResetConnection();

        await db.ClearSessionAsync("session-1");
    }
}
