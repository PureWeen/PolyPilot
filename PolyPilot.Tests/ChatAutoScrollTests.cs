using System.Text.RegularExpressions;
using Xunit;

namespace PolyPilot.Tests;

/// <summary>
/// Verifies that the chat auto-scroll logic in index.html:
/// 1. Continuously scrolls to bottom while a response is streaming (wasAtBottom tracking).
/// 2. Pauses auto-scroll when the user clicks inside the chat message area (__userPaused).
/// 3. Re-enables auto-scroll when forceScroll is true (new message sent).
/// 4. Re-enables auto-scroll when the user manually scrolls back to the bottom.
/// </summary>
public class ChatAutoScrollTests
{
    private static string GetIndexHtml()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        var root = dir ?? throw new DirectoryNotFoundException("Could not find repo root");
        return File.ReadAllText(Path.Combine(root, "PolyPilot", "wwwroot", "index.html"));
    }

    // ── Click handler to pause auto-scroll ───────────────────────────────────

    [Fact]
    public void IndexHtml_HasClickHandlerThatSetsUserPausedOnMessagesContainer()
    {
        var html = GetIndexHtml();
        // Must listen for click events in capture phase
        Assert.Contains("document.addEventListener('click'", html);
        // Must set __userPaused on the .messages container
        Assert.Contains("__userPaused = true", html);
        Assert.Contains(".messages')", html);
    }

    [Fact]
    public void IndexHtml_ClickHandler_ExcludesInteractiveElements()
    {
        var html = GetIndexHtml();
        // Clicks on buttons, links, and inputs should NOT pause scroll
        Assert.Contains("button, a, input, textarea, select", html);
    }

    [Fact]
    public void IndexHtml_ClickHandler_AlsoPausesCardMessages()
    {
        var html = GetIndexHtml();
        // Grid card containers should also respect the pause flag
        Assert.Contains(".card-messages')", html);
    }

    // ── __userPaused respected in doScroll ───────────────────────────────────

    [Fact]
    public void IndexHtml_DoScroll_ChecksUserPausedBeforeScrolling()
    {
        var html = GetIndexHtml();
        // doScroll must check __userPaused before scrolling the .messages element
        Assert.Contains("__userPaused", html);
        Assert.Contains("!el.__userPaused", html);
    }

    [Fact]
    public void IndexHtml_DoScroll_InitializesUserPausedToFalse()
    {
        var html = GetIndexHtml();
        // On first init, __userPaused must be explicitly false (not undefined)
        Assert.Contains("el.__userPaused = false", html);
    }

    // ── Re-enable auto-scroll on forceScroll ─────────────────────────────────

    [Fact]
    public void IndexHtml_DoScroll_ClearsUserPausedWhenForceScroll()
    {
        var html = GetIndexHtml();
        // forceScroll (triggered when a new message is sent) must re-enable auto-scroll
        Assert.Contains("if (forceScroll)", html);
        // The block must clear __userPaused
        var forceScrollIdx = html.IndexOf("if (forceScroll)", StringComparison.Ordinal);
        Assert.True(forceScrollIdx >= 0);
        var snippet = html.Substring(forceScrollIdx, Math.Min(200, html.Length - forceScrollIdx));
        Assert.Contains("__userPaused = false", snippet);
    }

    // ── Re-enable auto-scroll when user scrolls to bottom ────────────────────

    [Fact]
    public void IndexHtml_DoScroll_ClearsUserPausedWhenNearBottom()
    {
        var html = GetIndexHtml();
        // If the user scrolls back near the bottom, auto-scroll should resume
        // Find the isNearBottom block and verify __userPaused is cleared there
        var nearBottomIdx = html.IndexOf("if (isNearBottom)", StringComparison.Ordinal);
        Assert.True(nearBottomIdx >= 0, "Expected isNearBottom check in doScroll");
        var snippet = html.Substring(nearBottomIdx, Math.Min(300, html.Length - nearBottomIdx));
        Assert.Contains("__userPaused = false", snippet);
    }

    // ── wasAtBottom tracking for streaming ───────────────────────────────────

    [Fact]
    public void IndexHtml_DoScroll_TracksWasAtBottomForStreamingScroll()
    {
        var html = GetIndexHtml();
        // __wasAtBottom enables continuous scroll during streaming without forceScroll
        Assert.Contains("__wasAtBottom", html);
        Assert.Contains("el.__wasAtBottom = true", html);
        Assert.Contains("el.__wasAtBottom = false", html);
    }

    [Fact]
    public void IndexHtml_DoScroll_ScrollsToBottomWhenWasAtBottom()
    {
        var html = GetIndexHtml();
        // The condition that drives streaming auto-scroll:
        // !__userPaused && (forceScroll || isNearBottom || __wasAtBottom)
        Assert.Contains("forceScroll || isNearBottom || el.__wasAtBottom", html);
    }
}
