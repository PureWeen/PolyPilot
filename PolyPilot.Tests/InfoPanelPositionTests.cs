using System.Text.RegularExpressions;
using Xunit;

namespace PolyPilot.Tests;

/// <summary>
/// Regression tests for the info panel positioning bug.
/// The ExpandedSessionView info panel (ℹ︎ next to session name) used
/// <c>right: -0.5rem</c> which caused it to extend leftward, getting
/// clipped by the parent's overflow:hidden and appearing partially
/// hidden under the sidebar. Fixed by using <c>left: 0</c> so the
/// panel extends rightward into the content area.
/// </summary>
public class InfoPanelPositionTests
{
    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new DirectoryNotFoundException("Could not find repo root");
    }

    private static string ExpandedViewCssPath =>
        Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "ExpandedSessionView.razor.css");

    private static string ExpandedViewRazorPath =>
        Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "ExpandedSessionView.razor");

    private static string? ExtractCssBlock(string css, string selector)
    {
        var escaped = Regex.Escape(selector);
        var pattern = new Regex(escaped + @"\s*\{([^}]*)\}", RegexOptions.Singleline);
        var match = pattern.Match(css);
        return match.Success ? match.Groups[1].Value : null;
    }

    [Fact]
    public void InfoPanel_UsesLeftPositioning_NotRight()
    {
        var css = File.ReadAllText(ExpandedViewCssPath);
        var block = ExtractCssBlock(css, ".info-panel");
        Assert.NotNull(block);

        // Must use left-anchored positioning to extend rightward into content area
        Assert.Contains("left:", block);
        // Must NOT use right positioning which extends leftward under the sidebar
        Assert.DoesNotContain("right:", block);
    }

    [Fact]
    public void InfoPanel_HasAbsolutePositioning()
    {
        var css = File.ReadAllText(ExpandedViewCssPath);
        var block = ExtractCssBlock(css, ".info-panel");
        Assert.NotNull(block);
        Assert.Contains("position: absolute", block);
    }

    [Fact]
    public void InfoPanel_HasSufficientZIndex()
    {
        var css = File.ReadAllText(ExpandedViewCssPath);
        var block = ExtractCssBlock(css, ".info-panel");
        Assert.NotNull(block);
        // z-index must be high enough to appear above sibling content
        var match = Regex.Match(block, @"z-index:\s*(\d+)");
        Assert.True(match.Success, "info-panel should have a z-index");
        var zIndex = int.Parse(match.Groups[1].Value);
        Assert.True(zIndex >= 100, $"z-index should be >= 100, was {zIndex}");
    }

    [Fact]
    public void InfoPopover_HasRelativePosition()
    {
        var css = File.ReadAllText(ExpandedViewCssPath);
        var block = ExtractCssBlock(css, ".info-popover");
        Assert.NotNull(block);
        Assert.Contains("position: relative", block);
    }

    [Fact]
    public void ExpandedView_HasInfoPopoverMarkup()
    {
        var razor = File.ReadAllText(ExpandedViewRazorPath);
        // The info popover should exist next to the session name
        Assert.Contains("class=\"info-popover\"", razor);
        Assert.Contains("class=\"info-trigger\"", razor);
        Assert.Contains("class=\"info-panel\"", razor);
    }
}
