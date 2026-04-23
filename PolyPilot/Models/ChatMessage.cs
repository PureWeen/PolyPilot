namespace PolyPilot.Models;

public enum ChatMessageType
{
    User,
    Assistant,
    Reasoning,
    ToolCall,
    Error,
    System,
    ShellOutput,
    Diff,
    Reflection,
    Image
}

public class ChatMessage
{
    // Parameterless constructor for JSON deserialization
    public ChatMessage() : this("assistant", "", DateTimeOffset.UtcNow) { }

    public ChatMessage(string role, string content, DateTimeOffset timestamp, ChatMessageType messageType = ChatMessageType.User)
    {
        Role = role;
        Content = content;
        Timestamp = timestamp;
        MessageType = messageType;

        if (role == "user") MessageType = ChatMessageType.User;
        else if (messageType == ChatMessageType.User) MessageType = ChatMessageType.Assistant;
    }

    public string Role { get; set; }
    public string Content { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public ChatMessageType MessageType { get; set; }

    // Tool call fields
    public string? ToolName { get; set; }
    public string? ToolCallId { get; set; }
    public string? ToolInput { get; set; }
    public bool IsComplete { get; set; } = true;
    public bool IsCollapsed { get; set; } = true;
    public bool IsSuccess { get; set; }
    // True when the response was cut off by a steering message (user interrupted)
    public bool IsInterrupted { get; set; }

    // Reasoning fields
    public string? ReasoningId { get; set; }

    // Image fields (for ChatMessageType.Image)
    public string? ImagePath { get; set; }
    public string? ImageDataUri { get; set; }
    public string? Caption { get; set; }

    // When set, Content is a wrapped/orchestration prompt and this holds the user's original text
    public string? OriginalContent { get; set; }

    // Model that generated this message
    public string? Model { get; set; }

    // Convenience properties
    public bool IsUser => Role == "user";
    public bool IsAssistant => Role == "assistant";

    // Factory methods
    public static ChatMessage UserMessage(string content) =>
        new("user", content, DateTimeOffset.UtcNow, ChatMessageType.User) { IsComplete = true };

    public static ChatMessage AssistantMessage(string content) =>
        new("assistant", content, DateTimeOffset.UtcNow, ChatMessageType.Assistant) { IsComplete = true };

    public static ChatMessage ReasoningMessage(string reasoningId) =>
        new("assistant", "", DateTimeOffset.UtcNow, ChatMessageType.Reasoning) { ReasoningId = reasoningId, IsComplete = false, IsCollapsed = false };

    public static ChatMessage ToolCallMessage(string toolName, string? toolCallId = null, string? toolInput = null) =>
        new("assistant", "", DateTimeOffset.UtcNow, ChatMessageType.ToolCall) { ToolName = toolName, ToolCallId = toolCallId, ToolInput = toolInput, IsComplete = false };

    public static ChatMessage ErrorMessage(string content, string? toolName = null) =>
        new("assistant", content, DateTimeOffset.UtcNow, ChatMessageType.Error) { ToolName = toolName, IsComplete = true };

    public static ChatMessage SystemMessage(string content) =>
        new("system", content, DateTimeOffset.UtcNow, ChatMessageType.System) { IsComplete = true };

    public static ChatMessage ShellOutputMessage(string content, int exitCode = 0) =>
        new("system", content, DateTimeOffset.UtcNow, ChatMessageType.ShellOutput) { IsComplete = true, IsSuccess = exitCode == 0 };

    public static ChatMessage DiffMessage(string rawDiff) =>
        new("system", rawDiff, DateTimeOffset.UtcNow, ChatMessageType.Diff) { IsComplete = true };

    public static ChatMessage ReflectionMessage(string content) =>
        new("system", content, DateTimeOffset.UtcNow, ChatMessageType.Reflection) { IsComplete = true };

    public static ChatMessage ImageMessage(string? imagePath, string? imageDataUri, string? caption = null, string? toolCallId = null) =>
        new("assistant", "", DateTimeOffset.UtcNow, ChatMessageType.Image) { ImagePath = imagePath, ImageDataUri = imageDataUri, Caption = caption, ToolCallId = toolCallId, ToolName = "show_image", IsComplete = true, IsSuccess = true };
}

public class ToolActivity
{
    public string Name { get; set; } = "";
    public string CallId { get; set; } = "";
    public string? Input { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public bool IsComplete { get; set; }
    public bool IsSuccess { get; set; }
    public string? Result { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public string ElapsedDisplay
    {
        get
        {
            var end = CompletedAt ?? DateTimeOffset.UtcNow;
            var elapsed = end - StartedAt;
            return elapsed.TotalSeconds < 1 ? "<1s" : $"{elapsed.TotalSeconds:F0}s";
        }
    }
}
