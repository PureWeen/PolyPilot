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
    public async Task AddRepositoryFromLocal_ClonesLocallyAndSetsRemoteUrl()
    {
        // Create a real local git repo with an origin remote, then call
        // AddRepositoryFromLocalAsync and verify the bare clone's remote URL
        // is the network URL (not the local path).
        var tempDir = Path.Combine(Path.GetTempPath(), $"local-clone-test-{Guid.NewGuid():N}");
        var testBaseDir = Path.Combine(Path.GetTempPath(), $"rmtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(testBaseDir);
        try
        {
            var remoteUrl = "https://github.com/test-owner/local-clone-test.git";

            await RunProcess("git", "init", tempDir);
            await RunProcess("git", "-C", tempDir, "config", "user.email", "test@test.com");
            await RunProcess("git", "-C", tempDir, "config", "user.name", "Test");
            await RunProcess("git", "-C", tempDir, "commit", "--allow-empty", "-m", "init");
            await RunProcess("git", "-C", tempDir, "remote", "add", "origin", remoteUrl);

            var rm = new RepoManager();
            RepoManager.SetBaseDirForTesting(testBaseDir);
            try
            {
                var progressMessages = new List<string>();
                var repo = await rm.AddRepositoryFromLocalAsync(
                    tempDir, msg => progressMessages.Add(msg));

                // Should have used local clone, not network
                Assert.Contains(progressMessages, m => m.Contains("local folder", StringComparison.OrdinalIgnoreCase));

                // The bare clone's remote origin should point to the network URL
                var bareRemoteUrl = await RunGitOutput(repo.BareClonePath, "remote", "get-url", "origin");
                Assert.Equal(remoteUrl, bareRemoteUrl.Trim());

                // Verify the repo was registered
                Assert.Contains(rm.Repositories, r => r.Id == repo.Id);
            }
            finally
            {
                RepoManager.SetBaseDirForTesting(TestSetup.TestBaseDir);
            }
        }
        finally
        {
            ForceDeleteDirectory(tempDir);
            ForceDeleteDirectory(testBaseDir);
        }
    }

    [Fact]
    public async Task AddRepositoryAsync_LocalCloneSource_InvalidPath_Throws()
    {
        var rm = new RepoManager();
        var method = typeof(RepoManager).GetMethod("AddRepositoryAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null,
            new[] { typeof(string), typeof(Action<string>), typeof(string), typeof(CancellationToken) },
            null)!;

        var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await (Task<RepositoryInfo>)method.Invoke(rm,
                new object?[] { "https://github.com/test/repo", null, "/nonexistent/path", CancellationToken.None })!);

        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Bug 2 (second block): WorktreeId-based reconcile prefers local folder ─

    [Fact]
    public void Reconcile_SessionWithWorktreeId_InDefault_WithLocalFolderGroup_AssignsToLocalGroup()
    {
        // When a session has a WorktreeId but is in the Default group (e.g., after
        // group deletion healing), ReconcileOrganization should prefer the local
        // folder group over creating a duplicate URL-based group.
        var localRepoPath = Path.Combine(Path.GetTempPath(), "WorktreeIdTest");
        var nestedWtPath = Path.Combine(localRepoPath, ".polypilot", "worktrees", "wt-1");

        var repos = new List<RepositoryInfo>
        {
            new() { Id = "test-wt-repo", Name = "WorktreeIdTest", Url = "https://github.com/test/worktreeidtest" }
        };
        var worktrees = new List<WorktreeInfo>
        {
            new() { Id = "ext-1", RepoId = "test-wt-repo", Branch = "main", Path = localRepoPath },
            new() { Id = "wt-1", RepoId = "test-wt-repo", Branch = "feature", Path = nestedWtPath }
        };
        var rm = CreateRepoManagerWithState(repos, worktrees);
        var svc = CreateService(rm);

        // Create local folder group (as when user added via "Existing folder")
        var localGroup = svc.GetOrCreateLocalFolderGroup(localRepoPath, "test-wt-repo");

        // Session has a WorktreeId but is in Default (simulates group-deletion healing)
        var meta = new SessionMeta
        {
            SessionName = "healed-session",
            GroupId = SessionGroup.DefaultId,
            WorktreeId = "wt-1"
        };
        svc.Organization.Sessions.Add(meta);
        AddDummySessionWithWorkingDir(svc, "healed-session", nestedWtPath);

        svc.ReconcileOrganization();

        // Session should land in the local folder group, not a new URL-based group
        var updated = svc.Organization.Sessions.First(m => m.SessionName == "healed-session");
        Assert.Equal(localGroup.Id, updated.GroupId);

        // No URL-based repo group should have been created
        var urlGroups = svc.Organization.Groups.Count(g =>
            g.RepoId == "test-wt-repo" && !g.IsLocalFolder && !g.IsMultiAgent);
        Assert.Equal(0, urlGroups);
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private static Task RunProcess(string exe, params string[] args)
    {
        var tcs = new TaskCompletionSource();
        var psi = new System.Diagnostics.ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Exited += (_, _) =>
        {
            if (p.ExitCode == 0) tcs.TrySetResult();
            else tcs.TrySetException(new Exception($"{exe} exited with {p.ExitCode}"));
        };
        p.Start();
        return tcs.Task;
    }

    private static async Task<string> RunGitOutput(string workingDir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
            throw new Exception($"git exited with {p.ExitCode}");
        return output;
    }

    private static void ForceDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;
        foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(path, true);
    }
}
