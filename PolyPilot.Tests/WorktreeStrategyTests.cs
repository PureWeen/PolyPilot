using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for per-worker worktree isolation strategies in multi-agent groups.
/// Validates that CreateGroupFromPresetAsync creates the correct number of
/// worktrees with unique paths per strategy, and that session metadata is
/// correctly wired up.
/// </summary>
public class WorktreeStrategyTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly IServiceProvider _serviceProvider;

    public WorktreeStrategyTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    /// <summary>
    /// A FakeRepoManager that doesn't touch git — returns fake worktrees
    /// with unique IDs and paths, tracking all creation calls.
    /// </summary>
    private class FakeRepoManager : RepoManager
    {
        public List<(string RepoId, string BranchName, bool SkipFetch)> CreateCalls { get; } = new();
        public int FetchCallCount { get; private set; }
        private int _worktreeCounter;

        public FakeRepoManager(List<RepositoryInfo> repos)
        {
            // Inject state via reflection (same pattern as existing tests)
            var stateField = typeof(RepoManager).GetField("_state",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var loadedField = typeof(RepoManager).GetField("_loaded",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            stateField.SetValue(this, new RepositoryState { Repositories = repos, Worktrees = new() });
            loadedField.SetValue(this, true);
        }

        public override Task<WorktreeInfo> CreateWorktreeAsync(string repoId, string branchName,
            string? baseBranch = null, bool skipFetch = false, CancellationToken ct = default)
        {
            CreateCalls.Add((repoId, branchName, skipFetch));
            var id = $"wt-{Interlocked.Increment(ref _worktreeCounter)}";
            var wt = new WorktreeInfo
            {
                Id = id,
                RepoId = repoId,
                Branch = branchName,
                Path = $"/fake/worktrees/{id}"
            };
            return Task.FromResult(wt);
        }

        public override Task FetchAsync(string repoId, CancellationToken ct = default)
        {
            FetchCallCount++;
            return Task.CompletedTask;
        }
    }

    private CopilotService CreateDemoService(RepoManager rm)
    {
        var svc = new CopilotService(_chatDb, _serverManager, _bridgeClient, rm, _serviceProvider, _demoService);
        // Enable demo mode so CreateSessionAsync works without a real Copilot client
        var prop = typeof(CopilotService).GetProperty("IsDemoMode")!;
        prop.SetValue(svc, true);
        return svc;
    }

    private static GroupPreset MakePreset(int workerCount, WorktreeStrategy? defaultStrategy = null)
    {
        return new GroupPreset(
            "TestTeam", "Test", "🧪", MultiAgentMode.Orchestrator,
            "claude-opus-4.6", Enumerable.Repeat("claude-sonnet-4.6", workerCount).ToArray())
        {
            DefaultWorktreeStrategy = defaultStrategy
        };
    }

    #region WorktreeStrategy Enum Serialization

    [Fact]
    public void WorktreeStrategy_AllValues_RoundTrip()
    {
        foreach (var strategy in Enum.GetValues<WorktreeStrategy>())
        {
            var group = new SessionGroup
            {
                Id = $"test-{strategy}",
                Name = $"Test {strategy}",
                IsMultiAgent = true,
                WorktreeStrategy = strategy
            };

            var json = JsonSerializer.Serialize(group);
            var restored = JsonSerializer.Deserialize<SessionGroup>(json)!;

            Assert.Equal(strategy, restored.WorktreeStrategy);
        }
    }

    [Fact]
    public void WorktreeStrategy_DefaultsToShared()
    {
        var group = new SessionGroup { Id = "x", Name = "X" };
        Assert.Equal(WorktreeStrategy.Shared, group.WorktreeStrategy);
    }

    [Fact]
    public void GroupPreset_DefaultWorktreeStrategy_Nullable()
    {
        var preset = new GroupPreset("T", "D", "E", MultiAgentMode.Broadcast, "m", new[] { "w" });
        Assert.Null(preset.DefaultWorktreeStrategy);

        var presetWithStrategy = new GroupPreset("T", "D", "E", MultiAgentMode.Broadcast, "m", new[] { "w" })
        {
            DefaultWorktreeStrategy = WorktreeStrategy.FullyIsolated
        };
        Assert.Equal(WorktreeStrategy.FullyIsolated, presetWithStrategy.DefaultWorktreeStrategy);
    }

    [Fact]
    public void PRReviewSquad_DefaultsToFullyIsolated()
    {
        var prSquad = GroupPreset.BuiltIn.First(p => p.Name == "PR Review Squad");
        Assert.Equal(WorktreeStrategy.FullyIsolated, prSquad.DefaultWorktreeStrategy);
    }

    #endregion

    #region FullyIsolated Strategy

    [Fact]
    public async Task FullyIsolated_CreatesUniqueWorktreePerSession()
    {
        var rm = new FakeRepoManager(new() { new() { Id = "repo-1", Name = "Repo" } });
        var svc = CreateDemoService(rm);
        var preset = MakePreset(3, WorktreeStrategy.FullyIsolated);

        var group = await svc.CreateGroupFromPresetAsync(preset,
            workingDirectory: "/fallback",
            repoId: "repo-1");

        Assert.NotNull(group);
        Assert.Equal(WorktreeStrategy.FullyIsolated, group!.WorktreeStrategy);

        // 1 orchestrator + 3 workers = 4 worktrees
        Assert.Equal(4, rm.CreateCalls.Count);
    }

    [Fact]
    public async Task FullyIsolated_AllWorktreeIdsAreUnique()
    {
        var rm = new FakeRepoManager(new() { new() { Id = "repo-1", Name = "Repo" } });
        var svc = CreateDemoService(rm);
        var preset = MakePreset(3, WorktreeStrategy.FullyIsolated);

        var group = await svc.CreateGroupFromPresetAsync(preset,
            workingDirectory: "/fallback",
            repoId: "repo-1");

        // Get all session worktree IDs
        var worktreeIds = svc.Organization.Sessions
            .Where(s => s.GroupId == group!.Id)
            .Select(s => s.WorktreeId)
            .ToList();

        // All should be non-null and unique
        Assert.All(worktreeIds, id => Assert.NotNull(id));
        Assert.Equal(worktreeIds.Count, worktreeIds.Distinct().Count());
    }

    [Fact]
    public async Task FullyIsolated_OrchestratorHasDifferentWorktreeThanWorkers()
    {
        var rm = new FakeRepoManager(new() { new() { Id = "repo-1", Name = "Repo" } });
        var svc = CreateDemoService(rm);
        var preset = MakePreset(2, WorktreeStrategy.FullyIsolated);

        var group = await svc.CreateGroupFromPresetAsync(preset,
            workingDirectory: "/fallback",
            repoId: "repo-1");

        var orchMeta = svc.Organization.Sessions
            .First(s => s.GroupId == group!.Id && s.Role == MultiAgentRole.Orchestrator);
        var workerMetas = svc.Organization.Sessions
            .Where(s => s.GroupId == group!.Id && s.Role != MultiAgentRole.Orchestrator)
            .ToList();

        Assert.NotNull(orchMeta.WorktreeId);
        Assert.All(workerMetas, w =>
        {
            Assert.NotNull(w.WorktreeId);
            Assert.NotEqual(orchMeta.WorktreeId, w.WorktreeId);
        });
    }

    [Fact]
    public async Task FullyIsolated_SkipsFetchOnWorkerWorktrees()
    {
        var rm = new FakeRepoManager(new() { new() { Id = "repo-1", Name = "Repo" } });
        var svc = CreateDemoService(rm);
        var preset = MakePreset(3, WorktreeStrategy.FullyIsolated);

        await svc.CreateGroupFromPresetAsync(preset,
            workingDirectory: "/fallback",
            repoId: "repo-1");

        // One explicit FetchAsync call upfront
        Assert.Equal(1, rm.FetchCallCount);
        // All CreateWorktreeAsync calls should have skipFetch=true
        Assert.All(rm.CreateCalls, c => Assert.True(c.SkipFetch));
    }

    #endregion

    #region OrchestratorIsolated Strategy

    [Fact]
    public async Task OrchestratorIsolated_Creates2Worktrees()
    {
        var rm = new FakeRepoManager(new() { new() { Id = "repo-1", Name = "Repo" } });
        var svc = CreateDemoService(rm);
        var preset = MakePreset(3, WorktreeStrategy.OrchestratorIsolated);

        var group = await svc.CreateGroupFromPresetAsync(preset,
            workingDirectory: "/fallback",
            repoId: "repo-1");

        Assert.NotNull(group);
        Assert.Equal(WorktreeStrategy.OrchestratorIsolated, group!.WorktreeStrategy);

        // 1 orchestrator + 1 shared worker = 2 worktrees
        Assert.Equal(2, rm.CreateCalls.Count);
    }

    [Fact]
    public async Task OrchestratorIsolated_WorkersShareSameWorktree()
    {
        var rm = new FakeRepoManager(new() { new() { Id = "repo-1", Name = "Repo" } });
        var svc = CreateDemoService(rm);
        var preset = MakePreset(3, WorktreeStrategy.OrchestratorIsolated);

        var group = await svc.CreateGroupFromPresetAsync(preset,
            workingDirectory: "/fallback",
            repoId: "repo-1");

        var workerMetas = svc.Organization.Sessions
            .Where(s => s.GroupId == group!.Id && s.Role != MultiAgentRole.Orchestrator)
            .ToList();

        Assert.Equal(3, workerMetas.Count);
        // All workers should share the same worktree ID
        var workerWtIds = workerMetas.Select(w => w.WorktreeId).Distinct().ToList();
        Assert.Single(workerWtIds);
        Assert.NotNull(workerWtIds[0]);
    }

    [Fact]
    public async Task OrchestratorIsolated_OrchestratorHasDifferentWorktreeThanWorkers()
    {
        var rm = new FakeRepoManager(new() { new() { Id = "repo-1", Name = "Repo" } });
        var svc = CreateDemoService(rm);
        var preset = MakePreset(2, WorktreeStrategy.OrchestratorIsolated);

        var group = await svc.CreateGroupFromPresetAsync(preset,
            workingDirectory: "/fallback",
            repoId: "repo-1");

        var orchMeta = svc.Organization.Sessions
            .First(s => s.GroupId == group!.Id && s.Role == MultiAgentRole.Orchestrator);
        var workerMeta = svc.Organization.Sessions
            .First(s => s.GroupId == group!.Id && s.Role != MultiAgentRole.Orchestrator);

        Assert.NotNull(orchMeta.WorktreeId);
        Assert.NotNull(workerMeta.WorktreeId);
        Assert.NotEqual(orchMeta.WorktreeId, workerMeta.WorktreeId);
    }

    #endregion

    #region Shared Strategy

    [Fact]
    public async Task Shared_CreatesNoWorktrees()
    {
        var rm = new FakeRepoManager(new() { new() { Id = "repo-1", Name = "Repo" } });
        var svc = CreateDemoService(rm);
        var preset = MakePreset(3, WorktreeStrategy.Shared);

        var group = await svc.CreateGroupFromPresetAsync(preset,
            workingDirectory: "/fallback",
            repoId: "repo-1");

        Assert.NotNull(group);
        Assert.Equal(WorktreeStrategy.Shared, group!.WorktreeStrategy);

        // No worktrees created
        Assert.Empty(rm.CreateCalls);
        Assert.Equal(0, rm.FetchCallCount);
    }

    [Fact]
    public async Task Shared_AllSessionsGetNullWorktreeId()
    {
        var rm = new FakeRepoManager(new() { new() { Id = "repo-1", Name = "Repo" } });
        var svc = CreateDemoService(rm);
        var preset = MakePreset(2, WorktreeStrategy.Shared);

        var group = await svc.CreateGroupFromPresetAsync(preset,
            workingDirectory: "/fallback",
            repoId: "repo-1");

        var allMetas = svc.Organization.Sessions
            .Where(s => s.GroupId == group!.Id)
            .ToList();

        Assert.Equal(3, allMetas.Count); // 1 orch + 2 workers
        Assert.All(allMetas, m => Assert.Null(m.WorktreeId));
    }

    #endregion

    #region StrategyOverride

    [Fact]
    public async Task StrategyOverride_OverridesPresetDefault()
    {
        var rm = new FakeRepoManager(new() { new() { Id = "repo-1", Name = "Repo" } });
        var svc = CreateDemoService(rm);
        // Preset defaults to FullyIsolated
        var preset = MakePreset(2, WorktreeStrategy.FullyIsolated);

        // Override to Shared
        var group = await svc.CreateGroupFromPresetAsync(preset,
            workingDirectory: "/fallback",
            repoId: "repo-1",
            strategyOverride: WorktreeStrategy.Shared);

        Assert.Equal(WorktreeStrategy.Shared, group!.WorktreeStrategy);
        Assert.Empty(rm.CreateCalls);
    }

    [Fact]
    public async Task StrategyOverride_NullUsesPresetDefault()
    {
        var rm = new FakeRepoManager(new() { new() { Id = "repo-1", Name = "Repo" } });
        var svc = CreateDemoService(rm);
        var preset = MakePreset(2, WorktreeStrategy.FullyIsolated);

        var group = await svc.CreateGroupFromPresetAsync(preset,
            workingDirectory: "/fallback",
            repoId: "repo-1",
            strategyOverride: null);

        Assert.Equal(WorktreeStrategy.FullyIsolated, group!.WorktreeStrategy);
        Assert.Equal(3, rm.CreateCalls.Count); // 1 orch + 2 workers
    }

    [Fact]
    public async Task NoRepoId_SkipsWorktreeCreation()
    {
        var rm = new FakeRepoManager(new() { new() { Id = "repo-1", Name = "Repo" } });
        var svc = CreateDemoService(rm);
        var preset = MakePreset(2, WorktreeStrategy.FullyIsolated);

        // No repoId — can't create worktrees
        var group = await svc.CreateGroupFromPresetAsync(preset,
            workingDirectory: "/fallback",
            repoId: null);

        Assert.NotNull(group);
        Assert.Empty(rm.CreateCalls);
    }

    #endregion

    #region Session Creation Correctness

    [Fact]
    public async Task FullyIsolated_CorrectNumberOfSessions()
    {
        var rm = new FakeRepoManager(new() { new() { Id = "repo-1", Name = "Repo" } });
        var svc = CreateDemoService(rm);
        var preset = MakePreset(5, WorktreeStrategy.FullyIsolated);

        var group = await svc.CreateGroupFromPresetAsync(preset,
            workingDirectory: "/fallback",
            repoId: "repo-1");

        var members = svc.Organization.Sessions
            .Where(s => s.GroupId == group!.Id)
            .ToList();

        Assert.Equal(6, members.Count); // 1 orchestrator + 5 workers
        Assert.Single(members.Where(m => m.Role == MultiAgentRole.Orchestrator));
    }

    [Fact]
    public async Task FullyIsolated_OrchestratorIsPinned()
    {
        var rm = new FakeRepoManager(new() { new() { Id = "repo-1", Name = "Repo" } });
        var svc = CreateDemoService(rm);
        var preset = MakePreset(2, WorktreeStrategy.FullyIsolated);

        var group = await svc.CreateGroupFromPresetAsync(preset,
            workingDirectory: "/fallback",
            repoId: "repo-1");

        var orchMeta = svc.Organization.Sessions
            .First(s => s.GroupId == group!.Id && s.Role == MultiAgentRole.Orchestrator);
        Assert.True(orchMeta.IsPinned);
    }

    [Fact]
    public async Task FullyIsolated_GroupWorktreeIdIsOrchestratorWorktree()
    {
        var rm = new FakeRepoManager(new() { new() { Id = "repo-1", Name = "Repo" } });
        var svc = CreateDemoService(rm);
        var preset = MakePreset(2, WorktreeStrategy.FullyIsolated);

        var group = await svc.CreateGroupFromPresetAsync(preset,
            workingDirectory: "/fallback",
            repoId: "repo-1");

        var orchMeta = svc.Organization.Sessions
            .First(s => s.GroupId == group!.Id && s.Role == MultiAgentRole.Orchestrator);

        // Group-level WorktreeId should match orchestrator's worktree
        Assert.Equal(orchMeta.WorktreeId, group!.WorktreeId);
    }

    #endregion

    #region Error Resilience

    [Fact]
    public async Task WorktreeCreationFailure_StillCreatesSessions()
    {
        var rm = new FailingRepoManager(new() { new() { Id = "repo-1", Name = "Repo" } });
        var svc = CreateDemoService(rm);
        var preset = MakePreset(3, WorktreeStrategy.FullyIsolated);

        // Should not throw
        var group = await svc.CreateGroupFromPresetAsync(preset,
            workingDirectory: "/fallback",
            repoId: "repo-1");

        Assert.NotNull(group);

        // Sessions should still be created even though worktrees failed
        var members = svc.Organization.Sessions
            .Where(s => s.GroupId == group!.Id)
            .ToList();

        Assert.Equal(4, members.Count); // 1 orch + 3 workers
    }

    /// <summary>
    /// A RepoManager that always throws on worktree creation.
    /// </summary>
    private class FailingRepoManager : RepoManager
    {
        public FailingRepoManager(List<RepositoryInfo> repos)
        {
            var stateField = typeof(RepoManager).GetField("_state",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var loadedField = typeof(RepoManager).GetField("_loaded",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            stateField.SetValue(this, new RepositoryState { Repositories = repos, Worktrees = new() });
            loadedField.SetValue(this, true);
        }

        public override Task<WorktreeInfo> CreateWorktreeAsync(string repoId, string branchName,
            string? baseBranch = null, bool skipFetch = false, CancellationToken ct = default)
        {
            throw new InvalidOperationException("Simulated git failure");
        }

        public override Task FetchAsync(string repoId, CancellationToken ct = default)
        {
            throw new InvalidOperationException("Simulated git fetch failure");
        }
    }

    #endregion

    #region Branch Name Sanitization

    [Fact]
    public async Task BranchNames_SpacesReplacedWithDashes()
    {
        var rm = new FakeRepoManager(new() { new() { Id = "repo-1", Name = "Repo" } });
        var svc = CreateDemoService(rm);
        var preset = MakePreset(2, WorktreeStrategy.FullyIsolated);

        // "PR Review Squad" has spaces — branch names must not
        await svc.CreateGroupFromPresetAsync(preset,
            workingDirectory: "/fallback",
            repoId: "repo-1",
            nameOverride: "PR Review Squad");

        // All branch names should have no spaces
        Assert.All(rm.CreateCalls, c =>
        {
            Assert.DoesNotContain(" ", c.BranchName);
            Assert.StartsWith("PR-Review-Squad-", c.BranchName);
        });
    }

    [Fact]
    public async Task BranchNames_SpecialCharsRemoved()
    {
        var rm = new FakeRepoManager(new() { new() { Id = "repo-1", Name = "Repo" } });
        var svc = CreateDemoService(rm);
        var preset = MakePreset(1, WorktreeStrategy.FullyIsolated);

        await svc.CreateGroupFromPresetAsync(preset,
            workingDirectory: "/fallback",
            repoId: "repo-1",
            nameOverride: "My Team! @#$%");

        Assert.All(rm.CreateCalls, c =>
        {
            Assert.DoesNotContain(" ", c.BranchName);
            Assert.DoesNotContain("!", c.BranchName);
            Assert.DoesNotContain("@", c.BranchName);
            Assert.DoesNotContain("#", c.BranchName);
        });
    }

    #endregion

    #region CreatedWorktreeIds Tracking

    [Fact]
    public async Task FullyIsolated_CreatedWorktreeIds_TracksAllWorktrees()
    {
        var rm = new FakeRepoManager(new() { new() { Id = "repo-1", Name = "Repo" } });
        var svc = CreateDemoService(rm);
        var preset = MakePreset(3, WorktreeStrategy.FullyIsolated);

        var group = await svc.CreateGroupFromPresetAsync(preset,
            workingDirectory: "/fallback",
            repoId: "repo-1");

        // 1 orchestrator + 3 workers = 4 worktrees
        Assert.Equal(4, group!.CreatedWorktreeIds.Count);
        Assert.Equal(4, group.CreatedWorktreeIds.Distinct().Count());
    }

    [Fact]
    public async Task OrchestratorIsolated_CreatedWorktreeIds_TracksAllWorktrees()
    {
        var rm = new FakeRepoManager(new() { new() { Id = "repo-1", Name = "Repo" } });
        var svc = CreateDemoService(rm);
        var preset = MakePreset(3, WorktreeStrategy.OrchestratorIsolated);

        var group = await svc.CreateGroupFromPresetAsync(preset,
            workingDirectory: "/fallback",
            repoId: "repo-1");

        // 1 orchestrator + 1 shared worker = 2 worktrees
        Assert.Equal(2, group!.CreatedWorktreeIds.Count);
        Assert.Equal(2, group.CreatedWorktreeIds.Distinct().Count());
    }

    [Fact]
    public async Task Shared_CreatedWorktreeIds_Empty()
    {
        var rm = new FakeRepoManager(new() { new() { Id = "repo-1", Name = "Repo" } });
        var svc = CreateDemoService(rm);
        var preset = MakePreset(3, WorktreeStrategy.Shared);

        var group = await svc.CreateGroupFromPresetAsync(preset,
            workingDirectory: "/fallback",
            repoId: "repo-1");

        Assert.Empty(group!.CreatedWorktreeIds);
    }

    [Fact]
    public async Task FullyIsolated_CreatedWorktreeIds_MatchesSessionWorktreeIds()
    {
        var rm = new FakeRepoManager(new() { new() { Id = "repo-1", Name = "Repo" } });
        var svc = CreateDemoService(rm);
        var preset = MakePreset(2, WorktreeStrategy.FullyIsolated);

        var group = await svc.CreateGroupFromPresetAsync(preset,
            workingDirectory: "/fallback",
            repoId: "repo-1");

        var sessionWtIds = svc.Organization.Sessions
            .Where(s => s.GroupId == group!.Id && s.WorktreeId != null)
            .Select(s => s.WorktreeId!)
            .ToHashSet();

        // All session worktree IDs should be in CreatedWorktreeIds
        Assert.All(sessionWtIds, id => Assert.Contains(id, group!.CreatedWorktreeIds));
    }

    #endregion

    #region Review Finding: Shared strategy auto-creates worktree when no workingDirectory

    [Fact]
    public async Task Shared_WithRepoButNoWorkDir_CreatesSharedWorktree()
    {
        var rm = new FakeRepoManager(new() { new() { Id = "repo-1", Name = "Repo" } });
        var svc = CreateDemoService(rm);
        var preset = MakePreset(2, WorktreeStrategy.Shared);

        // workingDirectory: null and worktreeId: null — should auto-create a shared worktree
        var group = await svc.CreateGroupFromPresetAsync(preset,
            workingDirectory: null,
            repoId: "repo-1");

        Assert.NotNull(group);
        // Exactly 1 shared worktree created (not N per session)
        Assert.Single(rm.CreateCalls);
        Assert.Contains("shared", rm.CreateCalls[0].BranchName);
        // Group should track the created worktree
        Assert.Single(group!.CreatedWorktreeIds);
        Assert.NotNull(group.WorktreeId);
    }

    [Fact]
    public async Task Shared_WithExistingWorkDir_CreatesNoWorktree()
    {
        var rm = new FakeRepoManager(new() { new() { Id = "repo-1", Name = "Repo" } });
        var svc = CreateDemoService(rm);
        var preset = MakePreset(2, WorktreeStrategy.Shared);

        // workingDirectory provided — no auto-create needed
        var group = await svc.CreateGroupFromPresetAsync(preset,
            workingDirectory: "/existing/dir",
            repoId: "repo-1");

        Assert.NotNull(group);
        Assert.Empty(rm.CreateCalls);
    }

    #endregion

    #region Review Finding: WorktreeId set on AgentSessionInfo (not just SessionMeta)

    [Fact]
    public async Task FullyIsolated_WorktreeIdSetOnAgentSessionInfo()
    {
        var rm = new FakeRepoManager(new() { new() { Id = "repo-1", Name = "Repo" } });
        var svc = CreateDemoService(rm);
        var preset = MakePreset(2, WorktreeStrategy.FullyIsolated);

        var group = await svc.CreateGroupFromPresetAsync(preset,
            workingDirectory: "/fallback",
            repoId: "repo-1");

        // Check that AgentSessionInfo.WorktreeId is set (not just SessionMeta)
        var groupSessionNames = svc.Organization.Sessions
            .Where(s => s.GroupId == group!.Id)
            .Select(s => s.SessionName)
            .ToList();

        Assert.Equal(3, groupSessionNames.Count); // 1 orch + 2 workers

        var organized = svc.GetOrganizedSessions();
        var groupEntry = organized.FirstOrDefault(g => g.Group.Id == group!.Id);
        Assert.NotNull(groupEntry.Group);
        Assert.All(groupEntry.Sessions, s => Assert.NotNull(s.WorktreeId));
    }

    #endregion
}
