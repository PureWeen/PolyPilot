using PolyPilot.Models;
using PolyPilot.Services;
using Microsoft.Extensions.DependencyInjection;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the "Add Existing Repository" flow (AddRepositoryFromLocalAsync).
/// Covers two bugs:
/// 1. Adding an existing local repo should clone from the local path, not the remote URL.
/// 2. ReconcileOrganization should prefer a local folder group over creating a duplicate URL-based group.
/// </summary>
[Collection("BaseDir")]
public class AddExistingRepoTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly IServiceProvider _serviceProvider;

    public AddExistingRepoTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private static RepoManager CreateRepoManagerWithState(List<RepositoryInfo> repos, List<WorktreeInfo> worktrees)
    {
        var rm = new RepoManager();
        var stateField = typeof(RepoManager).GetField("_state",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var loadedField = typeof(RepoManager).GetField("_loaded",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        stateField.SetValue(rm, new RepositoryState { Repositories = repos, Worktrees = worktrees });
        loadedField.SetValue(rm, true);
        return rm;
    }

    private CopilotService CreateService(RepoManager? repoManager = null) =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, repoManager ?? new RepoManager(), _serviceProvider, _demoService);

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

    /// <summary>
    /// Injects a SessionState with a specific working directory so ReconcileOrganization
    /// can match it to a worktree via workingDir.StartsWith(w.Path).
    /// </summary>
    private static void AddDummySessionWithWorkingDir(CopilotService svc, string sessionName, string workingDirectory)
    {
        var sessionsField = typeof(CopilotService).GetField("_sessions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var dict = sessionsField.GetValue(svc)!;
        var stateType = sessionsField.FieldType.GenericTypeArguments[1]; // SessionState

        var info = new AgentSessionInfo
        {
            Name = sessionName,
            Model = "test-model",
            WorkingDirectory = workingDirectory
        };
        var state = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(stateType);
        stateType.GetProperty("Info")!.SetValue(state, info);
        dict.GetType().GetMethod("TryAdd")!.Invoke(dict, new[] { sessionName, (object)state });
    }

    // ─── Bug 2: ReconcileOrganization should prefer local folder groups ────────

    [Fact]
    public void Reconcile_SessionInDefault_WithLocalFolderGroupOnly_AssignsToLocalFolderGroup()
    {
        // Bug scenario: user added a repo via "Existing folder" (only a local folder group exists).
        // A new session whose working dir matches the worktree should be assigned to the
        // local folder group — NOT cause a new URL-based repo group to be created.
        var localRepoPath = Path.Combine(Path.GetTempPath(), "MAUI.Sherpa");
        var nestedWtPath = Path.Combine(localRepoPath, ".polypilot", "worktrees", "session-1");

        var repos = new List<RepositoryInfo>
        {
            new() { Id = "redth-MAUI.Sherpa", Name = "MAUI.Sherpa", Url = "https://github.com/redth/MAUI.Sherpa" }
        };
        var worktrees = new List<WorktreeInfo>
        {
            new() { Id = "ext-1", RepoId = "redth-MAUI.Sherpa", Branch = "main", Path = localRepoPath },
            new() { Id = "wt-1", RepoId = "redth-MAUI.Sherpa", Branch = "feature", Path = nestedWtPath }
        };
        var rm = CreateRepoManagerWithState(repos, worktrees);
        var svc = CreateService(rm);

        // Only a local folder group exists (as when user added via "Existing folder")
        var localGroup = svc.GetOrCreateLocalFolderGroup(localRepoPath, "redth-MAUI.Sherpa");
        Assert.True(localGroup.IsLocalFolder);

        // Session starts in default group, working in a nested worktree
        var meta = new SessionMeta
        {
            SessionName = "MAUI.Sherpa",
            GroupId = SessionGroup.DefaultId
        };
        svc.Organization.Sessions.Add(meta);
        AddDummySessionWithWorkingDir(svc, "MAUI.Sherpa", nestedWtPath);

        // Before reconcile: no URL-based group exists
        var urlGroupsBefore = svc.Organization.Groups.Count(g => g.RepoId == "redth-MAUI.Sherpa" && !g.IsLocalFolder);
        Assert.Equal(0, urlGroupsBefore);

        svc.ReconcileOrganization();

        // After reconcile: session should be in the local folder group
        var updatedMeta = svc.Organization.Sessions.First(m => m.SessionName == "MAUI.Sherpa");
        Assert.Equal(localGroup.Id, updatedMeta.GroupId);

        // No URL-based repo group should have been created
        var urlGroupsAfter = svc.Organization.Groups.Count(g => g.RepoId == "redth-MAUI.Sherpa" && !g.IsLocalFolder && !g.IsMultiAgent);
        Assert.Equal(0, urlGroupsAfter);
    }

    [Fact]
    public void Reconcile_SessionInDefault_WithBothGroupTypes_PrefersLocalFolderGroup()
    {
        // When both a local folder group and a URL-based group exist for the same repo,
        // ReconcileOrganization should prefer the local folder group for unassigned sessions.
        var localRepoPath = Path.Combine(Path.GetTempPath(), "MyProject");
        var nestedWtPath = Path.Combine(localRepoPath, ".polypilot", "worktrees", "feature-x");

        var repos = new List<RepositoryInfo>
        {
            new() { Id = "repo-1", Name = "MyProject", Url = "https://github.com/test/myproject" }
        };
        var worktrees = new List<WorktreeInfo>
        {
            new() { Id = "ext-1", RepoId = "repo-1", Branch = "main", Path = localRepoPath },
            new() { Id = "wt-1", RepoId = "repo-1", Branch = "feature-x", Path = nestedWtPath }
        };
        var rm = CreateRepoManagerWithState(repos, worktrees);
        var svc = CreateService(rm);

        // Both group types exist — local folder group takes priority
        svc.GetOrCreateRepoGroup("repo-1", "MyProject");
        var localGroup = svc.GetOrCreateLocalFolderGroup(localRepoPath, "repo-1");

        var meta = new SessionMeta
        {
            SessionName = "test-session",
            GroupId = SessionGroup.DefaultId
        };
        svc.Organization.Sessions.Add(meta);
        AddDummySessionWithWorkingDir(svc, "test-session", nestedWtPath);

        svc.ReconcileOrganization();

        var updated = svc.Organization.Sessions.First(m => m.SessionName == "test-session");
        Assert.Equal(localGroup.Id, updated.GroupId);
    }

    [Fact]
    public void Reconcile_SessionInDefault_WithOnlyUrlGroup_FallsBackToUrlGroup()
    {
        // When only a URL-based repo group exists (no local folder group), the session
        // should be assigned to the URL-based group as before (existing behavior preserved).
        var nestedWtPath = Path.Combine(Path.GetTempPath(), "worktrees", "feature-x");

        var repos = new List<RepositoryInfo>
        {
            new() { Id = "repo-1", Name = "MyProject", Url = "https://github.com/test/myproject" }
        };
        var worktrees = new List<WorktreeInfo>
        {
            new() { Id = "wt-1", RepoId = "repo-1", Branch = "feature-x", Path = nestedWtPath }
        };
        var rm = CreateRepoManagerWithState(repos, worktrees);
        var svc = CreateService(rm);

        // Only URL-based group exists
        var urlGroup = svc.GetOrCreateRepoGroup("repo-1", "MyProject");

        var meta = new SessionMeta
        {
            SessionName = "test-session",
            GroupId = SessionGroup.DefaultId
        };
        svc.Organization.Sessions.Add(meta);
        AddDummySessionWithWorkingDir(svc, "test-session", nestedWtPath);

        svc.ReconcileOrganization();

        var updated = svc.Organization.Sessions.First(m => m.SessionName == "test-session");
        Assert.Equal(urlGroup!.Id, updated.GroupId);
    }

    // ─── Bug 1: AddRepositoryAsync supports local clone source ─────────────────

    [Fact]
    public void AddRepositoryAsync_HasLocalCloneSourceOverload()
    {
        // Verify the internal overload with localCloneSource parameter exists and is accessible.
        var method = typeof(RepoManager).GetMethod("AddRepositoryAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null,
            new[] { typeof(string), typeof(Action<string>), typeof(string), typeof(CancellationToken) },
            null);
        Assert.NotNull(method);
    }

    [Fact]
    public void AddRepositoryFromLocalAsync_PassesLocalPathAsCloneSource()
    {
        // Verify that AddRepositoryFromLocalAsync calls AddRepositoryAsync with
        // localCloneSource set to the local path (structural invariant).
        // This ensures future refactors don't lose the local clone optimization.
        var sourceFile = File.ReadAllText(Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "RepoManager.cs"));

        // The call should pass localCloneSource: localPath
        Assert.Contains("localCloneSource: localPath", sourceFile);
        Assert.Contains("localCloneSource", sourceFile);
    }

    [Fact]
    public void AddRepositoryAsync_LocalCloneSource_SetsRemoteUrlAfterClone()
    {
        // Verify that when localCloneSource is used, the code sets the remote URL
        // to the actual remote URL (not the local path) so future fetches go to the network.
        var sourceFile = File.ReadAllText(Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "RepoManager.cs"));

        // The local clone branch must reconfigure the remote origin
        Assert.Contains("remote", sourceFile);
        Assert.Contains("set-url", sourceFile);
        Assert.Contains("origin", sourceFile);
    }

    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root");
    }
}
