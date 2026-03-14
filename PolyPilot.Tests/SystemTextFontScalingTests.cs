using System.Text.RegularExpressions;

namespace PolyPilot.Tests;

/// <summary>
/// Regression tests for slash command output font scaling bug:
/// System text (.system-text) used hardcoded pixel font sizes (12px)
/// which didn't scale with Cmd+/Cmd- font size adjustments.
/// Fix: use CSS variables (rem-based) so text scales with --app-font-size.
/// </summary>
public class SystemTextFontScalingTests
{
    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new DirectoryNotFoundException("Could not find repo root (PolyPilot.slnx not found)");
    }

    private static string ChatMessageListCssPath =>
        Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "ChatMessageList.razor.css");

    /// <summary>
    /// Extracts all CSS blocks matching a given selector from the CSS content.
    /// Returns the content between the opening { and closing } for each match.
    /// </summary>
    private static List<string> ExtractCssBlocks(string css, string selectorPattern)
    {
        var pattern = new Regex(selectorPattern + @"\s*\{([^}]*)\}", RegexOptions.Singleline);
        return pattern.Matches(css).Select(m => m.Groups[1].Value).ToList();
    }

    [Fact]
    public void SystemText_UsesScalableFontSize_NotHardcodedPixels()
    {
        var css = File.ReadAllText(ChatMessageListCssPath);
        var blocks = ExtractCssBlocks(css, @"\.system-text");

        Assert.NotEmpty(blocks);
        foreach (var block in blocks)
        {
            // Extract font-size declarations from this block
            var fontSizeMatch = Regex.Match(block, @"font-size:\s*([^;]+);");
            if (fontSizeMatch.Success)
            {
                var value = fontSizeMatch.Groups[1].Value.Trim();
                Assert.DoesNotMatch(@"^\d+px$", value);
                Assert.Contains("var(--type-", value);
            }
        }
    }

    [Fact]
    public void SystemText_DefaultStyle_UsesTypeSubhead()
    {
        var css = File.ReadAllText(ChatMessageListCssPath);
        // Match the non-minimal, non-media-query system-text rule
        var blocks = ExtractCssBlocks(css, @"\.chat-message-list\.full ::deep \.system-text");

        Assert.NotEmpty(blocks);
        var mainBlock = blocks[0];
        Assert.Contains("var(--type-callout)", mainBlock);
    }

    [Fact]
    public void SystemText_MinimalStyle_UsesTypeSubhead()
    {
        var css = File.ReadAllText(ChatMessageListCssPath);
        var blocks = ExtractCssBlocks(css, @"\.chat-message-list\.full\.style-minimal ::deep \.system-text");

        Assert.NotEmpty(blocks);
        Assert.Contains("var(--type-callout)", blocks[0]);
    }

    [Fact]
    public void ReflectionText_UsesScalableFontSize_NotHardcodedPixels()
    {
        var css = File.ReadAllText(ChatMessageListCssPath);
        var blocks = ExtractCssBlocks(css, @"\.reflection-text");

        Assert.NotEmpty(blocks);
        foreach (var block in blocks)
        {
            var fontSizeMatch = Regex.Match(block, @"font-size:\s*([^;]+);");
            if (fontSizeMatch.Success)
            {
                var value = fontSizeMatch.Groups[1].Value.Trim();
                Assert.DoesNotMatch(@"^\d+px$", value);
                Assert.Contains("var(--type-", value);
            }
        }
    }

    [Fact]
    public void TypeScale_CssVariables_AreDefinedInRem()
    {
        var appCssPath = Path.Combine(GetRepoRoot(), "PolyPilot", "wwwroot", "app.css");
        var css = File.ReadAllText(appCssPath);

        // Verify the CSS variables used by system-text exist and use rem units
        Assert.Matches(@"--type-callout:\s*[\d.]+rem", css);
        Assert.Matches(@"--type-footnote:\s*[\d.]+rem", css);
    }
}
