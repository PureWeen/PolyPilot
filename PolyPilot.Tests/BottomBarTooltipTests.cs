using System.Text.RegularExpressions;
using Xunit;

namespace PolyPilot.Tests;

/// <summary>
/// Ensures all interactive buttons and informational labels at the bottom of the
/// ExpandedSessionView, SessionSidebar footer, and Dashboard toolbar have
/// title attributes (tooltip / information bubbles) for discoverability.
/// </summary>
public class BottomBarTooltipTests
{
    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new DirectoryNotFoundException("Could not find repo root");
    }

    // --- ExpandedSessionView: input-status-bar ---

    [Fact]
    public void ExpandedSessionView_SendButton_HasTitle()
    {
        var content = ReadExpandedSessionView();
        // The send button (non-stop) must have a title attribute
        Assert.Contains(@"title=""Send message""", content);
    }

    [Fact]
    public void ExpandedSessionView_ChatModeButton_HasTitle()
    {
        var content = ReadExpandedSessionView();
        Assert.Contains(@"title=""Chat mode", content);
    }

    [Fact]
    public void ExpandedSessionView_PlanModeButton_HasTitle()
    {
        var content = ReadExpandedSessionView();
        Assert.Contains(@"title=""Plan mode", content);
    }

    [Fact]
    public void ExpandedSessionView_AutopilotModeButton_HasTitle()
    {
        var content = ReadExpandedSessionView();
        Assert.Contains(@"title=""Autopilot mode", content);
    }

    [Fact]
    public void ExpandedSessionView_LogLabel_HasTitle()
    {
        var content = ReadExpandedSessionView();
        // The "Log" label must have a title attribute
        Assert.Matches(@"class=""log-label""[^>]*\btitle=""[^""]+""", content);
    }

    [Fact]
    public void ExpandedSessionView_MessageCount_HasTitle()
    {
        var content = ReadExpandedSessionView();
        Assert.Matches(@"class=""status-msgs""[^>]*\btitle=""[^""]+""", content);
    }

    [Fact]
    public void ExpandedSessionView_SkillsTrigger_HasTitle()
    {
        var content = ReadExpandedSessionView();
        Assert.Matches(@"data-trigger=""skills""[^>]*\btitle=""[^""]+""", content);
    }

    [Fact]
    public void ExpandedSessionView_AgentsTrigger_HasTitle()
    {
        var content = ReadExpandedSessionView();
        Assert.Matches(@"data-trigger=""agents""[^>]*\btitle=""[^""]+""", content);
    }

    [Fact]
    public void ExpandedSessionView_PromptsTrigger_HasTitle()
    {
        var content = ReadExpandedSessionView();
        Assert.Matches(@"data-trigger=""prompts""[^>]*\btitle=""[^""]+""", content);
    }

    [Fact]
    public void ExpandedSessionView_TokenUsage_HasTitle()
    {
        var content = ReadExpandedSessionView();
        Assert.Matches(@"class=""status-tokens""[^>]*\btitle=""[^""]+""", content);
    }

    [Fact]
    public void ExpandedSessionView_ContextUsage_HasTitle()
    {
        var content = ReadExpandedSessionView();
        Assert.Matches(@"class=""status-ctx""[^>]*\btitle=""[^""]+""", content);
    }

    // --- SessionSidebar: footer buttons ---

    [Fact]
    public void SessionSidebar_SubmitIssueButton_HasTitle()
    {
        var content = ReadSessionSidebar();
        Assert.Matches(@"SubmitBugReport[^>]*\btitle=""[^""]+""", content);
    }

    [Fact]
    public void SessionSidebar_LaunchCopilotButton_HasTitle()
    {
        var content = ReadSessionSidebar();
        Assert.Matches(@"LaunchFixIt[^>]*\btitle=""[^""]+""", content);
    }

    // --- Dashboard: expanded toolbar ---

    [Fact]
    public void Dashboard_ExpandedToolbar_SendAllButton_HasTitle()
    {
        var content = ReadDashboard();
        // The expanded toolbar Send All button (SendToExpandedMultiAgentGroup) must have a title
        Assert.Matches(@"SendToExpandedMultiAgentGroup[^>]*\btitle=""[^""]+""", content);
    }

    // --- Helpers ---

    private string ReadExpandedSessionView()
    {
        var file = Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "ExpandedSessionView.razor");
        return File.ReadAllText(file);
    }

    private string ReadSessionSidebar()
    {
        var file = Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "Layout", "SessionSidebar.razor");
        return File.ReadAllText(file);
    }

    private string ReadDashboard()
    {
        var file = Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "Pages", "Dashboard.razor");
        return File.ReadAllText(file);
    }
}
