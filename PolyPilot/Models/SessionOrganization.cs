using System.Text.Json.Serialization;

namespace PolyPilot.Models;

public class SessionGroup
{
    public const string DefaultId = "_default";
    public const string DefaultName = "Sessions";

    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsCollapsed { get; set; }
}

public class SessionMeta
{
    public string SessionName { get; set; } = "";
    public string GroupId { get; set; } = SessionGroup.DefaultId;
    public bool IsPinned { get; set; }
    public int ManualOrder { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SessionSortMode
{
    LastActive,
    CreatedAt,
    Alphabetical,
    Manual
}

public class OrganizationState
{
    public List<SessionGroup> Groups { get; set; } = new()
    {
        new SessionGroup { Id = SessionGroup.DefaultId, Name = SessionGroup.DefaultName, SortOrder = 0 }
    };
    public List<SessionMeta> Sessions { get; set; } = new();
    public SessionSortMode SortMode { get; set; } = SessionSortMode.LastActive;
}
