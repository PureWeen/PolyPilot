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
        handler.Remove("/prompts");
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

        // Every autocomplete entry should appear in /help
        foreach (var cmd in autocomplete)
        {
            Assert.True(helpText.Contains(cmd),
                $"Autocomplete command {cmd} is not mentioned in /help output");
        }

        // Every /help entry should appear in autocomplete
        foreach (var cmd in helpText)
        {
            Assert.True(autocomplete.Contains(cmd),
                $"/help command {cmd} is missing from the autocomplete list");
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
        var noArgs = new[] { "/help", "/clear", "/compact", "/sessions", "/version" };
        foreach (var cmd in noArgs)
        {
            var pattern = $"cmd: '{cmd}',";
            var idx = html.IndexOf(pattern, StringComparison.Ordinal);
            Assert.True(idx >= 0, $"{cmd} not found in autocomplete list");
            var endOfLine = html.IndexOf('\n', idx);
            var line = html.Substring(idx, endOfLine - idx);
            Assert.Contains("hasArgs: false", line);
        }

        // Commands with args should have hasArgs: true
        var withArgs = new[] { "/new", "/rename", "/diff", "/reflect", "/mcp", "/plugin", "/prompt", "/status" };
        foreach (var cmd in withArgs)
        {
            var pattern = $"cmd: '{cmd}',";
            var idx = html.IndexOf(pattern, StringComparison.Ordinal);
            Assert.True(idx >= 0, $"{cmd} not found in autocomplete list");
            var endOfLine = html.IndexOf('\n', idx);
            var line = html.Substring(idx, endOfLine - idx);
            Assert.Contains("hasArgs: true", line);
        }

        // Ensure every command is accounted for in exactly one list
        var allFromTest = noArgs.Concat(withArgs).ToHashSet();
        var allFromHtml = GetAutocompleteCommands();
        Assert.Equal(allFromHtml.Count, allFromTest.Count);
        Assert.True(allFromHtml.SetEquals(allFromTest),
            $"Mismatch — in HTML but not test: {string.Join(", ", allFromHtml.Except(allFromTest))}; " +
            $"in test but not HTML: {string.Join(", ", allFromTest.Except(allFromHtml))}");
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

    [Fact]
    public void ChooseOption_PreservesTextAfterCursor()
    {
        // Bug: chooseOption must replace only the first token (command) and preserve
        // everything after it (args). It should use the full value's first space as the
        // boundary, not the cursor position.
        var html = File.ReadAllText(IndexHtmlPath);
        var slashSection = html.Substring(html.IndexOf("ensureSlashCommandAutocomplete", StringComparison.Ordinal));
        var fn = ExtractFunction(slashSection, "chooseOption");

        // The function must find the command boundary from the full text (first space),
        // not from text before cursor. It should use text/value.indexOf(' ') to find
        // the end of the command token, then preserve text.slice(cmdEnd) as args.
        var usesFullValueBoundary = fn.Contains("text.indexOf(' ')") || fn.Contains("text.indexOf(\" \")");
        var preservesSuffix = fn.Contains("text.slice(") || fn.Contains("text.substring(");
        Assert.True(usesFullValueBoundary && preservesSuffix,
            "chooseOption must find command boundary from full value (not cursor position) and preserve args after it");
    }

    [Fact]
    public void LockedMode_UsesFullValue_NotJustBeforeCursor()
    {
        // Bug: getSlashContext determines locked vs filter mode using only text before
        // cursor (selectionStart). If user arrows left past the space in `/new session`,
        // the context flips to 'filter', and Enter/Tab will overwrite args.
        // Locked mode should be based on the full value, not just the pre-cursor portion.
        var html = File.ReadAllText(IndexHtmlPath);
        var ctxFn = ExtractFunction(html, "getSlashContext");

        // The function should examine the full value (not just left of cursor) to determine
        // if a recognized command + args exists. It should check the full text, not just `left`.
        var usesFullValue = ctxFn.Contains(".value") && (
            // Either checks full value for space after command
            Regex.IsMatch(ctxFn, @"text\s*\.indexOf|text\s*\.includes|text\s*\.match") ||
            // Or explicitly handles cursor-in-command-portion case
            ctxFn.Contains("fullText") ||
            ctxFn.Contains("full value") ||
            // Or determines locked from the whole string, not just `left`
            (ctxFn.Contains("text.indexOf(' ')") || ctxFn.Contains("text.includes(' ')"))
        );
        Assert.True(usesFullValue,
            "getSlashContext should determine locked mode from full input value, not just text before cursor");
    }

    [Fact]
    public void DropdownPosition_InvalidatesOnTextareaResize()
    {
        // Bug: updateDropdownPosition caches position via _posValid, but textarea
        // auto-resizes on input (scrollHeight adjustment), shifting rect.top.
        // The cache must be invalidated when the textarea height changes.
        var html = File.ReadAllText(IndexHtmlPath);
        var slashSection = html.Substring(html.IndexOf("ensureSlashCommandAutocomplete", StringComparison.Ordinal));

        // Either: position cache is invalidated on input (in the input handler or refreshForTarget)
        // Or: the cache compares element dimensions and invalidates when they change
        // Or: the cache is removed entirely (always recompute)
        var inputHandler = ExtractBetween(slashSection, "document.addEventListener('input'", "}, true);");
        var positionFn = ExtractFunction(slashSection, "updateDropdownPosition");

        var invalidatesOnInput = inputHandler.Contains("_posValid = false")
                              || inputHandler.Contains("_posValid=false")
                              || inputHandler.Contains("invalidateAndReposition");
        // Must check actual element dimensions (offsetHeight, clientHeight, scrollHeight),
        // not just maxHeight which is a CSS property the function itself sets
        var checksDimensions = positionFn.Contains("offsetHeight")
                            || positionFn.Contains("clientHeight")
                            || positionFn.Contains("_posHeight");
        var noCaching = !positionFn.Contains("_posValid");

        Assert.True(invalidatesOnInput || checksDimensions || noCaching,
            "Dropdown position must be recomputed when textarea resizes — " +
            "either invalidate _posValid on input, check element dimensions in cache, or remove caching");
    }

    [Fact]
    public void Dropdown_HidesOnBlurOrFocusOut()
    {
        // Bug: Dropdown stays visible when user Tabs out of the input field.
        // There should be a blur/focusout listener that hides the dropdown.
        var html = File.ReadAllText(IndexHtmlPath);
        var slashSection = html.Substring(html.IndexOf("ensureSlashCommandAutocomplete", StringComparison.Ordinal));

        var hasBlurHandler = slashSection.Contains("'blur'")
                          || slashSection.Contains("'focusout'")
                          || slashSection.Contains("\"blur\"")
                          || slashSection.Contains("\"focusout\"");
        Assert.True(hasBlurHandler,
            "Slash autocomplete must listen for blur/focusout to hide dropdown when input loses focus");
    }

    [Fact]
    public void DropdownPosition_ClampsToViewport()
    {
        // Bug: Math.max(120, spaceAbove) forces 120px min height even if spaceAbove < 120,
        // causing dropdown to extend above the viewport. Should clamp or flip direction.
        var html = File.ReadAllText(IndexHtmlPath);
        // Get the slash-command-specific updateDropdownPosition (the second one)
        var slashSection = html.Substring(html.IndexOf("ensureSlashCommandAutocomplete", StringComparison.Ordinal));
        var positionFn = ExtractFunction(slashSection, "updateDropdownPosition");

        // Should either: flip dropdown below when no space above,
        // or clamp maxHeight to actual available space (no forced 120px min that overflows)
        var flipsDown = positionFn.Contains("openDown") || positionFn.Contains("spaceBelow")
                     || positionFn.Contains("below") || positionFn.Contains("flip");
        var clampsToViewport = Regex.IsMatch(positionFn, @"Math\.min\([^)]*spaceAbove")
                            && !Regex.IsMatch(positionFn, @"Math\.max\(\s*120");
        var noHardMin = !positionFn.Contains("Math.max(120");

        Assert.True(flipsDown || clampsToViewport || noHardMin,
            "Dropdown position must handle viewport top edge — either flip below or clamp " +
            "maxHeight to available space without a forced 120px minimum that overflows");
    }

    [Fact]
    public void FocusoutTimer_CancelledOnHideDropdown()
    {
        // Bug: focusout schedules hideDropdown via setTimeout. If hideDropdown is called
        // before the timeout fires (e.g. user selects an option), the stale timeout can
        // close a newly opened dropdown. hideDropdown must clearTimeout.
        var html = File.ReadAllText(IndexHtmlPath);
        var slashSection = html.Substring(html.IndexOf("ensureSlashCommandAutocomplete", StringComparison.Ordinal));
        var hideFn = ExtractFunction(slashSection, "hideDropdown");
        Assert.Contains("clearTimeout", hideFn);
    }

    [Fact]
    public void Dropdown_UsesBorderBoxSizing()
    {
        // Bug: Without border-box, maxHeight doesn't include padding/border,
        // causing the rendered box to exceed available space by ~14px.
        var html = File.ReadAllText(IndexHtmlPath);
        var slashSection = html.Substring(html.IndexOf("ensureSlashCommandAutocomplete", StringComparison.Ordinal));
        Assert.Contains("border-box", slashSection);
    }

    [Fact]
    public void NoArgsAutoSend_CancelsRafToPreventStaleReopen()
    {
        // Bug: After auto-sending a no-args command, the pending rAF from the input
        // event re-opens the dropdown before Blazor clears the value, causing a
        // stale dropdown and potential double-send.
        var html = File.ReadAllText(IndexHtmlPath);
        var slashSection = html.Substring(html.IndexOf("ensureSlashCommandAutocomplete", StringComparison.Ordinal));
        var fn = ExtractFunction(slashSection, "chooseOption");
        // The no-args branch (else block with sendBtn.click) must cancel _rafPending
        var elseBlock = fn.Substring(fn.IndexOf("} else {", StringComparison.Ordinal));
        Assert.Contains("cancelAnimationFrame", elseBlock);
    }

    [Fact]
    public void DropdownScrollbar_PreventsBlur()
    {
        // Bug: Clicking the dropdown scrollbar blurs the input, triggering focusout
        // which hides the dropdown. The dropdown container needs mousedown preventDefault.
        var html = File.ReadAllText(IndexHtmlPath);
        var slashSection = html.Substring(html.IndexOf("ensureSlashCommandAutocomplete", StringComparison.Ordinal));
        var dropdownFn = ExtractFunction(slashSection, "ensureDropdown");
        Assert.Contains("mousedown", dropdownFn);
        Assert.Contains("preventDefault", dropdownFn);
    }

    [Fact]
    public void ClearElementValue_HidesSlashDropdown()
    {
        // Bug: After sending a command with args, clearElementValue clears the value
        // but doesn't hide the dropdown, leaving it stale over an empty input.
        var html = File.ReadAllText(IndexHtmlPath);
        var clearFn = ExtractBetween(html, "window.clearElementValue", "};");
        // Must call the slash dropdown hide function
        Assert.Contains("_slashHideDropdown", clearFn);
    }

    [Fact]
    public void StatusCommand_HasArgs()
    {
        // Bug: /status was marked hasArgs: false but the handler accepts args
        // (e.g. /status --short, /status path/). Must be hasArgs: true.
        var html = File.ReadAllText(IndexHtmlPath);
        var pattern = "cmd: '/status',";
        var idx = html.IndexOf(pattern, StringComparison.Ordinal);
        Assert.True(idx >= 0, "/status not found in autocomplete list");
        var endOfLine = html.IndexOf('\n', idx);
        var line = html.Substring(idx, endOfLine - idx);
        Assert.Contains("hasArgs: true", line);
    }

    /// <summary>
    /// Extract a JS function body by name (from `function name(` to the next function at same indent).
    /// </summary>
    private static string ExtractFunction(string source, string funcName)
    {
        var pattern = $"function {funcName}";
        var start = source.IndexOf(pattern, StringComparison.Ordinal);
        if (start < 0) return "";
        // Find the closing of this function — next function at the same indent level
        var rest = source.Substring(start);
        var nextFunc = Regex.Match(rest.Substring(1), @"\n\s+function ");
        return nextFunc.Success ? rest.Substring(0, nextFunc.Index + 1) : rest.Substring(0, Math.Min(rest.Length, 1000));
    }

    /// <summary>
    /// Extract text between two marker strings.
    /// </summary>
    private static string ExtractBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0) return "";
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        return end > start ? source.Substring(start, end - start + endMarker.Length) : source.Substring(start, Math.Min(source.Length - start, 2000));
    }
}
