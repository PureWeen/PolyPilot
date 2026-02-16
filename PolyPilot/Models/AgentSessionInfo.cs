namespace PolyPilot.Models;

public class AgentSessionInfo
{
    public required string Name { get; set; }
    public required string Model { get; set; }
    public DateTime CreatedAt { get; init; }
    public int MessageCount { get; set; }
    public bool IsProcessing { get; set; }
    public List<ChatMessage> History { get; } = new();
    public List<string> MessageQueue { get; } = new();
    
    public string? WorkingDirectory { get; set; }
    public string? GitBranch { get; set; }
    
    // For resumed sessions
    public string? SessionId { get; set; }
    public bool IsResumed { get; init; }
    
    // Timestamp of last state change (message received, turn end, etc.)
    public DateTime LastUpdatedAt { get; set; } = DateTime.Now;
    
    // Accumulated token usage across all turns
    public int TotalInputTokens { get; set; }
    public int TotalOutputTokens { get; set; }
    public int? ContextCurrentTokens { get; set; }
    public int? ContextTokenLimit { get; set; }

    /// <summary>
    /// History.Count at the time the user last viewed this session.
    /// Messages added after this count are "unread".
    /// </summary>
    public int LastReadMessageCount { get; set; }

    public int UnreadCount => Math.Max(0, 
        History.Skip(LastReadMessageCount).Count(m => m.Role == "assistant"));

    // CCA context (for sessions loaded from CCA runs)
    public long? CcaRunId { get; set; }
    public int? CcaPrNumber { get; set; }
    public string? CcaBranch { get; set; }
}
