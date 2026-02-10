namespace AutoPilot.App.Models;

public class AgentSessionInfo
{
    public required string Name { get; set; }
    public required string Model { get; set; }
    public DateTime CreatedAt { get; init; }
    public int MessageCount { get; set; }
    public bool IsProcessing { get; set; }
    public List<ChatMessage> History { get; } = new();
    public List<string> MessageQueue { get; } = new();
    
    public string? WorkingDirectory { get; set; }
    public string? GitBranch { get; set; }
    
    // For resumed sessions
    public string? SessionId { get; set; }
    public bool IsResumed { get; init; }
    
    // Timestamp of last state change (message received, turn end, etc.)
    public DateTime LastUpdatedAt { get; set; } = DateTime.Now;
}
