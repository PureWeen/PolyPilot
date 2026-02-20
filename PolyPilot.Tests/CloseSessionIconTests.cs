using Xunit;

namespace PolyPilot.Tests;

/// <summary>
/// Ensures the "Close Session" button uses a non-destructive icon (not trash/wastebasket).
/// The trash icon (🗑) implies permanent deletion, but closing a session is reversible.
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

        // The close session button must not use the trash/wastebasket emoji
        Assert.DoesNotContain("🗑", content.Substring(content.IndexOf("Close Session") - 5, 10));
    }

    [Fact]
    public void SessionListItem_CloseButton_DoesNotUseTrashIcon()
    {
        var file = Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "Layout", "SessionListItem.razor");
        var content = File.ReadAllText(file);

        Assert.DoesNotContain("🗑", content.Substring(content.IndexOf("Close Session") - 5, 10));
    }

    [Fact]
    public void SessionCard_CloseButton_UsesCloseIcon()
    {
        var file = Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "SessionCard.razor");
        var content = File.ReadAllText(file);

        Assert.Contains("✕ Close Session", content);
    }

    [Fact]
    public void SessionListItem_CloseButton_UsesCloseIcon()
    {
        var file = Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "Layout", "SessionListItem.razor");
        var content = File.ReadAllText(file);

        Assert.Contains("✕ Close Session", content);
    }
}
