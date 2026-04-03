using PolyPilot.Models;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the tool call expand/collapse logic used by ChatMessageItem.razor.
/// Mirrors the _hasOutput and _canExpand conditions from the rendering code
/// to prevent regression of the "can't see full command while executing" bug.
/// </summary>
public class ToolCallExpandTests
{
    // Mirrors ChatMessageItem.razor line 115
    private static bool HasOutput(ChatMessage msg)
        => msg.IsComplete
           && !string.IsNullOrEmpty(msg.Content)
           && !IsUnusableResult(msg.Content);

    // Mirrors ChatMessageItem.razor: any tool with usable output or tool input can expand.
    private static bool CanExpand(ChatMessage msg)
        => HasOutput(msg) || !string.IsNullOrEmpty(msg.ToolInput);

    // Mirrors the rendering condition: show the expanded input block whenever there is tool input,
    // the bubble is expanded, and output is not taking over the body section.
    private static bool ShowsFullInputWhenNoOutput(ChatMessage msg)
        => !HasOutput(msg) && !msg.IsCollapsed && !string.IsNullOrEmpty(msg.ToolInput);

    // Mirrors ChatMessageList.IsUnusableResult
    private static bool IsUnusableResult(string? content)
    {
        if (string.IsNullOrEmpty(content)) return true;
        if (content.StartsWith("GitHub.Copilot.SDK.")) return true;
        if (content is "(no result)" or "Intent logged") return true;
        return false;
    }

    [Fact]
    public void RunningCommand_WithToolInput_CanExpand()
    {
        var msg = ChatMessage.ToolCallMessage("bash", "call-1", "{\"command\":\"dotnet build -f net10.0-maccatalyst\"}");
        // IsComplete defaults to false for ToolCallMessage
        Assert.False(msg.IsComplete);
        Assert.False(HasOutput(msg));
        Assert.True(CanExpand(msg));
    }

    [Fact]
    public void RunningCommand_WithoutToolInput_CannotExpand()
    {
        var msg = ChatMessage.ToolCallMessage("bash", "call-1");
        Assert.False(msg.IsComplete);
        Assert.Null(msg.ToolInput);
        Assert.False(CanExpand(msg));
    }

    [Fact]
    public void CompletedCommand_WithOutput_CanExpand()
    {
        var msg = ChatMessage.ToolCallMessage("bash", "call-1", "{\"command\":\"ls\"}");
        msg.IsComplete = true;
        msg.Content = "file1.txt\nfile2.txt";
        Assert.True(HasOutput(msg));
        Assert.True(CanExpand(msg));
    }

    [Fact]
    public void CompletedCommand_WithUnusableOutput_ButHasInput_CanExpand()
    {
        var msg = ChatMessage.ToolCallMessage("bash", "call-1", "{\"command\":\"ls\"}");
        msg.IsComplete = true;
        msg.Content = "(no result)";
        Assert.False(HasOutput(msg));
        Assert.True(CanExpand(msg));
    }

    [Fact]
    public void CompletedCommand_NoOutputNoInput_CannotExpand()
    {
        var msg = ChatMessage.ToolCallMessage("bash", "call-1");
        msg.IsComplete = true;
        msg.Content = "";
        Assert.False(HasOutput(msg));
        Assert.False(CanExpand(msg));
    }

    [Fact]
    public void RunningBashCommand_WithLongInput_IsCollapsedByDefault()
    {
        var longCmd = "{\"command\":\"cd /very/long/path/to/project && dotnet build -f net10.0-maccatalyst -p:SomeProperty=true -p:AnotherProperty=false\"}";
        var msg = ChatMessage.ToolCallMessage("bash", "call-1", longCmd);

        Assert.True(msg.IsCollapsed);
        Assert.True(CanExpand(msg));

        // User toggles to expand
        msg.IsCollapsed = false;
        Assert.False(msg.IsCollapsed);
        Assert.True(CanExpand(msg));
    }

    [Fact]
    public void RunningEditCommand_WithToolInput_CanExpand()
    {
        var msg = ChatMessage.ToolCallMessage("edit", "call-2", "{\"path\":\"/src/file.cs\",\"old_str\":\"foo\",\"new_str\":\"bar\"}");
        Assert.False(msg.IsComplete);
        Assert.True(CanExpand(msg));
    }

    [Fact]
    public void RunningGrepCommand_WithToolInput_CanExpand()
    {
        var msg = ChatMessage.ToolCallMessage("grep", "call-3", "{\"pattern\":\"TODO\",\"path\":\"src/\"}");
        Assert.False(msg.IsComplete);
        Assert.True(CanExpand(msg));
    }

    [Fact]
    public void CompletedCommand_WithSdkUnusableResult_ButHasInput_CanExpand()
    {
        var msg = ChatMessage.ToolCallMessage("bash", "call-1", "{\"command\":\"echo hello\"}");
        msg.IsComplete = true;
        msg.Content = "GitHub.Copilot.SDK.SomeType";
        Assert.False(HasOutput(msg));
        Assert.True(CanExpand(msg));
    }

    [Fact]
    public void ToggleCollapse_PreservesState()
    {
        var msg = ChatMessage.ToolCallMessage("bash", "call-1", "{\"command\":\"ls\"}");
        Assert.True(msg.IsCollapsed); // default

        msg.IsCollapsed = false;
        Assert.False(msg.IsCollapsed);

        msg.IsCollapsed = true;
        Assert.True(msg.IsCollapsed);
    }

    [Fact]
    public void CompletedNonBashCommand_WithIntentLoggedResult_ButHasInput_CanExpand()
    {
        var msg = ChatMessage.ToolCallMessage("report_intent", "call-4", "{\"intent\":\"Fixing bug\"}");
        msg.IsComplete = true;
        msg.Content = "Intent logged";
        Assert.False(HasOutput(msg));
        Assert.True(CanExpand(msg));
    }

    [Fact]
    public void RunningNonBashTool_ShowsFullInputWhenExpanded()
    {
        var msg = ChatMessage.ToolCallMessage("edit", "call-5", "{\"path\":\"/src/file.cs\"}");
        msg.IsCollapsed = false;
        Assert.True(CanExpand(msg));
        Assert.True(ShowsFullInputWhenNoOutput(msg));
    }

    [Fact]
    public void RunningBashTool_ShowsFullInputWhenExpanded()
    {
        var msg = ChatMessage.ToolCallMessage("bash", "call-6", "{\"command\":\"dotnet build\"}");
        msg.IsCollapsed = false;
        Assert.True(CanExpand(msg));
        Assert.True(ShowsFullInputWhenNoOutput(msg)); // bash shows full input when expanded
    }

    [Fact]
    public void RunningBashTool_CollapsedDoesNotShowFullInput()
    {
        var msg = ChatMessage.ToolCallMessage("bash", "call-7", "{\"command\":\"dotnet build\"}");
        Assert.True(msg.IsCollapsed);
        Assert.True(CanExpand(msg));
        Assert.False(ShowsFullInputWhenNoOutput(msg)); // collapsed = no full input
    }

    [Fact]
    public void CompletedNonBashTool_WithOutput_IsExpandable()
    {
        // Non-bash tools become expandable when they complete with output
        var msg = ChatMessage.ToolCallMessage("grep", "call-8", "{\"pattern\":\"TODO\"}");
        msg.IsComplete = true;
        msg.Content = "src/file.cs:10: // TODO fix this";
        Assert.True(HasOutput(msg));
        Assert.True(CanExpand(msg));
    }

    [Fact]
    public void CompletedBashWithOutput_ShowsFullInputWhenNoOutput_ReturnsFalse()
    {
        // A completed bash command with usable output takes the if (_hasOutput) branch in
        // ChatMessageItem.razor, NOT the else-if. ShowsFullInputWhenNoOutput must return false.
        var msg = ChatMessage.ToolCallMessage("bash", "call-1", "{\"command\":\"ls\"}");
        msg.IsComplete = true;
        msg.Content = "file1.txt";
        msg.IsCollapsed = false;
        Assert.True(HasOutput(msg));
        Assert.False(ShowsFullInputWhenNoOutput(msg));
    }
}
