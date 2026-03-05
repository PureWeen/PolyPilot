using Xunit;

namespace PolyPilot.Tests;

/// <summary>
/// Verifies that right-clicking (contextual click) on a session item in the
/// sidebar opens the ⋯ actions menu. The fix adds @oncontextmenu with
/// preventDefault on the session-item div in SessionListItem.razor.
/// </summary>
public class RightClickContextMenuTests
{
    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new DirectoryNotFoundException("Could not find repo root");
    }

    [Fact]
    public void SessionListItem_HasContextMenuHandler()
    {
        var razor = File.ReadAllText(Path.Combine(GetRepoRoot(),
            "PolyPilot", "Components", "Layout", "SessionListItem.razor"));
        Assert.Contains("@oncontextmenu=", razor);
    }

    [Fact]
    public void SessionListItem_PreventsDefaultContextMenu()
    {
        var razor = File.ReadAllText(Path.Combine(GetRepoRoot(),
            "PolyPilot", "Components", "Layout", "SessionListItem.razor"));
        Assert.Contains("@oncontextmenu:preventDefault=\"true\"", razor);
    }

    [Fact]
    public void SessionListItem_HasOpenContextMenuMethod()
    {
        var razor = File.ReadAllText(Path.Combine(GetRepoRoot(),
            "PolyPilot", "Components", "Layout", "SessionListItem.razor"));
        // The handler method should invoke OnToggleMenu to open the menu
        Assert.Contains("OpenContextMenu", razor);
        Assert.Contains("OnToggleMenu.InvokeAsync()", razor);
    }

    [Fact]
    public void SessionListItem_ContextMenuOnlyOpensWhenClosed()
    {
        // The OpenContextMenu method should check !IsMenuOpen before toggling
        // to prevent closing the menu on a second right-click
        var razor = File.ReadAllText(Path.Combine(GetRepoRoot(),
            "PolyPilot", "Components", "Layout", "SessionListItem.razor"));
        Assert.Contains("!IsMenuOpen", razor);
    }

    [Fact]
    public void SessionListItem_ContextMenuOnSessionItemDiv()
    {
        // The contextmenu handler must be on the main session-item div,
        // not buried inside a child element
        var razor = File.ReadAllText(Path.Combine(GetRepoRoot(),
            "PolyPilot", "Components", "Layout", "SessionListItem.razor"));

        // Find the session-item div and verify contextmenu is nearby
        var sessionItemIndex = razor.IndexOf("class=\"session-item");
        var contextMenuIndex = razor.IndexOf("@oncontextmenu=");
        Assert.True(sessionItemIndex >= 0, "session-item div not found");
        Assert.True(contextMenuIndex >= 0, "@oncontextmenu not found");
        // The contextmenu handler should be on the same element (within ~200 chars)
        Assert.True(Math.Abs(contextMenuIndex - sessionItemIndex) < 200,
            "contextmenu handler should be on the session-item div");
    }
}
