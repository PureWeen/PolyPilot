using AutoPilot.App.Models;

namespace AutoPilot.App.Tests;

public class ChatMessageTests
{
    [Fact]
    public void UserMessage_SetsRoleAndType()
    {
        var msg = ChatMessage.UserMessage("hello");

        Assert.Equal("user", msg.Role);
        Assert.Equal("hello", msg.Content);
        Assert.Equal(ChatMessageType.User, msg.MessageType);
        Assert.True(msg.IsUser);
        Assert.False(msg.IsAssistant);
        Assert.True(msg.IsComplete);
    }

    [Fact]
    public void AssistantMessage_SetsRoleAndType()
    {
        var msg = ChatMessage.AssistantMessage("response");

        Assert.Equal("assistant", msg.Role);
        Assert.Equal("response", msg.Content);
        Assert.Equal(ChatMessageType.Assistant, msg.MessageType);
        Assert.True(msg.IsAssistant);
        Assert.False(msg.IsUser);
        Assert.True(msg.IsComplete);
    }

    [Fact]
    public void ReasoningMessage_IsIncompleteAndNotCollapsed()
    {
        var msg = ChatMessage.ReasoningMessage("r-123");

        Assert.Equal("assistant", msg.Role);
        Assert.Equal("", msg.Content);
        Assert.Equal(ChatMessageType.Reasoning, msg.MessageType);
        Assert.Equal("r-123", msg.ReasoningId);
        Assert.False(msg.IsComplete);
        Assert.False(msg.IsCollapsed);
    }

    [Fact]
    public void ToolCallMessage_SetsToolFields()
    {
        var msg = ChatMessage.ToolCallMessage("bash", "call-1", "ls -la");

        Assert.Equal(ChatMessageType.ToolCall, msg.MessageType);
        Assert.Equal("bash", msg.ToolName);
        Assert.Equal("call-1", msg.ToolCallId);
        Assert.Equal("ls -la", msg.ToolInput);
        Assert.False(msg.IsComplete);
    }

    [Fact]
    public void ToolCallMessage_OptionalParams_DefaultToNull()
    {
        var msg = ChatMessage.ToolCallMessage("grep");

        Assert.Equal("grep", msg.ToolName);
        Assert.Null(msg.ToolCallId);
        Assert.Null(msg.ToolInput);
    }

    [Fact]
    public void ErrorMessage_SetsTypeAndContent()
    {
        var msg = ChatMessage.ErrorMessage("something broke", "bash");

        Assert.Equal(ChatMessageType.Error, msg.MessageType);
        Assert.Equal("something broke", msg.Content);
        Assert.Equal("bash", msg.ToolName);
        Assert.True(msg.IsComplete);
    }

    [Fact]
    public void ErrorMessage_OptionalToolName_DefaultsToNull()
    {
        var msg = ChatMessage.ErrorMessage("error");
        Assert.Null(msg.ToolName);
    }

    [Fact]
    public void SystemMessage_SetsSystemRole()
    {
        var msg = ChatMessage.SystemMessage("system prompt");

        Assert.Equal("system", msg.Role);
        Assert.Equal(ChatMessageType.System, msg.MessageType);
        Assert.Equal("system prompt", msg.Content);
        Assert.True(msg.IsComplete);
        Assert.False(msg.IsUser);
        Assert.False(msg.IsAssistant);
    }

    [Fact]
    public void Constructor_UserRole_OverridesMessageType()
    {
        // When role is "user", MessageType should always be User regardless of what's passed
        var msg = new ChatMessage("user", "test", DateTime.Now, ChatMessageType.Assistant);
        Assert.Equal(ChatMessageType.User, msg.MessageType);
    }

    [Fact]
    public void Constructor_AssistantRole_WithUserType_CorrectToAssistant()
    {
        // When role is not "user" but messageType is User, it should correct to Assistant
        var msg = new ChatMessage("assistant", "test", DateTime.Now, ChatMessageType.User);
        Assert.Equal(ChatMessageType.Assistant, msg.MessageType);
    }

    [Fact]
    public void Constructor_Parameterless_ForDeserialization()
    {
        var msg = new ChatMessage();
        Assert.Equal("assistant", msg.Role);
        Assert.Equal("", msg.Content);
    }

    [Fact]
    public void DefaultProperties_AreCorrect()
    {
        var msg = ChatMessage.UserMessage("test");
        Assert.True(msg.IsCollapsed); // default
        Assert.False(msg.IsSuccess); // default
        Assert.Null(msg.ReasoningId);
        Assert.Null(msg.ToolCallId);
    }
}

public class ToolActivityTests
{
    [Fact]
    public void ElapsedDisplay_LessThanOneSecond_ShowsLessThan1s()
    {
        var activity = new ToolActivity
        {
            StartedAt = DateTime.Now,
            CompletedAt = DateTime.Now.AddMilliseconds(500)
        };
        Assert.Equal("<1s", activity.ElapsedDisplay);
    }

    [Fact]
    public void ElapsedDisplay_MultipleSeconds_ShowsRoundedSeconds()
    {
        var activity = new ToolActivity
        {
            StartedAt = DateTime.Now.AddSeconds(-5),
            CompletedAt = DateTime.Now
        };
        Assert.Equal("5s", activity.ElapsedDisplay);
    }

    [Fact]
    public void ElapsedDisplay_NotCompleted_UsesCurrentTime()
    {
        var activity = new ToolActivity
        {
            StartedAt = DateTime.Now.AddSeconds(-2),
            CompletedAt = null
        };
        // Should be ~2s since it measures against DateTime.Now
        var display = activity.ElapsedDisplay;
        Assert.Matches(@"^\d+s$", display);
    }
}
