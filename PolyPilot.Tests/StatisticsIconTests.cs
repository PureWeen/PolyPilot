using System.Text.RegularExpressions;
using Xunit;

namespace PolyPilot.Tests;

/// <summary>
/// Regression tests for the statistics icon dark-mode visibility bug.
/// The Statistics button in the sidebar header used a &lt;button&gt; element
/// without browser reset styles (background: none; border: none), causing
/// it to render with default browser button styling that appeared near-white
/// in dark mode, unlike the other header icons which are &lt;a&gt; elements.
/// </summary>
public class StatisticsIconTests
{
    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new DirectoryNotFoundException("Could not find repo root");
    }

    private static string SidebarCssPath =>
        Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "Layout", "SessionSidebar.razor.css");

    private static string SidebarRazorPath =>
        Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "Layout", "SessionSidebar.razor");

    private static string? ExtractCssBlock(string css, string selector)
    {
        var escaped = Regex.Escape(selector);
        var pattern = new Regex(escaped + @"\s*\{([^}]*)\}", RegexOptions.Singleline);
        var match = pattern.Match(css);
        return match.Success ? match.Groups[1].Value : null;
    }

    [Fact]
    public void HeaderIconBtn_HasBackgroundNone()
    {
        var css = File.ReadAllText(SidebarCssPath);
        var block = ExtractCssBlock(css, ".header-icon-btn");
        Assert.NotNull(block);
        Assert.Contains("background: none", block);
    }

    [Fact]
    public void HeaderIconBtn_HasBorderNone()
    {
        var css = File.ReadAllText(SidebarCssPath);
        var block = ExtractCssBlock(css, ".header-icon-btn");
        Assert.NotNull(block);
        Assert.Contains("border: none", block);
    }

    [Fact]
    public void HeaderIconBtn_HasCursorPointer()
    {
        var css = File.ReadAllText(SidebarCssPath);
        var block = ExtractCssBlock(css, ".header-icon-btn");
        Assert.NotNull(block);
        Assert.Contains("cursor: pointer", block);
    }

    [Fact]
    public void HeaderIconBtn_UsesTextDimColor()
    {
        var css = File.ReadAllText(SidebarCssPath);
        var block = ExtractCssBlock(css, ".header-icon-btn");
        Assert.NotNull(block);
        Assert.Contains("var(--text-dim)", block);
    }

    [Fact]
    public void StatisticsButton_IsButtonElement()
    {
        // The statistics icon is a <button> (not an <a>) since it opens a popup.
        // This test ensures the button exists so the CSS reset tests stay relevant.
        var content = File.ReadAllText(SidebarRazorPath);
        Assert.Contains("title=\"Statistics\"", content);
        Assert.Matches(@"<button\s[^>]*title=""Statistics""", content);
    }

    [Fact]
    public void StatisticsButton_UsesStrokeCurrentColor()
    {
        // The SVG icon must use stroke="currentColor" so it inherits the CSS color
        var content = File.ReadAllText(SidebarRazorPath);
        var buttonMatch = Regex.Match(
            content,
            @"<button\s[^>]*title=""Statistics""[^>]*>(.*?)</button>",
            RegexOptions.Singleline);
        Assert.True(buttonMatch.Success, "Statistics button markup not found");
        Assert.Contains("<svg", buttonMatch.Groups[1].Value);
        Assert.Contains("stroke=\"currentColor\"", buttonMatch.Groups[1].Value);
    }

    [Fact]
    public void HeaderIconBtn_HoverUsesTextPrimary()
    {
        var css = File.ReadAllText(SidebarCssPath);
        var block = ExtractCssBlock(css, ".header-icon-btn:hover");
        Assert.NotNull(block);
        Assert.Contains("var(--text-primary)", block);
    }
}
