using System.Text.Json.Serialization;

namespace PolyPilot.Models;

public class UsageStatistics
{
    public int TotalSessionsCreated { get; set; }
    public int TotalSessionsClosed { get; set; }
    public long TotalSessionTimeSeconds { get; set; }
    public long LongestSessionSeconds { get; set; }
    public long TotalLinesSuggested { get; set; }
    public int TotalMessagesReceived { get; set; }
    public DateTime FirstUsedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
    
    // Active session tracking (not persisted, used for calculating durations)
    [JsonIgnore]
    public Dictionary<string, DateTime> ActiveSessions { get; set; } = new();
}
