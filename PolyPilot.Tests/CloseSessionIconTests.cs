using Xunit;

namespace PolyPilot.Tests;

/// <summary>
/// Ensures the "Close Session" button has an appropriate icon.
/// SessionCard uses ✕ (non-destructive inline close).
/// SessionListItem uses 🗑 (opens a dialog with destructive options like delete worktree/branch).
/// </summary>
public class CloseSessionIconTests
{
    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new DirectoryNotFoundException("Could not find repo root");
    }

    [Fact]
    public void SessionCard_CloseButton_DoesNotUseTrashIcon()
    {
        var file = Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "SessionCard.razor");
        var content = File.ReadAllText(file);

        // SessionCard close is a simple inline close — no destructive options
        Assert.DoesNotContain("🗑", content.Substring(content.IndexOf("Close Session") - 5, 10));
    }

    [Fact]
    public void SessionListItem_CloseButton_UsesTrashIcon()
    {
        // SessionListItem's close opens a dialog with destructive options
        // (delete worktree, delete branch) so the trash icon is appropriate.
        var file = Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "Layout", "SessionListItem.razor");
        var content = File.ReadAllText(file);

        Assert.Contains("🗑 Close Session", content);
    }

    [Fact]
    public void SessionCard_CloseButton_UsesCloseIcon()
    {
        var file = Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "SessionCard.razor");
        var content = File.ReadAllText(file);

        Assert.Contains("✕ Close Session", content);
    }

    [Fact]
    public void SessionListItem_CloseButton_IsDestructiveStyle()
    {
        // The close menu button should be marked as destructive since it can delete worktrees/branches
        var file = Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "Layout", "SessionListItem.razor");
        var content = File.ReadAllText(file);

        // Find the menu button line (contains 🗑 Close Session)
        Assert.Contains("class=\"menu-item destructive\" @onclick=\"ShowCloseConfirm\"", content);
    }
}
