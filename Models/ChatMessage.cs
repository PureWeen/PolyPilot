namespace AutoPilot.App.Models;

public record ChatMessage(string Role, string Content, DateTime Timestamp)
{
    public bool IsUser => Role == "user";
    public bool IsAssistant => Role == "assistant";
    public bool IsSystem => Role == "system";
}
