namespace AutoPilot.App.Models;

public enum ChatMessageType
{
    User,
    Assistant,
    Reasoning,
    ToolCall,
    Error,
    System
}

public class ChatMessage
{
    // Parameterless constructor for JSON deserialization
    public ChatMessage() : this("assistant", "", DateTime.Now) { }

    public ChatMessage(string role, string content, DateTime timestamp, ChatMessageType messageType = ChatMessageType.User)
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
    public DateTime Timestamp { get; set; }
    public ChatMessageType MessageType { get; set; }

    // Tool call fields
    public string? ToolName { get; set; }
    public string? ToolCallId { get; set; }
    public string? ToolInput { get; set; }
    public bool IsComplete { get; set; } = true;
    public bool IsCollapsed { get; set; }
    public bool IsSuccess { get; set; }

    // Reasoning fields
    public string? ReasoningId { get; set; }

    // Convenience properties
    public bool IsUser => Role == "user";
    public bool IsAssistant => Role == "assistant";

    // Factory methods
    public static ChatMessage UserMessage(string content) =>
        new("user", content, DateTime.Now, ChatMessageType.User) { IsComplete = true };

    public static ChatMessage AssistantMessage(string content) =>
        new("assistant", content, DateTime.Now, ChatMessageType.Assistant) { IsComplete = true };

    public static ChatMessage ReasoningMessage(string reasoningId) =>
        new("assistant", "", DateTime.Now, ChatMessageType.Reasoning) { ReasoningId = reasoningId, IsComplete = false, IsCollapsed = false };

    public static ChatMessage ToolCallMessage(string toolName, string? toolCallId = null, string? toolInput = null) =>
        new("assistant", "", DateTime.Now, ChatMessageType.ToolCall) { ToolName = toolName, ToolCallId = toolCallId, ToolInput = toolInput, IsComplete = false };

    public static ChatMessage ErrorMessage(string content, string? toolName = null) =>
        new("assistant", content, DateTime.Now, ChatMessageType.Error) { ToolName = toolName, IsComplete = true };

    public static ChatMessage SystemMessage(string content) =>
        new("system", content, DateTime.Now, ChatMessageType.System) { IsComplete = true };
}

public class ToolActivity
{
    public string Name { get; set; } = "";
    public string CallId { get; set; } = "";
    public string? Input { get; set; }
    public DateTime StartedAt { get; set; }
    public bool IsComplete { get; set; }
    public bool IsSuccess { get; set; }
    public string? Result { get; set; }
    public DateTime? CompletedAt { get; set; }

    public string ElapsedDisplay
    {
        get
        {
            var end = CompletedAt ?? DateTime.Now;
            var elapsed = end - StartedAt;
            return elapsed.TotalSeconds < 1 ? "<1s" : $"{elapsed.TotalSeconds:F0}s";
        }
    }
}
