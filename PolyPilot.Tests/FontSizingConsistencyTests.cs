using System.Text.RegularExpressions;

namespace PolyPilot.Tests;

/// <summary>
/// Regression tests for the font sizing inconsistency bug:
/// Multiple CSS files used hardcoded pixel font sizes (e.g. 10px, 11px, 12px, 13px, 15px, 16px)
/// that did not scale when the user adjusts the app font size via setAppFontSize().
/// Scattered monospace font stacks were also duplicated across files instead of using --font-mono.
/// Fix: replace all hardcoded px font-size values with CSS type-scale variables,
/// add --font-mono and --font-base to :root, and use them consistently.
/// </summary>
public class FontSizingConsistencyTests
{
    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new DirectoryNotFoundException("Could not find repo root (PolyPilot.slnx not found)");
    }

    private static string AppCssPath =>
        Path.Combine(GetRepoRoot(), "PolyPilot", "wwwroot", "app.css");

    private static string ChatMessageListCssPath =>
        Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "ChatMessageList.razor.css");

    private static string ExpandedSessionViewCssPath =>
        Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "ExpandedSessionView.razor.css");

    private static string SessionCardCssPath =>
        Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "SessionCard.razor.css");

    private static string DiffViewCssPath =>
        Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "DiffView.razor.css");

    // ── Type-scale variable definitions ──────────────────────────────────────

    [Fact]
    public void AppCss_DefinesFontMonoVariable()
    {
        var css = File.ReadAllText(AppCssPath);
        Assert.Matches(@"--font-mono\s*:", css);
    }

    [Fact]
    public void AppCss_DefinesFontBaseVariable()
    {
        var css = File.ReadAllText(AppCssPath);
        Assert.Matches(@"--font-base\s*:", css);
    }

    [Fact]
    public void AppCss_HtmlBody_UsesFontBaseVariable()
    {
        var css = File.ReadAllText(AppCssPath);
        // The html,body rule should use var(--font-base) instead of a hardcoded font stack
        var htmlBodyPattern = new Regex(@"html\s*,\s*body\s*\{([^}]*)\}", RegexOptions.Singleline);
        var match = htmlBodyPattern.Match(css);
        Assert.True(match.Success, "Could not find html, body rule");
        Assert.Contains("var(--font-base)", match.Groups[1].Value);
    }

    // ── No hardcoded px font sizes ────────────────────────────────────────────

    [Fact]
    public void AppCss_SkillsPopupHeader_UsesTypeVariable_NotPx()
    {
        var css = File.ReadAllText(AppCssPath);
        var block = ExtractCssBlock(css, @"\.skills-popup-header");
        Assert.NotNull(block);
        AssertNoHardcodedPxFontSize(block, ".skills-popup-header");
        Assert.Contains("var(--type-", block);
    }

    [Fact]
    public void AppCss_SkillsPopupRowTitle_UsesTypeVariable_NotPx()
    {
        var css = File.ReadAllText(AppCssPath);
        var block = ExtractCssBlock(css, @"\.skills-popup-row-title");
        Assert.NotNull(block);
        AssertNoHardcodedPxFontSize(block, ".skills-popup-row-title");
        Assert.Contains("var(--type-", block);
    }

    [Fact]
    public void AppCss_SkillsPopupRowSource_UsesTypeVariable_NotPx()
    {
        var css = File.ReadAllText(AppCssPath);
        var block = ExtractCssBlock(css, @"\.skills-popup-row-source");
        Assert.NotNull(block);
        AssertNoHardcodedPxFontSize(block, ".skills-popup-row-source");
        Assert.Contains("var(--type-", block);
    }

    [Fact]
    public void AppCss_SkillsPopupRowDesc_UsesTypeVariable_NotPx()
    {
        var css = File.ReadAllText(AppCssPath);
        var block = ExtractCssBlock(css, @"\.skills-popup-row-desc");
        Assert.NotNull(block);
        AssertNoHardcodedPxFontSize(block, ".skills-popup-row-desc");
        Assert.Contains("var(--type-", block);
    }

    [Fact]
    public void AppCss_SkillsPopupLogRow_UsesTypeVariable_NotPx()
    {
        var css = File.ReadAllText(AppCssPath);
        var block = ExtractCssBlock(css, @"\.skills-popup-log-row");
        Assert.NotNull(block);
        AssertNoHardcodedPxFontSize(block, ".skills-popup-log-row");
        Assert.Contains("var(--type-", block);
    }

    [Fact]
    public void AppCss_SkillsPopupLogRow_UsesFontMonoVariable()
    {
        var css = File.ReadAllText(AppCssPath);
        var block = ExtractCssBlock(css, @"\.skills-popup-log-row");
        Assert.NotNull(block);
        Assert.Contains("var(--font-mono)", block);
    }

    [Fact]
    public void AppCss_ReflectionPillSmall_UsesTypeVariable_NotPx()
    {
        var css = File.ReadAllText(AppCssPath);
        var block = ExtractCssBlock(css, @"\.reflection-pill-small");
        Assert.NotNull(block);
        AssertNoHardcodedPxFontSize(block, ".reflection-pill-small");
        Assert.Contains("var(--type-", block);
    }

    [Fact]
    public void ChatMessageList_CompactRole_UsesTypeVariable_NotPx()
    {
        var css = File.ReadAllText(ChatMessageListCssPath);
        // The compact chat-msg-role badge used 10px — must now use a CSS variable
        var blocks = ExtractCssBlocks(css, @"\.chat-message-list\.compact ::deep \.chat-msg-role");
        Assert.NotEmpty(blocks);
        foreach (var block in blocks)
            AssertNoHardcodedPxFontSize(block, ".chat-message-list.compact ::deep .chat-msg-role");
    }

    [Fact]
    public void ChatMessageList_MsgTime_UsesTypeVariable_NotPx()
    {
        var css = File.ReadAllText(ChatMessageListCssPath);
        // .chat-msg-time used hardcoded 16px; must now use a CSS variable
        var blocks = ExtractCssBlocks(css, @"::deep \.chat-msg-time");
        Assert.NotEmpty(blocks);
        foreach (var block in blocks)
            AssertNoHardcodedPxFontSize(block, "::deep .chat-msg-time");
    }

    [Fact]
    public void ChatMessageList_UserAvatar_UsesTypeVariable_NotPx()
    {
        var css = File.ReadAllText(ChatMessageListCssPath);
        // User avatar circle used 16px; must now use a CSS variable
        var blocks = ExtractCssBlocks(css, @"\.chat-message-list\.full ::deep \.chat-msg\.user \.chat-msg-avatar");
        Assert.NotEmpty(blocks);
        foreach (var block in blocks)
            AssertNoHardcodedPxFontSize(block, ".chat-msg.user .chat-msg-avatar");
    }

    [Fact]
    public void SessionCard_AttachmentRemove_UsesTypeVariable_NotPx()
    {
        var css = File.ReadAllText(SessionCardCssPath);
        var block = ExtractCssBlock(css, @"\.attachment-remove");
        Assert.NotNull(block);
        AssertNoHardcodedPxFontSize(block, ".attachment-remove (SessionCard)");
        Assert.Contains("var(--type-", block);
    }

    [Fact]
    public void SessionCard_UnreadBadge_UsesTypeVariable_NotPx()
    {
        var css = File.ReadAllText(SessionCardCssPath);
        var block = ExtractCssBlock(css, @"\.unread-badge");
        Assert.NotNull(block);
        AssertNoHardcodedPxFontSize(block, ".unread-badge");
        Assert.Contains("var(--type-", block);
    }

    [Fact]
    public void ExpandedSessionView_AttachmentRemove_UsesTypeVariable_NotPx()
    {
        var css = File.ReadAllText(ExpandedSessionViewCssPath);
        var block = ExtractCssBlock(css, @"\.attachment-remove");
        Assert.NotNull(block);
        AssertNoHardcodedPxFontSize(block, ".attachment-remove (ExpandedSessionView)");
        Assert.Contains("var(--type-", block);
    }

    // ── Monospace font family consistency ─────────────────────────────────────

    [Fact]
    public void DiffViewer_UsesFontMonoVariable()
    {
        var css = File.ReadAllText(DiffViewCssPath);
        var block = ExtractCssBlock(css, @"\.diff-viewer");
        Assert.NotNull(block);
        Assert.Contains("var(--font-mono)", block);
        Assert.DoesNotContain("'SF Mono'", block);
    }

    [Fact]
    public void ChatMessageList_ShellOutputText_UsesFontMonoVariable()
    {
        var css = File.ReadAllText(ChatMessageListCssPath);
        var blocks = ExtractCssBlocks(css, @"\.shell-output-text");
        Assert.NotEmpty(blocks);
        foreach (var block in blocks)
        {
            if (Regex.IsMatch(block, @"font-family"))
                Assert.Contains("var(--font-mono)", block);
        }
    }

    [Fact]
    public void ChatMessageList_ActionDesc_UsesFontMonoVariable()
    {
        var css = File.ReadAllText(ChatMessageListCssPath);
        var blocks = ExtractCssBlocks(css, @"::deep \.action-desc");
        Assert.NotEmpty(blocks);
        foreach (var block in blocks)
        {
            if (Regex.IsMatch(block, @"font-family"))
                Assert.Contains("var(--font-mono)", block);
        }
    }

    [Fact]
    public void ChatMessageList_ActionFullInputPre_UsesFontMonoVariable()
    {
        var css = File.ReadAllText(ChatMessageListCssPath);
        var blocks = ExtractCssBlocks(css, @"::deep \.action-full-input pre");
        Assert.NotEmpty(blocks);
        foreach (var block in blocks)
        {
            if (Regex.IsMatch(block, @"font-family"))
                Assert.Contains("var(--font-mono)", block);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AssertNoHardcodedPxFontSize(string cssBlock, string context)
    {
        var fontSizeMatch = Regex.Match(cssBlock, @"font-size\s*:\s*([^;]+);");
        if (fontSizeMatch.Success)
        {
            var value = fontSizeMatch.Groups[1].Value.Trim();
            Assert.False(
                Regex.IsMatch(value, @"^\d+px$"),
                $"CSS block '{context}' uses hardcoded px font-size '{value}' — use a CSS type-scale variable (var(--type-*)) instead so it scales with the user's font size setting.");
        }
    }

    private static string? ExtractCssBlock(string css, string selectorPattern)
    {
        var pattern = new Regex(selectorPattern + @"\s*\{([^}]*)\}", RegexOptions.Singleline);
        var match = pattern.Match(css);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static List<string> ExtractCssBlocks(string css, string selectorPattern)
    {
        var pattern = new Regex(selectorPattern + @"\s*\{([^}]*)\}", RegexOptions.Singleline);
        return pattern.Matches(css).Select(m => m.Groups[1].Value).ToList();
    }
}
