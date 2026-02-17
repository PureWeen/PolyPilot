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

/// <summary>
/// Tests for repo-based session grouping: GetOrCreateRepoGroup and ReconcileOrganization.
/// </summary>
public class RepoGroupingTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly IServiceProvider _serviceProvider;

    public RepoGroupingTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private static RepoManager CreateRepoManagerWithState(List<RepositoryInfo> repos, List<WorktreeInfo> worktrees)
    {
        var rm = new RepoManager();
        var stateField = typeof(RepoManager).GetField("_state", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var loadedField = typeof(RepoManager).GetField("_loaded", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        stateField.SetValue(rm, new RepositoryState { Repositories = repos, Worktrees = worktrees });
        loadedField.SetValue(rm, true);
        return rm;
    }

    private CopilotService CreateService(RepoManager? repoManager = null) =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, repoManager ?? new RepoManager(), _serviceProvider, _demoService);

    [Fact]
    public void GetOrCreateRepoGroup_CreatesNewGroup()
    {
        var svc = CreateService();
        var group = svc.GetOrCreateRepoGroup("repo-1", "MyRepo");

        Assert.NotNull(group);
        Assert.Equal("MyRepo", group.Name);
        Assert.Equal("repo-1", group.RepoId);
        Assert.Contains(svc.Organization.Groups, g => g.RepoId == "repo-1");
    }

    [Fact]
    public void GetOrCreateRepoGroup_ReturnsExisting()
    {
        var svc = CreateService();
        var first = svc.GetOrCreateRepoGroup("repo-1", "MyRepo");
        var second = svc.GetOrCreateRepoGroup("repo-1", "MyRepo");

        Assert.Same(first, second);
        Assert.Single(svc.Organization.Groups, g => g.RepoId == "repo-1");
    }

    [Fact]
    public void GetOrCreateRepoGroup_DifferentRepos_CreatesSeparateGroups()
    {
        var svc = CreateService();
        var g1 = svc.GetOrCreateRepoGroup("repo-1", "RepoA");
        var g2 = svc.GetOrCreateRepoGroup("repo-2", "RepoB");

        Assert.NotEqual(g1.Id, g2.Id);
        Assert.Equal(2, svc.Organization.Groups.Count(g => g.RepoId != null));
    }

    [Fact]
    public void GetOrCreateRepoGroup_SetsIncrementingSortOrder()
    {
        var svc = CreateService();
        var g1 = svc.GetOrCreateRepoGroup("repo-1", "RepoA");
        var g2 = svc.GetOrCreateRepoGroup("repo-2", "RepoB");

        Assert.True(g2.SortOrder > g1.SortOrder);
    }

    [Fact]
    public void HasMultipleGroups_TrueWhenRepoGroupExists()
    {
        var svc = CreateService();
        Assert.False(svc.HasMultipleGroups);

        svc.GetOrCreateRepoGroup("repo-1", "MyRepo");
        Assert.True(svc.HasMultipleGroups);
    }

    [Fact]
    public void Reconcile_SessionInDefaultGroup_WithWorktreeId_GetsReassigned()
    {
        var repos = new List<RepositoryInfo>
        {
            new() { Id = "repo-1", Name = "MyRepo", Url = "https://github.com/test/repo" }
        };
        var worktrees = new List<WorktreeInfo>
        {
            new() { Id = "wt-1", RepoId = "repo-1", Branch = "main", Path = "/tmp/worktree-1" }
        };
        var rm = CreateRepoManagerWithState(repos, worktrees);
        var svc = CreateService(rm);

        // Manually move session to repo group via public API (simulates what ReconcileOrganization does)
        var repoGroup = svc.GetOrCreateRepoGroup("repo-1", "MyRepo");
        var meta = new SessionMeta
        {
            SessionName = "test-session",
            GroupId = SessionGroup.DefaultId,
            WorktreeId = "wt-1"
        };
        svc.Organization.Sessions.Add(meta);

        // Verify the session starts in default
        Assert.Equal(SessionGroup.DefaultId, meta.GroupId);

        // Simulate what ReconcileOrganization does: find worktree, get repo, move to group
        var wt = rm.Worktrees.FirstOrDefault(w => w.Id == meta.WorktreeId);
        Assert.NotNull(wt);
        var repo = rm.Repositories.FirstOrDefault(r => r.Id == wt!.RepoId);
        Assert.NotNull(repo);
        var group = svc.GetOrCreateRepoGroup(repo!.Id, repo.Name);
        meta.GroupId = group.Id;

        // Verify reassignment
        Assert.Equal(repoGroup.Id, meta.GroupId);
        Assert.NotEqual(SessionGroup.DefaultId, meta.GroupId);
    }

    [Fact]
    public void Reconcile_SessionWithoutWorktree_StaysInDefaultGroup()
    {
        var rm = CreateRepoManagerWithState(new(), new());
        var svc = CreateService(rm);

        var meta = new SessionMeta
        {
            SessionName = "ungrouped",
            GroupId = SessionGroup.DefaultId,
            WorktreeId = null
        };
        svc.Organization.Sessions.Add(meta);

        // No worktree => can't reassign => stays in default
        Assert.Null(meta.WorktreeId);
        Assert.Equal(SessionGroup.DefaultId, meta.GroupId);
    }

    [Fact]
    public void Reconcile_SessionAlreadyInRepoGroup_StaysInRepoGroup()
    {
        var repos = new List<RepositoryInfo>
        {
            new() { Id = "repo-1", Name = "MyRepo", Url = "https://github.com/test/repo" }
        };
        var worktrees = new List<WorktreeInfo>
        {
            new() { Id = "wt-1", RepoId = "repo-1", Branch = "main", Path = "/tmp/worktree-1" }
        };
        var rm = CreateRepoManagerWithState(repos, worktrees);
        var svc = CreateService(rm);

        var repoGroup = svc.GetOrCreateRepoGroup("repo-1", "MyRepo");
        var meta = new SessionMeta
        {
            SessionName = "test-session",
            GroupId = repoGroup.Id,
            WorktreeId = "wt-1"
        };
        svc.Organization.Sessions.Add(meta);

        // Already in repo group — GetOrCreateRepoGroup returns same group
        var group = svc.GetOrCreateRepoGroup("repo-1", "MyRepo");
        Assert.Equal(repoGroup.Id, group.Id);
        Assert.Equal(repoGroup.Id, meta.GroupId);
    }

    [Fact]
    public void Reconcile_MultipleSessionsDifferentRepos_AllGetReassigned()
    {
        var repos = new List<RepositoryInfo>
        {
            new() { Id = "repo-1", Name = "RepoA", Url = "https://github.com/test/a" },
            new() { Id = "repo-2", Name = "RepoB", Url = "https://github.com/test/b" }
        };
        var worktrees = new List<WorktreeInfo>
        {
            new() { Id = "wt-1", RepoId = "repo-1", Branch = "main", Path = "/tmp/wt-1" },
            new() { Id = "wt-2", RepoId = "repo-2", Branch = "main", Path = "/tmp/wt-2" }
        };
        var rm = CreateRepoManagerWithState(repos, worktrees);
        var svc = CreateService(rm);

        var groupA = svc.GetOrCreateRepoGroup("repo-1", "RepoA");
        var groupB = svc.GetOrCreateRepoGroup("repo-2", "RepoB");

        var metaA = new SessionMeta { SessionName = "session-a", GroupId = SessionGroup.DefaultId, WorktreeId = "wt-1" };
        var metaB = new SessionMeta { SessionName = "session-b", GroupId = SessionGroup.DefaultId, WorktreeId = "wt-2" };
        svc.Organization.Sessions.Add(metaA);
        svc.Organization.Sessions.Add(metaB);

        // Simulate reconciliation: look up worktree -> repo -> group
        foreach (var meta in svc.Organization.Sessions.Where(m => m.WorktreeId != null && m.GroupId == SessionGroup.DefaultId))
        {
            var wt = rm.Worktrees.FirstOrDefault(w => w.Id == meta.WorktreeId);
            if (wt != null)
            {
                var repo = rm.Repositories.FirstOrDefault(r => r.Id == wt.RepoId);
                if (repo != null)
                    meta.GroupId = svc.GetOrCreateRepoGroup(repo.Id, repo.Name).Id;
            }
        }

        Assert.Equal(groupA.Id, metaA.GroupId);
        Assert.Equal(groupB.Id, metaB.GroupId);
    }

    [Fact]
    public void ParseTaskAssignments_ExtractsWorkerTasks()
    {
        var response = @"Here's my plan:

@worker:session-a
Implement the login form with email and password fields.
@end

@worker:session-b
Create the API endpoint for user authentication.
@end

That covers the full task.";

        var workers = new List<string> { "session-a", "session-b" };
        var assignments = CopilotService.ParseTaskAssignments(response, workers);

        Assert.Equal(2, assignments.Count);
        Assert.Equal("session-a", assignments[0].WorkerName);
        Assert.Contains("login form", assignments[0].Task);
        Assert.Equal("session-b", assignments[1].WorkerName);
        Assert.Contains("API endpoint", assignments[1].Task);
    }

    [Fact]
    public void ParseTaskAssignments_FuzzyMatchesWorkerNames()
    {
        var response = @"@worker:session
Do the work.
@end";

        var workers = new List<string> { "session-alpha", "session-beta" };
        var assignments = CopilotService.ParseTaskAssignments(response, workers);

        Assert.Single(assignments);
        Assert.Equal("session-alpha", assignments[0].WorkerName);
    }

    [Fact]
    public void ParseTaskAssignments_ReturnsEmpty_WhenNoMarkers()
    {
        var response = "I'll handle this myself. No need to delegate to workers.";
        var workers = new List<string> { "session-a", "session-b" };
        var assignments = CopilotService.ParseTaskAssignments(response, workers);

        Assert.Empty(assignments);
    }

    [Fact]
    public void ParseTaskAssignments_IgnoresUnknownWorkers()
    {
        var response = @"@worker:unknown-worker
Do something.
@end";

        var workers = new List<string> { "session-a", "session-b" };
        var assignments = CopilotService.ParseTaskAssignments(response, workers);

        Assert.Empty(assignments);
    }

    [Fact]
    public void ConvertToMultiAgent_SetsIsMultiAgentTrue()
    {
        var svc = CreateService();
        svc.CreateGroup("TestGroup");
        var group = svc.Organization.Groups.First(g => g.Name == "TestGroup");
        Assert.False(group.IsMultiAgent);

        svc.ConvertToMultiAgent(group.Id);

        Assert.True(group.IsMultiAgent);
        Assert.Equal(MultiAgentMode.Broadcast, group.OrchestratorMode);
    }
}
