using System.Text.Json.Serialization;

namespace PolyPilot.Models;

public enum FiestaMemberRole
{
    Organizer,
    Worker
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FiestaDiscoverySource
{
    LanMulticast,
    TailscalePoll,
    TailnetAnnouncement,
    Manual
}

public class FiestaPeerInfo
{
    public string InstanceId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Platform { get; set; } = "";
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    public FiestaDiscoverySource DiscoverySource { get; set; } = FiestaDiscoverySource.LanMulticast;
    public bool IsWorkerAvailable { get; set; }
    public bool IsTailscale { get; set; }
    public string? TailnetHost { get; set; }
    public string? AdvertisedJoinCode { get; set; }
}

public class FiestaRegisteredWorker
{
    public string InstanceId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Platform { get; set; } = "";
    public string? TailnetHost { get; set; }
    public string JoinCode { get; set; } = "";
    public string PairingToken { get; set; } = "";
    public bool IsConnected { get; set; }
    public DateTime LastConnectedAt { get; set; } = DateTime.MinValue;
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
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
    public string SessionName { get; set; } = "fiesta-session";
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
    public string? LastSummary { get; set; }
}

public class FiestaJoinRequest
{
    public string RequestId { get; set; } = "";
    public string FiestaId { get; set; } = "";
    public string OrganizerInstanceId { get; set; } = "";
    public string OrganizerMachineName { get; set; } = "";
    public string OrganizerTrustToken { get; set; } = "";
    public string JoinCode { get; set; } = "";
    public string? RemoteHost { get; set; }
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public FiestaJoinState Status { get; set; } = FiestaJoinState.Pending;
    public string? Reason { get; set; }
    public bool AutoApproved { get; set; }
}

public class FiestaTrustedOrganizer
{
    public string OrganizerInstanceId { get; set; } = "";
    public string TrustToken { get; set; } = "";
}

public class FiestaGroup
{
    public const string DefaultId = "_default";
    public const string DefaultName = "Fiestas";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsCollapsed { get; set; }
}

public class FiestaRoomMeta
{
    public string RoomId { get; set; } = "";
    public string GroupId { get; set; } = FiestaGroup.DefaultId;
    public bool IsPinned { get; set; }
    public int ManualOrder { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FiestaSortMode
{
    LastActivity,
    CreatedAt,
    Alphabetical,
    Manual
}

public class FiestaOrganizationState
{
    public List<FiestaGroup> Groups { get; set; } = new()
    {
        new FiestaGroup { Id = FiestaGroup.DefaultId, Name = FiestaGroup.DefaultName, SortOrder = 0 }
    };

    public List<FiestaRoomMeta> Rooms { get; set; } = new();
    public FiestaSortMode SortMode { get; set; } = FiestaSortMode.LastActivity;
}

public class FiestaStateStore
{
    public List<FiestaRoom> Rooms { get; set; } = new();
    public List<FiestaRegisteredWorker> RegisteredWorkers { get; set; } = new();
    public List<string> TrustedOrganizers { get; set; } = new();
    public List<FiestaTrustedOrganizer> TrustedOrganizerRecords { get; set; } = new();
    public FiestaOrganizationState Organization { get; set; } = new();
}
