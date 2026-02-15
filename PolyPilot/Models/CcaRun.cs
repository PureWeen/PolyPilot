namespace PolyPilot.Models;

/// <summary>
/// A CCA (Copilot Coding Agent) workflow run from GitHub Actions.
/// </summary>
public class CcaRun
{
    public long Id { get; set; }
    public string DisplayTitle { get; set; } = "";
    public string HeadBranch { get; set; } = "";
    public string Status { get; set; } = "";  // "in_progress", "completed", "queued"
    public string? Conclusion { get; set; }    // "success", "failure", null (if in progress)
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string HtmlUrl { get; set; } = "";

    public bool IsActive => Status == "in_progress" || Status == "queued";
}
