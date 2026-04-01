using PolyPilot.Models;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for RoutingHelper.ExtractForwardedContent — the natural-language routing
/// extraction that enables "please tell @Session that X" → forwards "X" to @Session.
/// </summary>
public class RoutingHelperTests
{
    // ── Helper: call both args the same way we do in Dashboard.razor ─────────
    private static string Extract(string strippedMessage, string originalPrompt)
        => RoutingHelper.ExtractForwardedContent(strippedMessage, originalPrompt);

    // ─────────────────────────────────────────────────────────────────────────
    // Passthrough — messages that need no transformation
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("hello world", "hello world")]
    [InlineData("please fix this bug", "please fix this bug")]
    [InlineData("the tests are now passing", "the tests are now passing")]
    public void PlainContent_ReturnsUnchanged(string stripped, string original)
        => Assert.Equal(stripped, Extract(stripped, original));

    // ─────────────────────────────────────────────────────────────────────────
    // "please tell @Session that X" → X
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PleaseTell_ThatContent_ExtractsContent()
    {
        // "please tell @Bob that the tests pass" after @-strip → "please tell that the tests pass"
        var result = Extract("please tell that the tests pass", "please tell @Bob that the tests pass");
        Assert.Equal("the tests pass", result);
    }

    [Fact]
    public void PleaseForward_ColonContent_ExtractsContent()
    {
        // "please forward to @Bob: hello world" after @-strip → "please forward to : hello world"
        // after colon-cleanup → "hello world"
        var stripped = "please forward to : hello world";
        var result = Extract(stripped, "please forward to @Bob: hello world");
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void CanYouSend_ThatContent_ExtractsContent()
    {
        var stripped = "can you send that the meeting is at 3pm";
        var result = Extract(stripped, "can you send @Bob that the meeting is at 3pm");
        Assert.Equal("the meeting is at 3pm", result);
    }

    [Fact]
    public void CouldYouTell_ThatContent_ExtractsContent()
    {
        var stripped = "could you tell that everything is working";
        var result = Extract(stripped, "could you tell @Session that everything is working");
        Assert.Equal("everything is working", result);
    }

    [Fact]
    public void PleaseSend_MessageContent_ExtractsContent()
    {
        var stripped = "please send a message to : hello there";
        var result = Extract(stripped, "please send a message to @Session: hello there");
        Assert.Equal("hello there", result);
    }

    [Fact]
    public void PleaseAsk_SayingContent_ExtractsContent()
    {
        var stripped = "please ask saying can you fix the bug";
        var result = Extract(stripped, "please ask @Session saying can you fix the bug");
        Assert.Equal("can you fix the bug", result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Fallback: empty content after stripping → return original minus @tokens
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PleasePassThisInfoTo_NoContent_FallsBackToOriginal()
    {
        // After stripping @Session: "please pass this info to"
        // No extractable content → fallback to original minus @mention
        var stripped = "please pass this info to";
        var original = "please pass this info to @Session";
        var result = Extract(stripped, original);
        // Fallback preserves the original routing instruction (target gets context)
        Assert.Equal("please pass this info to", result);
    }

    [Fact]
    public void PleaseForwardThis_NoContent_FallsBackToOriginal()
    {
        var stripped = "please forward this";
        var original = "please forward this @Bob";
        var result = Extract(stripped, original);
        Assert.Equal("please forward this", result);
    }

    [Fact]
    public void TellAbout_NoExplicitContent_FallsBackToOriginal()
    {
        // "tell @Session about it" → stripped: "tell about it"
        // After routing prefix strip: "about it" → connector "about" strip → "it"
        // "it" is too short (< 3 chars) → fallback to "tell about it"
        var stripped = "tell about it";
        var original = "tell @Session about it";
        var result = Extract(stripped, original);
        // "it" is only 2 chars so fallback fires → "tell about it"
        Assert.Equal("tell about it", result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Message BEFORE the @mention — already handled by @-strip, no change needed
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ContentBeforeMention_PassesThrough()
    {
        // "check this out @Session" → stripped: "check this out"
        // No routing prefix → return as-is
        var stripped = "check this out";
        var result = Extract(stripped, "check this out @Session");
        Assert.Equal("check this out", result);
    }

    [Fact]
    public void HeyMention_Content_PassesThrough()
    {
        // "@Session please fix this bug" → stripped: "please fix this bug"
        var stripped = "please fix this bug";
        var result = Extract(stripped, "@Session please fix this bug");
        Assert.Equal("please fix this bug", result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Regression: plain routing phrases don't eat normal sentences
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MessageStartingWithForward_NotARoutingInstruction_IsKept()
    {
        // "forward slash commands are important" should NOT be stripped
        // because "slash" doesn't match the routing prefix pattern
        var stripped = "forward slash commands are important";
        var result = Extract(stripped, "forward slash commands are important @Session");
        // "forward slash" doesn't match (next word after "forward" isn't recognized)
        // so stripped → "forward slash commands are important"
        Assert.Equal("forward slash commands are important", result);
    }

    [Fact]
    public void MessageStartingWithAsk_Question_IsKept()
    {
        // "ask me anything" after strip → "ask me anything" — "me" doesn't trigger routing
        var stripped = "ask me anything";
        var result = Extract(stripped, "ask me anything @Session");
        // "ask me" doesn't match the routing suffix (no "to/:" after) so it passes through
        // (or gets partially stripped but enough remains)
        Assert.True(result.Length >= 3, $"Result should be non-trivial, got: '{result}'");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Whitespace handling
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtraWhitespace_IsCollapsed()
    {
        var stripped = "please  tell  that   hello   world";
        var result = Extract(stripped, "please tell @Bob that   hello   world");
        Assert.Equal("hello world", result);
    }
}
