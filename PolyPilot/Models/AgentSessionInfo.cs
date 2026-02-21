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
    public bool IsResumed { get; set; }
    
    // Timestamp of last state change (message received, turn end, etc.)
    public DateTime LastUpdatedAt { get; set; } = DateTime.Now;
    
    // Processing progress tracking
    public DateTime? ProcessingStartedAt { get; set; }
    public int ToolCallCount { get; set; }
    /// <summary>
    /// Processing phase: 0=Sending, 1=ServerConnected (UsageInfo received),
    /// 2=Thinking (TurnStart), 3=Working (tools running)
    /// </summary>
    public int ProcessingPhase { get; set; }
    
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

    public int UnreadCount
    {
        get
        {
            try
            {
                // Snapshot to avoid collection-modified exceptions from background threads
                var snapshot = History.ToArray();
                return Math.Max(0,
                    snapshot.Skip(LastReadMessageCount).Count(m => m?.Role == "assistant"));
            }
            catch
            {
                return 0;
            }
        }
    }

    // Reflection cycle for iterative goal-driven refinement
    public ReflectionCycle? ReflectionCycle { get; set; }

    /// <summary>
    /// Hidden sessions are not shown in the sidebar (e.g., evaluator sessions).
    /// </summary>
    public bool IsHidden { get; set; }
}
