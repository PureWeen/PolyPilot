using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

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
}

/// <summary>
/// Tests for CopilotService.MoveSession behaviour including the auto-create-meta fix.
/// </summary>
public class MoveSessionTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public MoveSessionTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    [Fact]
    public void MoveSession_WithExistingMeta_UpdatesGroupId()
    {
        var svc = CreateService();

        // Set up a group and a session meta
        var group = svc.CreateGroup("Work");
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "my-session",
            GroupId = SessionGroup.DefaultId
        });

        svc.MoveSession("my-session", group.Id);

        var meta = svc.Organization.Sessions.FirstOrDefault(m => m.SessionName == "my-session");
        Assert.NotNull(meta);
        Assert.Equal(group.Id, meta!.GroupId);
    }

    [Fact]
    public void MoveSession_WithoutExistingMeta_CreatesMetaInTargetGroup()
    {
        var svc = CreateService();

        // Create a group but do NOT add a SessionMeta for the session
        var group = svc.CreateGroup("Work");

        svc.MoveSession("orphan-session", group.Id);

        var meta = svc.Organization.Sessions.FirstOrDefault(m => m.SessionName == "orphan-session");
        Assert.NotNull(meta);
        Assert.Equal(group.Id, meta!.GroupId);
    }

    [Fact]
    public void MoveSession_ToNonExistentGroup_DoesNothing()
    {
        var svc = CreateService();

        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "my-session",
            GroupId = SessionGroup.DefaultId
        });

        svc.MoveSession("my-session", "non-existent-group");

        var meta = svc.Organization.Sessions.FirstOrDefault(m => m.SessionName == "my-session");
        Assert.NotNull(meta);
        Assert.Equal(SessionGroup.DefaultId, meta!.GroupId);
    }

    [Fact]
    public void MoveSession_BetweenGroups_UpdatesCorrectly()
    {
        var svc = CreateService();

        var groupA = svc.CreateGroup("Group A");
        var groupB = svc.CreateGroup("Group B");
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "my-session",
            GroupId = groupA.Id
        });

        // Move from A to B
        svc.MoveSession("my-session", groupB.Id);

        var meta = svc.Organization.Sessions.FirstOrDefault(m => m.SessionName == "my-session");
        Assert.NotNull(meta);
        Assert.Equal(groupB.Id, meta!.GroupId);
    }

    [Fact]
    public void MoveSession_BackToDefaultGroup_Works()
    {
        var svc = CreateService();

        var group = svc.CreateGroup("Custom");
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "my-session",
            GroupId = group.Id
        });

        svc.MoveSession("my-session", SessionGroup.DefaultId);

        var meta = svc.Organization.Sessions.FirstOrDefault(m => m.SessionName == "my-session");
        Assert.NotNull(meta);
        Assert.Equal(SessionGroup.DefaultId, meta!.GroupId);
    }

    [Fact]
    public void MoveSession_FiresStateChanged()
    {
        var svc = CreateService();

        var group = svc.CreateGroup("Work");
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "my-session",
            GroupId = SessionGroup.DefaultId
        });

        bool stateChanged = false;
        svc.OnStateChanged += () => stateChanged = true;

        svc.MoveSession("my-session", group.Id);

        Assert.True(stateChanged);
    }
}
