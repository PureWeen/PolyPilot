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
    public async Task AddRepositoryFromLocal_PointsBareClonePathAtLocalRepo()
    {
        // AddRepositoryFromLocalAsync should set BareClonePath to the local path
        // (no bare clone is created) and register the repo.
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
                var repo = await rm.AddRepositoryFromLocalAsync(tempDir);

                // BareClonePath should point at the user's local repo — no bare clone
                Assert.Equal(Path.GetFullPath(tempDir), Path.GetFullPath(repo.BareClonePath));

                // No bare clone directory should exist under the managed repos dir
                var reposDir = Path.Combine(testBaseDir, "repos");
                if (Directory.Exists(reposDir))
                    Assert.Empty(Directory.GetDirectories(reposDir));

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
    public void AddRepositoryFromLocal_NoBareCloneCreatedInReposDir()
    {
        // Verify that AddRepositoryFromLocalAsync does NOT call AddRepositoryAsync
        // (which would create a bare clone). Our approach sets BareClonePath directly
        // to the local path — the internal localCloneSource overload is no longer used.
        var sourceFile = File.ReadAllText(Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "RepoManager.cs"));

        // AddRepositoryFromLocalAsync should NOT call AddRepositoryAsync
        // Instead it should directly create a RepositoryInfo with BareClonePath = localPath
        var methodBody = ExtractMethodBody(sourceFile, "AddRepositoryFromLocalAsync");
        Assert.DoesNotContain("AddRepositoryAsync(", methodBody);
        Assert.Contains("BareClonePath = localPath", methodBody);
    }

    [Fact]
    public async Task AddRepositoryFromLocal_CreatesSeparateRepoWhenUrlBasedExists()
    {
        // When a repo was already added via "Add from URL" (managed bare clone),
        // adding the same repo from a local folder must create a SEPARATE RepositoryInfo
        // with a distinct ID and BareClonePath pointing at the local folder.
        var tempDir = Path.Combine(Path.GetTempPath(), $"local-overwrite-test-{Guid.NewGuid():N}");
        var testBaseDir = Path.Combine(Path.GetTempPath(), $"rmtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(testBaseDir);
        try
        {
            var remoteUrl = "https://github.com/test-owner/overwrite-test.git";

            // Create a local git repo with an origin remote
            await RunProcess("git", "init", tempDir);
            await RunProcess("git", "-C", tempDir, "config", "user.email", "test@test.com");
            await RunProcess("git", "-C", tempDir, "config", "user.name", "Test");
            await RunProcess("git", "-C", tempDir, "commit", "--allow-empty", "-m", "init");
            await RunProcess("git", "-C", tempDir, "remote", "add", "origin", remoteUrl);

            var rm = new RepoManager();
            RepoManager.SetBaseDirForTesting(testBaseDir);
            try
            {
                // Simulate a repo already added via "Add from URL" with a managed bare clone.
                var urlId = RepoManager.RepoIdFromUrl(remoteUrl);
                var barePath = Path.Combine(testBaseDir, "repos", $"{urlId}.git");
                Directory.CreateDirectory(barePath);
                var urlRepo = new RepositoryInfo
                {
                    Id = urlId,
                    Name = "overwrite-test",
                    Url = remoteUrl,
                    BareClonePath = barePath,
                    AddedAt = DateTime.UtcNow
                };
                // Inject the URL-based repo into state
                var state = new RepositoryState();
                state.Repositories.Add(urlRepo);
                var stateFile = Path.Combine(testBaseDir, "repos.json");
                File.WriteAllText(stateFile, System.Text.Json.JsonSerializer.Serialize(state));
                rm.Load();

                // Now add the same repo from a local folder
                var localRepo = await rm.AddRepositoryFromLocalAsync(tempDir);

                // The returned repo should have a DIFFERENT ID from the URL-based repo
                Assert.NotEqual(urlId, localRepo.Id);
                Assert.StartsWith(urlId, localRepo.Id); // e.g. "test-owner-overwrite-test-local-..."

                // The local repo's BareClonePath must point at the local folder
                Assert.True(PathsEqual(localRepo.BareClonePath, tempDir),
                    $"Expected local repo BareClonePath to be '{tempDir}' but got '{localRepo.BareClonePath}'");

                // The original URL-based repo must be untouched
                var originalRepo = rm.Repositories.FirstOrDefault(r => r.Id == urlId);
                Assert.NotNull(originalRepo);
                Assert.Equal(Path.GetFullPath(barePath), Path.GetFullPath(originalRepo.BareClonePath));

                // The managed bare clone directory must still exist
                Assert.True(Directory.Exists(barePath));

                // There should be TWO repos total
                Assert.Equal(2, rm.Repositories.Count);
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
    public async Task AddRepositoryFromLocal_IdempotentForSameLocalFolder()
    {
        // Adding the same local folder twice should return the same repo, not create duplicates.
        var tempDir = Path.Combine(Path.GetTempPath(), $"local-idempotent-{Guid.NewGuid():N}");
        var testBaseDir = Path.Combine(Path.GetTempPath(), $"rmtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(testBaseDir);
        try
        {
            var remoteUrl = "https://github.com/test-owner/idempotent-test.git";
            await RunProcess("git", "init", tempDir);
            await RunProcess("git", "-C", tempDir, "config", "user.email", "test@test.com");
            await RunProcess("git", "-C", tempDir, "config", "user.name", "Test");
            await RunProcess("git", "-C", tempDir, "commit", "--allow-empty", "-m", "init");
            await RunProcess("git", "-C", tempDir, "remote", "add", "origin", remoteUrl);

            var rm = new RepoManager();
            RepoManager.SetBaseDirForTesting(testBaseDir);
            try
            {
                // Add the local folder twice
                var repo1 = await rm.AddRepositoryFromLocalAsync(tempDir);
                var repo2 = await rm.AddRepositoryFromLocalAsync(tempDir);

                // Both should return the same repo
                Assert.Equal(repo1.Id, repo2.Id);
                Assert.True(PathsEqual(repo1.BareClonePath, repo2.BareClonePath));

                // Should still be exactly one repo
                Assert.Single(rm.Repositories);
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

    private static bool PathsEqual(string? left, string? right)
    {
        if (left == null || right == null) return false;
        var a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractMethodBody(string source, string methodName)
    {
        var idx = source.IndexOf(methodName, StringComparison.Ordinal);
        if (idx < 0) return "";
        // Find opening brace
        var braceIdx = source.IndexOf('{', idx);
        if (braceIdx < 0) return "";
        // Find matching closing brace
        var depth = 1;
        var i = braceIdx + 1;
        while (i < source.Length && depth > 0)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}') depth--;
            i++;
        }
        return source[braceIdx..i];
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

    [Fact]
    public async Task AddRepositoryFromLocal_LocalRepoId_HasExpectedFormat()
    {
        // The local repo ID should follow the pattern "{baseId}-local-{pathHash}"
        // where pathHash is a hex-encoded hash of the normalized path.
        var tempDir = Path.Combine(Path.GetTempPath(), $"local-id-format-test-{Guid.NewGuid():N}");
        var testBaseDir = Path.Combine(Path.GetTempPath(), $"rmtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(testBaseDir);
        try
        {
            var remoteUrl = "https://github.com/test-owner/id-format-test.git";

            await RunProcess("git", "init", tempDir);
            await RunProcess("git", "-C", tempDir, "config", "user.email", "test@test.com");
            await RunProcess("git", "-C", tempDir, "config", "user.name", "Test");
            await RunProcess("git", "-C", tempDir, "commit", "--allow-empty", "-m", "init");
            await RunProcess("git", "-C", tempDir, "remote", "add", "origin", remoteUrl);

            var rm = new RepoManager();
            RepoManager.SetBaseDirForTesting(testBaseDir);
            try
            {
                // Pre-create a URL-based repo so the local one gets a distinct ID
                var urlId = RepoManager.RepoIdFromUrl(remoteUrl);
                var barePath = Path.Combine(testBaseDir, "repos", $"{urlId}.git");
                Directory.CreateDirectory(barePath);
                var state = new RepositoryState();
                state.Repositories.Add(new RepositoryInfo
                {
                    Id = urlId, Name = "id-format-test",
                    Url = remoteUrl, BareClonePath = barePath, AddedAt = DateTime.UtcNow
                });
                File.WriteAllText(Path.Combine(testBaseDir, "repos.json"),
                    System.Text.Json.JsonSerializer.Serialize(state));
                rm.Load();

                var localRepo = await rm.AddRepositoryFromLocalAsync(tempDir);

                // ID should match pattern: baseId-local-HEXHASH
                Assert.Matches(@"^test-owner-id-format-test-local-[0-9a-f]{8}$", localRepo.Id);
            }
            finally { RepoManager.SetBaseDirForTesting(TestSetup.TestBaseDir); }
        }
        finally
        {
            ForceDeleteDirectory(tempDir);
            ForceDeleteDirectory(testBaseDir);
        }
    }

    [Fact]
    public void EnsureRepoClone_SkipsCloneForNonBareRepo_WithGitDirectory()
    {
        // EnsureRepoCloneInCurrentRootAsync should detect a .git directory
        // and skip clone management for repos added via "Existing Folder".
        // This is a structural test that verifies the guard exists.
        var sourceFile = File.ReadAllText(Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "RepoManager.cs"));
        var methodBody = ExtractMethodBody(sourceFile, "EnsureRepoCloneInCurrentRootAsync");

        // Must check for both .git directory and .git file (worktree checkout)
        Assert.Contains("Directory.Exists(Path.Combine(repo.BareClonePath, \".git\"))", methodBody);
        Assert.Contains("File.Exists(Path.Combine(repo.BareClonePath, \".git\"))", methodBody);
    }

    [Fact]
    public async Task AddRepositoryFromLocal_ValidationErrors_ThrowDescriptiveExceptions()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}");
        var notGit = Path.Combine(Path.GetTempPath(), $"not-git-{Guid.NewGuid():N}");
        var noOrigin = Path.Combine(Path.GetTempPath(), $"no-origin-{Guid.NewGuid():N}");
        var testBaseDir = Path.Combine(Path.GetTempPath(), $"rmtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(notGit);
        Directory.CreateDirectory(noOrigin);
        Directory.CreateDirectory(testBaseDir);
        try
        {
            // Initialize noOrigin as git repo but without origin remote
            await RunProcess("git", "init", noOrigin);
            await RunProcess("git", "-C", noOrigin, "config", "user.email", "test@test.com");
            await RunProcess("git", "-C", noOrigin, "config", "user.name", "Test");
            await RunProcess("git", "-C", noOrigin, "commit", "--allow-empty", "-m", "init");

            var rm = new RepoManager();
            RepoManager.SetBaseDirForTesting(testBaseDir);
            try
            {
                // Non-existent folder
                var ex1 = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => rm.AddRepositoryFromLocalAsync(nonExistent));
                Assert.Contains("not found", ex1.Message, StringComparison.OrdinalIgnoreCase);

                // Folder that isn't a git repo
                var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => rm.AddRepositoryFromLocalAsync(notGit));
                Assert.Contains("not a git repository", ex2.Message, StringComparison.OrdinalIgnoreCase);

                // Git repo without origin remote
                var ex3 = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => rm.AddRepositoryFromLocalAsync(noOrigin));
                Assert.Contains("origin", ex3.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally { RepoManager.SetBaseDirForTesting(TestSetup.TestBaseDir); }
        }
        finally
        {
            ForceDeleteDirectory(notGit);
            ForceDeleteDirectory(noOrigin);
            ForceDeleteDirectory(testBaseDir);
        }
    }

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PolyPilot.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find repo root");
    }
}
