namespace PolyPilot.Models;

public class AgentSessionInfo
{
    public required string Name { get; set; }
    public required string Model { get; set; }
    public DateTime CreatedAt { get; set; }
    public int MessageCount { get; set; }
    public bool IsProcessing { get; set; }
    public List<ChatMessage> History { get; } = new();
    public SynchronizedMessageQueue MessageQueue { get; } = new();
    
    public string? WorkingDirectory { get; set; }
    public string? GitBranch { get; set; }
    /// <summary>Worktree ID if this session was created from a worktree.</summary>
    public string? WorktreeId { get; set; }
    
    // For resumed sessions
    public string? SessionId { get; set; }
    public bool IsResumed { get; set; }
    
    // Timestamp of last state change (message received, turn end, etc.)
    // Uses Interlocked ticks pattern for thread safety (updated from background SDK event threads).
    private long _lastUpdatedAtTicks = DateTime.Now.Ticks;
    public DateTime LastUpdatedAt
    {
        get => new DateTime(Interlocked.Read(ref _lastUpdatedAtTicks));
        set => Interlocked.Exchange(ref _lastUpdatedAtTicks, value.Ticks);
    }
    
    // Processing progress tracking
    // Backing field uses Interlocked to prevent torn reads between the UI thread (writer)
    // and the background watchdog thread (reader). DateTime? is not atomic — a torn read
    // can produce HasValue=true but Value=default (0 ticks), yielding a huge elapsed time
    // and triggering a false-positive watchdog timeout.
    private long _processingStartedAtTicks;
    public DateTime? ProcessingStartedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _processingStartedAtTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
        set => Interlocked.Exchange(ref _processingStartedAtTicks, value?.Ticks ?? 0);
    }
    public int _toolCallCount;
    public int ToolCallCount { get => Volatile.Read(ref _toolCallCount); set => Volatile.Write(ref _toolCallCount, value); }
    /// <summary>
    /// Processing phase: 0=Sending, 1=ServerConnected (UsageInfo received),
    /// 2=Thinking (TurnStart), 3=Working (tools running)
    /// </summary>
    public int ProcessingPhase { get; set; }

    /// <summary>
    /// Sliding window of recent tool results (true = permission denial, false = OK).
    /// Triggers recovery when 3+ of the last 5 results are denials, which handles
    /// cases where an occasional OK tool call resets what would otherwise be a denial streak.
    /// Thread-safe via lock on the queue itself.
    /// </summary>
    private readonly Queue<bool> _recentToolResults = new(5);
    public int _permissionDenialCount; // kept for Interlocked threshold detection
    
    /// <summary>
    /// Count of denials in the sliding window. Read-only for UI binding.
    /// </summary>
    public int PermissionDenialCount => Volatile.Read(ref _permissionDenialCount);

    /// <summary>
    /// Records a tool result into the sliding window. Returns the denial count in the window.
    /// </summary>
    public int RecordToolResult(bool isPermissionDenial)
    {
        lock (_recentToolResults)
        {
            _recentToolResults.Enqueue(isPermissionDenial);
            while (_recentToolResults.Count > 5)
                _recentToolResults.Dequeue();
            var denials = 0;
            foreach (var r in _recentToolResults)
                if (r) denials++;
            Volatile.Write(ref _permissionDenialCount, denials);
            return denials;
        }
    }

    /// <summary>
    /// Clears the sliding window (on new prompt, turn completion, or recovery).
    /// </summary>
    public void ClearPermissionDenials()
    {
        lock (_recentToolResults)
        {
            _recentToolResults.Clear();
            Volatile.Write(ref _permissionDenialCount, 0);
        }
    }

    /// <summary>
    /// True when permission denials suggest the permission callback binding is lost.
    /// </summary>
    public bool HasPermissionIssue => PermissionDenialCount >= 3;
    
    // Accumulated token usage across all turns
    public int TotalInputTokens { get; set; }
    public int TotalOutputTokens { get; set; }
    public int? ContextCurrentTokens { get; set; }
    public int? ContextTokenLimit { get; set; }

    /// <summary>
    /// Estimated number of premium requests used this session.
    /// Incremented on each AssistantTurnEndEvent (one per model invocation).
    /// </summary>
    public int PremiumRequestsUsed { get; set; }

    /// <summary>
    /// Total wall-clock seconds spent waiting for model responses (API time).
    /// Accumulated from ProcessingStartedAt on each turn completion.
    /// </summary>
    public double TotalApiTimeSeconds { get; set; }

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

    /// <summary>
    /// True while the SDK session is being created. The session appears in the UI
    /// immediately (optimistic add) but cannot accept prompts until creation completes.
    /// </summary>
    public bool IsCreating { get; set; }
}
