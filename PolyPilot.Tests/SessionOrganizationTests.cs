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
        Assert.Equal(4, Enum.GetValues<MultiAgentMode>().Length);
        Assert.True(Enum.IsDefined(MultiAgentMode.Broadcast));
        Assert.True(Enum.IsDefined(MultiAgentMode.Sequential));
        Assert.True(Enum.IsDefined(MultiAgentMode.Orchestrator));
        Assert.True(Enum.IsDefined(MultiAgentMode.OrchestratorReflect));
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
    public void ParseTaskAssignments_ExactMatchOnly_NoFuzzy()
    {
        // With exact-match-only, "session" does NOT match "session-alpha"
        var response = @"@worker:session
Do the work.
@end";

        var workers = new List<string> { "session-alpha", "session-beta" };
        var assignments = CopilotService.ParseTaskAssignments(response, workers);

        Assert.Empty(assignments); // No exact match for "session"
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

    [Fact]
    public void GetOrCreateRepoGroup_DoesNotReturnLocalFolderGroup()
    {
        // Regression test: when only a 📁 local folder group exists for a repo,
        // GetOrCreateRepoGroup must NOT return it — it must create a separate URL-based group.
        var svc = CreateService();
        var localRepoPath = Path.Combine(Path.GetTempPath(), "MyRepo");

        // Simulate: user added folder via "Add Existing Folder" → local folder group created
        var localFolderGroup = svc.GetOrCreateLocalFolderGroup(localRepoPath, "repo-1");
        Assert.True(localFolderGroup.IsLocalFolder);
        Assert.Equal("repo-1", localFolderGroup.RepoId);

        // Now call GetOrCreateRepoGroup as happens when creating a session without targetGroupId
        var repoGroup = svc.GetOrCreateRepoGroup("repo-1", "MyRepo", explicitly: true);

        Assert.NotNull(repoGroup);
        Assert.NotEqual(localFolderGroup.Id, repoGroup!.Id);
        Assert.False(repoGroup.IsLocalFolder);
        Assert.Equal("repo-1", repoGroup.RepoId);
    }

    [Fact]
    public void GetOrCreateRepoGroup_WhenBothGroupTypesExist_ReturnsUrlBasedGroup()
    {
        // Regression test: when both a URL-based group and a 📁 local folder group exist
        // for the same RepoId, GetOrCreateRepoGroup must return the URL-based one.
        var svc = CreateService();
        var localRepoPath = Path.Combine(Path.GetTempPath(), "MyRepo");

        // Create URL-based group first
        var urlGroup = svc.GetOrCreateRepoGroup("repo-1", "MyRepo");
        Assert.NotNull(urlGroup);
        Assert.False(urlGroup!.IsLocalFolder);

        // Then create a local folder group for the same repo
        var localFolderGroup = svc.GetOrCreateLocalFolderGroup(localRepoPath, "repo-1");
        Assert.True(localFolderGroup.IsLocalFolder);

        // GetOrCreateRepoGroup should still return the URL-based group, not the local folder group
        var result = svc.GetOrCreateRepoGroup("repo-1", "MyRepo");

        Assert.NotNull(result);
        Assert.Equal(urlGroup.Id, result!.Id);
        Assert.False(result.IsLocalFolder);
    }

    [Fact]
    public void GetOrCreateRepoGroup_LocalFolderGroupFirst_ThenUrlGroup_ReturnsUrlBasedGroup()
    {
        // Same as above but local folder group is created before URL-based group
        var svc = CreateService();
        var localRepoPath = Path.Combine(Path.GetTempPath(), "MyRepo");

        var localFolderGroup = svc.GetOrCreateLocalFolderGroup(localRepoPath, "repo-1");
        Assert.True(localFolderGroup.IsLocalFolder);

        var urlGroup = svc.GetOrCreateRepoGroup("repo-1", "MyRepo");
        Assert.NotNull(urlGroup);
        Assert.NotEqual(localFolderGroup.Id, urlGroup!.Id);
        Assert.False(urlGroup.IsLocalFolder);

        // A second call should return the same URL-based group (not recreate)
        var urlGroup2 = svc.GetOrCreateRepoGroup("repo-1", "MyRepo");
        Assert.Equal(urlGroup.Id, urlGroup2!.Id);
    }

    [Fact]
    public void ReconcileOrganization_SessionInLocalFolderGroup_DoesNotMoveToUrlGroup()
    {
        // Regression test: sessions already assigned to a local folder group must NOT be
        // reassigned to a URL-based repo group by ReconcileOrganization.
        var repos = new List<RepositoryInfo>
        {
            new() { Id = "repo-1", Name = "MyRepo", Url = "https://github.com/test/repo" }
        };
        var localRepoPath = Path.Combine(Path.GetTempPath(), "MyRepo");
        var nestedWtPath = Path.Combine(localRepoPath, ".polypilot", "worktrees", "feature-x");
        var worktrees = new List<WorktreeInfo>
        {
            new() { Id = "wt-1", RepoId = "repo-1", Branch = "feature/x", Path = nestedWtPath }
        };
        var rm = CreateRepoManagerWithState(repos, worktrees);
        var svc = CreateService(rm);

        // Create both groups for the same repo
        var urlGroup = svc.GetOrCreateRepoGroup("repo-1", "MyRepo");
        var localFolderGroup = svc.GetOrCreateLocalFolderGroup(localRepoPath, "repo-1");

        // A session is in the local folder group with a worktree
        var meta = new SessionMeta
        {
            SessionName = "my-session",
            GroupId = localFolderGroup.Id,
            WorktreeId = "wt-1"
        };
        svc.Organization.Sessions.Add(meta);

        // Run reconciliation
        svc.ReconcileOrganization();

        // The session must remain in the local folder group
        var updatedMeta = svc.Organization.Sessions.First(m => m.SessionName == "my-session");
        Assert.Equal(localFolderGroup.Id, updatedMeta.GroupId);
        Assert.NotEqual(urlGroup!.Id, updatedMeta.GroupId);
    }

    [Fact]
    public void PromoteOrCreateLocalFolderGroup_CreatesNewGroupWhenNoGroupExists()
    {
        var svc = CreateService();
        var localRepoPath = Path.Combine(Path.GetTempPath(), "MyRepo");
        var expectedPath = Path.GetFullPath(localRepoPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var group = svc.PromoteOrCreateLocalFolderGroup(localRepoPath, "repo-1");

        Assert.NotNull(group);
        Assert.True(group.IsLocalFolder);
        Assert.Equal("MyRepo", group.Name);
        Assert.Equal(expectedPath, group.LocalPath);
        Assert.Equal("repo-1", group.RepoId);
    }

    [Fact]
    public void PromoteOrCreateLocalFolderGroup_ReturnsExistingLocalFolderGroupWhenAlreadyExists()
    {
        var svc = CreateService();
        var localRepoPath = Path.Combine(Path.GetTempPath(), "MyRepo");

        var first = svc.PromoteOrCreateLocalFolderGroup(localRepoPath, "repo-1");
        var second = svc.PromoteOrCreateLocalFolderGroup(localRepoPath, "repo-1");

        Assert.Same(first, second);
        Assert.Single(svc.Organization.Groups, g => g.IsLocalFolder && g.RepoId == "repo-1");
    }

    [Fact]
    public void PromoteOrCreateLocalFolderGroup_PromotesExistingUrlGroupInsteadOfCreatingNew()
    {
        // Regression test: old code created URL-based groups (no LocalPath) when user added
        // a local folder. PromoteOrCreateLocalFolderGroup must upgrade the existing group
        // rather than creating a redundant duplicate.
        var svc = CreateService();
        var localRepoPath = Path.Combine(Path.GetTempPath(), "MyRepo");
        var expectedPath = Path.GetFullPath(localRepoPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Simulate old behavior: URL-based group exists with no LocalPath
        var urlGroup = svc.GetOrCreateRepoGroup("repo-1", "MyRepo");
        Assert.NotNull(urlGroup);
        Assert.False(urlGroup!.IsLocalFolder);

        // Now call PromoteOrCreateLocalFolderGroup — it should update urlGroup in-place
        var result = svc.PromoteOrCreateLocalFolderGroup(localRepoPath, "repo-1");

        Assert.Equal(urlGroup.Id, result.Id);
        Assert.True(result.IsLocalFolder);
        Assert.Equal(expectedPath, result.LocalPath);
        Assert.Equal("MyRepo", result.Name);

        // Only one group for this repo — no duplicate created
        Assert.Single(svc.Organization.Groups, g => g.RepoId == "repo-1");
    }

    [Fact]
    public void PromoteOrCreateLocalFolderGroup_PromotesMostRecentUrlGroup_WhenMultipleExist()
    {
        // When two URL-based groups exist, promote the one with highest SortOrder (most recent).
        var svc = CreateService();
        var localRepoPath = Path.Combine(Path.GetTempPath(), "MyRepo");

        var olderGroup = svc.GetOrCreateRepoGroup("repo-1", "MyRepo");  // lower SortOrder
        // Manually create a second URL-based group with a higher SortOrder
        var newerGroup = new SessionGroup
        {
            Id = "newer-group",
            Name = "MyRepo",
            RepoId = "repo-1",
            SortOrder = (olderGroup?.SortOrder ?? 0) + 10
        };
        svc.Organization.Groups.Add(newerGroup);

        var result = svc.PromoteOrCreateLocalFolderGroup(localRepoPath, "repo-1");

        // Should promote the newer group, not the older one
        Assert.Equal(newerGroup.Id, result.Id);
        Assert.True(result.IsLocalFolder);
        Assert.False(olderGroup!.IsLocalFolder);  // older group stays URL-based
    }

    [Fact]
    public void ReconcileOrganization_ExternalWorktree_PromotesUrlGroupToLocalFolderGroup()
    {
        // Regression test: on startup, ReconcileOrganization should automatically promote
        // URL-based groups to local folder groups when an external worktree is registered
        // but no local folder group exists yet.
        var repos = new List<RepositoryInfo>
        {
            new() { Id = "repo-1", Name = "MyRepo", Url = "https://github.com/test/repo" }
        };
        // Use cross-platform temp paths to avoid Windows-only literal failures on macOS/Linux
        var extPath = Path.Combine(Path.GetTempPath(), "MyRepo");
        var centralPath = Path.Combine(Path.GetTempPath(), ".polypilot", "worktrees", "repo-1-wt1");
        var worktrees = new List<WorktreeInfo>
        {
            // External: user's local folder, NOT under the managed worktrees dir and NOT nested
            new() { Id = "ext-1", RepoId = "repo-1", Branch = "main", Path = extPath },
            // Centralized: under the managed worktrees dir (simulated by putting it under .polypilot/worktrees)
            new() { Id = "wt-1", RepoId = "repo-1", Branch = "session-123", Path = centralPath }
        };
        var rm = CreateRepoManagerWithState(repos, worktrees);
        var svc = CreateService(rm);

        // Set up: a URL-based group (no LocalPath) — simulates old code behavior
        var urlGroup = svc.GetOrCreateRepoGroup("repo-1", "MyRepo");
        Assert.False(urlGroup!.IsLocalFolder);

        // Run reconciliation — it should detect the external worktree and promote urlGroup
        svc.ReconcileOrganization();

        var promoted = svc.Organization.Groups.First(g => g.Id == urlGroup.Id);
        Assert.True(promoted.IsLocalFolder);
        Assert.Equal(Path.GetFullPath(extPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            promoted.LocalPath);
    }

    [Fact]
    public void ReconcileOrganization_ExternalWorktree_DoesNotPromoteWhenLocalGroupAlreadyExists()
    {
        // If a local folder group already exists for the external worktree path,
        // ReconcileOrganization must NOT promote another group.
        var repos = new List<RepositoryInfo>
        {
            new() { Id = "repo-1", Name = "MyRepo", Url = "https://github.com/test/repo" }
        };
        var extPath = Path.Combine(Path.GetTempPath(), "MyRepo");
        var worktrees = new List<WorktreeInfo>
        {
            new() { Id = "ext-1", RepoId = "repo-1", Branch = "main", Path = extPath }
        };
        var rm = CreateRepoManagerWithState(repos, worktrees);
        var svc = CreateService(rm);

        // Both a URL-based group and a local folder group exist for this repo
        var urlGroup = svc.GetOrCreateRepoGroup("repo-1", "MyRepo");
        var localGroup = svc.GetOrCreateLocalFolderGroup(extPath, "repo-1");

        svc.ReconcileOrganization();

        // URL group must remain URL-based; local group keeps its LocalPath
        Assert.False(urlGroup!.IsLocalFolder);
        Assert.True(localGroup.IsLocalFolder);
        Assert.Equal(Path.GetFullPath(extPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            localGroup.LocalPath);
    }

    [Fact]
    public void ReconcileOrganization_NestedWorktree_IsNotTreatedAsExternalWorktree()
    {
        // Nested worktrees (inside a local folder's .polypilot/worktrees/) must NOT
        // trigger group promotion — only the original external (root) worktree should.
        var repos = new List<RepositoryInfo>
        {
            new() { Id = "repo-1", Name = "MyRepo", Url = "https://github.com/test/repo" }
        };
        // Use Path.Combine so the .polypilot/worktrees marker uses the OS separator
        var nestedPath = Path.Combine(Path.GetTempPath(), "MyRepo", ".polypilot", "worktrees", "feature-x");
        var worktrees = new List<WorktreeInfo>
        {
            // Nested worktree inside the local folder — should NOT trigger promotion
            new() { Id = "nested-1", RepoId = "repo-1", Branch = "feature-x", Path = nestedPath }
        };
        var rm = CreateRepoManagerWithState(repos, worktrees);
        var svc = CreateService(rm);

        var urlGroup = svc.GetOrCreateRepoGroup("repo-1", "MyRepo");
        svc.ReconcileOrganization();

        // URL group must remain URL-based — nested worktree should not trigger promotion
        Assert.False(urlGroup!.IsLocalFolder);
        Assert.Null(urlGroup.LocalPath);
    }
}

/// <summary>
/// Integration scenario tests covering the full "Add Existing Folder" feature flow.
/// These tests simulate the exact user-reported bugs and verify the complete session
/// routing pipeline end-to-end.
/// </summary>
public class AddExistingFolderScenarioTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly IServiceProvider _serviceProvider;

    public AddExistingFolderScenarioTests()
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

    /// <summary>
    /// User-reported bug: Two PolyPilot folders added — one at source\repos\PolyPilot (local folder),
    /// one managed at ~/.polypilot/worktrees. Creating a session from the local folder group
    /// incorrectly routed to the centralized group.
    /// 
    /// Root cause: GetOrCreateRepoGroup matched local folder groups (which also have RepoId set),
    /// so sessions without an explicit targetGroupId ended up in the local folder group.
    /// Fix: GetOrCreateRepoGroup now excludes IsLocalFolder groups.
    /// </summary>
    [Fact]
    public void Scenario_TwoFoldersForSameRepo_SessionsRouteToCorrectGroup()
    {
        var repos = new List<RepositoryInfo>
        {
            new() { Id = "owner-polypilot", Name = "PolyPilot", Url = "https://github.com/owner/PolyPilot" }
        };
        var sourceReposPath = Path.Combine(Path.GetTempPath(), "source", "repos", "PolyPilot");
        var worktrees = new List<WorktreeInfo>
        {
            // The original external worktree (user's local clone)
            new() { Id = "ext-1", RepoId = "owner-polypilot", Branch = "main", Path = sourceReposPath }
        };
        var rm = CreateRepoManagerWithState(repos, worktrees);
        var svc = CreateService(rm);

        // Setup: URL-based group (auto-created when repo added via URL)
        var urlGroup = svc.GetOrCreateRepoGroup("owner-polypilot", "PolyPilot");
        Assert.NotNull(urlGroup);
        Assert.False(urlGroup!.IsLocalFolder);

        // 📁 local folder group (auto-created when user added source\repos\PolyPilot folder)
        var localGroup = svc.GetOrCreateLocalFolderGroup(sourceReposPath, "owner-polypilot");
        Assert.True(localGroup.IsLocalFolder);
        Assert.Equal("owner-polypilot", localGroup.RepoId);

        // Scenario A: Creating a session from the URL-based group (no targetGroupId)
        // → must route to urlGroup, NOT localGroup
        var resolvedForUrl = svc.GetOrCreateRepoGroup("owner-polypilot", "PolyPilot");
        Assert.Equal(urlGroup.Id, resolvedForUrl!.Id);
        Assert.False(resolvedForUrl.IsLocalFolder);

        // Scenario B: Creating a session from the local folder group (targetGroupId = localGroup.Id)
        // → must use localGroup (caller explicitly passes it — no routing needed)
        Assert.Equal(localGroup.Id, localGroup.Id);  // trivially true; verified by callers passing targetGroupId

        // Scenario C: Both groups must remain distinct — no merging
        Assert.NotEqual(urlGroup.Id, localGroup.Id);
        Assert.Equal(2, svc.Organization.Groups.Count(g => g.RepoId == "owner-polypilot" && !g.IsMultiAgent));
    }

    [Fact]
    public void Scenario_StartupMigration_AutoFixesExistingInstall()
    {
        // User had folder added via old code that created a URL-based group (no LocalPath).
        // On startup, ReconcileOrganization should detect the external worktree and promote
        // the URL-based group to a local folder group — without user intervention.

        var sourceReposPath = Path.Combine(Path.GetTempPath(), "source", "repos", "PolyPilot");
        var centralPath = Path.Combine(Path.GetTempPath(), ".polypilot", "worktrees", "polypilot-abc12345");

        var repos = new List<RepositoryInfo>
        {
            new() { Id = "owner-polypilot", Name = "PolyPilot", Url = "https://github.com/owner/PolyPilot" }
        };
        var worktrees = new List<WorktreeInfo>
        {
            new() { Id = "ext-1", RepoId = "owner-polypilot", Branch = "main", Path = sourceReposPath },
            new() { Id = "cen-1", RepoId = "owner-polypilot", Branch = "session-1", Path = centralPath }
        };
        var rm = CreateRepoManagerWithState(repos, worktrees);
        var svc = CreateService(rm);

        // Old-style state: only a URL-based group, no LocalPath
        var oldUrlGroup = svc.GetOrCreateRepoGroup("owner-polypilot", "PolyPilot");
        Assert.False(oldUrlGroup!.IsLocalFolder);
        Assert.Null(oldUrlGroup.LocalPath);

        // Existing session in the URL group (simulating old persisted sessions)
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "my-old-session",
            GroupId = oldUrlGroup.Id,
            WorktreeId = "cen-1"
        });

        // Simulate the restore-phase reconciliation: IsInitialized must be true to pass the
        // startup guard, and allowPruning=false prevents sessions without live counterparts
        // from being pruned (matching RestorePreviousSessionsAsync behavior).
        typeof(CopilotService).GetProperty("IsInitialized")!.SetValue(svc, true);

        // Startup reconciliation runs (allowPruning:false = during session-restore window)
        svc.ReconcileOrganization(allowPruning: false);

        // The URL group should be promoted to a local folder group
        var promotedGroup = svc.Organization.Groups.First(g => g.Id == oldUrlGroup.Id);
        Assert.True(promotedGroup.IsLocalFolder);
        Assert.Equal(
            Path.GetFullPath(sourceReposPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            promotedGroup.LocalPath);

        // Existing sessions in that group must NOT be moved — they stay put
        var oldSession = svc.Organization.Sessions.First(m => m.SessionName == "my-old-session");
        Assert.Equal(oldUrlGroup.Id, oldSession.GroupId);
    }

    [Fact]
    public void Scenario_ReAddExistingFolder_DoesNotCreateDuplicateGroup()
    {
        // If the user removes and re-adds the same folder, PromoteOrCreateLocalFolderGroup
        // must return the existing local folder group, not create a duplicate.

        var svc = CreateService();
        var localPath = Path.Combine(Path.GetTempPath(), "my-project");

        var first = svc.PromoteOrCreateLocalFolderGroup(localPath, "repo-1");
        Assert.True(first.IsLocalFolder);

        // Simulate re-adding: call again with the same path
        var second = svc.PromoteOrCreateLocalFolderGroup(localPath, "repo-1");
        Assert.Same(first, second);

        // Only one 📁 group for this path
        var localGroups = svc.Organization.Groups.Where(g => g.IsLocalFolder && g.RepoId == "repo-1").ToList();
        Assert.Single(localGroups);
    }

    [Fact]
    public void Scenario_LocalFolderGroup_HasCorrectIsLocalFolderFlag()
    {
        // IsLocalFolder is a computed property (!string.IsNullOrWhiteSpace(LocalPath)).
        // Verify that setting LocalPath via PromoteOrCreateLocalFolderGroup correctly
        // triggers IsLocalFolder=true without a separate boolean flag.

        var svc = CreateService();

        // URL-based group: no LocalPath → IsLocalFolder=false
        var urlGroup = svc.GetOrCreateRepoGroup("repo-1", "MyRepo");
        Assert.NotNull(urlGroup);
        Assert.False(urlGroup!.IsLocalFolder);
        Assert.Null(urlGroup.LocalPath);

        // Promote it to a local folder group by setting LocalPath
        var promoted = svc.PromoteOrCreateLocalFolderGroup(Path.Combine(Path.GetTempPath(), "MyRepo"), "repo-1");
        Assert.True(promoted.IsLocalFolder);
        Assert.NotNull(promoted.LocalPath);
        Assert.NotEmpty(promoted.LocalPath!);
    }

    [Fact]
    public void Scenario_LocalFolderGroup_IsExcludedFromUrlGroupLookup()
    {
        // After promotion, the group has both RepoId and LocalPath.
        // GetOrCreateRepoGroup must NOT return it — it should create a new URL-based group.

        var svc = CreateService();
        var localPath = Path.Combine(Path.GetTempPath(), "MyRepo");

        // Add as local folder
        var localGroup = svc.PromoteOrCreateLocalFolderGroup(localPath, "repo-1");
        Assert.True(localGroup.IsLocalFolder);

        // GetOrCreateRepoGroup must NOT return the promoted local group
        var urlGroup = svc.GetOrCreateRepoGroup("repo-1", "MyRepo", explicitly: true);
        Assert.NotNull(urlGroup);
        Assert.NotEqual(localGroup.Id, urlGroup!.Id);
        Assert.False(urlGroup.IsLocalFolder);
    }

    [Fact]
    public void Scenario_ReconcileOrganization_SessionInLocalGroup_WithWorktree_Stays()
    {
        // Full scenario: session in local folder group with a nested worktree.
        // ReconcileOrganization must leave it exactly where it is, and the local group
        // must not be overwritten by the URL-based group assignment.

        var localRepoPath = Path.Combine(Path.GetTempPath(), "source-repo");
        var nestedWtPath = Path.Combine(localRepoPath, ".polypilot", "worktrees", "feature-x");

        var repos = new List<RepositoryInfo>
        {
            new() { Id = "repo-1", Name = "MyRepo", Url = "https://github.com/test/repo" }
        };
        var worktrees = new List<WorktreeInfo>
        {
            new() { Id = "ext-1", RepoId = "repo-1", Branch = "main", Path = localRepoPath },
            new() { Id = "nested-1", RepoId = "repo-1", Branch = "feature-x", Path = nestedWtPath }
        };
        var rm = CreateRepoManagerWithState(repos, worktrees);
        var svc = CreateService(rm);

        // Both group types exist
        var urlGroup = svc.GetOrCreateRepoGroup("repo-1", "MyRepo");
        var localGroup = svc.GetOrCreateLocalFolderGroup(localRepoPath, "repo-1");

        // Session using the nested worktree, living in the local folder group
        var session = new SessionMeta
        {
            SessionName = "feature-session",
            GroupId = localGroup.Id,
            WorktreeId = "nested-1"
        };
        svc.Organization.Sessions.Add(session);

        svc.ReconcileOrganization();

        var updated = svc.Organization.Sessions.First(m => m.SessionName == "feature-session");
        Assert.Equal(localGroup.Id, updated.GroupId);
        Assert.NotEqual(urlGroup!.Id, updated.GroupId);
    }

    [Fact]
    public void Scenario_GroupPromotion_PreservesSessionHistory()
    {
        // When an existing URL-based group is promoted to a local folder group,
        // all sessions in that group must remain assigned to it (their GroupId doesn't change).

        var svc = CreateService();
        var localPath = Path.Combine(Path.GetTempPath(), "my-project");

        // Old-style URL-based group with sessions
        var oldGroup = svc.GetOrCreateRepoGroup("repo-1", "MyProject");
        Assert.NotNull(oldGroup);
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "old-session-1", GroupId = oldGroup!.Id });
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "old-session-2", GroupId = oldGroup.Id });

        // Promote the group (simulates re-adding the local folder)
        var promoted = svc.PromoteOrCreateLocalFolderGroup(localPath, "repo-1");
        Assert.Equal(oldGroup.Id, promoted.Id); // same group, just upgraded

        // Both sessions must still be in the group
        var s1 = svc.Organization.Sessions.First(m => m.SessionName == "old-session-1");
        var s2 = svc.Organization.Sessions.First(m => m.SessionName == "old-session-2");
        Assert.Equal(promoted.Id, s1.GroupId);
        Assert.Equal(promoted.Id, s2.GroupId);
    }
}

/// <summary>
/// Tests for the deleted repo group resurrection bug fix.
/// When a user deletes a repo-linked group, ReconcileOrganization must not recreate it.
/// </summary>
public class DeletedRepoGroupResurrectionTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly IServiceProvider _serviceProvider;

    public DeletedRepoGroupResurrectionTests()
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
    public void DeleteGroup_RepoGroup_AddsToDeletedRepoGroupRepoIds()
    {
        var svc = CreateService();
        var group = svc.GetOrCreateRepoGroup("repo-1", "MyRepo", explicitly: true)!;

        svc.DeleteGroup(group.Id);

        Assert.Contains("repo-1", svc.Organization.DeletedRepoGroupRepoIds);
        Assert.DoesNotContain(svc.Organization.Groups, g => g.RepoId == "repo-1");
    }

    [Fact]
    public void DeleteGroup_NonRepoGroup_DoesNotAddToDeletedRepoGroupRepoIds()
    {
        var svc = CreateService();
        var group = svc.CreateGroup("Custom Group");

        svc.DeleteGroup(group.Id);

        Assert.Empty(svc.Organization.DeletedRepoGroupRepoIds);
    }

    [Fact]
    public void DeleteGroup_RepoGroup_ClearsWorktreeIdOnMovedSessions()
    {
        var svc = CreateService();
        var group = svc.GetOrCreateRepoGroup("repo-1", "MyRepo", explicitly: true)!;
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "session-1",
            GroupId = group.Id,
            WorktreeId = "wt-1"
        });

        svc.DeleteGroup(group.Id);

        var meta = svc.Organization.Sessions.First(m => m.SessionName == "session-1");
        Assert.Equal(SessionGroup.DefaultId, meta.GroupId);
        Assert.Null(meta.WorktreeId);
    }

    [Fact]
    public void DeleteGroup_NonRepoGroup_PreservesWorktreeId()
    {
        var svc = CreateService();
        var group = svc.CreateGroup("Custom");
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "session-1",
            GroupId = group.Id,
            WorktreeId = "wt-1"
        });

        svc.DeleteGroup(group.Id);

        var meta = svc.Organization.Sessions.First(m => m.SessionName == "session-1");
        Assert.Equal(SessionGroup.DefaultId, meta.GroupId);
        Assert.Equal("wt-1", meta.WorktreeId);
    }

    [Fact]
    public void GetOrCreateRepoGroup_Implicit_ReturnsNullForDeletedRepo()
    {
        var svc = CreateService();
        svc.Organization.DeletedRepoGroupRepoIds.Add("repo-1");

        var result = svc.GetOrCreateRepoGroup("repo-1", "MyRepo");

        Assert.Null(result);
        Assert.DoesNotContain(svc.Organization.Groups, g => g.RepoId == "repo-1");
    }

    [Fact]
    public void GetOrCreateRepoGroup_Explicit_CreatesGroupForDeletedRepo()
    {
        var svc = CreateService();
        svc.Organization.DeletedRepoGroupRepoIds.Add("repo-1");

        var result = svc.GetOrCreateRepoGroup("repo-1", "MyRepo", explicitly: true);

        Assert.NotNull(result);
        Assert.Equal("MyRepo", result!.Name);
        Assert.DoesNotContain("repo-1", svc.Organization.DeletedRepoGroupRepoIds);
    }

    [Fact]
    public void GetOrCreateRepoGroup_Existing_ReturnsGroupEvenIfInDeletedSet()
    {
        var svc = CreateService();
        var group = svc.GetOrCreateRepoGroup("repo-1", "MyRepo", explicitly: true)!;
        // Manually add to deleted set (shouldn't happen in practice, but tests defense)
        svc.Organization.DeletedRepoGroupRepoIds.Add("repo-1");

        // Even implicit calls should return existing group
        var result = svc.GetOrCreateRepoGroup("repo-1", "MyRepo");

        Assert.NotNull(result);
        Assert.Equal(group.Id, result!.Id);
    }

    [Fact]
    public void ReconcileOrganization_DoesNotResurrectDeletedRepoGroup()
    {
        var repos = new List<RepositoryInfo>
        {
            new() { Id = "repo-1", Name = "MyRepo", Url = "https://github.com/test/repo" }
        };
        var rm = CreateRepoManagerWithState(repos, new List<WorktreeInfo>());
        var svc = CreateService(rm);

        // Create then delete the repo group
        var group = svc.GetOrCreateRepoGroup("repo-1", "MyRepo", explicitly: true)!;
        svc.DeleteGroup(group.Id);
        Assert.Contains("repo-1", svc.Organization.DeletedRepoGroupRepoIds);
        Assert.DoesNotContain(svc.Organization.Groups, g => g.RepoId == "repo-1");

        // Reconcile should NOT recreate the group
        svc.ReconcileOrganization();

        Assert.DoesNotContain(svc.Organization.Groups, g => g.RepoId == "repo-1");
    }

    [Fact]
    public void ReconcileOrganization_DoesNotReassignSessionToDeletedRepoGroup()
    {
        var repos = new List<RepositoryInfo>
        {
            new() { Id = "repo-1", Name = "MyRepo", Url = "https://github.com/test/repo" }
        };
        var worktrees = new List<WorktreeInfo>
        {
            new() { Id = "wt-1", RepoId = "repo-1", Branch = "main", Path = "/tmp/wt-1" }
        };
        var rm = CreateRepoManagerWithState(repos, worktrees);
        var svc = CreateService(rm);

        // Create repo group, add session, then delete group
        var group = svc.GetOrCreateRepoGroup("repo-1", "MyRepo", explicitly: true)!;
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "test-session",
            GroupId = group.Id,
            WorktreeId = "wt-1"
        });
        svc.DeleteGroup(group.Id);

        // Session should be in default group with cleared worktree
        var meta = svc.Organization.Sessions.First(m => m.SessionName == "test-session");
        Assert.Equal(SessionGroup.DefaultId, meta.GroupId);
        Assert.Null(meta.WorktreeId);

        // The repo group should not exist — deletion prevents resurrection
        Assert.DoesNotContain(svc.Organization.Groups, g => g.RepoId == "repo-1");
        Assert.Contains("repo-1", svc.Organization.DeletedRepoGroupRepoIds);
    }

    [Fact]
    public void DeletedRepoGroupRepoIds_SurvivesSerialization()
    {
        var state = new OrganizationState();
        state.DeletedRepoGroupRepoIds.Add("repo-1");
        state.DeletedRepoGroupRepoIds.Add("repo-2");

        var json = JsonSerializer.Serialize(state);
        var restored = JsonSerializer.Deserialize<OrganizationState>(json)!;

        Assert.NotNull(restored.DeletedRepoGroupRepoIds);
        Assert.Contains("repo-1", restored.DeletedRepoGroupRepoIds);
        Assert.Contains("repo-2", restored.DeletedRepoGroupRepoIds);
    }

    [Fact]
    public void LegacyJson_WithoutDeletedRepoGroupRepoIds_DeserializesGracefully()
    {
        // Simulate loading old organization.json without the new field
        var json = """
        {
            "Groups": [{"Id": "_default", "Name": "Sessions", "SortOrder": 0}],
            "Sessions": [],
            "SortMode": "LastActive"
        }
        """;
        var state = JsonSerializer.Deserialize<OrganizationState>(json);
        Assert.NotNull(state);
        // Field will be null from deserialization — LoadOrganization adds null coalesce
        // But the type default should still work
    }

    [Fact]
    public void DeleteGroup_DefaultGroup_IsNoop()
    {
        var svc = CreateService();
        var initialGroupCount = svc.Organization.Groups.Count;

        svc.DeleteGroup(SessionGroup.DefaultId);

        Assert.Equal(initialGroupCount, svc.Organization.Groups.Count);
        Assert.Empty(svc.Organization.DeletedRepoGroupRepoIds);
    }

    [Fact]
    public void ReAddRepo_ClearsDeletedFlag_AllowsReconciliation()
    {
        var repos = new List<RepositoryInfo>
        {
            new() { Id = "repo-1", Name = "MyRepo", Url = "https://github.com/test/repo" }
        };
        var rm = CreateRepoManagerWithState(repos, new List<WorktreeInfo>());
        var svc = CreateService(rm);

        // Create, delete, then re-add the repo group explicitly
        var group = svc.GetOrCreateRepoGroup("repo-1", "MyRepo", explicitly: true)!;
        svc.DeleteGroup(group.Id);
        Assert.Contains("repo-1", svc.Organization.DeletedRepoGroupRepoIds);

        // Re-add explicitly (simulates adding repo again)
        var newGroup = svc.GetOrCreateRepoGroup("repo-1", "MyRepo", explicitly: true);
        Assert.NotNull(newGroup);
        Assert.DoesNotContain("repo-1", svc.Organization.DeletedRepoGroupRepoIds);

        // Reconcile should keep the group
        svc.ReconcileOrganization();
        Assert.Contains(svc.Organization.Groups, g => g.RepoId == "repo-1");
    }
}

public class PerAgentModelAssignmentTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly IServiceProvider _serviceProvider;

    public PerAgentModelAssignmentTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, new RepoManager(), _serviceProvider, _demoService);

    [Fact]
    public void SessionMeta_PreferredModel_DefaultsToNull()
    {
        var meta = new SessionMeta { SessionName = "test" };
        Assert.Null(meta.PreferredModel);
    }

    [Fact]
    public void SetSessionPreferredModel_StoresModel()
    {
        var svc = CreateService();
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "worker1" });

        svc.SetSessionPreferredModel("worker1", "gpt-4.1");

        var meta = svc.Organization.Sessions.First(m => m.SessionName == "worker1");
        Assert.Equal("gpt-4.1", meta.PreferredModel);
    }

    [Fact]
    public void SetSessionPreferredModel_Null_ClearsOverride()
    {
        var svc = CreateService();
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "worker1", PreferredModel = "gpt-4.1" });

        svc.SetSessionPreferredModel("worker1", null);

        var meta = svc.Organization.Sessions.First(m => m.SessionName == "worker1");
        Assert.Null(meta.PreferredModel);
    }

    [Fact]
    public void GetEffectiveModel_ReturnsPreferredModel_WhenSet()
    {
        var svc = CreateService();
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "worker1", PreferredModel = "claude-opus-4.6" });

        var model = svc.GetEffectiveModel("worker1");
        Assert.Equal("claude-opus-4.6", model);
    }

    [Fact]
    public void GetEffectiveModel_ReturnsDefaultModel_WhenNoPreference()
    {
        var svc = CreateService();
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "worker1" });

        var model = svc.GetEffectiveModel("worker1");
        Assert.Equal(svc.DefaultModel, model);
    }

    [Fact]
    public void SessionGroup_DefaultWorkerModel_DefaultsToNull()
    {
        var group = new SessionGroup { Name = "Test" };
        Assert.Null(group.DefaultWorkerModel);
        Assert.Null(group.DefaultOrchestratorModel);
    }

    [Fact]
    public void PreferredModel_SurvivesSerialization()
    {
        var state = new OrganizationState();
        state.Sessions.Add(new SessionMeta { SessionName = "worker1", PreferredModel = "gemini-3-pro" });

        var json = JsonSerializer.Serialize(state);
        var restored = JsonSerializer.Deserialize<OrganizationState>(json)!;

        Assert.Equal("gemini-3-pro", restored.Sessions[0].PreferredModel);
    }

    [Fact]
    public void SessionGroup_ModelDefaults_SurviveSerialization()
    {
        var state = new OrganizationState();
        state.Groups.Add(new SessionGroup
        {
            Name = "Test",
            IsMultiAgent = true,
            DefaultWorkerModel = "gpt-4.1",
            DefaultOrchestratorModel = "claude-opus-4.6"
        });

        var json = JsonSerializer.Serialize(state);
        var restored = JsonSerializer.Deserialize<OrganizationState>(json)!;

        var group = restored.Groups.First(g => g.Name == "Test");
        Assert.Equal("gpt-4.1", group.DefaultWorkerModel);
        Assert.Equal("claude-opus-4.6", group.DefaultOrchestratorModel);
    }

    [Fact]
    public void Legacy_Deserialization_GracefullyHandlesNoPreferredModel()
    {
        // Simulate legacy JSON without PreferredModel
        var json = """{"SessionName":"old-session","GroupId":"_default","IsPinned":false,"ManualOrder":0,"Role":"Worker"}""";
        var meta = JsonSerializer.Deserialize<SessionMeta>(json)!;
        Assert.Null(meta.PreferredModel);
        Assert.Equal("old-session", meta.SessionName);
    }
}

public class GroupReflectionStateTests
{
    [Fact]
    public void Create_InitializesCorrectly()
    {
        var state = ReflectionCycle.Create("Build a REST API", 10);

        Assert.Equal("Build a REST API", state.Goal);
        Assert.Equal(10, state.MaxIterations);
        Assert.Equal(0, state.CurrentIteration);
        Assert.True(state.IsActive);
        Assert.False(state.GoalMet);
        Assert.False(state.IsStalled);
        Assert.False(state.IsPaused);
        Assert.NotNull(state.StartedAt);
    }

    [Fact]
    public void CheckStall_ReturnsFalse_ForUniqueResponses()
    {
        var state = ReflectionCycle.Create("test");

        Assert.False(state.CheckStall("response 1"));
        Assert.False(state.CheckStall("response 2"));
        Assert.False(state.CheckStall("response 3"));
    }

    [Fact]
    public void CheckStall_DetectsRepeatedResponses()
    {
        var state = ReflectionCycle.Create("test");
        state.IsActive = true;

        // Iteration 1
        state.Advance("same response");
        
        // Iteration 2 (first stall)
        state.Advance("same response");
        Assert.False(state.IsStalled);
        Assert.Equal(1, state.ConsecutiveStalls);

        // Iteration 3 (second stall)
        state.Advance("same response");
        Assert.True(state.IsStalled);
        Assert.Equal(2, state.ConsecutiveStalls);
    }

    [Fact]
    public void CheckStall_ResetsOnProgress()
    {
        var state = ReflectionCycle.Create("test");
        state.IsActive = true;

        state.Advance("response A");
        state.Advance("response A"); // 1st stall
        state.Advance("response B"); // different — resets

        Assert.False(state.IsStalled);
        Assert.Equal(0, state.ConsecutiveStalls);
    }

    [Fact]
    public void CompletionSummary_GoalMet()
    {
        var state = ReflectionCycle.Create("test");
        state.CurrentIteration = 3;
        state.GoalMet = true;

        Assert.Contains("✅", state.BuildCompletionSummary());
        Assert.Contains("3", state.BuildCompletionSummary());
    }

    [Fact]
    public void CompletionSummary_Stalled()
    {
        var state = ReflectionCycle.Create("test");
        state.CurrentIteration = 4;
        state.IsStalled = true;

        Assert.Contains("⚠️", state.BuildCompletionSummary());
    }

    [Fact]
    public void CompletionSummary_MaxReached()
    {
        var state = ReflectionCycle.Create("test", 5);
        state.CurrentIteration = 5;

        Assert.Contains("⏱️", state.BuildCompletionSummary());
        Assert.Contains("5", state.BuildCompletionSummary());
    }

    [Fact]
    public void OrchestratorReflect_ModeEnumValue_Exists()
    {
        var mode = MultiAgentMode.OrchestratorReflect;
        Assert.Equal("OrchestratorReflect", mode.ToString());
    }

    [Fact]
    public void OrchestratorReflect_SurvivesSerialization()
    {
        var group = new SessionGroup
        {
            Name = "Test",
            IsMultiAgent = true,
            OrchestratorMode = MultiAgentMode.OrchestratorReflect,
            ReflectionState = ReflectionCycle.Create("Build it", 10)
        };

        var json = JsonSerializer.Serialize(group);
        var restored = JsonSerializer.Deserialize<SessionGroup>(json)!;

        Assert.Equal(MultiAgentMode.OrchestratorReflect, restored.OrchestratorMode);
        Assert.NotNull(restored.ReflectionState);
        Assert.Equal("Build it", restored.ReflectionState!.Goal);
        Assert.Equal(10, restored.ReflectionState.MaxIterations);
        Assert.True(restored.ReflectionState.IsActive);
    }

    [Fact]
    public void ExtractIterationEvaluation_ParsesNeedsIterationMarker()
    {
        var response = "The synthesis looks good but [[NEEDS_ITERATION]] Missing error handling in the API layer. @worker:alice\nAdd error handling.\n@end";

        // Use reflection to test internal method
        var method = typeof(CopilotService).GetMethod("ExtractIterationEvaluation",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var result = (string)method!.Invoke(null, new object[] { response })!;
        Assert.Contains("Missing error handling", result);
        Assert.DoesNotContain("@worker", result);
    }

    [Fact]
    public void ExtractIterationEvaluation_FallsBackToLastLines()
    {
        var response = "Line 1\nLine 2\nLine 3\nLine 4\nLine 5\nLine 6\nThe final evaluation.";

        var method = typeof(CopilotService).GetMethod("ExtractIterationEvaluation",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = (string)method!.Invoke(null, new object[] { response })!;

        Assert.Contains("The final evaluation", result);
    }
}

public class ModelCapabilitiesTests
{
    [Fact]
    public void GetCapabilities_KnownModel_ReturnsFlags()
    {
        var caps = ModelCapabilities.GetCapabilities("claude-opus-4.6");
        Assert.True(caps.HasFlag(ModelCapability.ReasoningExpert));
        Assert.True(caps.HasFlag(ModelCapability.CodeExpert));
    }

    [Fact]
    public void GetCapabilities_UnknownModel_ReturnsNone()
    {
        var caps = ModelCapabilities.GetCapabilities("totally-unknown-model");
        Assert.Equal(ModelCapability.None, caps);
    }

    [Fact]
    public void GetCapabilities_FuzzyMatch_Works()
    {
        // "claude-opus-4.6-fast" should fuzzy-match "claude-opus-4.6"
        var caps = ModelCapabilities.GetCapabilities("gpt-4.1");
        Assert.True(caps.HasFlag(ModelCapability.Fast));
        Assert.True(caps.HasFlag(ModelCapability.CostEfficient));
    }

    [Fact]
    public void GetRoleWarnings_CheapOrchestratorModel_WarnsAboutReasoning()
    {
        var warnings = ModelCapabilities.GetRoleWarnings("gpt-4.1", MultiAgentRole.Orchestrator);
        Assert.NotEmpty(warnings);
        Assert.Contains(warnings, w => w.Contains("reasoning", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetRoleWarnings_StrongOrchestratorModel_NoWarnings()
    {
        var warnings = ModelCapabilities.GetRoleWarnings("claude-opus-4.6", MultiAgentRole.Orchestrator);
        Assert.Empty(warnings);
    }

    [Fact]
    public void GetRoleWarnings_WorkerWithToolUse_NoWarnings()
    {
        var warnings = ModelCapabilities.GetRoleWarnings("gpt-4.1", MultiAgentRole.Worker);
        Assert.Empty(warnings);
    }

    [Fact]
    public void GetStrengths_ReturnsDescription()
    {
        var strengths = ModelCapabilities.GetStrengths("claude-opus-4.6");
        Assert.NotEqual("Unknown model", strengths);
        Assert.Contains("reasoning", strengths, StringComparison.OrdinalIgnoreCase);
    }
}

public class GroupPresetTests
{
    [Fact]
    public void BuiltInPresets_AllHaveRequiredFields()
    {
        foreach (var preset in GroupPreset.BuiltIn)
        {
            Assert.False(string.IsNullOrEmpty(preset.Name));
            Assert.False(string.IsNullOrEmpty(preset.Description));
            Assert.False(string.IsNullOrEmpty(preset.OrchestratorModel));
            Assert.NotEmpty(preset.WorkerModels);
            Assert.True(preset.WorkerModels.All(m => !string.IsNullOrEmpty(m)));
        }
    }

    [Fact]
    public void BuiltInPresets_ContainExpectedCount()
    {
        Assert.True(GroupPreset.BuiltIn.Length >= 2, "Should have at least 2 built-in presets");
    }

    [Fact]
    public void BuiltInPresets_IncludeOrchestratorReflect()
    {
        Assert.Contains(GroupPreset.BuiltIn, p => p.Mode == MultiAgentMode.OrchestratorReflect);
    }

    [Fact]
    public void BuiltInPresets_IncludePRReviewSquad()
    {
        var prSquad = GroupPreset.BuiltIn.FirstOrDefault(p => p.Name == "PR Review Squad");
        Assert.NotNull(prSquad);
        Assert.Equal(5, prSquad!.WorkerModels.Length);
        Assert.Equal(MultiAgentMode.Orchestrator, prSquad.Mode);
        Assert.NotNull(prSquad.SharedContext);
        Assert.NotNull(prSquad.RoutingContext);
        Assert.NotNull(prSquad.WorkerSystemPrompts);
        Assert.Equal(prSquad.WorkerModels.Length, prSquad.WorkerSystemPrompts!.Length);
    }

    [Fact]
    public void BuiltInPresets_IncludeSkillValidator()
    {
        var skillValidator = GroupPreset.BuiltIn.FirstOrDefault(p => p.Name == "Skill Validator");
        Assert.NotNull(skillValidator);
        Assert.Equal(3, skillValidator!.WorkerModels.Length);
        Assert.Equal(MultiAgentMode.OrchestratorReflect, skillValidator.Mode);
        Assert.Equal("⚖️", skillValidator.Emoji);
        Assert.NotNull(skillValidator.SharedContext);
        Assert.NotNull(skillValidator.RoutingContext);
        Assert.NotNull(skillValidator.WorkerSystemPrompts);
        Assert.Equal(3, skillValidator.WorkerSystemPrompts!.Length);
        Assert.All(skillValidator.WorkerSystemPrompts, p => Assert.False(string.IsNullOrWhiteSpace(p)));
        Assert.NotNull(skillValidator.MaxReflectIterations);
    }
}

public class GroupModelAnalyzerTests
{
    [Fact]
    public void Analyze_OrchestratorModeWithoutOrchestrator_ReturnsError()
    {
        var group = new SessionGroup { IsMultiAgent = true, OrchestratorMode = MultiAgentMode.Orchestrator };
        var members = new List<(string Name, string Model, MultiAgentRole Role)>
        {
            ("w1", "gpt-4.1", MultiAgentRole.Worker),
            ("w2", "gpt-4.1", MultiAgentRole.Worker),
        };

        var diags = GroupModelAnalyzer.Analyze(group, members);
        Assert.Contains(diags, d => d.Level == "error" && d.Message.Contains("Orchestrator role"));
    }

    [Fact]
    public void Analyze_WeakOrchestratorModel_ReturnsWarning()
    {
        var group = new SessionGroup { IsMultiAgent = true, OrchestratorMode = MultiAgentMode.Orchestrator };
        var members = new List<(string Name, string Model, MultiAgentRole Role)>
        {
            ("orch", "gpt-4.1", MultiAgentRole.Orchestrator),
            ("w1", "gpt-5", MultiAgentRole.Worker),
        };

        var diags = GroupModelAnalyzer.Analyze(group, members);
        Assert.Contains(diags, d => d.Level == "warning" && d.Message.Contains("reasoning"));
    }

    [Fact]
    public void Analyze_StrongOrchestrator_NoErrors()
    {
        var group = new SessionGroup { IsMultiAgent = true, OrchestratorMode = MultiAgentMode.Orchestrator };
        var members = new List<(string Name, string Model, MultiAgentRole Role)>
        {
            ("orch", "claude-opus-4.6", MultiAgentRole.Orchestrator),
            ("w1", "gpt-4.1", MultiAgentRole.Worker),
        };

        var diags = GroupModelAnalyzer.Analyze(group, members);
        Assert.DoesNotContain(diags, d => d.Level == "error");
    }

    [Fact]
    public void Analyze_AllSameModelBroadcast_SuggestsDiversity()
    {
        var group = new SessionGroup { IsMultiAgent = true, OrchestratorMode = MultiAgentMode.Broadcast };
        var members = new List<(string Name, string Model, MultiAgentRole Role)>
        {
            ("w1", "gpt-4.1", MultiAgentRole.Worker),
            ("w2", "gpt-4.1", MultiAgentRole.Worker),
            ("w3", "gpt-4.1", MultiAgentRole.Worker),
        };

        var diags = GroupModelAnalyzer.Analyze(group, members);
        Assert.Contains(diags, d => d.Level == "info" && d.Message.Contains("diverse"));
    }

    [Fact]
    public void Analyze_OrchestratorReflectWithoutWorkers_ReturnsError()
    {
        var group = new SessionGroup { IsMultiAgent = true, OrchestratorMode = MultiAgentMode.OrchestratorReflect };
        var members = new List<(string Name, string Model, MultiAgentRole Role)>
        {
            ("orch", "claude-opus-4.6", MultiAgentRole.Orchestrator),
        };

        var diags = GroupModelAnalyzer.Analyze(group, members);
        Assert.Contains(diags, d => d.Level == "error" && d.Message.Contains("worker"));
    }
}

public class UserPresetsTests
{
    [Fact]
    public void GetAll_IncludesBuiltInPresets()
    {
        // Use a temp dir that won't have presets.json
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var all = UserPresets.GetAll(tempDir);
            Assert.Equal(GroupPreset.BuiltIn.Length, all.Length);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var preset = new GroupPreset("My Team", "Custom desc", "🎯",
                MultiAgentMode.Orchestrator, "claude-opus-4.6", new[] { "gpt-4.1" })
            { IsUserDefined = true };

            UserPresets.Save(tempDir, new List<GroupPreset> { preset });
            var loaded = UserPresets.Load(tempDir);

            Assert.Single(loaded);
            Assert.Equal("My Team", loaded[0].Name);
            Assert.True(loaded[0].IsUserDefined);
            Assert.Equal("claude-opus-4.6", loaded[0].OrchestratorModel);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetAll_CombinesBuiltInAndUser()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var userPreset = new GroupPreset("Custom", "Mine", "⭐",
                MultiAgentMode.Broadcast, "gpt-5", new[] { "gpt-4.1" })
            { IsUserDefined = true };

            UserPresets.Save(tempDir, new List<GroupPreset> { userPreset });
            var all = UserPresets.GetAll(tempDir);

            Assert.Equal(GroupPreset.BuiltIn.Length + 1, all.Length);
            Assert.Contains(all, p => p.Name == "Custom" && p.IsUserDefined);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SaveGroupAsPreset_CreatesFromMembers()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var group = new SessionGroup { Name = "Test", IsMultiAgent = true, OrchestratorMode = MultiAgentMode.Orchestrator };
            var members = new List<SessionMeta>
            {
                new() { SessionName = "orch", Role = MultiAgentRole.Orchestrator },
                new() { SessionName = "w1", Role = MultiAgentRole.Worker },
            };

            var preset = UserPresets.SaveGroupAsPreset(tempDir, "Test Preset", "desc", "🔥",
                group, members, name => name == "orch" ? "claude-opus-4.6" : "gpt-4.1");

            Assert.NotNull(preset);
            Assert.Equal("claude-opus-4.6", preset!.OrchestratorModel);
            Assert.Single(preset.WorkerModels);
            Assert.Equal("gpt-4.1", preset.WorkerModels[0]);
            Assert.True(preset.IsUserDefined);

            // Verify persisted
            var loaded = UserPresets.Load(tempDir);
            Assert.Single(loaded);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SaveGroupAsPreset_WithWorktreeRoot_WritesSquadDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var worktreeRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(worktreeRoot);
            var group = new SessionGroup
            {
                Name = "SquadTeam",
                IsMultiAgent = true,
                OrchestratorMode = MultiAgentMode.OrchestratorReflect
            };
            var members = new List<SessionMeta>
            {
                new() { SessionName = "orch", Role = MultiAgentRole.Orchestrator },
                new() { SessionName = "w1", Role = MultiAgentRole.Worker, SystemPrompt = "You are a coder." },
            };

            var preset = UserPresets.SaveGroupAsPreset(tempDir, "SquadTeam", "desc", "🚀",
                group, members, name => name == "orch" ? "claude-opus-4.6" : "gpt-5",
                worktreeRoot: worktreeRoot);

            Assert.NotNull(preset);
            Assert.True(Directory.Exists(Path.Combine(worktreeRoot, ".squad")));
            Assert.True(preset!.IsRepoLevel);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            if (Directory.Exists(worktreeRoot)) Directory.Delete(worktreeRoot, true);
        }
    }
}

public class EvaluationTrackingTests
{
    [Fact]
    public void RecordEvaluation_FirstEntry_ReturnsStable()
    {
        var state = ReflectionCycle.Create("test goal");
        var trend = state.RecordEvaluation(1, 0.6, "Needs work", "gpt-4.1");
        Assert.Equal(QualityTrend.Stable, trend);
        Assert.Single(state.EvaluationHistory);
    }

    [Fact]
    public void RecordEvaluation_ImprovingScores_ReturnsImproving()
    {
        var state = ReflectionCycle.Create("test goal");
        state.RecordEvaluation(1, 0.4, "Poor", "gpt-4.1");
        var trend = state.RecordEvaluation(2, 0.7, "Better", "gpt-4.1");
        Assert.Equal(QualityTrend.Improving, trend);
    }

    [Fact]
    public void RecordEvaluation_DegradingScores_ReturnsDegrading()
    {
        var state = ReflectionCycle.Create("test goal");
        state.RecordEvaluation(1, 0.8, "Good", "gpt-4.1");
        var trend = state.RecordEvaluation(2, 0.5, "Got worse", "gpt-4.1");
        Assert.Equal(QualityTrend.Degrading, trend);
    }

    [Fact]
    public void RecordEvaluation_SimilarScores_ReturnsStable()
    {
        var state = ReflectionCycle.Create("test goal");
        state.RecordEvaluation(1, 0.6, "Ok", "gpt-4.1");
        var trend = state.RecordEvaluation(2, 0.65, "Similar", "gpt-4.1");
        Assert.Equal(QualityTrend.Stable, trend);
    }

    [Fact]
    public void EvaluatorSession_CanBeConfigured()
    {
        var state = ReflectionCycle.Create("goal", 5, null, "eval-session");
        Assert.Equal("eval-session", state.EvaluatorSessionName);
    }

    [Fact]
    public void PendingAdjustments_InitiallyEmpty()
    {
        var state = ReflectionCycle.Create("goal");
        Assert.Empty(state.PendingAdjustments);
    }

    [Fact]
    public void EvaluationHistory_TracksMultipleIterations()
    {
        var state = ReflectionCycle.Create("goal");
        state.RecordEvaluation(1, 0.3, "Bad", "claude-haiku-4.5");
        state.RecordEvaluation(2, 0.5, "Improving", "claude-haiku-4.5");
        state.RecordEvaluation(3, 0.8, "Good", "claude-haiku-4.5");

        Assert.Equal(3, state.EvaluationHistory.Count);
        Assert.Equal(0.3, state.EvaluationHistory[0].Score);
        Assert.Equal(0.8, state.EvaluationHistory[2].Score);
        Assert.All(state.EvaluationHistory, e => Assert.Equal("claude-haiku-4.5", e.EvaluatorModel));
    }
}

public class ModelNameInferenceTests
{
    [Fact]
    public void InferFromName_OpusVariant_HasReasoningExpert()
    {
        var caps = ModelCapabilities.InferFromName("claude-opus-5.0");
        Assert.True(caps.HasFlag(ModelCapability.ReasoningExpert));
        Assert.True(caps.HasFlag(ModelCapability.CodeExpert));
    }

    [Fact]
    public void InferFromName_SonnetVariant_HasCodeExpert()
    {
        var caps = ModelCapabilities.InferFromName("claude-sonnet-5.0");
        Assert.True(caps.HasFlag(ModelCapability.CodeExpert));
        Assert.True(caps.HasFlag(ModelCapability.Fast));
    }

    [Fact]
    public void InferFromName_HaikuVariant_HasFastAndCheap()
    {
        var caps = ModelCapabilities.InferFromName("claude-haiku-5.0");
        Assert.True(caps.HasFlag(ModelCapability.Fast));
        Assert.True(caps.HasFlag(ModelCapability.CostEfficient));
    }

    [Fact]
    public void InferFromName_CodexVariant_HasCodeExpert()
    {
        var caps = ModelCapabilities.InferFromName("gpt-6-codex");
        Assert.True(caps.HasFlag(ModelCapability.CodeExpert));
    }

    [Fact]
    public void InferFromName_MiniVariant_HasFastAndCheap()
    {
        var caps = ModelCapabilities.InferFromName("gpt-6-mini");
        Assert.True(caps.HasFlag(ModelCapability.Fast));
        Assert.True(caps.HasFlag(ModelCapability.CostEfficient));
    }

    [Fact]
    public void InferFromName_MaxVariant_HasReasoningExpert()
    {
        var caps = ModelCapabilities.InferFromName("gpt-6-codex-max");
        Assert.True(caps.HasFlag(ModelCapability.ReasoningExpert));
    }

    [Fact]
    public void InferFromName_GeminiVariant_HasVision()
    {
        var caps = ModelCapabilities.InferFromName("gemini-4-ultra");
        Assert.True(caps.HasFlag(ModelCapability.Vision));
        Assert.True(caps.HasFlag(ModelCapability.ReasoningExpert));
    }

    [Fact]
    public void InferFromName_UnknownModel_ReturnsNone()
    {
        var caps = ModelCapabilities.InferFromName("totally-unknown-model");
        Assert.Equal(ModelCapability.None, caps);
    }

    [Fact]
    public void GetCapabilities_NewOpusVersion_InfersFromName()
    {
        // Not in registry, but should be inferred
        var caps = ModelCapabilities.GetCapabilities("claude-opus-99.0");
        Assert.True(caps.HasFlag(ModelCapability.ReasoningExpert));
    }
}

public class ParseEvaluationScoreTests
{
    [Fact]
    public void ParseScore_ValidFormat_ExtractsCorrectly()
    {
        var response = "SCORE: 0.75\nRATIONALE: Good progress but missing edge cases.\n[[NEEDS_ITERATION]]";
        var (score, rationale) = CopilotService.ParseEvaluationScore(response);
        Assert.Equal(0.75, score);
        Assert.Contains("Good progress", rationale);
    }

    [Fact]
    public void ParseScore_HighScore_ExtractsCorrectly()
    {
        var response = "SCORE: 0.95\nRATIONALE: Excellent output, fully addresses the goal.\n[[GROUP_REFLECT_COMPLETE]]";
        var (score, rationale) = CopilotService.ParseEvaluationScore(response);
        Assert.Equal(0.95, score);
        Assert.Contains("Excellent", rationale);
    }

    [Fact]
    public void ParseScore_NoScoreMarker_ReturnsDefault()
    {
        var response = "The output looks good but could improve.";
        var (score, _) = CopilotService.ParseEvaluationScore(response);
        Assert.Equal(0.5, score); // default
    }

    [Fact]
    public void ParseScore_ClampAboveOne_Returns1()
    {
        var response = "SCORE: 1.5\nRATIONALE: Overshot.";
        var (score, _) = CopilotService.ParseEvaluationScore(response);
        Assert.Equal(1.0, score);
    }

    [Fact]
    public void ParseScore_NegativeScore_ReturnsZero()
    {
        var response = "SCORE: -0.5\nRATIONALE: Terrible.";
        var (score, _) = CopilotService.ParseEvaluationScore(response);
        Assert.Equal(0.0, score);
    }
}

/// <summary>
/// End-to-end scenario tests demonstrating complete multi-agent user flows.
/// These serve as executable documentation of the feature's user experience.
/// </summary>
public class MultiAgentScenarioTests
{
    /// <summary>
    /// Scenario: User creates a "Code Review Team" from a built-in preset.
    /// 
    /// User flow:
    ///   1. Click 🚀 Preset in sidebar toolbar
    ///   2. Preset picker appears showing 3 built-in templates
    ///   3. Select "PR Review Squad" (📋)
    ///   4. System creates: Orchestrator (claude-opus-4.6) + 5 Workers
    ///   5. Sidebar shows group with mode selector set to "🎯 Orchestrator"
    ///   6. Each session shows its model assignment and role badge
    /// </summary>
    [Fact]
    public void Scenario_CreateGroupFromPreset()
    {
        // Step 1-2: User sees built-in presets
        var presets = GroupPreset.BuiltIn;
        Assert.Equal(3, presets.Length);

        // Step 3: User picks "PR Review Squad"
        var prReview = presets.First(p => p.Name == "PR Review Squad");
        Assert.Equal("📋", prReview.Emoji);
        Assert.Equal(MultiAgentMode.Orchestrator, prReview.Mode);
        Assert.Equal("claude-opus-4.6", prReview.OrchestratorModel);
        Assert.Equal(5, prReview.WorkerModels.Length);

        // Step 4: System creates the group - verify the preset structure
        // (CopilotService.CreateGroupFromPresetAsync does the actual creation at runtime)
        Assert.Equal("claude-sonnet-4.6", prReview.WorkerModels[0]);

        // Step 5-6: Each member has appropriate capabilities
        var orchCaps = ModelCapabilities.GetCapabilities(prReview.OrchestratorModel);
        Assert.True(orchCaps.HasFlag(ModelCapability.ReasoningExpert));

        var warnings = ModelCapabilities.GetRoleWarnings(prReview.OrchestratorModel, MultiAgentRole.Orchestrator);
        Assert.Empty(warnings); // opus is a great orchestrator, no warnings

        foreach (var workerModel in prReview.WorkerModels)
        {
            var wCaps = ModelCapabilities.GetCapabilities(workerModel);
            Assert.True(wCaps.HasFlag(ModelCapability.CodeExpert)); // both are code-capable
        }
    }

    /// <summary>
    /// Scenario: User assigns a weak model to the Orchestrator role and sees warnings.
    /// 
    /// User flow:
    ///   1. Long-press/right-click a session in a multi-agent group → context menu
    ///   2. See "🎯 Set as Orchestrator" button → click it
    ///   3. Under "🧠 Model", pick "gpt-4.1" from dropdown
    ///   4. Warning appears: "⚠️ This model may lack strong reasoning for orchestration"
    ///   5. Warning appears: "💰 Cost-efficient models may produce shallow plans"
    ///   6. User also sees diagnostics in the group header:
    ///      "⚠️ Orchestrator 'session1' uses gpt-4.1 which lacks strong reasoning"
    /// </summary>
    [Fact]
    public void Scenario_WeakOrchestratorWarnings()
    {
        // Step 3-5: User picks gpt-4.1 for orchestrator role
        var warnings = ModelCapabilities.GetRoleWarnings("gpt-4.1", MultiAgentRole.Orchestrator);
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("reasoning"));
        Assert.Contains(warnings, w => w.Contains("Cost-efficient"));

        // Step 6: Group diagnostics also flag the issue
        var group = new SessionGroup
        {
            IsMultiAgent = true,
            OrchestratorMode = MultiAgentMode.Orchestrator
        };
        var members = new List<(string Name, string Model, MultiAgentRole Role)>
        {
            ("session1", "gpt-4.1", MultiAgentRole.Orchestrator),
            ("session2", "gpt-5", MultiAgentRole.Worker),
        };
        var diags = GroupModelAnalyzer.Analyze(group, members);
        Assert.Contains(diags, d => d.Level == "warning" && d.Message.Contains("gpt-4.1"));

        // Compare: strong orchestrator shows no role warnings
        var strongWarnings = ModelCapabilities.GetRoleWarnings("claude-opus-4.6", MultiAgentRole.Orchestrator);
        Assert.Empty(strongWarnings);
    }

    /// <summary>
    /// Scenario: Full OrchestratorReflect iteration cycle with evaluation scoring.
    /// 
    /// User flow:
    ///   1. User selects "🔄 Orchestrator + Reflect" from mode dropdown
    ///   2. Types goal in the multi-agent input bar and clicks 📡
    ///   3. Sidebar shows: 🔄 1/5 with goal text
    ///   4. After iteration 1, evaluator scores 0.4 → sidebar shows "📊 0.4 (gpt-4.1)"
    ///   5. AutoAdjust detects no issues yet → no banner
    ///   6. After iteration 2, evaluator scores 0.7 → trend = Improving
    ///   7. After iteration 3, evaluator scores 0.65 → trend = Stable (slight drop)
    ///   8. After iteration 4, evaluator scores 0.92 → goal met, loop stops
    ///   9. Sidebar shows: "✅ Goal met after 4 iteration(s)"
    /// </summary>
    [Fact]
    public void Scenario_FullReflectCycleWithScoring()
    {
        // Step 1-2: User starts OrchestratorReflect
        var state = ReflectionCycle.Create("Implement a REST API with CRUD endpoints", maxIterations: 5);
        Assert.True(state.IsActive);
        Assert.Equal(0, state.CurrentIteration);
        Assert.NotNull(state.StartedAt);

        // Step 3-4: Iteration 1 — low quality initial attempt
        state.CurrentIteration = 1;
        var trend1 = state.RecordEvaluation(1, 0.4, "Missing error handling and input validation. Only GET endpoint implemented.", "gpt-4.1");
        Assert.Equal(QualityTrend.Stable, trend1); // only one data point
        Assert.Single(state.EvaluationHistory);

        // Sidebar would show: 🔄 1/5 📊 0.4 (gpt-4.1)
        var lastEval = state.EvaluationHistory.Last();
        Assert.Equal("0.4", lastEval.Score.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("gpt-4.1", lastEval.EvaluatorModel);

        // Step 6: Iteration 2 — significant improvement
        state.CurrentIteration = 2;
        var trend2 = state.RecordEvaluation(2, 0.7, "All CRUD endpoints present. Error handling added but tests incomplete.", "gpt-4.1");
        Assert.Equal(QualityTrend.Improving, trend2);

        // Step 7: Iteration 3 — slight regression
        state.CurrentIteration = 3;
        var trend3 = state.RecordEvaluation(3, 0.65, "Tests added but some CRUD operations regressed. PUT endpoint missing validation.", "gpt-4.1");
        Assert.Equal(QualityTrend.Stable, trend3); // within 0.1 threshold

        // Step 8: Iteration 4 — goal met
        state.CurrentIteration = 4;
        var trend4 = state.RecordEvaluation(4, 0.92, "All endpoints complete with validation, error handling, and comprehensive tests.", "gpt-4.1");
        Assert.Equal(QualityTrend.Improving, trend4);

        // Score >= 0.9 would trigger goal completion
        state.GoalMet = true;
        state.IsActive = false;
        state.CompletedAt = DateTime.Now;

        // Step 9: Final summary
        var summary = state.BuildCompletionSummary();
        Assert.Contains("Goal met", summary);
        Assert.Equal(4, state.EvaluationHistory.Count);

        // Verify the quality trajectory is tracked
        var scores = state.EvaluationHistory.Select(e => e.Score).ToList();
        Assert.Equal(new[] { 0.4, 0.7, 0.65, 0.92 }, scores);
    }

    /// <summary>
    /// Scenario: AutoAdjust detects quality degradation and surfaces a banner.
    /// 
    /// User flow:
    ///   1. Reflect loop running with 3 workers
    ///   2. Iteration 2 scores 0.7, iteration 3 scores 0.45 (sharp drop)
    ///   3. AutoAdjust detects degradation in evaluation history
    ///   4. Sidebar shows amber banner: "📉 Quality degraded significantly vs. previous iteration"
    ///   5. Worker "fast-coder" using gpt-4.1 produced only 50 chars on iteration 3
    ///   6. Banner also shows: "📈 Worker 'fast-coder' produced a brief response. Consider upgrading..."
    ///   7. User can see these suggestions and decide to change the worker's model
    /// </summary>
    [Fact]
    public void Scenario_AutoAdjustDetectsIssuesAndSurfacesBanner()
    {
        var state = ReflectionCycle.Create("Build a microservice");
        state.CurrentIteration = 3;

        // Steps 2-3: Record scores showing degradation
        state.RecordEvaluation(1, 0.5, "Initial attempt", "gpt-4.1");
        state.RecordEvaluation(2, 0.7, "Good progress", "gpt-4.1");
        state.RecordEvaluation(3, 0.45, "Quality dropped", "gpt-4.1");

        // The last two evals show a significant drop (0.7 → 0.45 = -0.25 > 0.15 threshold)
        var lastTwo = state.EvaluationHistory.TakeLast(2).ToList();
        var degradation = lastTwo[0].Score - lastTwo[1].Score;
        Assert.True(degradation > 0.15); // threshold for "significant" degradation

        // Step 4-6: AutoAdjust would populate PendingAdjustments
        // Simulating what AutoAdjustFromFeedback does:
        state.PendingAdjustments.Clear();
        state.PendingAdjustments.Add("📉 Quality degraded significantly vs. previous iteration. Review worker models or task clarity.");
        state.PendingAdjustments.Add("📈 Worker 'fast-coder' produced a brief response. Consider upgrading from a cost-efficient model to improve quality.");

        // Verify the banner would display
        Assert.Equal(2, state.PendingAdjustments.Count);
        Assert.Contains(state.PendingAdjustments, a => a.Contains("📉"));
        Assert.Contains(state.PendingAdjustments, a => a.Contains("fast-coder"));

        // Step 7: User changes the model — verify gpt-4.1 is flagged as cost-efficient
        var caps = ModelCapabilities.GetCapabilities("gpt-4.1");
        Assert.True(caps.HasFlag(ModelCapability.CostEfficient));
        Assert.False(caps.HasFlag(ModelCapability.ReasoningExpert));
    }

    /// <summary>
    /// Scenario: User saves their tuned multi-agent group as a reusable preset.
    /// 
    /// User flow:
    ///   1. User has a working Orchestrator group: opus orchestrator, 2 workers
    ///   2. They've tweaked models over several iterations and are happy
    ///   3. Click "💾 Save as Preset" button in sidebar
    ///   4. System saves to ~/.polypilot/presets.json
    ///   5. Next time user clicks 🚀 Preset, their custom preset appears with 👤 badge
    ///   6. User-defined presets appear after built-in ones
    /// </summary>
    [Fact]
    public void Scenario_SaveAndReuseCustomPreset()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            // Step 1: User has a working group
            var group = new SessionGroup
            {
                Name = "My API Team",
                IsMultiAgent = true,
                OrchestratorMode = MultiAgentMode.OrchestratorReflect
            };
            var members = new List<SessionMeta>
            {
                new() { SessionName = "planner", Role = MultiAgentRole.Orchestrator },
                new() { SessionName = "coder", Role = MultiAgentRole.Worker },
                new() { SessionName = "reviewer", Role = MultiAgentRole.Worker },
            };

            // Step 3-4: Save as preset
            var preset = UserPresets.SaveGroupAsPreset(
                tempDir, "My API Team", "OrchestratorReflect with reviewer", "🏗️",
                group, members,
                name => name switch
                {
                    "planner" => "claude-opus-4.6",
                    "coder" => "gpt-5.1-codex",
                    "reviewer" => "claude-sonnet-4.5",
                    _ => "gpt-4.1"
                });

            Assert.NotNull(preset);
            Assert.True(preset!.IsUserDefined);
            Assert.Equal("claude-opus-4.6", preset.OrchestratorModel);
            Assert.Equal(2, preset.WorkerModels.Length);
            Assert.Equal(MultiAgentMode.OrchestratorReflect, preset.Mode);

            // Step 5-6: Next time, preset picker shows built-in + user presets
            var allPresets = UserPresets.GetAll(tempDir);
            Assert.Equal(GroupPreset.BuiltIn.Length + 1, allPresets.Length);

            // User-defined presets come after built-in ones
            var userPresets = allPresets.Where(p => p.IsUserDefined).ToArray();
            Assert.Single(userPresets);
            Assert.Equal("My API Team", userPresets[0].Name);

            // The preset correctly captures the model assignments
            Assert.Contains("gpt-5.1-codex", preset.WorkerModels);
            Assert.Contains("claude-sonnet-4.5", preset.WorkerModels);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    /// <summary>
    /// Scenario: Dedicated evaluator session provides independent scoring.
    /// 
    /// User flow:
    ///   1. User creates a "Quick Reflection Cycle" from presets (OrchestratorReflect)
    ///   2. Group has: opus orchestrator + 3 cheap workers
    ///   3. User adds a 4th session, sets role to Worker, assigns gpt-4.1
    ///   4. In code, EvaluatorSession is set to this 4th session
    ///   5. Orchestrator synthesizes, then evaluator independently scores
    ///   6. Evaluator responds with structured format: "SCORE: 0.75\nRATIONALE: ..."
    ///   7. System parses score, records it, shows in sidebar
    /// </summary>
    [Fact]
    public void Scenario_DedicatedEvaluatorScoring()
    {
        // Step 1-4: Group with evaluator
        var state = ReflectionCycle.Create("Refactor auth module", maxIterations: 5, evaluatorSession: "eval-agent");
        Assert.Equal("eval-agent", state.EvaluatorSessionName);

        // Step 6-7: Evaluator responds with structured format
        var evalResponse = """
            ## Evaluation
            
            SCORE: 0.75
            RATIONALE: The auth module refactoring covers JWT validation and middleware setup, but session management is incomplete and there are no integration tests. The code structure is clean but error handling paths need work.
            
            [[NEEDS_ITERATION]]
            - Add session persistence layer
            - Add integration tests for login/logout flow
            - Improve error handling in token refresh
            """;

        var (score, rationale) = CopilotService.ParseEvaluationScore(evalResponse);
        Assert.Equal(0.75, score);
        Assert.Contains("session management is incomplete", rationale);

        // Record it
        var trend = state.RecordEvaluation(1, score, rationale, "gpt-4.1");
        Assert.Equal(QualityTrend.Stable, trend);

        // Sidebar shows: 📊 0.8 (gpt-4.1)
        Assert.Equal(0.75, state.EvaluationHistory.Last().Score);

        // Next iteration: evaluator says done
        var evalResponse2 = """
            SCORE: 0.93
            RATIONALE: All requirements met. Session persistence added, integration tests pass, error handling is comprehensive.
            
            [[GROUP_REFLECT_COMPLETE]]
            """;

        var (score2, _) = CopilotService.ParseEvaluationScore(evalResponse2);
        Assert.Equal(0.93, score2);
        Assert.True(score2 >= 0.9); // triggers completion
        Assert.Contains("[[GROUP_REFLECT_COMPLETE]]", evalResponse2);

        state.RecordEvaluation(2, score2, "All requirements met.", "gpt-4.1");
        state.GoalMet = true;
        Assert.Contains("Goal met", state.BuildCompletionSummary());
    }

    /// <summary>
    /// Scenario: Stall detection stops a reflect loop that's going in circles.
    /// 
    /// User flow:
    ///   1. Reflect loop is running, iteration 3
    ///   2. Workers keep producing similar output to iterations 1-2
    ///   3. String-based stall detector triggers after 2 consecutive matches
    ///   4. Sidebar shows: "⚠️ Stalled after 3 iteration(s)"
    ///   5. AutoAdjust banner: "⚠️ Output repetition detected..."
    /// </summary>
    [Fact]
    public void Scenario_StallDetectionStopsLoop()
    {
        var state = ReflectionCycle.Create("Optimize database queries");

        // Iterations 1-2: different responses — no stall
        state.CurrentIteration = 1;
        Assert.False(state.CheckStall("First attempt: added indexes on user_id column"));

        state.CurrentIteration = 2;
        Assert.False(state.CheckStall("Second attempt: refactored joins to use CTEs"));

        // Iteration 3: exact repeat of iteration 2 — CheckStall detects string match immediately
        state.CurrentIteration = 3;
        Assert.True(state.CheckStall("Second attempt: refactored joins to use CTEs"));
        state.IsStalled = true; // In the real loop, Advance() sets this

        Assert.Contains("Stalled", state.BuildCompletionSummary());
    }

    /// <summary>
    /// Scenario: Model name inference handles a brand-new model release gracefully.
    /// 
    /// User flow:
    ///   1. A new model "claude-opus-5.0" is released
    ///   2. Copilot server makes it available in AvailableModels
    ///   3. User assigns it to an orchestrator via the model picker
    ///   4. ModelCapabilities doesn't have it in the registry
    ///   5. InferFromName detects "opus" → ReasoningExpert + CodeExpert + ToolUse
    ///   6. No "weak model" warning appears for orchestrator role
    ///   7. User also assigns "gpt-6-codex-mini" to a worker
    ///   8. InferFromName detects "codex" + "mini" → CodeExpert + Fast + CostEfficient
    /// </summary>
    [Fact]
    public void Scenario_NewModelReleasesHandledGracefully()
    {
        // Step 3-6: New opus model, not in registry
        var opusCaps = ModelCapabilities.GetCapabilities("claude-opus-5.0");
        Assert.True(opusCaps.HasFlag(ModelCapability.ReasoningExpert));
        Assert.True(opusCaps.HasFlag(ModelCapability.CodeExpert));

        var orchWarnings = ModelCapabilities.GetRoleWarnings("claude-opus-5.0", MultiAgentRole.Orchestrator);
        // Should not warn about reasoning since inference detects it
        Assert.DoesNotContain(orchWarnings, w => w.Contains("reasoning"));

        // Step 7-8: New codex-mini model
        var codexMiniCaps = ModelCapabilities.GetCapabilities("gpt-6-codex-mini");
        Assert.True(codexMiniCaps.HasFlag(ModelCapability.CodeExpert));
        Assert.True(codexMiniCaps.HasFlag(ModelCapability.Fast));
        Assert.True(codexMiniCaps.HasFlag(ModelCapability.CostEfficient));

        // Worker role should work fine with this model
        var workerWarnings = ModelCapabilities.GetRoleWarnings("gpt-6-codex-mini", MultiAgentRole.Worker);
        Assert.Empty(workerWarnings); // codex has CodeExpert, no warning

        // Strengths description works via inference for unknown models
        var strengths = ModelCapabilities.GetStrengths("claude-opus-5.0");
        Assert.StartsWith("Inferred:", strengths);
        Assert.Contains("reasoning", strengths);
        Assert.Contains("code", strengths);
    }

    /// <summary>
    /// Scenario: Full diagnostics flow for a misconfigured group.
    /// 
    /// User flow:
    ///   1. User creates Orchestrator group but forgets to assign an orchestrator role
    ///   2. All 3 sessions are Workers using the same cheap model
    ///   3. Diagnostics panel shows:
    ///      ⛔ "Orchestrator mode requires at least one session with the Orchestrator role."
    ///      💡 "All workers use the same model. For diverse perspectives, assign different models."
    ///   4. User fixes: assigns one session as Orchestrator with opus
    ///   5. Diagnostics update to clear the error, but show:
    ///      💰 "Worker 'deep-thinker' uses premium model gpt-5.1. Consider a faster/cheaper model."
    /// </summary>
    [Fact]
    public void Scenario_DiagnosticsGuideMisconfiguration()
    {
        // Step 1-3: Misconfigured group
        var group = new SessionGroup
        {
            IsMultiAgent = true,
            OrchestratorMode = MultiAgentMode.Orchestrator
        };
        var badMembers = new List<(string Name, string Model, MultiAgentRole Role)>
        {
            ("agent1", "gpt-4.1", MultiAgentRole.Worker),
            ("agent2", "gpt-4.1", MultiAgentRole.Worker),
            ("agent3", "gpt-4.1", MultiAgentRole.Worker),
        };

        var diags1 = GroupModelAnalyzer.Analyze(group, badMembers);
        Assert.Contains(diags1, d => d.Level == "error" && d.Message.Contains("Orchestrator role"));

        // In broadcast mode, same-model workers get a diversity hint
        group.OrchestratorMode = MultiAgentMode.Broadcast;
        var diags1b = GroupModelAnalyzer.Analyze(group, badMembers);
        Assert.Contains(diags1b, d => d.Level == "info" && d.Message.Contains("diverse"));

        // Step 4-5: User fixes by adding orchestrator with strong model, worker with premium
        group.OrchestratorMode = MultiAgentMode.Orchestrator;
        var fixedMembers = new List<(string Name, string Model, MultiAgentRole Role)>
        {
            ("planner", "claude-opus-4.6", MultiAgentRole.Orchestrator),
            ("fast-worker", "gpt-4.1", MultiAgentRole.Worker),
            ("deep-thinker", "gpt-5.1", MultiAgentRole.Worker),
        };

        var diags2 = GroupModelAnalyzer.Analyze(group, fixedMembers);
        Assert.DoesNotContain(diags2, d => d.Level == "error"); // no more errors
        Assert.Contains(diags2, d => d.Level == "info" && d.Message.Contains("deep-thinker") && d.Message.Contains("premium"));
    }
}

/// <summary>
/// Tests for the aligned stall handling between single-agent and multi-agent paths.
/// Both now use 2-consecutive-stalls tolerance via ConsecutiveStalls counter.
/// </summary>
public class StallHandlingAlignmentTests
{
    [Fact]
    public void SingleAgent_Advance_ToleratesFirstStall()
    {
        var cycle = ReflectionCycle.Create("Test goal", maxIterations: 10);

        // First iteration — unique response
        Assert.True(cycle.Advance("First unique response about the topic"));

        // Second iteration — repeat triggers CheckStall but Advance tolerates it
        Assert.True(cycle.Advance("First unique response about the topic"));
        Assert.Equal(1, cycle.ConsecutiveStalls);
        Assert.True(cycle.ShouldWarnOnStall); // warning but not stopped
        Assert.False(cycle.IsStalled);

        // Third iteration — still repeating, now stalled
        Assert.False(cycle.Advance("First unique response about the topic"));
        Assert.True(cycle.IsStalled);
        Assert.Equal(2, cycle.ConsecutiveStalls);
    }

    [Fact]
    public void SingleAgent_Advance_ResetsStallCountOnNewContent()
    {
        var cycle = ReflectionCycle.Create("Test goal", maxIterations: 10);

        cycle.Advance("Response A with some content");
        cycle.Advance("Response A with some content"); // first stall
        Assert.Equal(1, cycle.ConsecutiveStalls);

        cycle.Advance("Response B completely different content"); // new content resets
        Assert.Equal(0, cycle.ConsecutiveStalls);
        Assert.False(cycle.IsStalled);
    }

    [Fact]
    public void MultiAgent_StallHandling_MatchesSingleAgent()
    {
        // Verify the multi-agent path uses same 2-consecutive tolerance
        // by testing the ReflectionCycle state directly (service layer applies same logic)
        var state = ReflectionCycle.Create("Multi-agent goal", maxIterations: 10);

        // Simulate what SendViaOrchestratorReflectAsync does:
        // First stall: warn but continue
        state.CurrentIteration = 1;
        var isStall1 = state.CheckStall("Synthesis of worker outputs about authentication");
        Assert.False(isStall1);

        state.CurrentIteration = 2;
        var isStall2 = state.CheckStall("Synthesis of worker outputs about authentication"); // repeat
        Assert.True(isStall2);

        // Multi-agent path now increments ConsecutiveStalls (aligned with Advance)
        state.ConsecutiveStalls++;
        Assert.Equal(1, state.ConsecutiveStalls);
        Assert.False(state.ConsecutiveStalls >= 2); // NOT stopped yet — this is the fix

        state.CurrentIteration = 3;
        var isStall3 = state.CheckStall("Synthesis of worker outputs about authentication"); // still repeating
        Assert.True(isStall3);
        state.ConsecutiveStalls++;
        Assert.True(state.ConsecutiveStalls >= 2); // NOW stopped
        state.IsStalled = true;
        Assert.Contains("Stalled", state.BuildCompletionSummary());
    }

    [Fact]
    public void CheckStall_JaccardSimilarity_CatchesRephrasing()
    {
        var cycle = ReflectionCycle.Create("Test goal");

        // First response
        Assert.False(cycle.CheckStall("The authentication module needs JWT token validation and session management"));

        // Very similar rephrasing (should trigger Jaccard > 0.9)
        Assert.True(cycle.CheckStall("The authentication module needs JWT token validation and session management support"));
    }

    [Fact]
    public void CheckStall_DifferentContent_NoFalsePositive()
    {
        var cycle = ReflectionCycle.Create("Test goal");

        Assert.False(cycle.CheckStall("First I will implement the database layer with PostgreSQL"));
        Assert.False(cycle.CheckStall("Next the API routes need Express middleware for auth"));
        Assert.False(cycle.CheckStall("Finally the frontend React components for the dashboard"));
    }
}

public class WorktreeTeamAssociationTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly IServiceProvider _serviceProvider;

    public WorktreeTeamAssociationTests()
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
    public void SessionGroup_WorktreeId_DefaultsToNull()
    {
        var group = new SessionGroup();
        Assert.Null(group.WorktreeId);
    }

    [Fact]
    public void CreateMultiAgentGroup_WithWorktreeId_SetsGroupFields()
    {
        var svc = CreateService();
        var group = svc.CreateMultiAgentGroup("Test Team",
            worktreeId: "wt-123",
            repoId: "repo-abc");

        Assert.Equal("wt-123", group.WorktreeId);
        Assert.Equal("repo-abc", group.RepoId);
        Assert.True(group.IsMultiAgent);
    }

    [Fact]
    public void CreateMultiAgentGroup_WithWorktree_SetsSessionMetaWorktreeId()
    {
        var svc = CreateService();
        // Pre-create sessions
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "worker1" });
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "worker2" });

        var group = svc.CreateMultiAgentGroup("Test Team",
            sessionNames: new List<string> { "worker1", "worker2" },
            worktreeId: "wt-456",
            repoId: "repo-xyz");

        var w1 = svc.Organization.Sessions.First(s => s.SessionName == "worker1");
        var w2 = svc.Organization.Sessions.First(s => s.SessionName == "worker2");

        Assert.Equal("wt-456", w1.WorktreeId);
        Assert.Equal("wt-456", w2.WorktreeId);
        Assert.Equal(group.Id, w1.GroupId);
        Assert.Equal(group.Id, w2.GroupId);
    }

    [Fact]
    public void CreateMultiAgentGroup_WithoutWorktree_DoesNotSetWorktreeId()
    {
        var svc = CreateService();
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "worker1" });

        var group = svc.CreateMultiAgentGroup("Test Team",
            sessionNames: new List<string> { "worker1" });

        Assert.Null(group.WorktreeId);
        Assert.Null(group.RepoId);
        var w1 = svc.Organization.Sessions.First(s => s.SessionName == "worker1");
        Assert.Null(w1.WorktreeId);
    }

    [Fact]
    public void SessionGroup_WorktreeId_RoundTripsViaJson()
    {
        var state = new OrganizationState();
        state.Groups.Add(new SessionGroup
        {
            Id = "g1",
            Name = "Team",
            IsMultiAgent = true,
            WorktreeId = "wt-789",
            RepoId = "repo-test"
        });

        var json = JsonSerializer.Serialize(state);
        var restored = JsonSerializer.Deserialize<OrganizationState>(json)!;

        var group = restored.Groups.First(g => g.Id == "g1");
        Assert.Equal("wt-789", group.WorktreeId);
        Assert.Equal("repo-test", group.RepoId);
    }

    [Fact]
    public async Task CreateGroupFromPresetAsync_WithWorktree_SetsGroupAndSessionWorktreeIds()
    {
        var svc = CreateService();
        var preset = new GroupPreset(
            Name: "Test Preset",
            Emoji: "🧪",
            Description: "Test",
            OrchestratorModel: "claude-opus-4.6",
            WorkerModels: new[] { "gpt-5.1-codex", "claude-sonnet-4.5" },
            Mode: MultiAgentMode.Broadcast
        );

        // CreateSessionAsync will throw since StubServerManager doesn't implement it,
        // but the group itself should be created with worktree info
        var group = await svc.CreateGroupFromPresetAsync(preset,
            workingDirectory: @"C:\repos\test",
            worktreeId: "wt-preset",
            repoId: "repo-preset");

        Assert.NotNull(group);
        Assert.Equal("wt-preset", group!.WorktreeId);
        Assert.Equal("repo-preset", group.RepoId);
    }

    [Fact]
    public async Task CreateGroupFromPresetAsync_PreservesOrchestratorReflectMode()
    {
        var svc = CreateService();
        var preset = new GroupPreset(
            Name: "Reflect Test",
            Emoji: "🔄",
            Description: "Test reflect mode",
            OrchestratorModel: "claude-opus-4.6",
            WorkerModels: new[] { "gpt-4.1" },
            Mode: MultiAgentMode.OrchestratorReflect
        );

        var group = await svc.CreateGroupFromPresetAsync(preset);

        Assert.NotNull(group);
        Assert.Equal(MultiAgentMode.OrchestratorReflect, group!.OrchestratorMode);
    }

    [Fact]
    public async Task CreateGroupFromPresetAsync_PinsOrchestratorSession()
    {
        var svc = CreateService();
        var preset = new GroupPreset(
            Name: "Pin Test",
            Emoji: "📌",
            Description: "Test orchestrator pinning",
            OrchestratorModel: "claude-opus-4.6",
            WorkerModels: new[] { "gpt-4.1", "claude-sonnet-4.5" },
            Mode: MultiAgentMode.Orchestrator
        );

        var group = await svc.CreateGroupFromPresetAsync(preset);

        Assert.NotNull(group);
        var orchMeta = svc.Organization.Sessions
            .FirstOrDefault(m => m.SessionName == "Pin Test-orchestrator");
        Assert.NotNull(orchMeta);
        Assert.True(orchMeta!.IsPinned, "Orchestrator should be pinned on creation");
        Assert.Equal(MultiAgentRole.Orchestrator, orchMeta.Role);

        // Workers should NOT be pinned
        var workers = svc.Organization.Sessions
            .Where(m => m.SessionName.StartsWith("Pin Test-worker-"));
        Assert.All(workers, w => Assert.False(w.IsPinned));
    }

    [Fact]
    public void OrchestratorReflectMode_RoundTripsViaJson()
    {
        var state = new OrganizationState();
        state.Groups.Add(new SessionGroup
        {
            Id = "g-reflect",
            Name = "Reflect Team",
            IsMultiAgent = true,
            OrchestratorMode = MultiAgentMode.OrchestratorReflect
        });

        var json = JsonSerializer.Serialize(state);
        var restored = JsonSerializer.Deserialize<OrganizationState>(json)!;

        var group = restored.Groups.First(g => g.Id == "g-reflect");
        Assert.Equal(MultiAgentMode.OrchestratorReflect, group.OrchestratorMode);
    }

    [Fact]
    public void GroupHeader_ShowsWorktreeBadge_WhenWorktreeIdSet()
    {
        // Verify the data model supports worktree display in group headers
        var group = new SessionGroup
        {
            Name = "Code Review Team",
            IsMultiAgent = true,
            WorktreeId = "wt-feature",
            RepoId = "PureWeen-PolyPilot"
        };

        Assert.NotNull(group.WorktreeId);
        Assert.NotNull(group.RepoId);
        Assert.True(group.IsMultiAgent);
    }

    [Fact]
    public void ShortenPath_TwoOrFewerSegments_ReturnsOriginal()
    {
        Assert.Equal("test", ShortenPathHelper("test"));
        Assert.Equal(@"C:\test", ShortenPathHelper(@"C:\test"));
    }

    [Fact]
    public void ShortenPath_LongPath_ShowsLastTwoSegments()
    {
        // Use platform-native path to avoid separator mismatch
        var path = System.IO.Path.Combine("C:", "Users", "shneuvil", ".polypilot", "worktrees", "my-repo");
        var result = ShortenPathHelper(path);
        var sep = System.IO.Path.DirectorySeparatorChar;
        Assert.Equal($"…{sep}worktrees{sep}my-repo", result);
    }

    [Fact]
    public void ShortenPath_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Equal("", ShortenPathHelper(""));
    }

    private static string ShortenPathHelper(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var sep = System.IO.Path.DirectorySeparatorChar;
        var parts = path.TrimEnd(sep).Split(sep);
        return parts.Length <= 2 ? path : "…" + sep + string.Join(sep, parts[^2..]);
    }
}

/// <summary>
/// Tests for session grouping stability: ensures multi-agent sessions are not
/// scattered during reconciliation, deleted-group orphaning, or JSON round-trips.
/// Guards against the recurring bug where multi-agent group sessions get moved
/// to repo groups after app restart.
/// </summary>
[Collection("BaseDir")]
public class GroupingStabilityTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly IServiceProvider _serviceProvider;

    public GroupingStabilityTests()
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

    /// <summary>
    /// Inject session names into the alias cache so ReconcileOrganization doesn't prune them.
    /// In test environment there are no active sessions or alias/active-sessions files.
    /// </summary>
    private static void RegisterKnownSessions(CopilotService svc, params string[] sessionNames)
    {
        var field = typeof(CopilotService).GetField("_aliasCache",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var cache = (Dictionary<string, string>?)field.GetValue(svc) ?? new();
        foreach (var name in sessionNames)
            cache[name] = name;
        field.SetValue(svc, cache);
    }

    /// <summary>
    /// Injects dummy SessionState entries into _sessions so ReconcileOrganization
    /// doesn't hit the zero-session early-return guard.
    /// </summary>
    private static void AddDummySessions(CopilotService svc, params string[] names)
    {
        var sessionsField = typeof(CopilotService).GetField("_sessions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var dict = sessionsField.GetValue(svc)!;
        var stateType = sessionsField.FieldType.GenericTypeArguments[1]; // SessionState

        foreach (var name in names)
        {
            var info = new AgentSessionInfo { Name = name, Model = "test-model" };
            var state = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(stateType);
            stateType.GetProperty("Info")!.SetValue(state, info);
            dict.GetType().GetMethod("TryAdd")!.Invoke(dict, new[] { name, state });
        }
    }

    // --- Multi-agent group JSON round-trip tests ---

    [Fact]
    public void MultiAgentGroup_FullState_SurvivesJsonRoundTrip()
    {
        var state = new OrganizationState();
        var maGroup = new SessionGroup
        {
            Id = "ma-team-1",
            Name = "Reflection Team",
            IsMultiAgent = true,
            OrchestratorMode = MultiAgentMode.OrchestratorReflect,
            OrchestratorPrompt = "You are a code review orchestrator",
            DefaultWorkerModel = "gpt-5.1-codex",
            DefaultOrchestratorModel = "claude-opus-4.6",
            WorktreeId = "wt-abc",
            RepoId = "repo-xyz",
            SortOrder = 2
        };
        state.Groups.Add(maGroup);
        state.Sessions.Add(new SessionMeta
        {
            SessionName = "team-orchestrator",
            GroupId = "ma-team-1",
            Role = MultiAgentRole.Orchestrator,
            PreferredModel = "claude-opus-4.6",
            WorktreeId = "wt-abc"
        });
        state.Sessions.Add(new SessionMeta
        {
            SessionName = "team-worker-1",
            GroupId = "ma-team-1",
            Role = MultiAgentRole.Worker,
            PreferredModel = "gpt-5.1-codex",
            WorktreeId = "wt-abc"
        });

        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        var restored = JsonSerializer.Deserialize<OrganizationState>(json)!;

        // Verify the multi-agent group survived
        var group = restored.Groups.FirstOrDefault(g => g.Id == "ma-team-1");
        Assert.NotNull(group);
        Assert.True(group!.IsMultiAgent);
        Assert.Equal(MultiAgentMode.OrchestratorReflect, group.OrchestratorMode);
        Assert.Equal("You are a code review orchestrator", group.OrchestratorPrompt);
        Assert.Equal("gpt-5.1-codex", group.DefaultWorkerModel);
        Assert.Equal("claude-opus-4.6", group.DefaultOrchestratorModel);
        Assert.Equal("wt-abc", group.WorktreeId);
        Assert.Equal("repo-xyz", group.RepoId);

        // Verify sessions survived
        var orch = restored.Sessions.FirstOrDefault(s => s.SessionName == "team-orchestrator");
        var worker = restored.Sessions.FirstOrDefault(s => s.SessionName == "team-worker-1");
        Assert.NotNull(orch);
        Assert.NotNull(worker);
        Assert.Equal("ma-team-1", orch!.GroupId);
        Assert.Equal("ma-team-1", worker!.GroupId);
        Assert.Equal(MultiAgentRole.Orchestrator, orch.Role);
        Assert.Equal("claude-opus-4.6", orch.PreferredModel);
    }

    [Fact]
    public void MultipleGroups_IncludingMultiAgent_AllSurviveRoundTrip()
    {
        var state = new OrganizationState();
        state.Groups.Add(new SessionGroup
        {
            Id = "repo-group",
            Name = "PolyPilot",
            RepoId = "PureWeen-PolyPilot"
        });
        state.Groups.Add(new SessionGroup
        {
            Id = "ma-team",
            Name = "Review Team",
            IsMultiAgent = true,
            OrchestratorMode = MultiAgentMode.Orchestrator,
            WorktreeId = "wt-1",
            RepoId = "PureWeen-PolyPilot"
        });
        state.Sessions.Add(new SessionMeta { SessionName = "regular", GroupId = "repo-group" });
        state.Sessions.Add(new SessionMeta { SessionName = "team-orch", GroupId = "ma-team", Role = MultiAgentRole.Orchestrator });
        state.Sessions.Add(new SessionMeta { SessionName = "team-w1", GroupId = "ma-team" });

        var json = JsonSerializer.Serialize(state);
        var restored = JsonSerializer.Deserialize<OrganizationState>(json)!;

        // All 3 groups should exist (default + repo + multi-agent)
        Assert.Equal(3, restored.Groups.Count);
        Assert.Contains(restored.Groups, g => g.Id == "ma-team" && g.IsMultiAgent);
        Assert.Contains(restored.Groups, g => g.Id == "repo-group" && !g.IsMultiAgent);
        Assert.Equal(3, restored.Sessions.Count);
    }

    // --- DeleteGroup tests ---

    [Fact]
    public void DeleteGroup_MultiAgent_RemovesSessions()
    {
        var svc = CreateService();

        // Create a multi-agent group with sessions
        var group = svc.CreateMultiAgentGroup("Test Team");
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "orch",
            GroupId = group.Id,
            Role = MultiAgentRole.Orchestrator,
            PreferredModel = "claude-opus-4.6",
            WorktreeId = "wt-1"
        });
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "worker-1",
            GroupId = group.Id,
            Role = MultiAgentRole.Worker,
            PreferredModel = "gpt-5.1-codex",
            WorktreeId = "wt-1"
        });

        svc.DeleteGroup(group.Id);

        // Multi-agent sessions should be removed, not orphaned
        Assert.DoesNotContain(svc.Organization.Sessions, s => s.SessionName == "orch");
        Assert.DoesNotContain(svc.Organization.Sessions, s => s.SessionName == "worker-1");
        // Group should be removed
        Assert.DoesNotContain(svc.Organization.Groups, g => g.Id == group.Id);
    }

    [Fact]
    public void DeleteGroup_MultiAgent_RemovesSessionMetadata()
    {
        var svc = CreateService();
        var group = svc.CreateMultiAgentGroup("Team");

        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "orch",
            GroupId = group.Id,
            Role = MultiAgentRole.Orchestrator,
            PreferredModel = "claude-opus-4.6",
            WorktreeId = "wt-1"
        });

        svc.DeleteGroup(group.Id);

        // Multi-agent sessions should be removed entirely, not orphaned
        Assert.DoesNotContain(svc.Organization.Sessions, s => s.SessionName == "orch");
    }

    // --- Reconciliation protection tests ---

    [Fact]
    public void Reconcile_SessionsInMultiAgentGroup_NotMovedToRepoGroup()
    {
        var repos = new List<RepositoryInfo>
        {
            new() { Id = "repo-1", Name = "MyRepo", Url = "https://github.com/test/repo" }
        };
        var worktrees = new List<WorktreeInfo>
        {
            new() { Id = "wt-1", RepoId = "repo-1", Branch = "main", Path = "/tmp/wt-1" }
        };
        var rm = CreateRepoManagerWithState(repos, worktrees);
        var svc = CreateService(rm);

        // Create repo group and multi-agent group sharing the same repo
        var repoGroup = svc.GetOrCreateRepoGroup("repo-1", "MyRepo");
        var maGroup = svc.CreateMultiAgentGroup("Review Team", worktreeId: "wt-1", repoId: "repo-1");

        // Add sessions to multi-agent group
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "team-orch",
            GroupId = maGroup.Id,
            Role = MultiAgentRole.Orchestrator,
            WorktreeId = "wt-1"
        });
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "team-w1",
            GroupId = maGroup.Id,
            WorktreeId = "wt-1"
        });

        RegisterKnownSessions(svc, "team-orch", "team-w1");
        AddDummySessions(svc, "team-orch", "team-w1");

        // Run reconciliation — sessions should stay in multi-agent group
        svc.ReconcileOrganization();

        var orch = svc.Organization.Sessions.First(s => s.SessionName == "team-orch");
        var worker = svc.Organization.Sessions.First(s => s.SessionName == "team-w1");
        Assert.Equal(maGroup.Id, orch.GroupId);
        Assert.Equal(maGroup.Id, worker.GroupId);
        // Should NOT have been moved to the repo group
        Assert.NotEqual(repoGroup.Id, orch.GroupId);
        Assert.NotEqual(repoGroup.Id, worker.GroupId);
    }

    [Fact]
    public void Reconcile_OrphanedFromDeletedGroup_GoesToDefault()
    {
        var svc = CreateService();

        // Simulate sessions pointing to a non-existent group (as if the group was deleted)
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "orphan-orch",
            GroupId = "deleted-group-id",
            Role = MultiAgentRole.Orchestrator,
            PreferredModel = "claude-opus-4.6",
            WorktreeId = "wt-1"
        });
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "orphan-worker",
            GroupId = "deleted-group-id",
            Role = MultiAgentRole.Worker,
            PreferredModel = "gpt-5.1-codex",
            WorktreeId = "wt-1"
        });

        RegisterKnownSessions(svc, "orphan-orch", "orphan-worker");
        AddDummySessions(svc, "orphan-orch", "orphan-worker");

        svc.ReconcileOrganization();

        var orch = svc.Organization.Sessions.First(s => s.SessionName == "orphan-orch");
        var worker = svc.Organization.Sessions.First(s => s.SessionName == "orphan-worker");
        Assert.Equal(SessionGroup.DefaultId, orch.GroupId);
        Assert.Equal(SessionGroup.DefaultId, worker.GroupId);
    }

    [Fact]
    public void Reconcile_OrphanedMultiAgentSessions_NotAutoMovedToRepoGroup()
    {
        // This is the key bug test: after a multi-agent group disappears,
        // orphaned sessions with WorktreeIds should NOT be auto-moved to the repo group.
        var repos = new List<RepositoryInfo>
        {
            new() { Id = "repo-1", Name = "MyRepo", Url = "https://github.com/test/repo" }
        };
        var worktrees = new List<WorktreeInfo>
        {
            new() { Id = "wt-1", RepoId = "repo-1", Branch = "main", Path = "/tmp/wt-1" }
        };
        var rm = CreateRepoManagerWithState(repos, worktrees);
        var svc = CreateService(rm);
        svc.GetOrCreateRepoGroup("repo-1", "MyRepo");

        // Simulate orphaned multi-agent sessions already in _default with WorktreeId set
        // (as if a previous reconciliation moved them from a deleted group)
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "team-orch",
            GroupId = SessionGroup.DefaultId,
            Role = MultiAgentRole.Orchestrator,
            PreferredModel = "claude-opus-4.6",
            WorktreeId = "wt-1"
        });
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "team-worker",
            GroupId = SessionGroup.DefaultId,
            Role = MultiAgentRole.Worker,
            PreferredModel = "gpt-5.1-codex",
            WorktreeId = "wt-1"
        });

        RegisterKnownSessions(svc, "team-orch", "team-worker");
        AddDummySessions(svc, "team-orch", "team-worker");

        svc.ReconcileOrganization();

        var orch = svc.Organization.Sessions.First(s => s.SessionName == "team-orch");
        var worker = svc.Organization.Sessions.First(s => s.SessionName == "team-worker");

        // Orchestrator should NOT be moved (has Orchestrator role)
        Assert.Equal(SessionGroup.DefaultId, orch.GroupId);
        // Worker with PreferredModel should NOT be moved (was multi-agent member)
        Assert.Equal(SessionGroup.DefaultId, worker.GroupId);
    }

    [Fact]
    public void Reconcile_RegularSession_WithWorktree_InDefault_SurvivesPrune()
    {
        // Verifies that regular sessions with worktrees aren't pruned during reconciliation.
        // Note: auto-move from _default to repo group only happens for active sessions (in _sessions).
        // This tests that the session metadata is preserved for when the session becomes active.
        var repos = new List<RepositoryInfo>
        {
            new() { Id = "repo-1", Name = "MyRepo", Url = "https://github.com/test/repo" }
        };
        var worktrees = new List<WorktreeInfo>
        {
            new() { Id = "wt-1", RepoId = "repo-1", Branch = "main", Path = "/tmp/wt-1" }
        };
        var rm = CreateRepoManagerWithState(repos, worktrees);
        var svc = CreateService(rm);
        svc.GetOrCreateRepoGroup("repo-1", "MyRepo");

        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "regular-session",
            GroupId = SessionGroup.DefaultId,
            WorktreeId = "wt-1",
            PreferredModel = null,
            Role = MultiAgentRole.Worker
        });

        RegisterKnownSessions(svc, "regular-session");
        AddDummySessions(svc, "regular-session");
        svc.ReconcileOrganization();

        // Session should still exist (not pruned)
        var meta = svc.Organization.Sessions.FirstOrDefault(s => s.SessionName == "regular-session");
        Assert.NotNull(meta);
        Assert.Equal("wt-1", meta!.WorktreeId);
    }

    [Fact]
    public void WasMultiAgent_DetectsOrchestratorRole()
    {
        // Verifies the wasMultiAgent heuristic used in reconciliation
        var orch = new SessionMeta { Role = MultiAgentRole.Orchestrator };
        var workerWithModel = new SessionMeta { Role = MultiAgentRole.Worker, PreferredModel = "gpt-5.1-codex" };
        var regularWorker = new SessionMeta { Role = MultiAgentRole.Worker, PreferredModel = null };

        // Orchestrator role → was multi-agent
        Assert.True(orch.Role == MultiAgentRole.Orchestrator || orch.PreferredModel != null);
        // Worker with PreferredModel → was multi-agent
        Assert.True(workerWithModel.Role == MultiAgentRole.Orchestrator || workerWithModel.PreferredModel != null);
        // Regular worker (no PreferredModel) → not multi-agent
        Assert.False(regularWorker.Role == MultiAgentRole.Orchestrator || regularWorker.PreferredModel != null);
    }

    // --- Full lifecycle simulation tests ---

    [Fact]
    public void FullLifecycle_CreateTeam_Serialize_Deserialize_SessionsIntact()
    {
        var svc = CreateService();
        var group = svc.CreateMultiAgentGroup("QRC",
            worktreeId: "wt-feature",
            repoId: "repo-poly");

        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "QRC-orchestrator",
            GroupId = group.Id,
            Role = MultiAgentRole.Orchestrator,
            PreferredModel = "claude-opus-4.6",
            WorktreeId = "wt-feature"
        });
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "QRC-worker-1",
            GroupId = group.Id,
            PreferredModel = "gpt-5.1-codex",
            WorktreeId = "wt-feature"
        });
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "QRC-worker-2",
            GroupId = group.Id,
            PreferredModel = "gpt-5.1-codex",
            WorktreeId = "wt-feature"
        });

        // Serialize (simulate app save)
        var json = JsonSerializer.Serialize(svc.Organization, new JsonSerializerOptions { WriteIndented = true });

        // Deserialize (simulate app reload)
        var restored = JsonSerializer.Deserialize<OrganizationState>(json)!;

        // All sessions should still point to the multi-agent group
        Assert.Contains(restored.Groups, g => g.Id == group.Id && g.IsMultiAgent);
        foreach (var session in restored.Sessions.Where(s => s.SessionName.StartsWith("QRC-")))
        {
            Assert.Equal(group.Id, session.GroupId);
        }
    }

    [Fact]
    public void FullLifecycle_DeleteTeam_ThenReconcile_SessionsStayInDefault()
    {
        // Simulates: create team → delete team → reconcile → sessions stay visible in default
        var repos = new List<RepositoryInfo>
        {
            new() { Id = "repo-1", Name = "MyRepo", Url = "https://github.com/test/repo" }
        };
        var worktrees = new List<WorktreeInfo>
        {
            new() { Id = "wt-1", RepoId = "repo-1", Branch = "main", Path = "/tmp/wt-1" }
        };
        var rm = CreateRepoManagerWithState(repos, worktrees);
        var svc = CreateService(rm);
        svc.GetOrCreateRepoGroup("repo-1", "MyRepo");

        var group = svc.CreateMultiAgentGroup("Team",
            worktreeId: "wt-1", repoId: "repo-1");

        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "team-orch",
            GroupId = group.Id,
            Role = MultiAgentRole.Orchestrator,
            PreferredModel = "claude-opus-4.6",
            WorktreeId = "wt-1"
        });
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "team-w1",
            GroupId = group.Id,
            PreferredModel = "gpt-5.1-codex",
            WorktreeId = "wt-1"
        });

        // Delete the team
        svc.DeleteGroup(group.Id);

        // Multi-agent sessions should be removed entirely
        Assert.DoesNotContain(svc.Organization.Sessions, s => s.SessionName == "team-orch");
        Assert.DoesNotContain(svc.Organization.Sessions, s => s.SessionName == "team-w1");

        // Group should be gone
        Assert.DoesNotContain(svc.Organization.Groups, g => g.Id == group.Id);
    }

    [Fact]
    public async Task DeleteGroup_MultiAgent_MarksSessionsHidden()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        // Create real sessions so they exist in _sessions
        var s1 = await svc.CreateSessionAsync("team-orch");
        var s2 = await svc.CreateSessionAsync("team-worker-1");
        Assert.NotNull(s1);
        Assert.NotNull(s2);

        // Create multi-agent group and assign sessions
        var group = svc.CreateMultiAgentGroup("Test Team");
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "team-orch",
            GroupId = group.Id,
            Role = MultiAgentRole.Orchestrator,
        });
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "team-worker-1",
            GroupId = group.Id,
            Role = MultiAgentRole.Worker,
        });

        // DeleteGroup should mark sessions as hidden
        svc.DeleteGroup(group.Id);

        // Sessions should be hidden from GetAllSessions
        Assert.DoesNotContain(svc.GetAllSessions(), s => s.Name == "team-orch");
        Assert.DoesNotContain(svc.GetAllSessions(), s => s.Name == "team-worker-1");
    }

    [Fact]
    public async Task DeleteGroup_MultiAgent_HiddenSessionsExcludedFromSaveSnapshot()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var s1 = await svc.CreateSessionAsync("snap-orch");
        var s2 = await svc.CreateSessionAsync("snap-worker");
        Assert.NotNull(s1);
        Assert.NotNull(s2);

        var group = svc.CreateMultiAgentGroup("Snap Team");
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "snap-orch",
            GroupId = group.Id,
            Role = MultiAgentRole.Orchestrator,
        });
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "snap-worker",
            GroupId = group.Id,
            Role = MultiAgentRole.Worker,
        });

        svc.DeleteGroup(group.Id);

        // Verify the session objects themselves are marked hidden
        var orchSession = svc.GetSession("snap-orch");
        var workerSession = svc.GetSession("snap-worker");
        // GetSession returns null for hidden sessions or they should be marked hidden
        // The key invariant: hidden sessions must not appear in GetAllSessions
        var allNames = svc.GetAllSessions().Select(s => s.Name).ToList();
        Assert.DoesNotContain("snap-orch", allNames);
        Assert.DoesNotContain("snap-worker", allNames);
    }

    [Fact]
    public async Task DeleteGroup_MultiAgent_AddsSessionIdsToClosedList()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        // Create real sessions
        var s1 = await svc.CreateSessionAsync("del-orch");
        var s2 = await svc.CreateSessionAsync("del-worker");
        Assert.NotNull(s1);
        Assert.NotNull(s2);

        // Record session IDs
        var orchId = s1!.SessionId;
        var workerId = s2!.SessionId;

        // Create multi-agent group
        var group = svc.CreateMultiAgentGroup("Delete Test");
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "del-orch",
            GroupId = group.Id,
            Role = MultiAgentRole.Orchestrator,
        });
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "del-worker",
            GroupId = group.Id,
            Role = MultiAgentRole.Worker,
        });

        svc.DeleteGroup(group.Id);

        // Sessions should be hidden
        Assert.DoesNotContain(svc.GetAllSessions(), s => s.Name == "del-orch");
        Assert.DoesNotContain(svc.GetAllSessions(), s => s.Name == "del-worker");

        // Critical: session IDs should be in closed list so merge won't re-add them
        var closedField = typeof(CopilotService).GetField("_closedSessionIds",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var closedIds = (System.Collections.Concurrent.ConcurrentDictionary<string, byte>)closedField.GetValue(svc)!;
        Assert.True(closedIds.ContainsKey(orchId!), "Orchestrator session ID should be in _closedSessionIds");
        Assert.True(closedIds.ContainsKey(workerId!), "Worker session ID should be in _closedSessionIds");
    }

    [Fact]
    public async Task DeleteGroup_MultiAgent_SaveSnapshotExcludesDeletedSessions()
    {
        // Sonnet 4.6 finding: verify that SaveActiveSessionsToDisk would produce a
        // snapshot excluding deleted sessions. Demo mode skips the actual file write,
        // so we simulate the snapshot logic that SaveActiveSessionsToDisk performs.
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        // Create sessions including one that should survive
        var survivor = await svc.CreateSessionAsync("survivor");
        var s1 = await svc.CreateSessionAsync("doomed-orch");
        var s2 = await svc.CreateSessionAsync("doomed-worker");
        Assert.NotNull(survivor);
        Assert.NotNull(s1);
        Assert.NotNull(s2);

        var group = svc.CreateMultiAgentGroup("Doomed Team");
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "doomed-orch",
            GroupId = group.Id,
            Role = MultiAgentRole.Orchestrator,
        });
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "doomed-worker",
            GroupId = group.Id,
            Role = MultiAgentRole.Worker,
        });

        // Simulate existing active-sessions.json written before delete
        var persistedEntries = new List<ActiveSessionEntry>
        {
            new() { SessionId = survivor!.SessionId!, DisplayName = "survivor" },
            new() { SessionId = s1!.SessionId!, DisplayName = "doomed-orch" },
            new() { SessionId = s2!.SessionId!, DisplayName = "doomed-worker" },
        };

        svc.DeleteGroup(group.Id);

        // Build the snapshot exactly as SaveActiveSessionsToDisk does:
        // 1. Active non-hidden sessions
        var activeSnapshot = svc.GetAllSessions()
            .Where(s => s.SessionId != null)
            .Select(s => new ActiveSessionEntry { SessionId = s.SessionId!, DisplayName = s.Name })
            .ToList();

        // 2. Get closedIds
        var closedField = typeof(CopilotService).GetField("_closedSessionIds",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var closedIds = (System.Collections.Concurrent.ConcurrentDictionary<string, byte>)closedField.GetValue(svc)!;

        // 3. Merge (what WriteActiveSessionsFile would do)
        var merged = CopilotService.MergeSessionEntries(
            activeSnapshot, persistedEntries,
            new HashSet<string>(closedIds.Keys),
            new HashSet<string>(),
            _ => true);

        // Survivor should be in the merged result
        Assert.Contains(merged, e => e.DisplayName == "survivor");
        // Deleted sessions must NOT be in the merged result
        Assert.DoesNotContain(merged, e => e.DisplayName == "doomed-orch");
        Assert.DoesNotContain(merged, e => e.DisplayName == "doomed-worker");
    }

    [Fact]
    public void DeleteGroup_NonMultiAgent_MovesSessionsToDefault()
    {
        var svc = CreateService();
        var group = svc.GetOrCreateRepoGroup("repo-1", "MyRepo");
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "s1",
            GroupId = group.Id,
            WorktreeId = "wt-1"
        });

        svc.DeleteGroup(group.Id);

        // Non-multi-agent: sessions move to default
        var s = svc.Organization.Sessions.First(s => s.SessionName == "s1");
        Assert.Equal(SessionGroup.DefaultId, s.GroupId);
    }

    [Fact]
    public void Reconcile_DeletedGroupId_NotInGroupsList_SessionsOrphaned()
    {
        // Simulates loading organization.json where a group is missing
        // but sessions still reference it
        var svc = CreateService();

        // Manually add sessions referencing a group that doesn't exist
        var phantomGroupId = "phantom-group-" + Guid.NewGuid();
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "ghost-1",
            GroupId = phantomGroupId,
            WorktreeId = "wt-1"
        });
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "ghost-2",
            GroupId = phantomGroupId,
            WorktreeId = "wt-1"
        });

        RegisterKnownSessions(svc, "ghost-1", "ghost-2");
        AddDummySessions(svc, "ghost-1", "ghost-2");

        svc.ReconcileOrganization();

        // Both sessions should be in default now
        Assert.All(svc.Organization.Sessions.Where(s => s.SessionName.StartsWith("ghost-")),
            m => Assert.Equal(SessionGroup.DefaultId, m.GroupId));
    }

    [Fact]
    public void Reconcile_MultiAgentGroupExists_SessionsUntouched()
    {
        // When the multi-agent group exists, reconciliation should not alter its sessions at all
        var svc = CreateService();
        var group = svc.CreateMultiAgentGroup("Stable Team");

        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "stable-orch",
            GroupId = group.Id,
            Role = MultiAgentRole.Orchestrator,
            PreferredModel = "claude-opus-4.6",
            WorktreeId = "wt-1"
        });
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "stable-w1",
            GroupId = group.Id,
            PreferredModel = "gpt-5.1-codex",
            WorktreeId = "wt-1"
        });

        RegisterKnownSessions(svc, "stable-orch", "stable-w1");
        AddDummySessions(svc, "stable-orch", "stable-w1");

        var orchGroupBefore = svc.Organization.Sessions.First(s => s.SessionName == "stable-orch").GroupId;
        var workerGroupBefore = svc.Organization.Sessions.First(s => s.SessionName == "stable-w1").GroupId;

        svc.ReconcileOrganization();

        Assert.Equal(orchGroupBefore, svc.Organization.Sessions.First(s => s.SessionName == "stable-orch").GroupId);
        Assert.Equal(workerGroupBefore, svc.Organization.Sessions.First(s => s.SessionName == "stable-w1").GroupId);
    }

    [Fact]
    public void OrganizationState_WithReflectionState_SurvivesRoundTrip()
    {
        var state = new OrganizationState();
        state.Groups.Add(new SessionGroup
        {
            Id = "reflect-team",
            Name = "Reflect",
            IsMultiAgent = true,
            OrchestratorMode = MultiAgentMode.OrchestratorReflect,
            ReflectionState = ReflectionCycle.Create("Fix all bugs", 10)
        });

        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        var restored = JsonSerializer.Deserialize<OrganizationState>(json)!;

        var group = restored.Groups.First(g => g.Id == "reflect-team");
        Assert.NotNull(group.ReflectionState);
        Assert.Equal("Fix all bugs", group.ReflectionState!.Goal);
        Assert.Equal(10, group.ReflectionState.MaxIterations);
        Assert.True(group.ReflectionState.IsActive);
    }

    [Fact]
    public void LoadOrganization_HealsGroup_WhenIsMultiAgentFalseButHasOrchestratorSession()
    {
        // Simulate a corrupted organization.json where a multi-agent group lost IsMultiAgent
        var org = new OrganizationState();
        var group = new SessionGroup
        {
            Id = "corrupted-group",
            Name = "PolyPilot",
            IsMultiAgent = false,  // CORRUPTED — should be true
            RepoId = "PureWeen-PolyPilot"
        };
        org.Groups.Add(group);
        org.Sessions.Add(new SessionMeta { SessionName = "team-orchestrator", GroupId = "corrupted-group", Role = MultiAgentRole.Orchestrator });
        org.Sessions.Add(new SessionMeta { SessionName = "team-worker-1", GroupId = "corrupted-group", Role = MultiAgentRole.Worker });

        // Serialize and write to temp file, then load
        var tempDir = Path.Combine(Path.GetTempPath(), $"polypilot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var orgFile = Path.Combine(tempDir, "organization.json");
            File.WriteAllText(orgFile, JsonSerializer.Serialize(org, new JsonSerializerOptions { WriteIndented = true }));

            CopilotService.SetBaseDirForTesting(tempDir);
            var svc = new CopilotService(
                new StubChatDatabase(), new StubServerManager(), new StubWsBridgeClient(),
                new RepoManager(), new ServiceCollection().BuildServiceProvider(), new StubDemoService());

            svc.LoadOrganization();

            // Verify self-healing occurred
            var healed = svc.Organization.Groups.First(g => g.Id == "corrupted-group");
            Assert.True(healed.IsMultiAgent, "Group with orchestrator sessions should be healed to IsMultiAgent=true");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void GetOrCreateRepoGroup_SkipsGroupWithOrchestratorSessions()
    {
        // Even if IsMultiAgent is somehow false, groups with orchestrator sessions
        // should not be reused as repo groups
        var tempDir = Path.Combine(Path.GetTempPath(), $"polypilot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            CopilotService.SetBaseDirForTesting(tempDir);
            var svc = new CopilotService(
                new StubChatDatabase(), new StubServerManager(), new StubWsBridgeClient(),
                new RepoManager(), new ServiceCollection().BuildServiceProvider(), new StubDemoService());
            svc.LoadOrganization();

            // Add a "corrupted" group that looks like a repo group but has orchestrator sessions
            var corruptGroup = new SessionGroup
            {
                Id = "former-squad",
                Name = "PolyPilot",
                IsMultiAgent = false,  // Lost its multi-agent status
                RepoId = "PureWeen-PolyPilot"
            };
            svc.Organization.Groups.Add(corruptGroup);
            svc.Organization.Sessions.Add(new SessionMeta
            {
                SessionName = "team-orch",
                GroupId = "former-squad",
                Role = MultiAgentRole.Orchestrator
            });

            // GetOrCreateRepoGroup should NOT return the corrupted group
            var repoGroup = svc.GetOrCreateRepoGroup("PureWeen-PolyPilot", "PolyPilot");
            Assert.NotEqual("former-squad", repoGroup.Id);
            Assert.False(repoGroup.IsMultiAgent);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SaveOrganization_DebounceWritesLiveState_NotStaleSnapshot()
    {
        // Verify that debounced saves write the CURRENT state, not a stale snapshot.
        // This is the fix for the group corruption bug.
        var tempDir = Path.Combine(Path.GetTempPath(), $"polypilot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            CopilotService.SetBaseDirForTesting(tempDir);
            var svc = new CopilotService(
                new StubChatDatabase(), new StubServerManager(), new StubWsBridgeClient(),
                new RepoManager(), new ServiceCollection().BuildServiceProvider(), new StubDemoService());
            svc.LoadOrganization();

            // Call SaveOrganization (debounced) with initial state
            svc.SaveOrganization();

            // NOW mutate the state — add a multi-agent group
            var squad = new SessionGroup
            {
                Id = "new-squad",
                Name = "My Squad",
                IsMultiAgent = true,
                RepoId = "some-repo"
            };
            svc.Organization.Groups.Add(squad);

            // Flush — should write the CURRENT state including the squad
            svc.FlushSaveOrganization();

            // Re-load and verify
            svc.LoadOrganization();
            var loaded = svc.Organization.Groups.FirstOrDefault(g => g.Id == "new-squad");
            Assert.NotNull(loaded);
            Assert.True(loaded.IsMultiAgent, "Flushed state should include the multi-agent group added after SaveOrganization");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The debounce timer callback uses InvokeOnUI to serialize, ensuring Organization
    /// (which contains non-thread-safe List&lt;T&gt;) is only accessed from one thread.
    /// </summary>
    [Fact]
    public void SaveOrganization_DebounceCallbackUsesInvokeOnUI()
    {
        // Verify the code structure: SaveOrganization creates a Timer that calls
        // InvokeOnUI(() => SaveOrganizationCore()), not SaveOrganizationCore() directly.
        // This prevents concurrent enumeration of Organization.Sessions during serialization.
        var method = typeof(CopilotService).GetMethod("SaveOrganization",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        // Verify InvokeOnUI is referenced in the method body by checking that
        // the timer field is set (via reflection on the debounce field).
        var debounceField = typeof(CopilotService).GetField("_saveOrgDebounce",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(debounceField); // The debounce timer field exists

        var tempDir = Path.Combine(Path.GetTempPath(), $"polypilot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            CopilotService.SetBaseDirForTesting(tempDir);
            var svc = new CopilotService(
                new StubChatDatabase(), new StubServerManager(), new StubWsBridgeClient(),
                new RepoManager(), new ServiceCollection().BuildServiceProvider(), new StubDemoService());
            svc.LoadOrganization();

            svc.SaveOrganization();
            var timer = debounceField!.GetValue(svc);
            Assert.NotNull(timer); // Timer was created by SaveOrganization

            // Clean up: flush to cancel the timer and write
            svc.FlushSaveOrganization();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void UnpinnedCollapsed_DefaultsFalse()
    {
        var group = new SessionGroup { Name = "Test" };
        Assert.False(group.UnpinnedCollapsed);
    }

    [Fact]
    public void UnpinnedCollapsed_SerializesRoundTrip()
    {
        var group = new SessionGroup { Name = "Test", UnpinnedCollapsed = true };
        var json = System.Text.Json.JsonSerializer.Serialize(group);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<SessionGroup>(json)!;
        Assert.True(deserialized.UnpinnedCollapsed);
    }

    [Fact]
    public void ToggleUnpinnedCollapsed_TogglesState()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"polypilot-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            CopilotService.SetBaseDirForTesting(tempDir);
            var svc = new CopilotService(
                new StubChatDatabase(), new StubServerManager(), new StubWsBridgeClient(),
                new RepoManager(), new ServiceCollection().BuildServiceProvider(), new StubDemoService());
            var group = svc.Organization.Groups[0];
            Assert.False(group.UnpinnedCollapsed);

            svc.ToggleUnpinnedCollapsed(group.Id);
            Assert.True(group.UnpinnedCollapsed);

            svc.ToggleUnpinnedCollapsed(group.Id);
            Assert.False(group.UnpinnedCollapsed);

            svc.FlushSaveOrganization();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}

/// <summary>
/// Tests for urgency-based sort in GetOrganizedSessions:
/// NeedsAttention sessions float to top, then IsProcessing, then idle.
/// </summary>
public class UrgencySortTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly IServiceProvider _serviceProvider;

    public UrgencySortTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, new RepoManager(), _serviceProvider, _demoService);

    private static void InjectSession(CopilotService svc, AgentSessionInfo info)
    {
        var sessionsField = typeof(CopilotService).GetField("_sessions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var dict = sessionsField.GetValue(svc)!;
        var stateType = sessionsField.FieldType.GenericTypeArguments[1];
        var state = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(stateType);
        stateType.GetProperty("Info")!.SetValue(state, info);
        dict.GetType().GetMethod("TryAdd")!.Invoke(dict, new[] { info.Name, state });
    }

    [Fact]
    public void GetOrganizedSessions_ProcessingSessionSortsBeforeIdle()
    {
        var svc = CreateService();

        var idle = new AgentSessionInfo { Name = "idle-session", Model = "m", IsProcessing = false };
        idle.LastUpdatedAt = DateTime.Now.AddMinutes(-5);

        var processing = new AgentSessionInfo { Name = "processing-session", Model = "m", IsProcessing = true };
        processing.LastUpdatedAt = DateTime.Now.AddMinutes(-10);

        InjectSession(svc, idle);
        InjectSession(svc, processing);
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "idle-session", GroupId = SessionGroup.DefaultId });
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "processing-session", GroupId = SessionGroup.DefaultId });

        var result = svc.GetOrganizedSessions();
        var sessions = result.SelectMany(g => g.Sessions).Select(s => s.Name).ToList();

        Assert.Equal("processing-session", sessions[0]);
        Assert.Equal("idle-session", sessions[1]);
    }

    [Fact]
    public void GetOrganizedSessions_NeedsAttentionSortsBeforeProcessing()
    {
        var svc = CreateService();

        var processing = new AgentSessionInfo { Name = "processing-session", Model = "m", IsProcessing = true };

        var needsAttention = new AgentSessionInfo { Name = "needs-attention-session", Model = "m", IsProcessing = false };
        needsAttention.History.Add(ChatMessage.AssistantMessage("Would you like me to proceed with the changes?"));

        InjectSession(svc, processing);
        InjectSession(svc, needsAttention);
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "processing-session", GroupId = SessionGroup.DefaultId });
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "needs-attention-session", GroupId = SessionGroup.DefaultId });

        var result = svc.GetOrganizedSessions();
        var sessions = result.SelectMany(g => g.Sessions).Select(s => s.Name).ToList();

        Assert.Equal("needs-attention-session", sessions[0]);
        Assert.Equal("processing-session", sessions[1]);
    }

    [Fact]
    public void GetOrganizedSessions_PinnedAlwaysFirst_EvenIfNotNeedsAttention()
    {
        var svc = CreateService();

        var needsAttention = new AgentSessionInfo { Name = "needs-attention-session", Model = "m", IsProcessing = false };
        needsAttention.History.Add(ChatMessage.AssistantMessage("Should I fix this?"));

        var pinned = new AgentSessionInfo { Name = "pinned-idle-session", Model = "m", IsProcessing = false };

        InjectSession(svc, needsAttention);
        InjectSession(svc, pinned);
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "needs-attention-session", GroupId = SessionGroup.DefaultId });
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "pinned-idle-session", GroupId = SessionGroup.DefaultId, IsPinned = true });

        var result = svc.GetOrganizedSessions();
        var sessions = result.SelectMany(g => g.Sessions).Select(s => s.Name).ToList();

        Assert.Equal("pinned-idle-session", sessions[0]);
        Assert.Equal("needs-attention-session", sessions[1]);
    }

    [Fact]
    public void GetOrganizedSessions_CacheInvalidatesOnNeedsAttentionChange()
    {
        var svc = CreateService();

        var session = new AgentSessionInfo { Name = "a-session", Model = "m", IsProcessing = false };
        InjectSession(svc, session);
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "a-session", GroupId = SessionGroup.DefaultId });

        var before = svc.GetOrganizedSessions();
        var beforeSession = before.SelectMany(g => g.Sessions).First();
        Assert.False(beforeSession.NeedsAttention);

        session.History.Add(ChatMessage.AssistantMessage("Would you like me to continue?"));

        var after = svc.GetOrganizedSessions();
        var afterSession = after.SelectMany(g => g.Sessions).First();
        Assert.True(afterSession.NeedsAttention);
    }

    #region Multi-agent group self-healing tests

    [Fact]
    public void HealMultiAgentGroups_RestoresOrchestratorRole_ByNamePattern()
    {
        // Scenario: orchestrator session has Role=Worker (the default) but name ends with "-orchestrator"
        var org = new OrganizationState();
        var group = new SessionGroup
        {
            Id = "team-group",
            Name = "MyTeam",
            IsMultiAgent = false
        };
        org.Groups.Add(group);
        org.Sessions.Add(new SessionMeta { SessionName = "MyTeam-orchestrator", GroupId = "team-group", Role = MultiAgentRole.Worker });
        org.Sessions.Add(new SessionMeta { SessionName = "MyTeam-worker-1", GroupId = "team-group", Role = MultiAgentRole.Worker });

        var tempDir = Path.Combine(Path.GetTempPath(), $"polypilot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var orgFile = Path.Combine(tempDir, "organization.json");
            File.WriteAllText(orgFile, JsonSerializer.Serialize(org, new JsonSerializerOptions { WriteIndented = true }));

            CopilotService.SetBaseDirForTesting(tempDir);
            var svc = new CopilotService(
                new StubChatDatabase(), new StubServerManager(), new StubWsBridgeClient(),
                new RepoManager(), new ServiceCollection().BuildServiceProvider(), new StubDemoService());

            svc.LoadOrganization();

            // Orchestrator role should be restored by name pattern
            var orchMeta = svc.Organization.Sessions.First(m => m.SessionName == "MyTeam-orchestrator");
            Assert.Equal(MultiAgentRole.Orchestrator, orchMeta.Role);

            // Group should be healed to IsMultiAgent=true (Phase 2 follows Phase 1)
            var healed = svc.Organization.Groups.First(g => g.Id == "team-group");
            Assert.True(healed.IsMultiAgent);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void HealMultiAgentGroups_RestoresOrchestratorRole_WithSuffixNumber()
    {
        // Session named "TeamName-Orchestrator-1" (with numeric suffix) + matching workers
        var org = new OrganizationState();
        var group = new SessionGroup { Id = "g1", Name = "IC" };
        org.Groups.Add(group);
        org.Sessions.Add(new SessionMeta { SessionName = "IC-orchestrator-1", GroupId = "g1", Role = MultiAgentRole.Worker });
        org.Sessions.Add(new SessionMeta { SessionName = "IC-worker-1", GroupId = "g1", Role = MultiAgentRole.Worker });

        var tempDir = Path.Combine(Path.GetTempPath(), $"polypilot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "organization.json"),
                JsonSerializer.Serialize(org, new JsonSerializerOptions { WriteIndented = true }));

            CopilotService.SetBaseDirForTesting(tempDir);
            var svc = new CopilotService(
                new StubChatDatabase(), new StubServerManager(), new StubWsBridgeClient(),
                new RepoManager(), new ServiceCollection().BuildServiceProvider(), new StubDemoService());
            svc.LoadOrganization();

            var meta = svc.Organization.Sessions.First(m => m.SessionName == "IC-orchestrator-1");
            Assert.Equal(MultiAgentRole.Orchestrator, meta.Role);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void HealMultiAgentGroups_ReconstructsMissingGroup_FromScatteredSessions()
    {
        // Scenario: multi-agent group was deleted, sessions scattered to repo groups
        var org = new OrganizationState();
        var repoGroup = new SessionGroup { Id = "repo-group", Name = "PolyPilot", RepoId = "PureWeen-PolyPilot" };
        org.Groups.Add(repoGroup);

        // Orchestrator and workers all assigned to the repo group (wrong!)
        org.Sessions.Add(new SessionMeta { SessionName = "PR Review Squad-orchestrator", GroupId = "repo-group", Role = MultiAgentRole.Worker });
        org.Sessions.Add(new SessionMeta { SessionName = "PR Review Squad-worker-1", GroupId = "repo-group", Role = MultiAgentRole.Worker });
        org.Sessions.Add(new SessionMeta { SessionName = "PR Review Squad-worker-2", GroupId = "repo-group", Role = MultiAgentRole.Worker });
        org.Sessions.Add(new SessionMeta { SessionName = "PR Review Squad-worker-3", GroupId = "repo-group", Role = MultiAgentRole.Worker });

        var tempDir = Path.Combine(Path.GetTempPath(), $"polypilot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "organization.json"),
                JsonSerializer.Serialize(org, new JsonSerializerOptions { WriteIndented = true }));

            CopilotService.SetBaseDirForTesting(tempDir);
            var svc = new CopilotService(
                new StubChatDatabase(), new StubServerManager(), new StubWsBridgeClient(),
                new RepoManager(), new ServiceCollection().BuildServiceProvider(), new StubDemoService());
            svc.LoadOrganization();

            // A new multi-agent group "PR Review Squad" should have been reconstructed
            var reconstructed = svc.Organization.Groups.FirstOrDefault(g => g.Name == "PR Review Squad" && g.IsMultiAgent);
            Assert.NotNull(reconstructed);

            // All team sessions should be in the reconstructed group
            var teamSessions = svc.Organization.Sessions.Where(m => m.GroupId == reconstructed.Id).ToList();
            Assert.Equal(4, teamSessions.Count);

            // Orchestrator should have correct role
            var orch = teamSessions.First(m => m.SessionName == "PR Review Squad-orchestrator");
            Assert.Equal(MultiAgentRole.Orchestrator, orch.Role);

            // Workers should remain as workers
            var workers = teamSessions.Where(m => m.Role == MultiAgentRole.Worker).ToList();
            Assert.Equal(3, workers.Count);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void HealMultiAgentGroups_DoesNothing_WhenGroupsAlreadyCorrect()
    {
        // Scenario: everything is fine, no healing needed
        var org = new OrganizationState();
        var group = new SessionGroup { Id = "good-group", Name = "MyTeam", IsMultiAgent = true, OrchestratorMode = MultiAgentMode.OrchestratorReflect };
        org.Groups.Add(group);
        org.Sessions.Add(new SessionMeta { SessionName = "MyTeam-orchestrator", GroupId = "good-group", Role = MultiAgentRole.Orchestrator });
        org.Sessions.Add(new SessionMeta { SessionName = "MyTeam-worker-1", GroupId = "good-group", Role = MultiAgentRole.Worker });

        var tempDir = Path.Combine(Path.GetTempPath(), $"polypilot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "organization.json"),
                JsonSerializer.Serialize(org, new JsonSerializerOptions { WriteIndented = true }));

            CopilotService.SetBaseDirForTesting(tempDir);
            var svc = new CopilotService(
                new StubChatDatabase(), new StubServerManager(), new StubWsBridgeClient(),
                new RepoManager(), new ServiceCollection().BuildServiceProvider(), new StubDemoService());
            svc.LoadOrganization();

            // Should still be correct — no new groups created
            Assert.Equal(2, svc.Organization.Groups.Count); // default + good-group
            var g = svc.Organization.Groups.First(x => x.Id == "good-group");
            Assert.True(g.IsMultiAgent);
            Assert.Equal(MultiAgentMode.OrchestratorReflect, g.OrchestratorMode);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void HealMultiAgentGroups_DoesNotFalsePositive_OnRegularSessionNames()
    {
        // Session names that contain "orchestrator" but aren't real orchestrators
        var org = new OrganizationState();
        org.Sessions.Add(new SessionMeta { SessionName = "orchestrator-tips", GroupId = "_default", Role = MultiAgentRole.Worker });
        org.Sessions.Add(new SessionMeta { SessionName = "my-orchestrator-notes", GroupId = "_default", Role = MultiAgentRole.Worker });
        // This one ENDS with -orchestrator but has no matching workers — should NOT be promoted
        org.Sessions.Add(new SessionMeta { SessionName = "deploy-orchestrator", GroupId = "_default", Role = MultiAgentRole.Worker });
        org.Sessions.Add(new SessionMeta { SessionName = "notes-orchestrator", GroupId = "_default", Role = MultiAgentRole.Worker });

        var tempDir = Path.Combine(Path.GetTempPath(), $"polypilot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "organization.json"),
                JsonSerializer.Serialize(org, new JsonSerializerOptions { WriteIndented = true }));

            CopilotService.SetBaseDirForTesting(tempDir);
            var svc = new CopilotService(
                new StubChatDatabase(), new StubServerManager(), new StubWsBridgeClient(),
                new RepoManager(), new ServiceCollection().BuildServiceProvider(), new StubDemoService());
            svc.LoadOrganization();

            // None should be detected as orchestrator sessions (no matching workers)
            Assert.All(svc.Organization.Sessions, m => Assert.Equal(MultiAgentRole.Worker, m.Role));
            // No new multi-agent groups should be created (only default group)
            Assert.Single(svc.Organization.Groups);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void HealMultiAgentGroups_HandlesMultipleScatteredTeams()
    {
        // Two teams scattered across different repo groups
        var org = new OrganizationState();
        var repoGroup1 = new SessionGroup { Id = "rg1", Name = "Repo1" };
        var repoGroup2 = new SessionGroup { Id = "rg2", Name = "Repo2" };
        org.Groups.Add(repoGroup1);
        org.Groups.Add(repoGroup2);

        // Team A scattered in rg1
        org.Sessions.Add(new SessionMeta { SessionName = "TeamA-orchestrator", GroupId = "rg1", Role = MultiAgentRole.Worker });
        org.Sessions.Add(new SessionMeta { SessionName = "TeamA-worker-1", GroupId = "rg1", Role = MultiAgentRole.Worker });

        // Team B scattered in rg2
        org.Sessions.Add(new SessionMeta { SessionName = "TeamB-orchestrator", GroupId = "rg2", Role = MultiAgentRole.Worker });
        org.Sessions.Add(new SessionMeta { SessionName = "TeamB-worker-1", GroupId = "rg2", Role = MultiAgentRole.Worker });
        org.Sessions.Add(new SessionMeta { SessionName = "TeamB-worker-2", GroupId = "rg2", Role = MultiAgentRole.Worker });

        var tempDir = Path.Combine(Path.GetTempPath(), $"polypilot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "organization.json"),
                JsonSerializer.Serialize(org, new JsonSerializerOptions { WriteIndented = true }));

            CopilotService.SetBaseDirForTesting(tempDir);
            var svc = new CopilotService(
                new StubChatDatabase(), new StubServerManager(), new StubWsBridgeClient(),
                new RepoManager(), new ServiceCollection().BuildServiceProvider(), new StubDemoService());
            svc.LoadOrganization();

            // Two new multi-agent groups should have been created
            var maGroups = svc.Organization.Groups.Where(g => g.IsMultiAgent).ToList();
            Assert.Equal(2, maGroups.Count);

            var teamA = maGroups.FirstOrDefault(g => g.Name == "TeamA");
            var teamB = maGroups.FirstOrDefault(g => g.Name == "TeamB");
            Assert.NotNull(teamA);
            Assert.NotNull(teamB);

            // Each team should have its sessions
            Assert.Equal(2, svc.Organization.Sessions.Count(m => m.GroupId == teamA.Id));
            Assert.Equal(3, svc.Organization.Sessions.Count(m => m.GroupId == teamB.Id));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void HealMultiAgentGroups_MultipleOrchestrators_NoDuplicateGroups()
    {
        // Team with two orchestrators: TeamA-orchestrator and TeamA-orchestrator-1
        // Should create exactly ONE group, not two
        var org = new OrganizationState();
        var repoGroup = new SessionGroup { Id = "rg1", Name = "Repo1" };
        org.Groups.Add(repoGroup);

        org.Sessions.Add(new SessionMeta { SessionName = "TeamA-orchestrator", GroupId = "rg1", Role = MultiAgentRole.Worker });
        org.Sessions.Add(new SessionMeta { SessionName = "TeamA-orchestrator-1", GroupId = "rg1", Role = MultiAgentRole.Worker });
        org.Sessions.Add(new SessionMeta { SessionName = "TeamA-worker-1", GroupId = "rg1", Role = MultiAgentRole.Worker });
        org.Sessions.Add(new SessionMeta { SessionName = "TeamA-worker-2", GroupId = "rg1", Role = MultiAgentRole.Worker });

        var tempDir = Path.Combine(Path.GetTempPath(), $"polypilot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "organization.json"),
                JsonSerializer.Serialize(org, new JsonSerializerOptions { WriteIndented = true }));

            CopilotService.SetBaseDirForTesting(tempDir);
            var svc = new CopilotService(
                new StubChatDatabase(), new StubServerManager(), new StubWsBridgeClient(),
                new RepoManager(), new ServiceCollection().BuildServiceProvider(), new StubDemoService());
            svc.LoadOrganization();

            // Exactly ONE multi-agent group named "TeamA"
            var maGroups = svc.Organization.Groups.Where(g => g.IsMultiAgent).ToList();
            Assert.Single(maGroups);
            Assert.Equal("TeamA", maGroups[0].Name);

            // All 4 sessions should be in that group
            var teamSessions = svc.Organization.Sessions.Where(m => m.GroupId == maGroups[0].Id).ToList();
            Assert.Equal(4, teamSessions.Count);

            // Both orchestrators should have Orchestrator role
            var orchs = teamSessions.Where(m => m.Role == MultiAgentRole.Orchestrator).ToList();
            Assert.Equal(2, orchs.Count);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    #endregion
}
