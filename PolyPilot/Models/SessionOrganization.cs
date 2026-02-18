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
    public ReflectionCycle? ReflectionState { get; set; }
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

// GroupReflectionState class removed and merged into ReflectionCycle

