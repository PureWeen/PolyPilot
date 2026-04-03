using PolyPilot.Models;

namespace PolyPilot.Tests;

public class ChatCopyPayloadTests
{
    [Fact]
    public void UserMessage_WithOriginalContent_CopiesVisiblePrompt()
    {
        var msg = ChatMessage.UserMessage("Wrapped orchestration prompt");
        msg.OriginalContent = "push the changes to the PR";

        Assert.Equal("push the changes to the PR", ChatCopyPayloads.GetPrimaryCopyText(msg));
        Assert.Equal("Wrapped orchestration prompt", ChatCopyPayloads.GetOrchestrationPromptCopyText(msg));
    }

    [Fact]
    public void AssistantMessage_CopiesRawMessageContent()
    {
        var msg = ChatMessage.AssistantMessage("Here is a **markdown** answer.");

        Assert.Equal("Here is a **markdown** answer.", ChatCopyPayloads.GetPrimaryCopyText(msg));
    }

    [Fact]
    public void BashToolInput_CopiesFullCommandWithoutJsonWrapper()
    {
        var msg = ChatMessage.ToolCallMessage("bash", "call-1", "{\"command\":\"cd /repo && dotnet test --filter FullyQualifiedName~Foo\"}");

        Assert.Equal("cd /repo && dotnet test --filter FullyQualifiedName~Foo", ChatCopyPayloads.GetToolInputCopyText(msg));
    }

    [Fact]
    public void ViewToolInput_CopiesFullPathAndRange()
    {
        var msg = ChatMessage.ToolCallMessage("view", "call-2", "{\"path\":\"/src/file.cs\",\"view_range\":[10,25]}");

        Assert.Equal("/src/file.cs lines [10,25]", ChatCopyPayloads.GetToolInputCopyText(msg));
    }

    [Fact]
    public void UnknownToolInput_FallsBackToRawPayload()
    {
        var payload = "{\"foo\":\"bar\",\"count\":2}";

        Assert.Equal(payload, ChatCopyPayloads.GetToolInputCopyText("custom_tool", payload));
    }

    [Fact]
    public void GrepToolInput_QuotesPatternAndTarget()
    {
        var input = "{\"pattern\":\"hello world\",\"path\":\"/my project/src\"}";

        Assert.Equal("grep 'hello world' '/my project/src'", ChatCopyPayloads.GetToolInputCopyText("grep", input));
    }

    [Fact]
    public void GrepToolInput_EscapesSingleQuotesInPattern()
    {
        var input = "{\"pattern\":\"it's a test\",\"glob\":\"*.cs\"}";

        Assert.Equal(@"grep 'it'\''s a test' '*.cs'", ChatCopyPayloads.GetToolInputCopyText("grep", input));
    }

    [Fact]
    public void ToolOutput_CopiesFullStoredOutput()
    {
        var msg = ChatMessage.ToolCallMessage("bash", "call-3", "{\"command\":\"git status\"}");
        msg.IsComplete = true;
        msg.Content = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line {i}"));

        Assert.Equal(msg.Content, ChatCopyPayloads.GetToolOutputCopyText(msg));
    }

    [Fact]
    public void ToolOutput_IgnoresUnusableSdkResults()
    {
        var msg = ChatMessage.ToolCallMessage("report_intent", "call-4", "{\"intent\":\"Fixing bug\"}");
        msg.IsComplete = true;
        msg.Content = "Intent logged";

        Assert.Null(ChatCopyPayloads.GetToolOutputCopyText(msg));
    }

    [Fact]
    public void ShellOutput_CopiesRawShellText()
    {
        var msg = ChatMessage.ShellOutputMessage("build succeeded\nwarnings: 0");

        Assert.Equal("build succeeded\nwarnings: 0", ChatCopyPayloads.GetPrimaryCopyText(msg));
    }
}
