using System.Text.Json;
using PolyPilot.Models;

namespace PolyPilot.Tests;

public class SessionOrganizationTests
{
    [Fact]
    public void DefaultState_HasDefaultGroup()
    {
        var state = new OrganizationState();
        Assert.Single(state.Groups);
        Assert.Equal(SessionGroup.DefaultId, state.Groups[0].Id);
        Assert.Equal(SessionGroup.DefaultName, state.Groups[0].Name);
    }

    [Fact]
    public void DefaultState_HasLastActiveSortMode()
    {
        var state = new OrganizationState();
        Assert.Equal(SessionSortMode.LastActive, state.SortMode);
    }

    [Fact]
    public void SessionMeta_DefaultsToDefaultGroup()
    {
        var meta = new SessionMeta { SessionName = "test" };
        Assert.Equal(SessionGroup.DefaultId, meta.GroupId);
        Assert.False(meta.IsPinned);
        Assert.Equal(0, meta.ManualOrder);
    }

    [Fact]
    public void Serialization_RoundTrips()
    {
        var state = new OrganizationState
        {
            SortMode = SessionSortMode.Alphabetical
        };
        state.Groups.Add(new SessionGroup
        {
            Id = "custom-1",
            Name = "Work",
            SortOrder = 1,
            IsCollapsed = true
        });
        state.Sessions.Add(new SessionMeta
        {
            SessionName = "my-session",
            GroupId = "custom-1",
            IsPinned = true,
            ManualOrder = 3
        });

        var json = JsonSerializer.Serialize(state);
        var deserialized = JsonSerializer.Deserialize<OrganizationState>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized!.Groups.Count);
        Assert.Equal(SessionSortMode.Alphabetical, deserialized.SortMode);

        var customGroup = deserialized.Groups.Find(g => g.Id == "custom-1");
        Assert.NotNull(customGroup);
        Assert.Equal("Work", customGroup!.Name);
        Assert.True(customGroup.IsCollapsed);
        Assert.Equal(1, customGroup.SortOrder);

        var meta = deserialized.Sessions[0];
        Assert.Equal("my-session", meta.SessionName);
        Assert.Equal("custom-1", meta.GroupId);
        Assert.True(meta.IsPinned);
        Assert.Equal(3, meta.ManualOrder);
    }

    [Fact]
    public void SortMode_SerializesAsString()
    {
        var state = new OrganizationState { SortMode = SessionSortMode.CreatedAt };
        var json = JsonSerializer.Serialize(state);
        Assert.Contains("\"CreatedAt\"", json);
    }

    [Fact]
    public void EmptyState_DeserializesGracefully()
    {
        var json = "{}";
        var state = JsonSerializer.Deserialize<OrganizationState>(json);
        Assert.NotNull(state);
        // Default group is created by constructor
        Assert.Single(state!.Groups);
        Assert.Equal(SessionGroup.DefaultId, state.Groups[0].Id);
    }

    [Fact]
    public void SessionGroup_DefaultConstants()
    {
        Assert.Equal("_default", SessionGroup.DefaultId);
        Assert.Equal("Sessions", SessionGroup.DefaultName);
    }

    [Fact]
    public void OrganizationCommandPayload_Serializes()
    {
        var cmd = new OrganizationCommandPayload
        {
            Command = "pin",
            SessionName = "test-session"
        };
        var json = JsonSerializer.Serialize(cmd, BridgeJson.Options);
        Assert.Contains("pin", json);
        Assert.Contains("test-session", json);

        var deserialized = JsonSerializer.Deserialize<OrganizationCommandPayload>(json, BridgeJson.Options);
        Assert.NotNull(deserialized);
        Assert.Equal("pin", deserialized!.Command);
        Assert.Equal("test-session", deserialized.SessionName);
    }

    [Fact]
    public void SessionGroup_MultiAgent_DefaultsToFalse()
    {
        var group = new SessionGroup { Name = "Test" };
        Assert.False(group.IsMultiAgent);
        Assert.Equal(MultiAgentMode.Broadcast, group.OrchestratorMode);
        Assert.Null(group.OrchestratorPrompt);
    }

    [Fact]
    public void SessionGroup_MultiAgent_Serializes()
    {
        var group = new SessionGroup
        {
            Name = "Multi-Agent Team",
            IsMultiAgent = true,
            OrchestratorMode = MultiAgentMode.Orchestrator,
            OrchestratorPrompt = "You are the lead coordinator."
        };

        var json = JsonSerializer.Serialize(group);
        var deserialized = JsonSerializer.Deserialize<SessionGroup>(json);

        Assert.NotNull(deserialized);
        Assert.True(deserialized!.IsMultiAgent);
        Assert.Equal(MultiAgentMode.Orchestrator, deserialized.OrchestratorMode);
        Assert.Equal("You are the lead coordinator.", deserialized.OrchestratorPrompt);
    }

    [Fact]
    public void SessionMeta_Role_DefaultsToWorker()
    {
        var meta = new SessionMeta { SessionName = "test" };
        Assert.Equal(MultiAgentRole.Worker, meta.Role);
    }

    [Fact]
    public void SessionMeta_Role_SerializesAsString()
    {
        var meta = new SessionMeta
        {
            SessionName = "leader",
            Role = MultiAgentRole.Orchestrator
        };
        var json = JsonSerializer.Serialize(meta);
        Assert.Contains("\"Orchestrator\"", json);

        var deserialized = JsonSerializer.Deserialize<SessionMeta>(json);
        Assert.NotNull(deserialized);
        Assert.Equal(MultiAgentRole.Orchestrator, deserialized!.Role);
    }

    [Fact]
    public void MultiAgentMode_AllValues()
    {
        Assert.Equal(3, Enum.GetValues<MultiAgentMode>().Length);
        Assert.True(Enum.IsDefined(MultiAgentMode.Broadcast));
        Assert.True(Enum.IsDefined(MultiAgentMode.Sequential));
        Assert.True(Enum.IsDefined(MultiAgentMode.Orchestrator));
    }

    [Fact]
    public void MultiAgentMode_SerializesAsString()
    {
        var group = new SessionGroup
        {
            Name = "test",
            OrchestratorMode = MultiAgentMode.Sequential
        };
        var json = JsonSerializer.Serialize(group);
        Assert.Contains("\"Sequential\"", json);
    }

    [Fact]
    public void OrganizationState_MultiAgentGroup_RoundTrips()
    {
        var state = new OrganizationState();
        var maGroup = new SessionGroup
        {
            Id = "ma-group-1",
            Name = "Dev Team",
            IsMultiAgent = true,
            OrchestratorMode = MultiAgentMode.Orchestrator,
            OrchestratorPrompt = "Coordinate the workers",
            SortOrder = 1
        };
        state.Groups.Add(maGroup);
        state.Sessions.Add(new SessionMeta
        {
            SessionName = "orchestrator-session",
            GroupId = "ma-group-1",
            Role = MultiAgentRole.Orchestrator
        });
        state.Sessions.Add(new SessionMeta
        {
            SessionName = "worker-1",
            GroupId = "ma-group-1",
            Role = MultiAgentRole.Worker
        });

        var json = JsonSerializer.Serialize(state);
        var deserialized = JsonSerializer.Deserialize<OrganizationState>(json);

        Assert.NotNull(deserialized);
        var group = deserialized!.Groups.Find(g => g.Id == "ma-group-1");
        Assert.NotNull(group);
        Assert.True(group!.IsMultiAgent);
        Assert.Equal(MultiAgentMode.Orchestrator, group.OrchestratorMode);
        Assert.Equal("Coordinate the workers", group.OrchestratorPrompt);

        var orchSession = deserialized.Sessions.Find(s => s.SessionName == "orchestrator-session");
        Assert.NotNull(orchSession);
        Assert.Equal(MultiAgentRole.Orchestrator, orchSession!.Role);

        var workerSession = deserialized.Sessions.Find(s => s.SessionName == "worker-1");
        Assert.NotNull(workerSession);
        Assert.Equal(MultiAgentRole.Worker, workerSession!.Role);
    }

    [Fact]
    public void LegacyState_WithoutMultiAgent_DeserializesGracefully()
    {
        // Simulates loading organization.json from before multi-agent was added
        var json = """
        {
            "Groups": [
                {"Id": "_default", "Name": "Sessions", "SortOrder": 0}
            ],
            "Sessions": [
                {"SessionName": "old-session", "GroupId": "_default", "IsPinned": false}
            ],
            "SortMode": "LastActive"
        }
        """;
        var state = JsonSerializer.Deserialize<OrganizationState>(json);
        Assert.NotNull(state);
        Assert.False(state!.Groups[0].IsMultiAgent);
        Assert.Equal(MultiAgentMode.Broadcast, state.Groups[0].OrchestratorMode);
        Assert.Null(state.Groups[0].OrchestratorPrompt);
        Assert.Equal(MultiAgentRole.Worker, state.Sessions[0].Role);
    }

    [Fact]
    public void OrchestratorInvariant_PromotingNewOrchestrator_DemotesPrevious()
    {
        var state = new OrganizationState();
        var group = new SessionGroup
        {
            Id = "ma-group-1",
            Name = "Team",
            IsMultiAgent = true,
            OrchestratorMode = MultiAgentMode.Orchestrator
        };
        state.Groups.Add(group);

        var session1 = new SessionMeta { SessionName = "s1", GroupId = "ma-group-1", Role = MultiAgentRole.Orchestrator };
        var session2 = new SessionMeta { SessionName = "s2", GroupId = "ma-group-1", Role = MultiAgentRole.Worker };
        var session3 = new SessionMeta { SessionName = "s3", GroupId = "ma-group-1", Role = MultiAgentRole.Worker };
        state.Sessions.Add(session1);
        state.Sessions.Add(session2);
        state.Sessions.Add(session3);

        // Simulate the demotion logic from SetSessionRole
        foreach (var other in state.Sessions.Where(m => m.GroupId == "ma-group-1" && m.SessionName != "s2" && m.Role == MultiAgentRole.Orchestrator))
        {
            other.Role = MultiAgentRole.Worker;
        }
        session2.Role = MultiAgentRole.Orchestrator;

        Assert.Equal(MultiAgentRole.Worker, session1.Role);
        Assert.Equal(MultiAgentRole.Orchestrator, session2.Role);
        Assert.Equal(MultiAgentRole.Worker, session3.Role);
        Assert.Single(state.Sessions, s => s.GroupId == "ma-group-1" && s.Role == MultiAgentRole.Orchestrator);
    }

    [Fact]
    public void MultiAgentSetRolePayload_Serializes()
    {
        var payload = new MultiAgentSetRolePayload
        {
            SessionName = "worker-1",
            Role = "Orchestrator"
        };
        var json = JsonSerializer.Serialize(payload, BridgeJson.Options);
        Assert.Contains("worker-1", json);
        Assert.Contains("Orchestrator", json);

        var deserialized = JsonSerializer.Deserialize<MultiAgentSetRolePayload>(json, BridgeJson.Options);
        Assert.NotNull(deserialized);
        Assert.Equal("worker-1", deserialized!.SessionName);
        Assert.Equal("Orchestrator", deserialized.Role);
    }

    [Fact]
    public void MultiAgentSetRole_BridgeMessageType_Exists()
    {
        Assert.Equal("multi_agent_set_role", BridgeMessageTypes.MultiAgentSetRole);
    }
}
