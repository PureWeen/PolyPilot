namespace PolyPilot.Models;

public class AgentSessionInfo
{
    public required string Name { get; set; }
    public required string Model { get; set; }
    /// <summary>Reasoning effort level: "low", "medium", "high", "xhigh", or null for model default.</summary>
    public string? ReasoningEffort { get; set; }
    public DateTime CreatedAt { get; set; }
    public int MessageCount { get; set; }
    public bool IsProcessing { get; set; }
    public List<ChatMessage> History { get; } = new();
    /// <summary>
    /// Guards ToArray() snapshot reads of <see cref="History"/> from background threads.
    /// Callers on background threads (e.g., <c>NeedsAttention</c>, <c>UnreadCount</c>,
    /// <c>LastUserPrompt</c>) lock on this object before calling <c>History.ToArray()</c>.
    /// <para>
    /// Write-side discipline: most <c>History.Add()</c> calls go through
    /// <c>InvokeOnUI</c> (UI thread) so they are serialized against Blazor rendering
    /// and do not race with each other. A subset (e.g., bulk session restore) runs
    /// before the UI references the session, so no reader lock is needed.
    /// The lock therefore serializes background snapshot reads against any
    /// concurrent writes; try/catch in each reader handles the residual race edge case.
    /// </para>
    /// </summary>
    public readonly object HistoryLock = new();
    public SynchronizedMessageQueue MessageQueue { get; } = new();
    
    public string? WorkingDirectory { get; set; }
    public string? GitBranch { get; set; }
    /// <summary>Worktree ID if this session was created from a worktree.</summary>
    public string? WorktreeId { get; set; }
    /// <summary>PR number associated with this session's worktree, if any.</summary>
    public int? PrNumber { get; set; }
    
    // For resumed sessions
    public string? SessionId { get; set; }
    public bool IsResumed { get; set; }
    /// <summary>
    /// When this session was recreated from an older session during restore/recovery,
    /// records the source session ID whose history was explicitly injected into this one.
    /// Persisted to active-sessions.json so merge logic can safely suppress the obsolete
    /// predecessor only when recovery actually happened.
    /// </summary>
    public string? RecoveredFromSessionId { get; set; }
    
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
                ChatMessage[] snapshot;
                lock (HistoryLock) { snapshot = History.ToArray(); }
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

    /// <summary>
    /// Number of consecutive watchdog timeouts without a successful turn completion.
    /// Incremented in the watchdog kill path (Case C); reset to 0 in CompleteResponse.
    /// When >= 3, the session is in a repeated-stuck cycle — the watchdog skips adding
    /// system messages to History (preventing unbounded history growth) and shows
    /// a stronger warning suggesting the user start a new session.
    /// </summary>
    public int ConsecutiveStuckCount { get; set; }

    /// <summary>
    /// The content of the most recent user message in this session's history, or null if none.
    /// Useful for displaying context in the session list without rendering the full history.
    /// </summary>
    public string? LastUserPrompt
    {
        get
        {
            try
            {
                ChatMessage[] snapshot;
                lock (HistoryLock) { snapshot = History.ToArray(); }
                return snapshot.LastOrDefault(m => m.IsUser)?.Content;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// True when this session is a worker in an Orchestrator or OrchestratorReflect group.
    /// Workers are driven by the orchestrator automatically — they should not show the
    /// "Awaiting your response" attention banner, as a human doesn't respond to them directly.
    /// Set by CopilotService when session roles are assigned.
    /// </summary>
    public bool IsOrchestratorWorker { get; set; }

    /// <summary>
    /// The name of the CLI subagent currently active in this session (e.g. "code-review"),
    /// or null if no subagent is active. Updated by SubagentSelectedEvent / SubagentDeselectedEvent.
    /// </summary>
    public string? ActiveAgentName { get; set; }

    /// <summary>
    /// Display name of the active subagent (e.g. "Code Review"), or null if none.
    /// </summary>
    public string? ActiveAgentDisplayName { get; set; }

    internal static readonly string[] QuestionPhrases =
    [
        "let me know", "which would you prefer", "would you like", "should i", "do you want",
        "what would you", "how would you", "please confirm", "is that correct", "does that work",
        "shall i", "which option", "what do you think", "any preference"
    ];

    /// <summary>
    /// True when the session is idle (not processing) and the last assistant message appears
    /// to be asking the user a question — indicating the agent is waiting for user input.
    /// Only triggers when there is no subsequent user message (the user hasn't replied yet).
    /// </summary>
    public bool NeedsAttention
    {
        get
        {
            if (IsProcessing) return false;
            // Workers in orchestrator groups are driven by the orchestrator, not the user.
            // They should never show "Awaiting your response".
            if (IsOrchestratorWorker) return false;
            try
            {
                ChatMessage[] snapshot;
                lock (HistoryLock) { snapshot = History.ToArray(); }
                // Find the last conversational message (user or assistant, not tool/system/diff)
                var lastConversational = snapshot.LastOrDefault(m =>
                    (m.IsUser || m.IsAssistant) &&
                    m.MessageType is ChatMessageType.User or ChatMessageType.Assistant);
                // Only trigger if the last conversational turn is from the assistant
                if (lastConversational == null || !lastConversational.IsAssistant) return false;
                var content = lastConversational.Content;
                if (string.IsNullOrEmpty(content)) return false;
                if (content.TrimEnd().EndsWith('?')) return true;
                var lower = content.ToLowerInvariant();
                foreach (var phrase in QuestionPhrases)
                    if (lower.Contains(phrase)) return true;
                return false;
            }
            catch { return false; }
        }
    }
}
