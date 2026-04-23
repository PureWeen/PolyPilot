using PolyPilot.Models;

namespace PolyPilot.Tests;

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
    public void ReflectionMessage_SetsReflectionType()
    {
        var msg = ChatMessage.ReflectionMessage("🔄 Iteration 2/5");

        Assert.Equal("system", msg.Role);
        Assert.Equal(ChatMessageType.Reflection, msg.MessageType);
        Assert.Equal("🔄 Iteration 2/5", msg.Content);
        Assert.True(msg.IsComplete);
    }

    [Fact]
    public void Constructor_UserRole_OverridesMessageType()
    {
        // When role is "user", MessageType should always be User regardless of what's passed
        var msg = new ChatMessage("user", "test", DateTimeOffset.UtcNow, ChatMessageType.Assistant);
        Assert.Equal(ChatMessageType.User, msg.MessageType);
    }

    [Fact]
    public void Constructor_AssistantRole_WithUserType_CorrectToAssistant()
    {
        // When role is not "user" but messageType is User, it should correct to Assistant
        var msg = new ChatMessage("assistant", "test", DateTimeOffset.UtcNow, ChatMessageType.User);
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

    [Fact]
    public void Model_DefaultsToNull()
    {
        var msg = ChatMessage.AssistantMessage("test");
        Assert.Null(msg.Model);
    }

    [Fact]
    public void Model_CanBeSetViaInitializer()
    {
        var msg = new ChatMessage("assistant", "test", DateTimeOffset.UtcNow) { Model = "gpt-4.1" };
        Assert.Equal("gpt-4.1", msg.Model);
    }

    [Fact]
    public void Model_PreservedOnAssistantMessages()
    {
        var msg = new ChatMessage("assistant", "response", DateTimeOffset.UtcNow) { Model = "claude-sonnet-4.5" };
        Assert.True(msg.IsAssistant);
        Assert.Equal("claude-sonnet-4.5", msg.Model);
    }

    [Fact]
    public void Model_NullForUserMessages()
    {
        var msg = ChatMessage.UserMessage("hello");
        Assert.Null(msg.Model);
    }

    [Fact]
    public void OriginalContent_DefaultsToNull()
    {
        var msg = ChatMessage.UserMessage("hello");
        Assert.Null(msg.OriginalContent);
    }

    [Fact]
    public void OriginalContent_CanBeSet()
    {
        var msg = ChatMessage.UserMessage("[Multi-agent context: ...]\n\nfix the bug");
        msg.OriginalContent = "fix the bug";

        Assert.Equal("fix the bug", msg.OriginalContent);
        Assert.Equal("[Multi-agent context: ...]\n\nfix the bug", msg.Content);
    }

    [Fact]
    public void OriginalContent_PreservedOnDeserialization()
    {
        var msg = new ChatMessage("user", "full orchestration prompt", DateTimeOffset.UtcNow)
        {
            OriginalContent = "user typed this"
        };

        // Simulate round-trip via JSON
        var json = System.Text.Json.JsonSerializer.Serialize(msg);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<ChatMessage>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("user typed this", deserialized!.OriginalContent);
        Assert.Equal("full orchestration prompt", deserialized.Content);
    }

    [Fact]
    public void OriginalContent_NullWhenNotOrchestrated()
    {
        // Regular user messages should not have OriginalContent set
        var msg = ChatMessage.UserMessage("simple prompt");
        Assert.Null(msg.OriginalContent);
        Assert.Equal("simple prompt", msg.Content);
    }

    // --- Interrupted turn system messages ---

    [Fact]
    public void InterruptedTurn_SystemMessage_ContainsWarning()
    {
        var interruptMsg = "⚠️ Your previous request was interrupted by an app restart. You may need to resend your last message.";
        var msg = ChatMessage.SystemMessage(interruptMsg);

        Assert.Equal("system", msg.Role);
        Assert.Equal(ChatMessageType.System, msg.MessageType);
        Assert.Contains("interrupted by an app restart", msg.Content);
        Assert.Contains("resend your last message", msg.Content);
        Assert.True(msg.IsComplete);
    }

    [Fact]
    public void InterruptedTurn_SystemMessage_IncludesLastPrompt()
    {
        var lastPrompt = "fix the authentication bug in UserController.cs";
        var truncated = lastPrompt.Length > 80 ? lastPrompt[..80] + "…" : lastPrompt;
        var interruptMsg = $"⚠️ Your previous request was interrupted by an app restart. You may need to resend your last message.\n📝 Last message: \"{truncated}\"";
        var msg = ChatMessage.SystemMessage(interruptMsg);

        Assert.Contains("Last message:", msg.Content);
        Assert.Contains("fix the authentication bug", msg.Content);
    }

    [Fact]
    public void InterruptedTurn_SystemMessage_TruncatesLongPrompt()
    {
        var longPrompt = new string('x', 200);
        var truncated = longPrompt[..80] + "…";
        var interruptMsg = $"⚠️ Your previous request was interrupted by an app restart. You may need to resend your last message.\n📝 Last message: \"{truncated}\"";
        var msg = ChatMessage.SystemMessage(interruptMsg);

        Assert.Contains("…", msg.Content);
        // The truncated version should be 80 chars + ellipsis, not the full 200
        Assert.DoesNotContain(longPrompt, msg.Content);
    }
    // --- Multiline system message detection (help output alignment) ---

    [Fact]
    public void SystemMessage_HelpOutput_IsMultiline()
    {
        var helpContent = "**Available commands:**\n" +
            "- `/help` — Show this help\n" +
            "- `/clear` — Clear chat history\n" +
            "- `/new [name]` — Create a new session";
        var msg = ChatMessage.SystemMessage(helpContent);

        Assert.True(msg.Content.Contains("\n-"), "Help output should be detected as multiline list content");
    }

    [Fact]
    public void SystemMessage_ShortMessage_IsNotMultiline()
    {
        var msg = ChatMessage.SystemMessage("Session cleared.");

        Assert.False(msg.Content.Contains("\n-"), "Short system messages should not be detected as multiline");
    }
}

public class ToolActivityTests
{
    [Fact]
    public void ElapsedDisplay_LessThanOneSecond_ShowsLessThan1s()
    {
        var activity = new ToolActivity
        {
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow.AddMilliseconds(500)
        };
        Assert.Equal("<1s", activity.ElapsedDisplay);
    }

    [Fact]
    public void ElapsedDisplay_MultipleSeconds_ShowsRoundedSeconds()
    {
        var activity = new ToolActivity
        {
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
            CompletedAt = DateTimeOffset.UtcNow
        };
        Assert.Equal("5s", activity.ElapsedDisplay);
    }

    [Fact]
    public void ElapsedDisplay_NotCompleted_UsesCurrentTime()
    {
        var activity = new ToolActivity
        {
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-2),
            CompletedAt = null
        };
        // Should be ~2s since it measures against DateTimeOffset.UtcNow
        var display = activity.ElapsedDisplay;
        Assert.Matches(@"^\d+s$", display);
    }

    [Fact]
    public void FactoryMethods_UseUtcTimestamps()
    {
        // All factory methods should produce UTC timestamps (issue #386)
        var before = DateTimeOffset.UtcNow;
        var user = ChatMessage.UserMessage("test");
        var assistant = ChatMessage.AssistantMessage("test");
        var system = ChatMessage.SystemMessage("test");
        var error = ChatMessage.ErrorMessage("test");
        var after = DateTimeOffset.UtcNow;

        Assert.Equal(TimeSpan.Zero, user.Timestamp.Offset);
        Assert.Equal(TimeSpan.Zero, assistant.Timestamp.Offset);
        Assert.Equal(TimeSpan.Zero, system.Timestamp.Offset);
        Assert.Equal(TimeSpan.Zero, error.Timestamp.Offset);

        Assert.InRange(user.Timestamp, before, after);
        Assert.InRange(assistant.Timestamp, before, after);
    }

    [Fact]
    public void Timestamp_IsDateTimeOffset()
    {
        // ChatMessage.Timestamp should be DateTimeOffset, not DateTime (issue #386)
        var msg = ChatMessage.UserMessage("test");
        Assert.IsType<DateTimeOffset>(msg.Timestamp);
    }

    [Fact]
    public void Timestamp_CrossTimezoneComparison_Works()
    {
        // The core bug: comparing UTC dispatch time with local message timestamps.
        // With DateTimeOffset, this comparison is timezone-aware.
        var utcTime = new DateTimeOffset(2026, 4, 23, 15, 0, 0, TimeSpan.Zero);
        var localTime = new DateTimeOffset(2026, 4, 23, 11, 0, 0, TimeSpan.FromHours(-4)); // Same instant, different offset

        var msg = new ChatMessage("user", "test", localTime);
        // These represent the same instant — comparison should be equal
        Assert.True(msg.Timestamp >= utcTime);
        Assert.True(msg.Timestamp <= utcTime);
    }

    [Fact]
    public void ToolActivity_UsesDateTimeOffset()
    {
        var activity = new ToolActivity
        {
            Name = "test",
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow.AddSeconds(5)
        };
        Assert.Equal(TimeSpan.Zero, activity.StartedAt.Offset);
        Assert.Equal(TimeSpan.Zero, activity.CompletedAt!.Value.Offset);
        Assert.Equal("5s", activity.ElapsedDisplay);
    }

    [Fact]
    public void DefaultConstructor_ProducesUtcTimestamp()
    {
        // Parameterless constructor (used by JSON deserialization) should produce UTC
        var msg = new ChatMessage();
        Assert.Equal(TimeSpan.Zero, msg.Timestamp.Offset);
    }
}
