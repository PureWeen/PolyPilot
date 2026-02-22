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

    /// <summary>
    /// Shared worktree for the entire multi-agent group (used as orchestrator worktree
    /// when strategy is OrchestratorIsolated or FullyIsolated).
    /// </summary>
    public string? WorktreeId { get; set; }

    /// <summary>How worktrees are allocated across sessions in this group.</summary>
    public WorktreeStrategy WorktreeStrategy { get; set; } = WorktreeStrategy.Shared;

    /// <summary>
    /// All worktree IDs created for this group (orchestrator + workers).
    /// Used by DeleteGroup for reliable cleanup even when session creation fails.
    /// </summary>
    public List<string> CreatedWorktreeIds { get; set; } = new();

    /// <summary>Active reflection state for OrchestratorReflect mode. Null when not in a reflect loop.</summary>
    public ReflectionCycle? ReflectionState { get; set; }

    /// <summary>
    /// Shared context from Squad decisions.md or similar, prepended to all worker prompts.
    /// </summary>
    public string? SharedContext { get; set; }

    /// <summary>
    /// Routing context from Squad routing.md, injected into orchestrator planning prompt.
    /// </summary>
    public string? RoutingContext { get; set; }
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

    /// <summary>
    /// System prompt / charter that defines this worker's specialization.
    /// Prepended to every task dispatched to this worker. Null = generic worker prompt.
    /// Example: "You are a security auditor. Focus on vulnerabilities, input validation, and auth flaws."
    /// </summary>
    public string? SystemPrompt { get; set; }
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

/// <summary>How worktrees are allocated across sessions in a multi-agent group.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorktreeStrategy
{
    /// <summary>All sessions share one worktree (current default behavior).</summary>
    Shared,
    /// <summary>Orchestrator gets its own worktree; all workers share a separate one.</summary>
    OrchestratorIsolated,
    /// <summary>Every session (orchestrator + each worker) gets its own worktree.</summary>
    FullyIsolated
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
    /// <summary>
    /// Repo IDs whose auto-created sidebar groups were explicitly deleted by the user.
    /// ReconcileOrganization skips these to prevent resurrection.
    /// Cleared when the repo is re-added via GetOrCreateRepoGroup with explicit=true.
    /// </summary>
    public HashSet<string> DeletedRepoGroupRepoIds { get; set; } = new();
}

// GroupReflectionState class removed and merged into ReflectionCycle

