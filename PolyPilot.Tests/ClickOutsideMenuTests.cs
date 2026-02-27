using System.Text.RegularExpressions;
using Xunit;

namespace PolyPilot.Tests;

/// <summary>
/// Verifies that contextual menus (session ⋯, group ⋯, card ⋯) close when clicking
/// outside, even when the menu is inside a stacking context (e.g. the sidebar with
/// position:sticky in WebKit). The fix uses a global mousedown listener in index.html
/// that programmatically clicks the overlay when the real click lands outside the
/// menu container.
/// </summary>
public class ClickOutsideMenuTests
{
    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new DirectoryNotFoundException("Could not find repo root");
    }

    // -------- Global JS click-outside handler --------

    [Fact]
    public void IndexHtml_HasGlobalClickOutsideHandler()
    {
        var html = File.ReadAllText(Path.Combine(GetRepoRoot(), "PolyPilot", "wwwroot", "index.html"));
        // Must listen for mousedown on document
        Assert.Contains("document.addEventListener('mousedown'", html);
        // Must query for overlay elements
        Assert.Contains(".menu-overlay", html);
        Assert.Contains(".group-menu-overlay", html);
    }

    [Fact]
    public void IndexHtml_ClickOutsideHandler_ChecksContainerClasses()
    {
        var html = File.ReadAllText(Path.Combine(GetRepoRoot(), "PolyPilot", "wwwroot", "index.html"));
        // Handler must check the known container wrappers
        Assert.Contains(".session-actions", html);
        Assert.Contains(".card-more-wrapper", html);
        Assert.Contains(".group-menu-wrapper", html);
    }

    // -------- Overlay markup in components --------

    [Fact]
    public void SessionListItem_HasMenuOverlay()
    {
        var razor = File.ReadAllText(Path.Combine(GetRepoRoot(),
            "PolyPilot", "Components", "Layout", "SessionListItem.razor"));
        Assert.Contains("menu-overlay", razor);
        Assert.Contains("OnCloseMenu.InvokeAsync()", razor);
    }

    [Fact]
    public void SessionCard_HasMenuOverlay()
    {
        var razor = File.ReadAllText(Path.Combine(GetRepoRoot(),
            "PolyPilot", "Components", "SessionCard.razor"));
        Assert.Contains("menu-overlay", razor);
        Assert.Contains("OnCloseMenu.InvokeAsync()", razor);
    }

    [Fact]
    public void SessionSidebar_HasGroupMenuOverlay()
    {
        var razor = File.ReadAllText(Path.Combine(GetRepoRoot(),
            "PolyPilot", "Components", "Layout", "SessionSidebar.razor"));
        Assert.Contains("group-menu-overlay", razor);
        Assert.Contains("openGroupMenuId = null", razor);
    }

    // -------- Overlay CSS is position:fixed --------

    [Fact]
    public void SessionListItem_OverlayCss_IsPositionFixed()
    {
        var css = File.ReadAllText(Path.Combine(GetRepoRoot(),
            "PolyPilot", "Components", "Layout", "SessionListItem.razor.css"));
        AssertOverlayCssCoversViewport(css, ".menu-overlay");
    }

    [Fact]
    public void SessionCard_OverlayCss_IsPositionFixed()
    {
        var css = File.ReadAllText(Path.Combine(GetRepoRoot(),
            "PolyPilot", "Components", "SessionCard.razor.css"));
        AssertOverlayCssCoversViewport(css, ".menu-overlay");
    }

    [Fact]
    public void SessionSidebar_GroupOverlayCss_IsPositionFixed()
    {
        var css = File.ReadAllText(Path.Combine(GetRepoRoot(),
            "PolyPilot", "Components", "Layout", "SessionSidebar.razor.css"));
        AssertOverlayCssCoversViewport(css, ".group-menu-overlay");
    }

    private static void AssertOverlayCssCoversViewport(string css, string selector)
    {
        // Find the rule block for the selector
        var escapedSelector = Regex.Escape(selector);
        var match = Regex.Match(css, escapedSelector + @"\s*\{([^}]+)\}");
        Assert.True(match.Success, $"Expected CSS rule for {selector}");
        var rule = match.Groups[1].Value;
        Assert.Contains("position: fixed", rule);
        Assert.Contains("z-index:", rule);
    }
}
