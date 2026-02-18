using System.Text.Json.Serialization;

namespace PolyPilot.Models;

public class SessionGroup
{
    public const string DefaultId = "_default";
    public const string DefaultName = "Sessions";

    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsCollapsed { get; set; }
    /// <summary>If set, this group auto-tracks a repository managed by RepoManager.</summary>
    public string? RepoId { get; set; }

    /// <summary>When true, this group operates as a multi-agent orchestration group.</summary>
    public bool IsMultiAgent { get; set; }

    /// <summary>The orchestration mode for multi-agent groups.</summary>
    public MultiAgentMode OrchestratorMode { get; set; } = MultiAgentMode.Broadcast;

    /// <summary>Optional system prompt appended to all sessions in this multi-agent group.</summary>
    public string? OrchestratorPrompt { get; set; }

    /// <summary>Default model for new worker sessions added to this group. Null = use app default.</summary>
    public string? DefaultWorkerModel { get; set; }

    /// <summary>Default model for the orchestrator role. Null = use app default.</summary>
    public string? DefaultOrchestratorModel { get; set; }

    /// <summary>Active reflection state for OrchestratorReflect mode. Null when not in a reflect loop.</summary>
    public GroupReflectionState? ReflectionState { get; set; }
}

public class SessionMeta
{
    public string SessionName { get; set; } = "";
    public string GroupId { get; set; } = SessionGroup.DefaultId;
    public bool IsPinned { get; set; }
    public int ManualOrder { get; set; }
    /// <summary>Worktree ID if this session was created from a worktree.</summary>
    public string? WorktreeId { get; set; }

    /// <summary>Role of this session within a multi-agent group.</summary>
    public MultiAgentRole Role { get; set; } = MultiAgentRole.Worker;

    /// <summary>
    /// Preferred model for this session in multi-agent context.
    /// Null = use whatever model the session was created with (no override).
    /// When set, the model is switched before dispatch via EnsureSessionModelAsync.
    /// </summary>
    public string? PreferredModel { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SessionSortMode
{
    LastActive,
    CreatedAt,
    Alphabetical,
    Manual
}

/// <summary>How prompts are distributed in a multi-agent group.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MultiAgentMode
{
    /// <summary>Send the same prompt to all sessions simultaneously.</summary>
    Broadcast,
    /// <summary>Send the prompt to sessions one at a time in order.</summary>
    Sequential,
    /// <summary>An orchestrator session decides how to delegate work to other sessions.</summary>
    Orchestrator,
    /// <summary>Orchestrator with iterative reflection: plan→dispatch→collect→evaluate→repeat until goal met.</summary>
    OrchestratorReflect
}

/// <summary>Role of a session within a multi-agent group.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MultiAgentRole
{
    /// <summary>Regular worker session that receives prompts.</summary>
    Worker,
    /// <summary>Orchestrator session that delegates work (used in Orchestrator mode).</summary>
    Orchestrator
}

public class OrganizationState
{
    public List<SessionGroup> Groups { get; set; } = new()
    {
        new SessionGroup { Id = SessionGroup.DefaultId, Name = SessionGroup.DefaultName, SortOrder = 0 }
    };
    public List<SessionMeta> Sessions { get; set; } = new();
    public SessionSortMode SortMode { get; set; } = SessionSortMode.LastActive;
}

/// <summary>
/// Tracks iterative orchestration state for a multi-agent group in OrchestratorReflect mode.
/// The orchestrator evaluates worker results against a goal and re-dispatches until satisfied.
/// </summary>
public class GroupReflectionState
{
    public string Goal { get; set; } = "";
    public int MaxIterations { get; set; } = 5;
    public int CurrentIteration { get; set; }
    public bool IsActive { get; set; }
    public bool GoalMet { get; set; }
    public bool IsStalled { get; set; }
    public bool IsPaused { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>The orchestrator's evaluation from the last iteration.</summary>
    public string? LastEvaluation { get; set; }

    /// <summary>Per-iteration evaluation results for trend tracking.</summary>
    public List<EvaluationResult> EvaluationHistory { get; set; } = new();

    /// <summary>Optional: session name of a dedicated evaluator (different from orchestrator).</summary>
    public string? EvaluatorSession { get; set; }

    /// <summary>Auto-adjustment suggestions surfaced to the user.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<string> PendingAdjustments { get; } = new();

    /// <summary>Hash window for stall detection (last N response hashes).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    internal List<int> ResponseHashes { get; } = new();
    internal const int StallWindowSize = 3;
    internal int ConsecutiveStalls { get; set; }

    public static GroupReflectionState Create(string goal, int maxIterations = 5, string? evaluatorSession = null) => new()
    {
        Goal = goal,
        MaxIterations = maxIterations,
        IsActive = true,
        StartedAt = DateTime.Now,
        EvaluatorSession = evaluatorSession
    };

    /// <summary>Check if the latest synthesis is repeating (stall detection).</summary>
    public bool CheckStall(string synthesisResponse)
    {
        var hash = synthesisResponse.GetHashCode();
        if (ResponseHashes.Contains(hash))
        {
            ConsecutiveStalls++;
            if (ConsecutiveStalls >= 2)
            {
                IsStalled = true;
                return true;
            }
        }
        else
        {
            ConsecutiveStalls = 0;
        }
        ResponseHashes.Add(hash);
        if (ResponseHashes.Count > StallWindowSize)
            ResponseHashes.RemoveAt(0);
        return false;
    }

    /// <summary>Record an evaluation result and return the quality trend.</summary>
    public QualityTrend RecordEvaluation(int iteration, double score, string rationale, string evaluatorModel)
    {
        EvaluationHistory.Add(new EvaluationResult
        {
            Iteration = iteration,
            Score = score,
            Rationale = rationale,
            EvaluatorModel = evaluatorModel,
            Timestamp = DateTime.Now
        });

        if (EvaluationHistory.Count < 2) return QualityTrend.Stable;

        var recent = EvaluationHistory.TakeLast(3).Select(e => e.Score).ToList();
        if (recent.Count >= 2 && recent.Last() > recent[^2] + 0.1) return QualityTrend.Improving;
        if (recent.Count >= 2 && recent.Last() < recent[^2] - 0.1) return QualityTrend.Degrading;
        return QualityTrend.Stable;
    }

    public string CompletionSummary =>
        GoalMet ? $"✅ Goal met after {CurrentIteration} iteration(s)"
        : IsStalled ? $"⚠️ Stalled after {CurrentIteration} iteration(s)"
        : $"⏱️ Reached max iterations ({MaxIterations})";
}

/// <summary>Quality trend across iterations.</summary>
public enum QualityTrend { Improving, Stable, Degrading }

/// <summary>Structured evaluation result from one reflect iteration.</summary>
public class EvaluationResult
{
    public int Iteration { get; set; }
    /// <summary>Quality score 0.0-1.0.</summary>
    public double Score { get; set; }
    public string Rationale { get; set; } = "";
    public string EvaluatorModel { get; set; } = "";
    public DateTime Timestamp { get; set; }
}
