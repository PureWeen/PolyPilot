namespace PolyPilot.Models;

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
    
    // Track last skill used for display
    public string? LastSkillUsed { get; set; }
    public int SkillTrackingAttempts { get; set; } // DEBUG: count how many times we try to track
    public int SkillArgsFound { get; set; } // DEBUG: args object found
    public int SkillDictCast { get; set; } // DEBUG: cast to Dictionary succeeded
    public int SkillValueFound { get; set; } // DEBUG: skill value extracted
    public int SkillSetSuccess { get; set; } // DEBUG: LastSkillUsed set successfully
    public string? ArgsTypeName { get; set; } // DEBUG: actual type of Arguments
    
    // Accumulated token usage across all turns
    public int TotalInputTokens { get; set; }
    public int TotalOutputTokens { get; set; }
    public int? ContextCurrentTokens { get; set; }
    public int? ContextTokenLimit { get; set; }
}
