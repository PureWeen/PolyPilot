using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

[Collection("BaseDir")]
public class RepoManagerTests
{
    [Theory]
    [InlineData("https://github.com/Owner/Repo.git", "Owner-Repo")]
    [InlineData("https://github.com/Owner/Repo", "Owner-Repo")]
    [InlineData("https://github.com/dotnet/maui.git", "dotnet-maui")]
    [InlineData("https://gitlab.com/group/subgroup/repo.git", "group-subgroup-repo")]
    [InlineData("https://github.com/owner/my.git-repo.git", "owner-my.git-repo")]  // .git in middle preserved
    public void RepoIdFromUrl_Https_ExtractsCorrectId(string url, string expected)
    {
        Assert.Equal(expected, RepoManager.RepoIdFromUrl(url));
    }

    [Theory]
    [InlineData("git@github.com:Owner/Repo.git", "Owner-Repo")]
    [InlineData("git@github.com:Owner/Repo", "Owner-Repo")]
    public void RepoIdFromUrl_SshColon_ExtractsCorrectId(string url, string expected)
    {
        Assert.Equal(expected, RepoManager.RepoIdFromUrl(url));
    }

    [Theory]
    [InlineData("ssh://git@github.com/Owner/Repo.git", "Owner-Repo")]
    [InlineData("https://user@github.com/Owner/Repo.git", "Owner-Repo")]
    [InlineData("https://user:token@github.com/Owner/Repo", "Owner-Repo")]
    public void RepoIdFromUrl_ProtocolWithCredentials_ExtractsCorrectId(string url, string expected)
    {
        Assert.Equal(expected, RepoManager.RepoIdFromUrl(url));
    }

    [Theory]
    [InlineData("dotnet/maui", "https://github.com/dotnet/maui")]
    [InlineData("PureWeen/PolyPilot", "https://github.com/PureWeen/PolyPilot")]
    [InlineData("mono/SkiaSharp.Extended", "https://github.com/mono/SkiaSharp.Extended")]
    [InlineData("owner/repo.js", "https://github.com/owner/repo.js")]
    public void NormalizeRepoUrl_Shorthand_ExpandsToGitHub(string input, string expected)
    {
        Assert.Equal(expected, RepoManager.NormalizeRepoUrl(input));
    }

    [Theory]
    [InlineData("https://github.com/a/b")]
    [InlineData("git@github.com:a/b.git")]
    public void NormalizeRepoUrl_FullUrl_PassesThrough(string url)
    {
        Assert.Equal(url, RepoManager.NormalizeRepoUrl(url));
    }

    [Theory]
    [InlineData("a/b/c")]          // 3 segments — not shorthand
    [InlineData("gitlab.com/myrepo")]   // owner has dot → not shorthand (hostname-like)
    [InlineData("192.168.1.1/admin")]   // owner has dot → not shorthand (IP address)
    public void NormalizeRepoUrl_NonShorthand_PassesThrough(string input)
    {
        Assert.Equal(input, RepoManager.NormalizeRepoUrl(input));
    }

    #region Save Guard Tests (Review Finding #9)

    private static readonly System.Reflection.BindingFlags NonPublic =
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

    private static void SetField(object obj, string name, object value)
    {
        var field = obj.GetType().GetField(name, NonPublic)!;
        field.SetValue(obj, value);
    }

    private static T GetField<T>(object obj, string name)
    {
        var field = obj.GetType().GetField(name, NonPublic)!;
        return (T)field.GetValue(obj)!;
    }

    private static void InvokeSave(RepoManager rm)
    {
        var method = typeof(RepoManager).GetMethod("Save", NonPublic)!;
        method.Invoke(rm, null);
    }

    /// <summary>
    /// Deletes a directory tree including files marked read-only.
    /// Git creates read-only object files on Windows, which causes plain
    /// Directory.Delete to throw UnauthorizedAccessException in test cleanup.
    /// </summary>
    private static void ForceDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;
        foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(path, true);
    }

    [Fact]
    public void Save_AfterFailedLoad_DoesNotOverwriteWithEmptyState()
    {
        var rm = new RepoManager();
        var tempDir = Path.Combine(Path.GetTempPath(), $"repomgr-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var stateFile = Path.Combine(tempDir, "repos.json");

        try
        {
            // Write valid state to file
            var validJson = """{"Repositories":[{"Id":"test-1","Name":"TestRepo","Url":"https://example.com","BareClonePath":"","AddedAt":"2026-01-01T00:00:00Z"}],"Worktrees":[]}""";
            File.WriteAllText(stateFile, validJson);

            // Simulate failed load: _loaded=true, _loadedSuccessfully=false, empty state
            SetField(rm, "_loaded", true);
            SetField(rm, "_loadedSuccessfully", false);
            SetField(rm, "_state", new RepositoryState());

            // Redirect RepoManager to our temp dir (safe — uses the lock-protected setter)
            RepoManager.SetBaseDirForTesting(tempDir);
            try
            {
                // Save should be blocked — empty state after failed load
                InvokeSave(rm);

                // Original file should still have our repo
                var content = File.ReadAllText(stateFile);
                Assert.Contains("test-1", content);
            }
            finally
            {
                RepoManager.SetBaseDirForTesting(TestSetup.TestBaseDir);
            }
        }
        finally
        {
            ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Save_AfterSuccessfulLoad_PersistsEmptyState()
    {
        var rm = new RepoManager();
        var tempDir = Path.Combine(Path.GetTempPath(), $"repomgr-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Simulate successful load then all repos removed
            SetField(rm, "_loaded", true);
            SetField(rm, "_loadedSuccessfully", true);
            SetField(rm, "_state", new RepositoryState());

            // Redirect RepoManager to our temp dir (safe — uses the lock-protected setter)
            RepoManager.SetBaseDirForTesting(tempDir);
            try
            {
                // Save should proceed — load was successful, intentional empty state
                InvokeSave(rm);

                var stateFile = Path.Combine(tempDir, "repos.json");
                var content = File.ReadAllText(stateFile);
                Assert.Contains("Repositories", content);
                Assert.DoesNotContain("test-1", content);
            }
            finally
            {
                RepoManager.SetBaseDirForTesting(TestSetup.TestBaseDir);
            }
        }
        finally
        {
            ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Repositories_ReturnsCopy_ThreadSafe()
    {
        var rm = new RepoManager();
        // Inject state with some repos
        SetField(rm, "_loaded", true);
        SetField(rm, "_loadedSuccessfully", true);
        var state = new RepositoryState
        {
            Repositories = new() { new() { Id = "r1", Name = "R1" }, new() { Id = "r2", Name = "R2" } }
        };
        SetField(rm, "_state", state);

        // Get a snapshot
        var repos = rm.Repositories;
        Assert.Equal(2, repos.Count);

        // Mutate the underlying state
        state.Repositories.RemoveAll(r => r.Id == "r1");

        // Snapshot should be unaffected (it's a copy)
        Assert.Equal(2, repos.Count);
    }

    #endregion

    #region Self-Healing Tests

    [Fact]
    public void HealMissingRepos_DiscoversUntracked_BareClones()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repomgr-heal-{Guid.NewGuid():N}");
        var reposDir = Path.Combine(tempDir, "repos");
        Directory.CreateDirectory(reposDir);

        try
        {
            // Create a fake bare clone directory with a git config
            var bareDir = Path.Combine(reposDir, "Owner-Repo.git");
            Directory.CreateDirectory(bareDir);
            File.WriteAllText(Path.Combine(bareDir, "config"),
                "[remote \"origin\"]\n\turl = https://github.com/Owner/Repo\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n");

            var rm = new RepoManager();
            SetField(rm, "_loaded", true);
            SetField(rm, "_loadedSuccessfully", true);
            SetField(rm, "_state", new RepositoryState());

            RepoManager.SetBaseDirForTesting(tempDir);
            try
            {
                var healed = rm.HealMissingRepos();

                Assert.Equal(1, healed);
                var repos = rm.Repositories;
                Assert.Single(repos);
                Assert.Equal("Owner-Repo", repos[0].Id);
                Assert.Equal("Repo", repos[0].Name);
                Assert.Equal("https://github.com/Owner/Repo", repos[0].Url);
                Assert.Equal(bareDir, repos[0].BareClonePath);
            }
            finally
            {
                RepoManager.SetBaseDirForTesting(TestSetup.TestBaseDir);
            }
        }
        finally
        {
            ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void HealMissingRepos_SkipsAlreadyTrackedRepos()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repomgr-heal-{Guid.NewGuid():N}");
        var reposDir = Path.Combine(tempDir, "repos");
        Directory.CreateDirectory(reposDir);

        try
        {
            // Create a bare clone that IS tracked
            var bareDir = Path.Combine(reposDir, "Owner-Repo.git");
            Directory.CreateDirectory(bareDir);
            File.WriteAllText(Path.Combine(bareDir, "config"),
                "[remote \"origin\"]\n\turl = https://github.com/Owner/Repo\n");

            var state = new RepositoryState();
            state.Repositories.Add(new RepositoryInfo
            {
                Id = "Owner-Repo",
                Name = "Repo",
                Url = "https://github.com/Owner/Repo",
                BareClonePath = bareDir
            });

            var rm = new RepoManager();
            SetField(rm, "_loaded", true);
            SetField(rm, "_loadedSuccessfully", true);
            SetField(rm, "_state", state);

            RepoManager.SetBaseDirForTesting(tempDir);
            try
            {
                var healed = rm.HealMissingRepos();
                Assert.Equal(0, healed);
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
        }
    }

    [Fact]
    public void HealMissingRepos_MultipleUntracked_AllDiscovered()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repomgr-heal-{Guid.NewGuid():N}");
        var reposDir = Path.Combine(tempDir, "repos");
        Directory.CreateDirectory(reposDir);

        try
        {
            // Create 3 bare clones
            foreach (var name in new[] { "dotnet-maui.git", "PureWeen-PolyPilot.git", "github-sdk.git" })
            {
                var dir = Path.Combine(reposDir, name);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "config"),
                    $"[remote \"origin\"]\n\turl = https://github.com/test/{name.Replace(".git", "")}\n");
            }

            // Only one is tracked
            var state = new RepositoryState();
            state.Repositories.Add(new RepositoryInfo { Id = "dotnet-maui", Name = "maui", Url = "https://github.com/dotnet/maui" });

            var rm = new RepoManager();
            SetField(rm, "_loaded", true);
            SetField(rm, "_loadedSuccessfully", true);
            SetField(rm, "_state", state);

            RepoManager.SetBaseDirForTesting(tempDir);
            try
            {
                var healed = rm.HealMissingRepos();
                Assert.Equal(2, healed); // PureWeen-PolyPilot and github-sdk
                Assert.Equal(3, rm.Repositories.Count);
            }
            finally
            {
                RepoManager.SetBaseDirForTesting(TestSetup.TestBaseDir);
            }
        }
        finally
        {
            ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Load_WithCorruptedState_HealsFromDisk()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repomgr-heal-{Guid.NewGuid():N}");
        var reposDir = Path.Combine(tempDir, "repos");
        Directory.CreateDirectory(reposDir);

        try
        {
            // Create a bare clone on disk
            var bareDir = Path.Combine(reposDir, "Owner-Repo.git");
            Directory.CreateDirectory(bareDir);
            File.WriteAllText(Path.Combine(bareDir, "config"),
                "[remote \"origin\"]\n\turl = https://github.com/Owner/Repo\n");

            // Write corrupted repos.json (test data — like the actual bug)
            var stateFile = Path.Combine(tempDir, "repos.json");
            File.WriteAllText(stateFile, """{"Repositories":[{"Id":"repo-1","Name":"MyRepo","Url":"https://github.com/test/repo","BareClonePath":"","AddedAt":"2026-02-27T01:23:18Z"}],"Worktrees":[]}""");

            RepoManager.SetBaseDirForTesting(tempDir);
            try
            {
                var rm = new RepoManager();
                rm.Load();

                var repos = rm.Repositories;
                // Should have both the corrupted entry AND the healed one
                Assert.Equal(2, repos.Count);
                Assert.Contains(repos, r => r.Id == "repo-1"); // original corrupted entry
                Assert.Contains(repos, r => r.Id == "Owner-Repo"); // healed from disk
            }
            finally
            {
                RepoManager.SetBaseDirForTesting(TestSetup.TestBaseDir);
            }
        }
        finally
        {
            ForceDeleteDirectory(tempDir);
        }
    }

    #endregion

    #region AddRepositoryFromLocalAsync Validation Tests

    [Fact]
    public async Task AddRepositoryFromLocal_NonExistentFolder_ThrowsWithClearMessage()
    {
        var rm = new RepoManager();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rm.AddRepositoryFromLocalAsync("/this/path/does/not/exist"));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task AddRepositoryFromLocal_FolderWithNoGit_ThrowsWithClearMessage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"not-a-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var rm = new RepoManager();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => rm.AddRepositoryFromLocalAsync(tempDir));
            Assert.Contains("not a git repository", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task AddRepositoryFromLocal_GitRepoWithNoOrigin_ThrowsWithClearMessage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"no-origin-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Initialize a real git repo with no remotes
            await RunProcess("git", "init", tempDir);
            var rm = new RepoManager();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => rm.AddRepositoryFromLocalAsync(tempDir));
            Assert.Contains("origin", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task RegisterExternalWorktree_AddsWorktreeToState()
    {
        // STRUCTURAL: Verifies that RegisterExternalWorktreeAsync stores a WorktreeInfo
        // and fires OnStateChanged, so the sidebar updates after adding a local folder.
        var tempDir = Path.Combine(Path.GetTempPath(), $"ext-wt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            await RunProcess("git", "init", tempDir);
            await RunProcess("git", "-C", tempDir, "commit", "--allow-empty", "-m", "init");

            var rm = new RepoManager();
            RepoManager.SetBaseDirForTesting(Path.Combine(Path.GetTempPath(), $"rmtest-{Guid.NewGuid():N}"));
            try
            {
                // Seed a fake repo entry (skip network)
                var repoId = "test-owner-testrepo";
                var fakeRepo = new RepositoryInfo { Id = repoId, Name = "testrepo", Url = "https://github.com/test-owner/testrepo.git" };
                var stateChangedFired = false;
                rm.OnStateChanged += () => stateChangedFired = true;

                // Directly inject state (bypass load)
                var stateField = typeof(RepoManager).GetField("_state", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                var loadedField = typeof(RepoManager).GetField("_loaded", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                var successField = typeof(RepoManager).GetField("_loadedSuccessfully", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                var state = new RepositoryState { Repositories = [fakeRepo] };
                stateField.SetValue(rm, state);
                loadedField.SetValue(rm, true);
                successField.SetValue(rm, true);

                await rm.RegisterExternalWorktreeAsync(fakeRepo, tempDir, default);

                Assert.True(stateChangedFired, "OnStateChanged must fire so the sidebar refreshes");
                Assert.Single(rm.Worktrees, w => w.RepoId == repoId && PathsEqual(w.Path, tempDir));
            }
            finally
            {
                RepoManager.SetBaseDirForTesting(TestSetup.TestBaseDir);
            }
        }
        finally
        {
            ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task RegisterExternalWorktree_Idempotent()
    {
        // Adding the same path twice should not create duplicate worktree entries.
        var tempDir = Path.Combine(Path.GetTempPath(), $"ext-wt-idem-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            await RunProcess("git", "init", tempDir);
            await RunProcess("git", "-C", tempDir, "commit", "--allow-empty", "-m", "init");

            var rm = new RepoManager();
            RepoManager.SetBaseDirForTesting(Path.Combine(Path.GetTempPath(), $"rmtest2-{Guid.NewGuid():N}"));
            try
            {
                var fakeRepo = new RepositoryInfo { Id = "owner-repo", Name = "repo", Url = "https://github.com/owner/repo.git" };
                var stateField = typeof(RepoManager).GetField("_state", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                var loadedField = typeof(RepoManager).GetField("_loaded", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                var successField = typeof(RepoManager).GetField("_loadedSuccessfully", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                var state = new RepositoryState { Repositories = [fakeRepo] };
                stateField.SetValue(rm, state);
                loadedField.SetValue(rm, true);
                successField.SetValue(rm, true);

                await rm.RegisterExternalWorktreeAsync(fakeRepo, tempDir, default);
                await rm.RegisterExternalWorktreeAsync(fakeRepo, tempDir, default); // second call

                Assert.Single(rm.Worktrees); // exactly one entry
            }
            finally
            {
                RepoManager.SetBaseDirForTesting(TestSetup.TestBaseDir);
            }
        }
        finally
        {
            ForceDeleteDirectory(tempDir);
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        var l = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var r = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(l, r, StringComparison.OrdinalIgnoreCase);
    }

    #region EnsureGitIgnoreEntry Tests

    [Fact]
    public void EnsureGitIgnoreEntry_CreatesGitIgnoreIfMissing()
    {
        var tmpDir = Directory.CreateTempSubdirectory("polypilot-test-").FullName;
        try
        {
            var method = typeof(RepoManager).GetMethod("EnsureGitIgnoreEntry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            method.Invoke(null, [tmpDir, ".polypilot/"]);

            var gitignorePath = Path.Combine(tmpDir, ".gitignore");
            Assert.True(File.Exists(gitignorePath));
            var content = File.ReadAllText(gitignorePath);
            Assert.Contains(".polypilot/", content);
        }
        finally { ForceDeleteDirectory(tmpDir); }
    }

    [Fact]
    public void EnsureGitIgnoreEntry_AppendsIfNotPresent()
    {
        var tmpDir = Directory.CreateTempSubdirectory("polypilot-test-").FullName;
        try
        {
            var gitignorePath = Path.Combine(tmpDir, ".gitignore");
            File.WriteAllText(gitignorePath, "*.user\nbin/\n");

            var method = typeof(RepoManager).GetMethod("EnsureGitIgnoreEntry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            method.Invoke(null, [tmpDir, ".polypilot/"]);

            var content = File.ReadAllText(gitignorePath);
            Assert.Contains(".polypilot/", content);
            Assert.Contains("*.user", content); // existing content preserved
        }
        finally { ForceDeleteDirectory(tmpDir); }
    }

    [Fact]
    public void EnsureGitIgnoreEntry_IdempotentIfAlreadyPresent()
    {
        var tmpDir = Directory.CreateTempSubdirectory("polypilot-test-").FullName;
        try
        {
            var gitignorePath = Path.Combine(tmpDir, ".gitignore");
            File.WriteAllText(gitignorePath, ".polypilot/\n");

            var method = typeof(RepoManager).GetMethod("EnsureGitIgnoreEntry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            method.Invoke(null, [tmpDir, ".polypilot/"]);
            method.Invoke(null, [tmpDir, ".polypilot/"]); // call twice

            var lines = File.ReadAllLines(gitignorePath);
            Assert.Equal(1, lines.Count(l => l.Trim() == ".polypilot/")); // only one entry
        }
        finally { ForceDeleteDirectory(tmpDir); }
    }

    [Fact]
    public void EnsureGitIgnoreEntry_MatchesWithoutTrailingSlash()
    {
        var tmpDir = Directory.CreateTempSubdirectory("polypilot-test-").FullName;
        try
        {
            var gitignorePath = Path.Combine(tmpDir, ".gitignore");
            File.WriteAllText(gitignorePath, ".polypilot\n"); // no trailing slash variant

            var method = typeof(RepoManager).GetMethod("EnsureGitIgnoreEntry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            method.Invoke(null, [tmpDir, ".polypilot/"]);

            var content = File.ReadAllText(gitignorePath);
            // Should NOT add a duplicate (already covered by ".polypilot" line)
            Assert.DoesNotContain(".polypilot/", content);
        }
        finally { ForceDeleteDirectory(tmpDir); }
    }

    #endregion

    #region Nested Worktree Path Traversal Tests

    [Theory]
    [InlineData("../../evil")]
    [InlineData("../sibling")]
    [InlineData("foo/../../escape")]
    [InlineData("")]        // empty branch name resolves to repoWorktreesDir itself
    [InlineData(".")]       // dot resolves to repoWorktreesDir itself
    public void CreateWorktree_PathTraversal_InBranchName_IsRejected(string maliciousBranch)
    {
        // Simulate what CreateWorktreeAsync does: combine repoWorktreesDir + branchName then GetFullPath
        var fakeRepoDir = Path.Combine(Path.GetTempPath(), "fake-repo");
        var repoWorktreesDir = Path.Combine(fakeRepoDir, ".polypilot", "worktrees");
        var worktreePath = Path.Combine(repoWorktreesDir, maliciousBranch);
        var resolved = Path.GetFullPath(worktreePath);
        var managedBase = Path.GetFullPath(repoWorktreesDir) + Path.DirectorySeparatorChar;

        // Production guard (single condition): resolved must start with managedBase.
        // "" and "." both resolve to repoWorktreesDir itself, which does NOT start with
        // repoWorktreesDir + separator — so they are correctly rejected.
        var wouldEscape = !resolved.StartsWith(managedBase, StringComparison.OrdinalIgnoreCase);

        Assert.True(wouldEscape, $"Branch '{maliciousBranch}' should escape the managed dir but guard says it doesn't. Resolved: {resolved}");
    }

    [Theory]
    [InlineData("my-feature")]
    [InlineData("feature/login")]
    [InlineData("fix.typo")]
    public void CreateWorktree_ValidBranchName_StaysInsideDir(string safeBranch)
    {
        var fakeRepoDir = Path.Combine(Path.GetTempPath(), "fake-repo");
        var repoWorktreesDir = Path.Combine(fakeRepoDir, ".polypilot", "worktrees");
        var worktreePath = Path.Combine(repoWorktreesDir, safeBranch);
        var resolved = Path.GetFullPath(worktreePath);
        var managedBase = Path.GetFullPath(repoWorktreesDir) + Path.DirectorySeparatorChar;

        // Production guard (single condition)
        var wouldEscape = !resolved.StartsWith(managedBase, StringComparison.OrdinalIgnoreCase);

        Assert.False(wouldEscape, $"Branch '{safeBranch}' should NOT escape the managed dir. Resolved: {resolved}");
    }

    #endregion

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
        // Set EnableRaisingEvents and subscribe Exited BEFORE Start() to avoid the race
        // where a fast process exits between Start() and EnableRaisingEvents = true.
        var p = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Exited += (_, _) =>
        {
            if (p.ExitCode == 0) tcs.TrySetResult();
            else tcs.TrySetException(new Exception($"{exe} exited with {p.ExitCode}"));
        };
        p.Start();
        return tcs.Task;
    }

    #endregion
}

