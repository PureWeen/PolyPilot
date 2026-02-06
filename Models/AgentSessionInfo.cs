namespace AutoPilot.App.Models;

public class AgentSessionInfo
{
    public required string Name { get; init; }
    public required string Model { get; init; }
    public DateTime CreatedAt { get; init; }
    public int MessageCount { get; set; }
    public bool IsProcessing { get; set; }
    public List<ChatMessage> History { get; } = new();
    
    public string? WorkingDirectory { get; set; }
    
    // For resumed sessions
    public string? SessionId { get; set; }
    public bool IsResumed { get; init; }
}
