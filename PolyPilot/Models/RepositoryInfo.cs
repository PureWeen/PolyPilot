using System.Text.Json.Serialization;

namespace PolyPilot.Models;

/// <summary>
/// A tracked git repository managed as a bare clone.
/// </summary>
public class RepositoryInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>Path to the bare clone (e.g. ~/.polypilot/repos/PureWeen-PolyPilot.git)</summary>
    public string BareClonePath { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// An active git worktree associated with a repository.
/// </summary>
public class WorktreeInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string RepoId { get; set; } = "";
    public string Branch { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>Session name using this worktree as CWD, if any.</summary>
    public string? SessionName { get; set; }
    /// <summary>GitHub PR number if this worktree was created from a PR.</summary>
    public int? PrNumber { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Persisted state for all tracked repositories and worktrees.
/// </summary>
public class RepositoryState
{
    public List<RepositoryInfo> Repositories { get; set; } = new();
    public List<WorktreeInfo> Worktrees { get; set; } = new();
}
