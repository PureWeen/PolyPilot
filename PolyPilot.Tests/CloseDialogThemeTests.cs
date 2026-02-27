using System.Text.RegularExpressions;

namespace PolyPilot.Tests;

/// <summary>
/// Regression tests for the close session dialog theming bug:
/// The JS-rendered close session dialog used hardcoded dark-theme colors
/// instead of CSS variables, making it look wrong in light themes.
/// </summary>
public class CloseDialogThemeTests
{
    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new DirectoryNotFoundException("Could not find repo root (PolyPilot.slnx not found)");
    }

    private static string AppCssPath => Path.Combine(GetRepoRoot(), "PolyPilot", "wwwroot", "app.css");

    /// <summary>
    /// Extracts the CSS block for a given selector from the CSS content.
    /// Returns the content between the opening { and closing }.
    /// </summary>
    private static string? ExtractCssBlock(string css, string selector)
    {
        var escaped = Regex.Escape(selector);
        var pattern = new Regex(escaped + @"\s*\{([^}]*)\}", RegexOptions.Singleline);
        var match = pattern.Match(css);
        return match.Success ? match.Groups[1].Value : null;
    }

    [Fact]
    public void CloseDialog_Background_UsesThemeVariable()
    {
        var css = File.ReadAllText(AppCssPath);
        var block = ExtractCssBlock(css, ".js-close-dialog");
        Assert.NotNull(block);
        Assert.Contains("var(--bg-secondary)", block);
        Assert.DoesNotContain("#2a2a3e", block);
    }

    [Fact]
    public void CloseDialog_Border_UsesThemeVariable()
    {
        var css = File.ReadAllText(AppCssPath);
        var block = ExtractCssBlock(css, ".js-close-dialog");
        Assert.NotNull(block);
        Assert.Contains("var(--border-accent)", block);
        Assert.DoesNotContain("rgba(124, 92, 252", block);
    }

    [Fact]
    public void CloseDialog_Title_UsesThemeVariable()
    {
        var css = File.ReadAllText(AppCssPath);
        var block = ExtractCssBlock(css, ".js-close-dialog-title");
        Assert.NotNull(block);
        Assert.Contains("var(--text-bright)", block);
        Assert.DoesNotContain("color: #fff", block);
    }

    [Fact]
    public void CloseDialog_Button_UsesThemeVariable()
    {
        var css = File.ReadAllText(AppCssPath);
        var block = ExtractCssBlock(css, ".js-close-dialog-btn");
        Assert.NotNull(block);
        Assert.Contains("var(--accent-error)", block);
        Assert.DoesNotContain("#ff6b6b", block);
    }

    [Fact]
    public void CloseDialog_BoxShadow_DoesNotUseHardcodedPurple()
    {
        var css = File.ReadAllText(AppCssPath);
        var block = ExtractCssBlock(css, ".js-close-dialog");
        Assert.NotNull(block);
        Assert.DoesNotContain("rgba(124, 92, 252", block);
    }
}
