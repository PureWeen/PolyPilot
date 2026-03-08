namespace PolyPilot.Provider;

public class ProviderChatMessage
{
    public string Role { get; init; } = "assistant";
    public string Content { get; init; } = "";
    public DateTime Timestamp { get; init; }
    public ProviderMessageType Type { get; init; }
    public string? ToolName { get; init; }
    public string? ToolCallId { get; init; }
    public bool IsComplete { get; init; } = true;
}

public enum ProviderMessageType
{
    User,
    Assistant,
    Reasoning,
    ToolCall,
    Error,
    System
}
