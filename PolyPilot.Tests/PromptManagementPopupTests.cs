using System.Text.RegularExpressions;
using Xunit;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the prompts management popup in ExpandedSessionView.
/// Verifies trigger text, visibility, and popup content patterns.
/// </summary>
public class PromptManagementPopupTests
{
    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new DirectoryNotFoundException("Could not find repo root");
    }

    private string ReadExpandedSessionView()
    {
        var file = Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "ExpandedSessionView.razor");
        return File.ReadAllText(file);
    }

    [Fact]
    public void PromptsTrigger_ShownWhenAvailablePromptsNotNull()
    {
        var content = ReadExpandedSessionView();
        // The trigger should show when availablePrompts != null (not just count > 0)
        Assert.Contains("availablePrompts != null", content);
    }

    [Fact]
    public void PromptsTrigger_HasCorrectPluralization()
    {
        var content = ReadExpandedSessionView();
        // Should use "1 prompt" (singular) vs "N prompts" (plural)
        Assert.Contains("1 prompt", content);
        Assert.DoesNotContain("1 prompts", content);
    }

    [Fact]
    public void PromptsTrigger_HasManageTitle()
    {
        var content = ReadExpandedSessionView();
        Assert.Matches(@"data-trigger=""prompts""[^>]*\btitle=""[^""]*[Mm]anage", content);
    }

    [Fact]
    public void PromptsPopup_HasNewButton()
    {
        var content = ReadExpandedSessionView();
        // The popup JS should contain a "New" button
        Assert.Contains("+ New", content);
    }

    [Fact]
    public void PromptsPopup_HasEditButton()
    {
        var content = ReadExpandedSessionView();
        Assert.Contains("Edit prompt", content);
    }

    [Fact]
    public void PromptsPopup_HasDeleteButton()
    {
        var content = ReadExpandedSessionView();
        Assert.Contains("Delete prompt", content);
    }

    [Fact]
    public void PromptsPopup_HasSavePromptCallback()
    {
        var content = ReadExpandedSessionView();
        // JSInvokable save method must exist
        Assert.Contains("SavePromptFromPopup", content);
    }

    [Fact]
    public void PromptsPopup_HasDeletePromptCallback()
    {
        var content = ReadExpandedSessionView();
        // JSInvokable delete method must exist
        Assert.Contains("DeletePromptFromPopup", content);
    }

    [Fact]
    public void PromptsPopup_EditButtonOnlyForUserPrompts()
    {
        var content = ReadExpandedSessionView();
        // Edit/delete buttons should only appear for user-owned prompts (isUser check)
        Assert.Contains("if(p.isUser)", content);
    }

    [Fact]
    public void PromptsPopup_HasFormWithNameContentFields()
    {
        var content = ReadExpandedSessionView();
        // The form should have name and content inputs
        Assert.Contains("Prompt name", content);
        Assert.Contains("Prompt content", content);
    }

    [Fact]
    public void PromptsPopup_ShowsEmptyStateMessage()
    {
        var content = ReadExpandedSessionView();
        // When no prompts exist, should show a helpful message
        Assert.Contains("No prompts yet", content);
    }

    [Fact]
    public void PromptsPopup_UsesDotNetObjectReference()
    {
        var content = ReadExpandedSessionView();
        // Should use DotNetObjectReference for JS→C# callbacks
        Assert.Contains("DotNetObjectReference", content);
    }
}
