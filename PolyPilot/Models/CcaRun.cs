namespace PolyPilot.Models;

/// <summary>
/// A CCA (Copilot Coding Agent) workflow run from GitHub Actions.
/// </summary>
public class CcaRun
{
    public long Id { get; set; }
    public string Name { get; set; } = "";         // workflow name, e.g. "Running Copilot coding agent"
    public string Event { get; set; } = "";        // trigger event, e.g. "dynamic"
    public string DisplayTitle { get; set; } = "";
    public string HeadBranch { get; set; } = "";
    public string Status { get; set; } = "";  // "in_progress", "completed", "queued"
    public string? Conclusion { get; set; }    // "success", "failure", null (if in progress)
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string HtmlUrl { get; set; } = "";

    public bool IsActive => Status == "in_progress" || Status == "queued";

    /// <summary>
    /// True if this is a coding agent run (creates branches, pushes code, opens PRs).
    /// False if this is a comment-response run (e.g. "Addressing comment on PR #123").
    /// </summary>
    public bool IsCodingAgent => Name.StartsWith("Running Copilot coding agent", StringComparison.OrdinalIgnoreCase);
}
