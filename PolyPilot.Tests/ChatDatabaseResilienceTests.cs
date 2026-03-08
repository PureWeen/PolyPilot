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
    // A path that is truly impossible on ALL platforms (Windows, macOS, Linux).
    // We create a regular file, then reference a path "inside" it — no OS allows
    // creating directories underneath a regular file.
    private readonly string _impossibleDbPath;

    public ChatDatabaseResilienceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"polypilot-chatdb-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var blockerFile = Path.Combine(_tempDir, "blocker");
        File.WriteAllText(blockerFile, "x");
        _impossibleDbPath = Path.Combine(blockerFile, "sub", "test.db");
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
        ChatDatabase.SetDbPathForTesting(_impossibleDbPath);
        var db = new ChatDatabase();
        db.ResetConnection();

        var msg = ChatMessage.UserMessage("hello");
        var id = await db.AddMessageAsync("session-1", msg);

        Assert.Equal(-1, id);
    }

    [Fact]
    public async Task BulkInsertAsync_WithInvalidPath_DoesNotThrow()
    {
        ChatDatabase.SetDbPathForTesting(_impossibleDbPath);
        var db = new ChatDatabase();
        db.ResetConnection();

        var messages = new List<ChatMessage> { ChatMessage.UserMessage("hello") };

        // Should not throw — exception is caught internally
        await db.BulkInsertAsync("session-1", messages);
    }

    [Fact]
    public async Task UpdateToolCompleteAsync_WithInvalidPath_DoesNotThrow()
    {
        ChatDatabase.SetDbPathForTesting(_impossibleDbPath);
        var db = new ChatDatabase();
        db.ResetConnection();

        await db.UpdateToolCompleteAsync("session-1", "tool-1", "result", true);
    }

    [Fact]
    public async Task UpdateReasoningContentAsync_WithInvalidPath_DoesNotThrow()
    {
        ChatDatabase.SetDbPathForTesting(_impossibleDbPath);
        var db = new ChatDatabase();
        db.ResetConnection();

        await db.UpdateReasoningContentAsync("session-1", "reason-1", "content", true);
    }

    [Fact]
    public async Task GetConnectionAsync_DoesNotCacheBrokenConnection()
    {
        // First: use an invalid path to trigger failure
        ChatDatabase.SetDbPathForTesting(_impossibleDbPath);
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
        ChatDatabase.SetDbPathForTesting(_impossibleDbPath);
        var db = new ChatDatabase();
        db.ResetConnection();

        var result = await db.GetAllMessagesAsync("session-1");
        Assert.Empty(result);
    }

    [Fact]
    public async Task HasMessagesAsync_WithInvalidPath_ReturnsFalse()
    {
        ChatDatabase.SetDbPathForTesting(_impossibleDbPath);
        var db = new ChatDatabase();
        db.ResetConnection();

        var result = await db.HasMessagesAsync("session-1");
        Assert.False(result);
    }

    [Fact]
    public async Task GetMessageCountAsync_WithInvalidPath_ReturnsZero()
    {
        ChatDatabase.SetDbPathForTesting(_impossibleDbPath);
        var db = new ChatDatabase();
        db.ResetConnection();

        var count = await db.GetMessageCountAsync("session-1");
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ClearSessionAsync_WithInvalidPath_DoesNotThrow()
    {
        ChatDatabase.SetDbPathForTesting(_impossibleDbPath);
        var db = new ChatDatabase();
        db.ResetConnection();

        await db.ClearSessionAsync("session-1");
    }

    [Fact]
    public async Task AddMessageAsync_WithCorruptDbFile_ReturnsNegativeOne()
    {
        // Simulate a corrupt DB by writing garbage bytes to the DB path.
        // SQLite may throw AggregateException or NotSupportedException rather than
        // the originally-filtered SQLiteException/IOException/UnauthorizedAccessException.
        var dbPath = Path.Combine(_tempDir, "corrupt.db");
        await File.WriteAllBytesAsync(dbPath, new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x02 });

        ChatDatabase.SetDbPathForTesting(dbPath);
        var db = new ChatDatabase();
        db.ResetConnection();

        var msg = ChatMessage.UserMessage("hello");
        var id = await db.AddMessageAsync("session-1", msg);

        Assert.Equal(-1, id);
    }

    [Fact]
    public async Task BulkInsertAsync_WithCorruptDbFile_DoesNotThrow()
    {
        var dbPath = Path.Combine(_tempDir, "corrupt-bulk.db");
        await File.WriteAllBytesAsync(dbPath, new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x02 });

        ChatDatabase.SetDbPathForTesting(dbPath);
        var db = new ChatDatabase();
        db.ResetConnection();

        var messages = new List<ChatMessage> { ChatMessage.UserMessage("hello") };
        await db.BulkInsertAsync("session-1", messages);
    }

    [Fact]
    public async Task GetAllMessagesAsync_WithCorruptDbFile_ReturnsEmptyList()
    {
        var dbPath = Path.Combine(_tempDir, "corrupt-get.db");
        await File.WriteAllBytesAsync(dbPath, new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x02 });

        ChatDatabase.SetDbPathForTesting(dbPath);
        var db = new ChatDatabase();
        db.ResetConnection();

        var result = await db.GetAllMessagesAsync("session-1");
        Assert.Empty(result);
    }

    [Fact]
    public async Task HasMessagesAsync_WithCorruptDbFile_ReturnsFalse()
    {
        var dbPath = Path.Combine(_tempDir, "corrupt-has.db");
        await File.WriteAllBytesAsync(dbPath, new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x02 });

        ChatDatabase.SetDbPathForTesting(dbPath);
        var db = new ChatDatabase();
        db.ResetConnection();

        var result = await db.HasMessagesAsync("session-1");
        Assert.False(result);
    }

    [Fact]
    public async Task FireAndForget_AddMessageAsync_DoesNotCauseUnobservedTaskException()
    {
        // This test verifies the exact bug pattern: fire-and-forget calls to
        // AddMessageAsync with a broken DB must NOT produce unobserved task exceptions.
        // Before the fix, AggregateException from SQLite async internals would escape
        // the narrow catch filter and become unobserved.
        var unobservedException = false;
        void handler(object? s, UnobservedTaskExceptionEventArgs e)
        {
            unobservedException = true;
            e.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            ChatDatabase.SetDbPathForTesting(_impossibleDbPath);
            var db = new ChatDatabase();
            db.ResetConnection();

            var msg = ChatMessage.UserMessage("test");

            // Fire-and-forget — same pattern as CopilotService.Events.cs
            _ = db.AddMessageAsync("session-1", msg);
            _ = db.UpdateToolCompleteAsync("session-1", "tool-1", "result", true);
            _ = db.UpdateReasoningContentAsync("session-1", "reason-1", "content", true);

            // Give tasks time to complete and finalize
            await Task.Delay(200);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(100);

            Assert.False(unobservedException,
                "Fire-and-forget ChatDatabase calls must not produce unobserved task exceptions");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    // -----------------------------------------------------------------------
    // Missing corrupt-DB tests (PR #276 gap: only AddMessage, BulkInsert,
    // GetAllMessages, HasMessages had corrupt-file coverage)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UpdateToolCompleteAsync_WithCorruptDbFile_DoesNotThrow()
    {
        var dbPath = Path.Combine(_tempDir, "corrupt-update-tool.db");
        await File.WriteAllBytesAsync(dbPath, new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x02 });

        ChatDatabase.SetDbPathForTesting(dbPath);
        var db = new ChatDatabase();
        db.ResetConnection();

        await db.UpdateToolCompleteAsync("session-1", "tool-1", "result", true);
    }

    [Fact]
    public async Task UpdateReasoningContentAsync_WithCorruptDbFile_DoesNotThrow()
    {
        var dbPath = Path.Combine(_tempDir, "corrupt-update-reasoning.db");
        await File.WriteAllBytesAsync(dbPath, new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x02 });

        ChatDatabase.SetDbPathForTesting(dbPath);
        var db = new ChatDatabase();
        db.ResetConnection();

        await db.UpdateReasoningContentAsync("session-1", "reason-1", "content", true);
    }

    [Fact]
    public async Task GetMessagesAsync_WithCorruptDbFile_ReturnsEmptyList()
    {
        var dbPath = Path.Combine(_tempDir, "corrupt-paged.db");
        await File.WriteAllBytesAsync(dbPath, new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x02 });

        ChatDatabase.SetDbPathForTesting(dbPath);
        var db = new ChatDatabase();
        db.ResetConnection();

        var result = await db.GetMessagesAsync("session-1", limit: 10, offset: 0);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetMessageCountAsync_WithCorruptDbFile_ReturnsZero()
    {
        var dbPath = Path.Combine(_tempDir, "corrupt-count.db");
        await File.WriteAllBytesAsync(dbPath, new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x02 });

        ChatDatabase.SetDbPathForTesting(dbPath);
        var db = new ChatDatabase();
        db.ResetConnection();

        var count = await db.GetMessageCountAsync("session-1");
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ClearSessionAsync_WithCorruptDbFile_DoesNotThrow()
    {
        var dbPath = Path.Combine(_tempDir, "corrupt-clear.db");
        await File.WriteAllBytesAsync(dbPath, new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x02 });

        ChatDatabase.SetDbPathForTesting(dbPath);
        var db = new ChatDatabase();
        db.ResetConnection();

        await db.ClearSessionAsync("session-1");
    }

    // -----------------------------------------------------------------------
    // Missing paged GetMessagesAsync with invalid path
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetMessagesAsync_WithInvalidPath_ReturnsEmptyList()
    {
        ChatDatabase.SetDbPathForTesting(_impossibleDbPath);
        var db = new ChatDatabase();
        db.ResetConnection();

        var result = await db.GetMessagesAsync("session-1", limit: 10, offset: 0);
        Assert.Empty(result);
    }

    // -----------------------------------------------------------------------
    // Different failure modes: DB path replaced by a directory triggers
    // IOException or NotSupportedException — exception types that the
    // original narrow catch filter would have missed.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AddMessageAsync_WithDbReplacedByDirectory_ReturnsNegativeOne()
    {
        var dbPath = Path.Combine(_tempDir, "dir-as-db.db");
        Directory.CreateDirectory(dbPath);

        ChatDatabase.SetDbPathForTesting(dbPath);
        var db = new ChatDatabase();
        db.ResetConnection();

        var msg = ChatMessage.UserMessage("hello");
        var id = await db.AddMessageAsync("session-1", msg);

        Assert.Equal(-1, id);
    }

    // -----------------------------------------------------------------------
    // Runtime corruption tests: DB starts healthy, then gets corrupted or
    // deleted while the app is running. These simulate the real-world
    // scenarios that produce AggregateException, InvalidOperationException,
    // or IOException — the exact exception types from the crash log.
    //
    // Note: sqlite-net-pcl auto-reconnects after CloseAsync(), so we can't
    // simulate ObjectDisposedException by closing the cached connection.
    // Instead we corrupt/delete the file and reset, which forces a fresh
    // open attempt against the broken state.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AddMessageAsync_WithRuntimeDbCorruption_ReturnsNegativeOne()
    {
        // DB works initially, then gets corrupted mid-session
        var dbPath = Path.Combine(_tempDir, "runtime-corrupt.db");
        ChatDatabase.SetDbPathForTesting(dbPath);
        var db = new ChatDatabase();

        var id = await db.AddMessageAsync("s1", ChatMessage.UserMessage("before-corrupt"));
        Assert.True(id > 0);

        // Corrupt the file and reset connection to force a fresh open
        db.ResetConnection();
        await File.WriteAllBytesAsync(dbPath, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        var id2 = await db.AddMessageAsync("s1", ChatMessage.UserMessage("after-corrupt"));
        Assert.Equal(-1, id2);
    }

    [Fact]
    public async Task GetAllMessagesAsync_WithDeletedDbFile_ReturnsEmptyList()
    {
        // DB works, then the file is deleted (e.g., user cleanup, disk error)
        var dbPath = Path.Combine(_tempDir, "deleted-db.db");
        ChatDatabase.SetDbPathForTesting(dbPath);
        var db = new ChatDatabase();

        await db.AddMessageAsync("s1", ChatMessage.UserMessage("before-delete"));

        // Delete the file and its WAL/SHM siblings, then point to impossible path
        db.ResetConnection();
        foreach (var f in Directory.GetFiles(_tempDir, "deleted-db*"))
            File.Delete(f);
        ChatDatabase.SetDbPathForTesting(_impossibleDbPath);

        var result = await db.GetAllMessagesAsync("s1");
        Assert.Empty(result);
    }

    [Fact]
    public async Task HasMessagesAsync_WithDbFileReplacedByDirectory_ReturnsFalse()
    {
        // DB file replaced by a directory — triggers IOException, a type
        // outside the original narrow catch filter
        var dbPath = Path.Combine(_tempDir, "replaced-dir.db");
        ChatDatabase.SetDbPathForTesting(dbPath);
        var db = new ChatDatabase();

        await db.AddMessageAsync("s1", ChatMessage.UserMessage("before-replace"));

        db.ResetConnection();
        foreach (var f in Directory.GetFiles(_tempDir, "replaced-dir*"))
            File.Delete(f);
        Directory.CreateDirectory(dbPath);

        var result = await db.HasMessagesAsync("s1");
        Assert.False(result);
    }

    // -----------------------------------------------------------------------
    // Round-trip test for new ChatMessageEntity fields (ToolInput, ImagePath,
    // ImageDataUri, Caption) — added as part of the "new session opens empty"
    // fix to ensure the DB schema migration works correctly.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RoundTrip_ToolCallMessage_PreservesToolInput()
    {
        var dbPath = Path.Combine(_tempDir, "roundtrip-tool.db");
        ChatDatabase.SetDbPathForTesting(dbPath);
        var db = new ChatDatabase();

        var msg = ChatMessage.ToolCallMessage("bash", "call-1", "{\"command\":\"ls -la\"}");
        msg.IsComplete = true;
        msg.IsSuccess = true;
        msg.Content = "drwxr-xr-x ...";

        await db.AddMessageAsync("s1", msg);
        var messages = await db.GetAllMessagesAsync("s1");

        Assert.Single(messages);
        Assert.Equal("{\"command\":\"ls -la\"}", messages[0].ToolInput);
        Assert.Equal("bash", messages[0].ToolName);
        Assert.Equal("call-1", messages[0].ToolCallId);
    }

    [Fact]
    public async Task RoundTrip_ImageMessage_PreservesAllImageFields()
    {
        var dbPath = Path.Combine(_tempDir, "roundtrip-image.db");
        ChatDatabase.SetDbPathForTesting(dbPath);
        var db = new ChatDatabase();

        var msg = ChatMessage.ImageMessage("/path/img.png", "data:image/png;base64,abc", "A screenshot", "tc-1");

        await db.AddMessageAsync("s1", msg);
        var messages = await db.GetAllMessagesAsync("s1");

        Assert.Single(messages);
        Assert.Equal("/path/img.png", messages[0].ImagePath);
        Assert.Equal("data:image/png;base64,abc", messages[0].ImageDataUri);
        Assert.Equal("A screenshot", messages[0].Caption);
    }
}
