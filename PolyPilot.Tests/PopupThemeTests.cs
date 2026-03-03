using System.Text.RegularExpressions;

namespace PolyPilot.Tests;

/// <summary>
/// Regression tests for the skills/agents/prompts/log popup theming bug:
/// The JS-rendered popups used hardcoded dark-theme colors (e.g. #1e1e2e, #cdd6f4)
/// instead of CSS classes with CSS variables, making them look wrong in non-dark themes.
/// </summary>
public class PopupThemeTests
{
    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new DirectoryNotFoundException("Could not find repo root (PolyPilot.slnx not found)");
    }

    private static string AppCssPath => Path.Combine(GetRepoRoot(), "PolyPilot", "wwwroot", "app.css");
    private static string RazorPath => Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "ExpandedSessionView.razor");

    private static string? ExtractCssBlock(string css, string selector)
    {
        var escaped = Regex.Escape(selector);
        var pattern = new Regex(escaped + @"\s*\{([^}]*)\}", RegexOptions.Singleline);
        var match = pattern.Match(css);
        return match.Success ? match.Groups[1].Value : null;
    }

    // --- CSS class existence tests ---

    [Fact]
    public void SkillsPopup_CssClass_UsesThemeVariableForBackground()
    {
        var css = File.ReadAllText(AppCssPath);
        var block = ExtractCssBlock(css, ".skills-popup");
        Assert.NotNull(block);
        Assert.Contains("var(--bg-secondary)", block);
    }

    [Fact]
    public void SkillsPopup_CssClass_UsesThemeVariableForBorder()
    {
        var css = File.ReadAllText(AppCssPath);
        var block = ExtractCssBlock(css, ".skills-popup");
        Assert.NotNull(block);
        Assert.Contains("var(--control-border)", block);
    }

    [Fact]
    public void SkillsPopupHeader_CssClass_UsesThemeVariables()
    {
        var css = File.ReadAllText(AppCssPath);
        var block = ExtractCssBlock(css, ".skills-popup-header");
        Assert.NotNull(block);
        Assert.Contains("var(--text-muted)", block);
        Assert.Contains("var(--border-subtle)", block);
    }

    [Fact]
    public void SkillsPopupRow_CssClass_UsesThemeVariables()
    {
        var css = File.ReadAllText(AppCssPath);
        var block = ExtractCssBlock(css, ".skills-popup-row");
        Assert.NotNull(block);
        Assert.Contains("var(--border-subtle)", block);
    }

    [Fact]
    public void SkillsPopupRowName_CssClass_UsesThemeVariable()
    {
        var css = File.ReadAllText(AppCssPath);
        var block = ExtractCssBlock(css, ".skills-popup-row-name");
        Assert.NotNull(block);
        Assert.Contains("var(--text-bright)", block);
    }

    [Fact]
    public void SkillsPopupRowSource_CssClass_UsesThemeVariables()
    {
        var css = File.ReadAllText(AppCssPath);
        var block = ExtractCssBlock(css, ".skills-popup-row-source");
        Assert.NotNull(block);
        Assert.Contains("var(--text-muted)", block);
        Assert.Contains("var(--bg-input)", block);
    }

    [Fact]
    public void SkillsPopupRowDesc_CssClass_UsesThemeVariable()
    {
        var css = File.ReadAllText(AppCssPath);
        var block = ExtractCssBlock(css, ".skills-popup-row-desc");
        Assert.NotNull(block);
        Assert.Contains("var(--text-secondary)", block);
    }

    [Fact]
    public void SkillsPopupClickable_CssClass_UsesThemeHoverVariable()
    {
        var css = File.ReadAllText(AppCssPath);
        // Check the hover rule exists
        var hoverPattern = new Regex(@"\.skills-popup-row--clickable:hover\s*\{([^}]*)\}", RegexOptions.Singleline);
        var match = hoverPattern.Match(css);
        Assert.True(match.Success, "Expected .skills-popup-row--clickable:hover CSS rule");
        Assert.Contains("var(--hover-bg)", match.Groups[1].Value);
    }

    // --- Razor file: no hardcoded dark colors ---

    [Theory]
    [InlineData("#1e1e2e")]  // Catppuccin dark bg
    [InlineData("#45475a")]  // Catppuccin border
    [InlineData("#313244")]  // Catppuccin row separator
    [InlineData("#cdd6f4")]  // Catppuccin text
    [InlineData("#6c7086")]  // Catppuccin muted
    [InlineData("#a6adc8")]  // Catppuccin description
    [InlineData("#89b4fa")]  // Catppuccin blue
    [InlineData("#a6e3a1")]  // Catppuccin green
    [InlineData("#f9e2af")]  // Catppuccin yellow
    [InlineData("#f38ba8")]  // Catppuccin red
    public void Razor_ShowPopupMethods_NoHardcodedColors(string hardcodedColor)
    {
        var razor = File.ReadAllText(RazorPath);

        // Extract just the four popup methods
        var methodNames = new[] { "ShowSkillsPopup", "ShowAgentsPopup", "ShowPromptsPopup", "ShowLogPopup" };
        foreach (var method in methodNames)
        {
            var startIdx = razor.IndexOf($"private async Task {method}()", StringComparison.Ordinal);
            if (startIdx < 0) continue;
            // Find the end of the method (next "private " or "}" at column 4)
            var endIdx = razor.IndexOf("\n    private ", startIdx + 1, StringComparison.Ordinal);
            if (endIdx < 0) endIdx = razor.Length;
            var methodBody = razor[startIdx..endIdx];

            Assert.DoesNotContain(hardcodedColor, methodBody,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Razor_AgentsPopup_UsesCssClasses()
    {
        var razor = File.ReadAllText(RazorPath);
        var startIdx = razor.IndexOf("private async Task ShowAgentsPopup()", StringComparison.Ordinal);
        Assert.True(startIdx >= 0, "ShowAgentsPopup method not found");
        var endIdx = razor.IndexOf("\n    private ", startIdx + 1, StringComparison.Ordinal);
        var methodBody = razor[startIdx..(endIdx < 0 ? razor.Length : endIdx)];

        Assert.Contains("skills-popup-overlay", methodBody);
        Assert.Contains("skills-popup", methodBody);
        Assert.Contains("skills-popup-header", methodBody);
        Assert.Contains("skills-popup-row", methodBody);
        Assert.Contains("skills-popup-row-name", methodBody);
        Assert.Contains("skills-popup-row-source", methodBody);
    }

    [Fact]
    public void Razor_PromptsPopup_UsesCssClasses()
    {
        var razor = File.ReadAllText(RazorPath);
        var startIdx = razor.IndexOf("private async Task ShowPromptsPopup()", StringComparison.Ordinal);
        Assert.True(startIdx >= 0, "ShowPromptsPopup method not found");
        var endIdx = razor.IndexOf("\n    private ", startIdx + 1, StringComparison.Ordinal);
        var methodBody = razor[startIdx..(endIdx < 0 ? razor.Length : endIdx)];

        Assert.Contains("skills-popup-overlay", methodBody);
        Assert.Contains("skills-popup", methodBody);
        Assert.Contains("skills-popup-header", methodBody);
        Assert.Contains("skills-popup-row--clickable", methodBody);
        Assert.Contains("skills-popup-row-name", methodBody);
    }

    [Fact]
    public void Razor_LogPopup_UsesCssClassesAndThemeVars()
    {
        var razor = File.ReadAllText(RazorPath);
        var startIdx = razor.IndexOf("private async Task ShowLogPopup()", StringComparison.Ordinal);
        Assert.True(startIdx >= 0, "ShowLogPopup method not found");
        var endIdx = razor.IndexOf("\n    private ", startIdx + 1, StringComparison.Ordinal);
        var methodBody = razor[startIdx..(endIdx < 0 ? razor.Length : endIdx)];

        Assert.Contains("skills-popup-overlay", methodBody);
        Assert.Contains("skills-popup skills-popup--wide", methodBody);
        Assert.Contains("skills-popup-header", methodBody);
        Assert.Contains("skills-popup-log-row", methodBody);
        Assert.Contains("var(--accent-primary)", methodBody);
        Assert.Contains("var(--accent-success)", methodBody);
        Assert.Contains("var(--accent-warning)", methodBody);
        Assert.Contains("var(--accent-error)", methodBody);
    }

    // --- CSS overlay class ---

    [Fact]
    public void SkillsPopupOverlay_CssClass_Exists()
    {
        var css = File.ReadAllText(AppCssPath);
        var block = ExtractCssBlock(css, ".skills-popup-overlay");
        Assert.NotNull(block);
        Assert.Contains("position: fixed", block);
        Assert.Contains("z-index: 9998", block);
    }

    [Fact]
    public void SkillsPopupLogRow_CssClass_UsesThemeVariable()
    {
        var css = File.ReadAllText(AppCssPath);
        var block = ExtractCssBlock(css, ".skills-popup-log-row");
        Assert.NotNull(block);
        Assert.Contains("var(--border-subtle)", block);
    }
}
