using System.Text.Json.Serialization;

namespace PolyPilot.Models;

public class FiestaDiscoveredWorker
{
    public string InstanceId { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string BridgeUrl { get; set; } = "";
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}

public class FiestaLinkedWorker
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string BridgeUrl { get; set; } = "";
    public string Token { get; set; } = "";
    public DateTime LinkedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.MinValue;

    [JsonIgnore]
    public bool IsOnline { get; set; }
}

public class FiestaState
{
    public List<FiestaLinkedWorker> LinkedWorkers { get; set; } = new();
}

public class FiestaSessionState
{
    public string SessionName { get; set; } = "";
    public string FiestaName { get; set; } = "";
    public List<string> WorkerIds { get; set; } = new();
}

public class FiestaStartRequest
{
    public string FiestaName { get; set; } = "";
    public List<string> WorkerIds { get; set; } = new();
}

public enum FiestaTaskUpdateKind
{
    Started,
    Delta,
    Completed,
    Error,
    Info
}

public class FiestaTaskUpdate
{
    public string TaskId { get; set; } = "";
    public string WorkerName { get; set; } = "";
    public FiestaTaskUpdateKind Kind { get; set; }
    public string Content { get; set; } = "";
    public bool Success { get; set; }
}

public class FiestaDispatchResult
{
    public bool MentionsFound { get; set; }
    public int DispatchCount { get; set; }
    public List<string> UnresolvedMentions { get; set; } = new();
}
