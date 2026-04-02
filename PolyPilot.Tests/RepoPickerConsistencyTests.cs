using System.Text.RegularExpressions;

namespace PolyPilot.Tests;

/// <summary>
/// Regression tests for the repo picker consistency bug:
/// "New Session" (CreateSessionForm.razor) used a &lt;select&gt; dropdown for repositories,
/// while "New Multi-Agent" (SessionSidebar.razor) used a list of clickable buttons.
/// Fix: both flows now use the same &lt;select&gt; dropdown pattern with the "ns-repo-select" CSS class,
/// and auto-advance to step 2 when there's only one repository.
/// </summary>
public class RepoPickerConsistencyTests
{
    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new DirectoryNotFoundException("Could not find repo root (PolyPilot.slnx not found)");
    }

    private static string CreateSessionFormPath =>
        Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "Layout", "CreateSessionForm.razor");

    private static string SessionSidebarPath =>
        Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "Layout", "SessionSidebar.razor");

    // ── Both forms use <select> for repo picking ────────────────────────────

    [Fact]
    public void CreateSessionForm_UsesSelectDropdownForRepos()
    {
        var content = File.ReadAllText(CreateSessionFormPath);
        // The "new session" form uses <select class="ns-repo-select"> for repo picker
        Assert.Contains("ns-repo-select", content);
        Assert.Matches(new Regex(@"<select\s[^>]*class=""ns-repo-select"""), content);
    }

    [Fact]
    public void SessionSidebar_MultiAgent_UsesSelectDropdownForRepos()
    {
        var content = File.ReadAllText(SessionSidebarPath);
        // The multi-agent flow should also use <select class="ns-repo-select"> (not button list)
        Assert.Contains("ns-repo-select", content);
        // Verify the select is within the multi-agent repo picker section
        Assert.Contains("OnMultiAgentRepoChanged", content);
        Assert.Matches(new Regex(@"<select\s[^>]*class=""ns-repo-select""[^>]*@onchange=""OnMultiAgentRepoChanged"""), content);
    }

    [Fact]
    public void SessionSidebar_MultiAgent_DoesNotUseButtonListForRepos()
    {
        var content = File.ReadAllText(SessionSidebarPath);
        // The old pattern used <button class="worktree-item"> for each repo in the multi-agent picker.
        // After the fix, the shared multi-agent repo picker fragment should NOT use worktree-item buttons.
        var pickerMatch = Regex.Match(content,
            @"RenderMultiAgentRepoPicker\s*=>.*?(?=private\s+RenderFragment\s+RenderMultiAgentPresetPicker)",
            RegexOptions.Singleline);
        Assert.True(pickerMatch.Success,
            "Could not locate RenderMultiAgentRepoPicker in SessionSidebar.razor — regex pattern needs updating if helper structure changed.");
        Assert.DoesNotContain("worktree-item", pickerMatch.Value);
    }

    // ── Single-repo auto-advance ────────────────────────────────────────────

    [Fact]
    public void CreateSessionForm_ShowsLabelForSingleRepo()
    {
        var content = File.ReadAllText(CreateSessionFormPath);
        // When there's only 1 repo, CreateSessionForm shows a label instead of a dropdown
        Assert.Contains("ns-repo-label", content);
    }

    [Fact]
    public void SessionSidebar_MultiAgent_ShowsLabelForSingleRepo()
    {
        var content = File.ReadAllText(SessionSidebarPath);
        // When there's only 1 repo, the multi-agent picker should also show a label
        Assert.Contains("ns-repo-label", content);
    }

    [Fact]
    public void SessionSidebar_MultiAgent_AutoAdvancesForSingleRepo()
    {
        var content = File.ReadAllText(SessionSidebarPath);
        // StartAddMultiAgentGroup should auto-advance when Repositories.Count == 1
        Assert.Contains("Repositories.Count == 1", content);
        Assert.Contains("SelectRepoForGroup", content);
    }

    // ── Dropdown has placeholder option ─────────────────────────────────────

    [Fact]
    public void SessionSidebar_MultiAgent_DropdownHasPlaceholder()
    {
        var content = File.ReadAllText(SessionSidebarPath);
        // The dropdown should have a placeholder "Pick a repo" option
        Assert.Contains("Pick a repo", content);
    }

    // ── Handler exists for dropdown change event ────────────────────────────

    [Fact]
    public void SessionSidebar_HasOnMultiAgentRepoChangedHandler()
    {
        var content = File.ReadAllText(SessionSidebarPath);
        // The handler should look up the repo by ID and call SelectRepoForGroup
        Assert.Matches(new Regex(@"void\s+OnMultiAgentRepoChanged\s*\("), content);
    }

    // ── CreateSessionForm subscribes to RepoManager.OnStateChanged ──────────
    // Regression: adding a repo didn't update the picker because the form
    // never listened for repo state changes.

    [Fact]
    public void CreateSessionForm_ImplementsIDisposable()
    {
        var content = File.ReadAllText(CreateSessionFormPath);
        Assert.Contains("@implements IDisposable", content);
    }

    [Fact]
    public void CreateSessionForm_SubscribesToRepoManagerOnStateChanged()
    {
        var content = File.ReadAllText(CreateSessionFormPath);
        Assert.Contains("RepoManager.OnStateChanged += OnRepoStateChanged", content);
    }

    [Fact]
    public void CreateSessionForm_UnsubscribesFromRepoManagerOnStateChanged()
    {
        var content = File.ReadAllText(CreateSessionFormPath);
        Assert.Contains("RepoManager.OnStateChanged -= OnRepoStateChanged", content);
    }

    [Fact]
    public void CreateSessionForm_OnRepoStateChanged_CallsStateHasChanged()
    {
        var content = File.ReadAllText(CreateSessionFormPath);
        // The handler must invoke StateHasChanged on the UI thread
        Assert.Matches(new Regex(@"OnRepoStateChanged\(\).*InvokeAsync\(StateHasChanged\)"), content);
    }
}
