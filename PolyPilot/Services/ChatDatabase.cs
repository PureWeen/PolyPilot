using SQLite;
using PolyPilot.Models;

namespace PolyPilot.Services;

public class ChatMessageEntity
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public string SessionId { get; set; } = "";

    public int OrderIndex { get; set; }

    public string MessageType { get; set; } = "User"; // User, Assistant, Reasoning, ToolCall, Error

    public string Content { get; set; } = "";

    public string? ToolName { get; set; }
    public string? ToolCallId { get; set; }
    public bool IsComplete { get; set; } = true;
    public bool IsSuccess { get; set; }
    public string? ReasoningId { get; set; }

    public string? Model { get; set; }

    public DateTime Timestamp { get; set; }

    // Cached rendered HTML for assistant markdown messages
    public string? RenderedHtml { get; set; }

    // Cached base64 data URI for image tool results
    public string? ImageDataUri { get; set; }

    // Original user-typed prompt before orchestration wrapper was prepended
    public string? OriginalContent { get; set; }

    public ChatMessage ToChatMessage()
    {
        var type = Enum.TryParse<ChatMessageType>(MessageType, out var mt) ? mt : ChatMessageType.User;
        var role = type == ChatMessageType.User ? "user" : "assistant";

        var msg = new ChatMessage(role, Content, Timestamp, type)
        {
            ToolName = ToolName,
            ToolCallId = ToolCallId,
            IsComplete = IsComplete,
            IsSuccess = IsSuccess,
            IsCollapsed = type is ChatMessageType.ToolCall or ChatMessageType.Reasoning,
            ReasoningId = ReasoningId,
            Model = Model,
            OriginalContent = OriginalContent
        };
        return msg;
    }

    public static ChatMessageEntity FromChatMessage(ChatMessage msg, string sessionId, int orderIndex)
    {
        return new ChatMessageEntity
        {
            SessionId = sessionId,
            OrderIndex = orderIndex,
            MessageType = msg.MessageType.ToString(),
            Content = msg.Content,
            ToolName = msg.ToolName,
            ToolCallId = msg.ToolCallId,
            IsComplete = msg.IsComplete,
            IsSuccess = msg.IsSuccess,
            ReasoningId = msg.ReasoningId,
            Timestamp = msg.Timestamp,
            Model = msg.Model,
            OriginalContent = msg.OriginalContent
        };
    }
}

public class ChatDatabase : IChatDatabase
{
    private SQLiteAsyncConnection? _db;
    private static string? _dbPath;
    private static string DbPath => _dbPath ??= GetDbPath();

    private static string GetDbPath()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
                home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(home, ".polypilot", "chat_history.db");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), ".polypilot", "chat_history.db");
        }
    }

    public ChatDatabase()
    {
    }

    private async Task<SQLiteAsyncConnection> GetConnectionAsync()
    {
        if (_db != null) return _db;

        var dir = Path.GetDirectoryName(DbPath)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new SQLiteAsyncConnection(DbPath);
        await _db.CreateTableAsync<ChatMessageEntity>();

        // Create index for fast session + order lookups
        await _db.ExecuteAsync(
            "CREATE INDEX IF NOT EXISTS idx_session_order ON ChatMessageEntity (SessionId, OrderIndex)");

        return _db;
    }

    /// <summary>
    /// Check if a session has any stored messages.
    /// </summary>
    public async Task<bool> HasMessagesAsync(string sessionId)
    {
        var db = await GetConnectionAsync();
        var count = await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM ChatMessageEntity WHERE SessionId = ?", sessionId);
        return count > 0;
    }

    /// <summary>
    /// Get total message count for a session.
    /// </summary>
    public async Task<int> GetMessageCountAsync(string sessionId)
    {
        var db = await GetConnectionAsync();
        return await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM ChatMessageEntity WHERE SessionId = ?", sessionId);
    }

    /// <summary>
    /// Load a page of messages (newest first by default, returned in chronological order).
    /// </summary>
    public async Task<List<ChatMessage>> GetMessagesAsync(string sessionId, int limit = 50, int offset = 0)
    {
        var db = await GetConnectionAsync();
        var total = await GetMessageCountAsync(sessionId);

        // We want the LAST `limit` messages starting from offset from the end
        var skipFromStart = Math.Max(0, total - offset - limit);
        var take = Math.Min(limit, total - offset);

        if (take <= 0) return new List<ChatMessage>();

        var entities = await db.QueryAsync<ChatMessageEntity>(
            "SELECT * FROM ChatMessageEntity WHERE SessionId = ? ORDER BY OrderIndex ASC LIMIT ? OFFSET ?",
            sessionId, take, skipFromStart);

        return entities.Select(e => e.ToChatMessage()).ToList();
    }

    /// <summary>
    /// Load ALL messages for a session (for smaller sessions or when needed).
    /// </summary>
    public async Task<List<ChatMessage>> GetAllMessagesAsync(string sessionId)
    {
        var db = await GetConnectionAsync();
        var entities = await db.Table<ChatMessageEntity>()
            .Where(e => e.SessionId == sessionId)
            .OrderBy(e => e.OrderIndex)
            .ToListAsync();

        return entities.Select(e => e.ToChatMessage()).ToList();
    }

    /// <summary>
    /// Append a single message to a session's history.
    /// </summary>
    public async Task<int> AddMessageAsync(string sessionId, ChatMessage msg)
    {
        var db = await GetConnectionAsync();
        var maxOrder = await db.ExecuteScalarAsync<int>(
            "SELECT COALESCE(MAX(OrderIndex), -1) FROM ChatMessageEntity WHERE SessionId = ?", sessionId);

        var entity = ChatMessageEntity.FromChatMessage(msg, sessionId, maxOrder + 1);
        await db.InsertAsync(entity);
        return entity.Id;
    }

    /// <summary>
    /// Update a tool call message when it completes.
    /// </summary>
    public async Task UpdateToolCompleteAsync(string sessionId, string toolCallId, string content, bool isSuccess)
    {
        var db = await GetConnectionAsync();
        await db.ExecuteAsync(
            "UPDATE ChatMessageEntity SET Content = ?, IsComplete = 1, IsSuccess = ? WHERE SessionId = ? AND ToolCallId = ?",
            content, isSuccess, sessionId, toolCallId);
    }

    /// <summary>
    /// Update reasoning message content (appending delta text).
    /// </summary>
    public async Task UpdateReasoningContentAsync(string sessionId, string reasoningId, string content, bool isComplete)
    {
        var db = await GetConnectionAsync();
        await db.ExecuteAsync(
            "UPDATE ChatMessageEntity SET Content = ?, IsComplete = ? WHERE SessionId = ? AND ReasoningId = ?",
            content, isComplete, sessionId, reasoningId);
    }

    /// <summary>
    /// Bulk insert messages from events.jsonl parsing (for initial migration).
    /// </summary>
    public async Task BulkInsertAsync(string sessionId, List<ChatMessage> messages)
    {
        var db = await GetConnectionAsync();

        // Clear existing messages for this session first
        await db.ExecuteAsync("DELETE FROM ChatMessageEntity WHERE SessionId = ?", sessionId);

        var entities = messages.Select((m, i) => ChatMessageEntity.FromChatMessage(m, sessionId, i)).ToList();
        await db.InsertAllAsync(entities);
    }

    /// <summary>
    /// Clear all messages for a session.
    /// </summary>
    public async Task ClearSessionAsync(string sessionId)
    {
        var db = await GetConnectionAsync();
        await db.ExecuteAsync("DELETE FROM ChatMessageEntity WHERE SessionId = ?", sessionId);
    }
}
