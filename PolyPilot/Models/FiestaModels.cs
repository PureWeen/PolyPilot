namespace PolyPilot.Models;

public enum FiestaMemberRole
{
    Organizer,
    Worker
}

public class FiestaPeerInfo
{
    public string InstanceId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Platform { get; set; } = "";
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}

public class FiestaMember
{
    public string InstanceId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public FiestaMemberRole Role { get; set; } = FiestaMemberRole.Worker;
    public bool IsConnected { get; set; }
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
}

public class FiestaRoom
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string OrganizerInstanceId { get; set; } = "";
    public string OrganizerMachineName { get; set; } = "";
    public List<FiestaMember> Members { get; set; } = new();
}

public class FiestaJoinRequest
{
    public string RequestId { get; set; } = "";
    public string FiestaId { get; set; } = "";
    public string OrganizerInstanceId { get; set; } = "";
    public string OrganizerMachineName { get; set; } = "";
    public string JoinCode { get; set; } = "";
    public string? RemoteHost { get; set; }
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public FiestaJoinState Status { get; set; } = FiestaJoinState.Pending;
    public string? Reason { get; set; }
}

public class FiestaStateStore
{
    public List<FiestaRoom> Rooms { get; set; } = new();
}
