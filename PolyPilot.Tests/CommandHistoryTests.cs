using PolyPilot.Models;
using Xunit;

namespace PolyPilot.Tests;

public class CommandHistoryTests
{
    [Fact]
    public void Navigate_EmptyHistory_ReturnsNull()
    {
        var history = new CommandHistory();
        Assert.Null(history.Navigate(up: true));
        Assert.Null(history.Navigate(up: false));
    }

    [Fact]
    public void Navigate_Up_ReturnsCursorAtStart()
    {
        var history = new CommandHistory();
        history.Add("first");
        history.Add("second");

        var result = history.Navigate(up: true);
        Assert.NotNull(result);
        Assert.Equal("second", result!.Value.Text);
        Assert.True(result.Value.CursorAtStart, "ArrowUp should place cursor at start for immediate re-fire");
    }

    [Fact]
    public void Navigate_Down_ReturnsCursorAtEnd()
    {
        var history = new CommandHistory();
        history.Add("first");
        history.Add("second");

        // Navigate up twice then down
        history.Navigate(up: true);
        history.Navigate(up: true);
        var result = history.Navigate(up: false);
        Assert.NotNull(result);
        Assert.False(result!.Value.CursorAtStart, "ArrowDown should place cursor at end for immediate re-fire");
    }

    [Fact]
    public void Navigate_Up_CyclesThroughAllEntries()
    {
        var history = new CommandHistory();
        history.Add("first");
        history.Add("second");
        history.Add("third");

        // Each up-press should return the previous command (single stroke, not double)
        Assert.Equal("third", history.Navigate(up: true)!.Value.Text);
        Assert.Equal("second", history.Navigate(up: true)!.Value.Text);
        Assert.Equal("first", history.Navigate(up: true)!.Value.Text);
        // Stays at oldest
        Assert.Equal("first", history.Navigate(up: true)!.Value.Text);
    }

    [Fact]
    public void Navigate_UpThenDown_NavigatesCorrectly()
    {
        var history = new CommandHistory();
        history.Add("first");
        history.Add("second");
        history.Add("third");

        Assert.Equal("third", history.Navigate(up: true)!.Value.Text);
        Assert.Equal("second", history.Navigate(up: true)!.Value.Text);
        // Down goes back toward newest
        Assert.Equal("third", history.Navigate(up: false)!.Value.Text);
        // Past end clears to empty
        Assert.Equal("", history.Navigate(up: false)!.Value.Text);
    }

    [Fact]
    public void Add_ResetsCursorToEnd()
    {
        var history = new CommandHistory();
        history.Add("first");
        history.Add("second");

        // Navigate up
        history.Navigate(up: true);
        Assert.Equal(1, history.Index);

        // Add new command resets index past end
        history.Add("third");
        Assert.Equal(3, history.Index);

        // Next up should get "third" (the most recent)
        Assert.Equal("third", history.Navigate(up: true)!.Value.Text);
    }

    [Fact]
    public void Add_SkipsDuplicateConsecutive()
    {
        var history = new CommandHistory();
        history.Add("same");
        history.Add("same");
        history.Add("same");
        Assert.Equal(1, history.Count);
    }

    [Fact]
    public void Add_IgnoresEmptyAndNull()
    {
        var history = new CommandHistory();
        history.Add("");
        history.Add(null!);
        Assert.Equal(0, history.Count);
    }

    [Fact]
    public void Add_EnforcesMaxEntries()
    {
        var history = new CommandHistory();
        for (int i = 0; i < 55; i++)
            history.Add($"cmd-{i}");

        Assert.Equal(50, history.Count);
        // Most recent should still be accessible
        Assert.Equal("cmd-54", history.Navigate(up: true)!.Value.Text);
        // Navigate all the way up to oldest surviving entry
        for (int i = 0; i < 49; i++)
            history.Navigate(up: true);
        // Oldest surviving is cmd-5 (first 5 were trimmed)
        Assert.Equal("cmd-5", history.Navigate(up: true)!.Value.Text);
    }

    [Fact]
    public void IsNavigating_TrueAfterUpFalseAfterReturningToEnd()
    {
        var history = new CommandHistory();
        history.Add("first");
        history.Add("second");
        history.Add("third");

        Assert.False(history.IsNavigating, "IsNavigating should be false at start");
        history.Navigate(up: true);  // index -> 2, shows "third"
        Assert.True(history.IsNavigating, "IsNavigating should be true after first ArrowUp");
        history.Navigate(up: true);  // index -> 1, shows "second"
        Assert.True(history.IsNavigating, "IsNavigating should be true after second ArrowUp");
        history.Navigate(up: false); // index -> 2, shows "third"
        Assert.True(history.IsNavigating, "IsNavigating should be true when on 'third' (not yet at end)");
        history.Navigate(up: false); // index -> 3, past end, shows ""
        Assert.False(history.IsNavigating, "IsNavigating should be false after returning past end");
    }

    [Fact]
    public void Navigate_Down_PastEnd_ReturnsEmpty()
    {
        var history = new CommandHistory();
        history.Add("cmd");

        // Already past end, navigate down
        var result = history.Navigate(up: false);
        Assert.NotNull(result);
        Assert.Equal("", result!.Value.Text);
    }
}
