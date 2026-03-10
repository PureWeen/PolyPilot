using System.Text.RegularExpressions;

namespace PolyPilot.Tests;

/// <summary>
/// Regression tests for the settings page code block theming bug:
/// Inline <code> elements in the settings UI inherited Bootstrap's
/// --bs-code-color (#d63384, pinkish-red) instead of using theme-aware
/// CSS variables, making them jarring and hard to read in Dark mode.
/// The fix mirrors the approach used in ChatMessageList.razor.css where
/// code elements use var(--text-bright) and var(--border-subtle).
/// </summary>
public class SettingsCodeBlockThemeTests
{
    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new DirectoryNotFoundException("Could not find repo root (PolyPilot.slnx not found)");
    }

    private static string SettingsCssPath => Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "Pages", "Settings.razor.css");
    private static string ChatCssPath => Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "ChatMessageList.razor.css");

    private static string? ExtractCssBlock(string css, string selector)
    {
        var escaped = Regex.Escape(selector);
        var pattern = new Regex(escaped + @"\s*\{", RegexOptions.Singleline);
        var matches = pattern.Matches(css);
        if (matches.Count == 0)
            return null;

        var match = matches[matches.Count - 1];
        var blockStart = match.Index + match.Length;
        var depth = 1;

        for (var i = blockStart; i < css.Length; i++)
        {
            if (css[i] == '{')
            {
                depth++;
                continue;
            }

            if (css[i] != '}')
                continue;

            depth--;
            if (depth == 0)
                return css.Substring(blockStart, i - blockStart);
        }

        return null;
    }

    [Fact]
    public void SectionDescCode_UsesThemeVariableForColor()
    {
        var css = File.ReadAllText(SettingsCssPath);
        var block = ExtractCssBlock(css, ".section-desc code");
        Assert.NotNull(block);
        Assert.Contains("var(--text-bright)", block);
    }

    [Fact]
    public void SectionDescCode_UsesThemeVariableForBorder()
    {
        var css = File.ReadAllText(SettingsCssPath);
        var block = ExtractCssBlock(css, ".section-desc code");
        Assert.NotNull(block);
        Assert.Contains("var(--border-subtle)", block);
    }

    [Fact]
    public void SectionDescCode_DoesNotUseBootstrapRedColor()
    {
        var css = File.ReadAllText(SettingsCssPath);
        var block = ExtractCssBlock(css, ".section-desc code");
        Assert.NotNull(block);
        Assert.DoesNotContain("#d63384", block);
    }

    [Fact]
    public void TunnelWarningCode_HasExplicitColor()
    {
        var css = File.ReadAllText(SettingsCssPath);
        var block = ExtractCssBlock(css, ".tunnel-warning code");
        Assert.NotNull(block);
        Assert.Contains("color: var(--accent-warning)", block);
    }

    [Fact]
    public void TunnelUrlCode_HasExplicitColor()
    {
        var css = File.ReadAllText(SettingsCssPath);
        var block = ExtractCssBlock(css, ".tunnel-url code");
        Assert.NotNull(block);
        Assert.Contains("color: var(--accent-primary)", block);
    }

    [Fact]
    public void ChatMarkdownCode_UsesThemeVariableForColor()
    {
        var css = File.ReadAllText(ChatCssPath);
        var block = ExtractCssBlock(css, "::deep .markdown-body code");
        Assert.NotNull(block);
        Assert.Contains("var(--text-bright)", block);
    }

    [Fact]
    public void SettingsAndChat_CodeBlocks_UseSameColorVariable()
    {
        var settingsCss = File.ReadAllText(SettingsCssPath);
        var chatCss = File.ReadAllText(ChatCssPath);

        var settingsBlock = ExtractCssBlock(settingsCss, ".section-desc code");
        var chatBlock = ExtractCssBlock(chatCss, "::deep .markdown-body code");

        Assert.NotNull(settingsBlock);
        Assert.NotNull(chatBlock);

        // Both should use the same theme variable for text color
        Assert.Contains("var(--text-bright)", settingsBlock);
        Assert.Contains("var(--text-bright)", chatBlock);
    }
}
