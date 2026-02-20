using System.Text.RegularExpressions;

namespace PolyPilot.Tests;

/// <summary>
/// Validates that the slash command autocomplete list in index.html stays in sync
/// with the actual slash command handler in Dashboard.razor and the /help output.
/// </summary>
public class SlashCommandAutocompleteTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string IndexHtmlPath = Path.Combine(
        RepoRoot, "PolyPilot", "wwwroot", "index.html");

    private static readonly string DashboardPath = Path.Combine(
        RepoRoot, "PolyPilot", "Components", "Pages", "Dashboard.razor");

    /// <summary>
    /// Extract command names from the JS COMMANDS array in index.html.
    /// </summary>
    private static HashSet<string> GetAutocompleteCommands()
    {
        var html = File.ReadAllText(IndexHtmlPath);
        // Match: { cmd: '/help', desc: '...' }
        var matches = Regex.Matches(html, @"cmd:\s*'(/\w+)'");
        return matches.Select(m => m.Groups[1].Value).ToHashSet();
    }

    /// <summary>
    /// Extract command names from the switch cases in HandleSlashCommand.
    /// Only captures the top-level switch (before any nested private method).
    /// </summary>
    private static HashSet<string> GetHandlerCommands()
    {
        var razor = File.ReadAllText(DashboardPath);
        var handleMethodStart = razor.IndexOf("private async Task HandleSlashCommand", StringComparison.Ordinal);
        if (handleMethodStart < 0) throw new InvalidOperationException("HandleSlashCommand not found");
        var section = razor.Substring(handleMethodStart);
        // Stop at the next private method definition to avoid sub-command switch blocks
        var nextMethod = Regex.Match(section.Substring(1), @"\n\s+private\s+");
        if (nextMethod.Success)
            section = section.Substring(0, nextMethod.Index + 1);
        var matches = Regex.Matches(section, @"case\s+""(\w+)"":");
        return matches.Select(m => "/" + m.Groups[1].Value).ToHashSet();
    }

    /// <summary>
    /// Extract command names listed in the /help output text.
    /// </summary>
    private static HashSet<string> GetHelpTextCommands()
    {
        var razor = File.ReadAllText(DashboardPath);
        // Match: - `/help` — ...
        var matches = Regex.Matches(razor, @"- `/(\w+)");
        return matches.Select(m => "/" + m.Groups[1].Value).ToHashSet();
    }

    [Fact]
    public void AutocompleteList_Exists_InIndexHtml()
    {
        var html = File.ReadAllText(IndexHtmlPath);
        Assert.Contains("ensureSlashCommandAutocomplete", html);
        Assert.Contains("slash-cmd-autocomplete", html);
    }

    [Fact]
    public void AutocompleteCommands_MatchHandlerCommands()
    {
        var autocomplete = GetAutocompleteCommands();
        var handler = GetHandlerCommands();

        // The "plugins" alias and "default" fallback are not shown in autocomplete — exclude them
        handler.Remove("/plugins");
        // "default" is a catch-all, not a real command
        handler.ExceptWith(handler.Where(c => c == "/default").ToList());

        // Every autocomplete entry should have a handler
        foreach (var cmd in autocomplete)
        {
            Assert.True(handler.Contains(cmd),
                $"Autocomplete command {cmd} has no handler in Dashboard.razor");
        }

        // Every handler (except aliases) should appear in autocomplete
        foreach (var cmd in handler)
        {
            Assert.True(autocomplete.Contains(cmd),
                $"Handler command {cmd} is missing from the autocomplete list in index.html");
        }
    }

    [Fact]
    public void AutocompleteCommands_MatchHelpText()
    {
        var autocomplete = GetAutocompleteCommands();
        var helpText = GetHelpTextCommands();

        // /mcp appears twice in help (once as /mcp and once in subcommand description).
        // /plugin also. Just check that autocomplete covers help entries.
        foreach (var cmd in autocomplete)
        {
            Assert.True(helpText.Contains(cmd),
                $"Autocomplete command {cmd} is not mentioned in /help output");
        }
    }

    [Fact]
    public void AutocompleteList_HasExpectedMinimumCommands()
    {
        var commands = GetAutocompleteCommands();
        var expected = new[] { "/help", "/clear", "/compact", "/new", "/sessions",
                               "/rename", "/version", "/diff", "/status", "/mcp",
                               "/plugin", "/reflect" };

        foreach (var cmd in expected)
        {
            Assert.Contains(cmd, commands);
        }
    }

    [Fact]
    public void ParameterlessCommands_MarkedForAutoSend()
    {
        var html = File.ReadAllText(IndexHtmlPath);
        // Commands without required args should have hasArgs: false
        var noArgs = new[] { "/help", "/clear", "/compact", "/sessions", "/version", "/status" };
        foreach (var cmd in noArgs)
        {
            var pattern = $"cmd: '{cmd}',";
            var idx = html.IndexOf(pattern, StringComparison.Ordinal);
            Assert.True(idx >= 0, $"{cmd} not found in autocomplete list");
            var afterCmd = html.Substring(idx, 120);
            Assert.Contains("hasArgs: false", afterCmd);
        }

        // Commands with args should have hasArgs: true
        var withArgs = new[] { "/new", "/rename", "/diff", "/reflect" };
        foreach (var cmd in withArgs)
        {
            var pattern = $"cmd: '{cmd}',";
            var idx = html.IndexOf(pattern, StringComparison.Ordinal);
            Assert.True(idx >= 0, $"{cmd} not found in autocomplete list");
            var afterCmd = html.Substring(idx, 120);
            Assert.Contains("hasArgs: true", afterCmd);
        }
    }

    [Fact]
    public void AutocompleteDropdown_OpensAboveInput()
    {
        var html = File.ReadAllText(IndexHtmlPath);
        // Scope to slash autocomplete section only — the Fiesta autocomplete also has positioning code
        var slashSection = html.Substring(html.IndexOf("ensureSlashCommandAutocomplete", StringComparison.Ordinal));
        // The dropdown uses bottom-anchoring to stick to the textarea
        Assert.Contains("style.bottom", slashSection);
        Assert.Contains("rect.top", slashSection);
    }

    [Fact]
    public void AutocompleteDropdown_SupportsKeyboardNavigation()
    {
        var html = File.ReadAllText(IndexHtmlPath);
        // Verify ArrowUp/ArrowDown/Tab/Escape handlers exist in the slash autocomplete
        var slashSection = html.Substring(html.IndexOf("ensureSlashCommandAutocomplete", StringComparison.Ordinal));
        Assert.Contains("ArrowDown", slashSection);
        Assert.Contains("ArrowUp", slashSection);
        Assert.Contains("'Tab'", slashSection);
        Assert.Contains("'Enter'", slashSection);
        Assert.Contains("'Escape'", slashSection);
    }

    [Fact]
    public void AutocompleteDropdown_TargetsCorrectInputSelectors()
    {
        var html = File.ReadAllText(IndexHtmlPath);
        // Should target both expanded view textarea and card view input
        Assert.Contains(".input-row textarea", html);
        Assert.Contains(".card-input input", html);
    }

    [Fact]
    public void LockedMode_DoesNotInterceptEnter()
    {
        // When the user has typed a command + args (locked mode), Enter should NOT be
        // intercepted by the autocomplete — it must pass through to send the message.
        // The keydown handler must check the current context mode and skip Enter in 'locked' mode.
        var html = File.ReadAllText(IndexHtmlPath);
        var slashSection = html.Substring(html.IndexOf("ensureSlashCommandAutocomplete", StringComparison.Ordinal));
        // The keydown handler must check for locked mode before intercepting Enter/Tab
        // Look for the guard in the keydown handler section (near Enter/Tab handling)
        var keydownStart = slashSection.IndexOf("document.addEventListener('keydown'", StringComparison.Ordinal);
        Assert.True(keydownStart >= 0, "keydown handler not found in slash autocomplete");
        var keydownSection = slashSection.Substring(keydownStart, 600);
        // Must check context mode before processing Enter/Tab
        Assert.Contains("locked", keydownSection);
    }

    [Fact]
    public void LastValue_IsPerElement_NotGlobal()
    {
        // In card/grid view, multiple session cards each have their own input.
        // The debounce cache (_lastValue or equivalent) must be per-element, not a single
        // global variable, to avoid skipping updates when two cards have the same value.
        var html = File.ReadAllText(IndexHtmlPath);
        var slashSection = html.Substring(html.IndexOf("ensureSlashCommandAutocomplete", StringComparison.Ordinal));
        // Should store last value on the element itself (e.g., target._lastSlashValue)
        // and NOT use a bare `var _lastValue` shared across all inputs
        Assert.Contains("_lastSlashValue", slashSection);
    }

    [Fact]
    public void CardView_SendButton_HasSendBtnClass()
    {
        // The autocomplete's chooseOption uses querySelector('.send-btn:not(.stop-btn)')
        // to find the send button. The card view's send button must have this class.
        var cardRazor = File.ReadAllText(Path.Combine(
            RepoRoot, "PolyPilot", "Components", "SessionCard.razor"));
        // The send button in card view must have class="send-btn" for auto-send to work
        Assert.Matches(@"class=""[^""]*send-btn[^""]*""[^>]*>➤</button>", cardRazor);
    }
}
