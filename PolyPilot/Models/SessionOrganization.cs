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
    /// <summary>When true, unpinned sessions within this group are collapsed into a summary row.</summary>
    public bool UnpinnedCollapsed { get; set; }
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

    /// <summary>Maximum reflection iterations for OrchestratorReflect mode. Null = default (5).</summary>
    public int? MaxReflectIterations { get; set; }

    /// <summary>
    /// GitHub Codespace name (e.g. "fuzzy-space-guide-rj7wx59jr7hp6q5").
    /// When set, this group connects sessions to copilot running inside the codespace via port-forward tunnel.
    /// </summary>
    public string? CodespaceName { get; set; }

    /// <summary>Repository slug for the codespace (e.g. "github/reflect"). Used to derive the working directory.</summary>
    public string? CodespaceRepository { get; set; }

    /// <summary>Remote port for the copilot --headless server inside the codespace. Default 4321.</summary>
    public int CodespacePort { get; set; } = 4321;

    /// <summary>True when this group is backed by a GitHub Codespace (connected via port-forward tunnel).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCodespace => !string.IsNullOrWhiteSpace(CodespaceName);

    /// <summary>Working directory inside the codespace (e.g. "/workspaces/reflect").</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CodespaceWorkingDirectory =>
        !string.IsNullOrEmpty(CodespaceRepository) ? $"/workspaces/{CodespaceRepository.Split('/').Last()}" : null;

    /// <summary>
    /// Runtime connection state for codespace groups. Not persisted.
    /// Updated by the health-check loop and displayed in the sidebar.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public CodespaceConnectionState ConnectionState { get; set; } = CodespaceConnectionState.Unknown;

    /// <summary>Whether SSH (gh cs ssh) works for this codespace. Cached at runtime to avoid slow retries.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool? SshAvailable { get; set; }

    /// <summary>Number of reconnect attempts since last successful connection. Reset on Connected.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int ReconnectAttempts { get; set; }

    /// <summary>UTC time of the last reconnect attempt. Used for UI feedback.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime? LastReconnectAttempt { get; set; }

    /// <summary>Human-readable setup/error message shown when ConnectionState is SetupRequired.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SetupMessage { get; set; }
}

/// <summary>Connection state for a codespace group, tracked at runtime by the health-check loop.</summary>
public enum CodespaceConnectionState
{
    /// <summary>Initial state before first health check.</summary>
    Unknown,
    /// <summary>Tunnel is open and copilot client is connected.</summary>
    Connected,
    /// <summary>Health check detected tunnel/client failure; attempting to reconnect.</summary>
    Reconnecting,
    /// <summary>The codespace itself is stopped — user must start it.</summary>
    CodespaceStopped,
    /// <summary>Starting the codespace via gh cs start.</summary>
    StartingCodespace,
    /// <summary>Codespace is running but copilot is not listening and SSH is unavailable. User must start copilot manually.</summary>
    WaitingForCopilot,
    /// <summary>Codespace lacks SSH — user must install SSHD feature and run copilot. One-time setup.</summary>
    SetupRequired
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

