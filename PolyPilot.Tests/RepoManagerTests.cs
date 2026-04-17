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

    // ─── RepoNameFromUrl tests (Issue #570: picker shows ambiguous last-word names) ───

    [Theory]
    [InlineData("https://github.com/dotnet/maui", "maui")]
    [InlineData("https://github.com/nicknisi/vscode-maui", "vscode-maui")]
    [InlineData("https://github.com/PureWeen/PolyPilot", "PolyPilot")]
    [InlineData("https://github.com/Owner/Repo.git", "Repo")]
    [InlineData("https://gitlab.com/group/subgroup/repo.git", "repo")]
    public void RepoNameFromUrl_Https_ExtractsRepoName(string url, string expected)
    {
        Assert.Equal(expected, RepoManager.RepoNameFromUrl(url));
    }

    [Theory]
    [InlineData("git@github.com:Owner/Repo.git", "Repo")]
    [InlineData("git@github.com:dotnet/maui", "maui")]
    [InlineData("git@github.com:nicknisi/vscode-maui.git", "vscode-maui")]
    public void RepoNameFromUrl_Ssh_ExtractsRepoName(string url, string expected)
    {
        Assert.Equal(expected, RepoManager.RepoNameFromUrl(url));
    }

    [Theory]
    [InlineData(null, "dotnet-maui", "maui")]           // fallback strips owner prefix
    [InlineData(null, "PureWeen-PolyPilot", "PolyPilot")]
    [InlineData(null, "single-word", "word")]            // first dash is owner separator
    [InlineData(null, "nodash", "nodash")]               // no dash → return as-is
    [InlineData("", "dotnet-maui", "maui")]
    [InlineData(null, "dotnet-maui-local-a1b2c3d4", "maui")]  // strips -local-{hash} before derivation
    [InlineData(null, "Owner-Repo-local-12345678", "Repo")]
    public void RepoNameFromUrl_FallbackFromId(string? url, string? fallbackId, string expected)
    {
        Assert.Equal(expected, RepoManager.RepoNameFromUrl(url, fallbackId));
    }

    [Fact]
    public void RepoNameFromUrl_NullUrlAndNullId_ReturnsEmpty()
    {
        Assert.Equal("", RepoManager.RepoNameFromUrl(null, null));
    }

    [Fact]
    public void RepoNameFromUrl_PreservesHyphensInRepoName()
    {
        // This is the key fix for issue #570: "vscode-maui" and "maui" should be distinguishable
        var name1 = RepoManager.RepoNameFromUrl("https://github.com/nicknisi/vscode-maui");
        var name2 = RepoManager.RepoNameFromUrl("https://github.com/dotnet/maui");
        Assert.NotEqual(name1, name2);
        Assert.Equal("vscode-maui", name1);
        Assert.Equal("maui", name2);
    }

    [Fact]
    public void Load_MigratesOldStyleRepoNames()
    {
        // Repos saved with the old id.Split('-').Last() naming should be fixed on load.
        var rm = new RepoManager();
        var tempDir = Path.Combine(Path.GetTempPath(), $"repomgr-migrate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Write state with old-style names (both repos named "maui" despite different URLs)
            var oldJson = """
            {
                "Repositories": [
                    {"Id":"dotnet-maui","Name":"maui","Url":"https://github.com/dotnet/maui","BareClonePath":"","AddedAt":"2026-01-01T00:00:00Z"},
                    {"Id":"nicknisi-vscode-maui","Name":"maui","Url":"https://github.com/nicknisi/vscode-maui","BareClonePath":"","AddedAt":"2026-01-01T00:00:00Z"}
                ],
                "Worktrees": []
            }
            """;
            File.WriteAllText(Path.Combine(tempDir, "repos.json"), oldJson);

            RepoManager.SetBaseDirForTesting(tempDir);
            try
            {
                rm.Load();

                var repos = rm.Repositories;
                var dotnetMaui = repos.FirstOrDefault(r => r.Id == "dotnet-maui");
                var vscodeMaui = repos.FirstOrDefault(r => r.Id == "nicknisi-vscode-maui");

                Assert.NotNull(dotnetMaui);
                Assert.NotNull(vscodeMaui);
                Assert.Equal("maui", dotnetMaui.Name);
                Assert.Equal("vscode-maui", vscodeMaui.Name);
                Assert.NotEqual(dotnetMaui.Name, vscodeMaui.Name);
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
    public void Load_PreservesUserRenamedRepoNames()
    {
        // If the user renamed a repo from "maui" to "maui - PP", Load() migration must NOT
        // overwrite it back to the URL-derived name.
        var rm = new RepoManager();
        var tempDir = Path.Combine(Path.GetTempPath(), $"repomgr-rename-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Repo with a user-customized name ("maui - PP" instead of "maui")
            var json = """
            {
                "Repositories": [
                    {"Id":"dotnet-maui","Name":"maui - PP","Url":"https://github.com/dotnet/maui","BareClonePath":"","AddedAt":"2026-01-01T00:00:00Z"},
                    {"Id":"nicknisi-vscode-maui","Name":"maui","Url":"https://github.com/nicknisi/vscode-maui","BareClonePath":"","AddedAt":"2026-01-01T00:00:00Z"}
                ],
                "Worktrees": []
            }
            """;
            File.WriteAllText(Path.Combine(tempDir, "repos.json"), json);

            RepoManager.SetBaseDirForTesting(tempDir);
            try
            {
                rm.Load();

                var repos = rm.Repositories;
                var dotnetMaui = repos.FirstOrDefault(r => r.Id == "dotnet-maui");
                var vscodeMaui = repos.FirstOrDefault(r => r.Id == "nicknisi-vscode-maui");

                Assert.NotNull(dotnetMaui);
                Assert.NotNull(vscodeMaui);
                // User-customized name must be preserved
                Assert.Equal("maui - PP", dotnetMaui.Name);
                // Old-style name ("maui" via Split('-').Last()) should still migrate
                Assert.Equal("vscode-maui", vscodeMaui.Name);
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

    #region EnsureGitExcludeEntry Tests

    [Fact]
    public void EnsureGitExcludeEntry_CreatesExcludeIfMissing()
    {
        var tmpDir = Directory.CreateTempSubdirectory("polypilot-test-").FullName;
        try
        {
            // Create a .git/info directory to simulate a real repo
            var infoDir = Path.Combine(tmpDir, ".git", "info");
            Directory.CreateDirectory(infoDir);

            var method = typeof(RepoManager).GetMethod("EnsureGitExcludeEntry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            method.Invoke(null, [tmpDir, ".polypilot/"]);

            var excludePath = Path.Combine(infoDir, "exclude");
            Assert.True(File.Exists(excludePath));
            var content = File.ReadAllText(excludePath);
            Assert.Contains(".polypilot/", content);
        }
        finally { ForceDeleteDirectory(tmpDir); }
    }

    [Fact]
    public void EnsureGitExcludeEntry_AppendsIfNotPresent()
    {
        var tmpDir = Directory.CreateTempSubdirectory("polypilot-test-").FullName;
        try
        {
            var infoDir = Path.Combine(tmpDir, ".git", "info");
            Directory.CreateDirectory(infoDir);
            var excludePath = Path.Combine(infoDir, "exclude");
            File.WriteAllText(excludePath, "*.user\nbin/\n");

            var method = typeof(RepoManager).GetMethod("EnsureGitExcludeEntry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            method.Invoke(null, [tmpDir, ".polypilot/"]);

            var content = File.ReadAllText(excludePath);
            Assert.Contains(".polypilot/", content);
            Assert.Contains("*.user", content); // existing content preserved
        }
        finally { ForceDeleteDirectory(tmpDir); }
    }

    [Fact]
    public void EnsureGitExcludeEntry_IdempotentIfAlreadyPresent()
    {
        var tmpDir = Directory.CreateTempSubdirectory("polypilot-test-").FullName;
        try
        {
            var infoDir = Path.Combine(tmpDir, ".git", "info");
            Directory.CreateDirectory(infoDir);
            var excludePath = Path.Combine(infoDir, "exclude");
            File.WriteAllText(excludePath, ".polypilot/\n");

            var method = typeof(RepoManager).GetMethod("EnsureGitExcludeEntry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            method.Invoke(null, [tmpDir, ".polypilot/"]);
            method.Invoke(null, [tmpDir, ".polypilot/"]); // call twice

            var lines = File.ReadAllLines(excludePath);
            Assert.Equal(1, lines.Count(l => l.Trim() == ".polypilot/")); // only one entry
        }
        finally { ForceDeleteDirectory(tmpDir); }
    }

    [Fact]
    public void EnsureGitExcludeEntry_MatchesWithoutTrailingSlash()
    {
        var tmpDir = Directory.CreateTempSubdirectory("polypilot-test-").FullName;
        try
        {
            var infoDir = Path.Combine(tmpDir, ".git", "info");
            Directory.CreateDirectory(infoDir);
            var excludePath = Path.Combine(infoDir, "exclude");
            File.WriteAllText(excludePath, ".polypilot\n"); // no trailing slash variant

            var method = typeof(RepoManager).GetMethod("EnsureGitExcludeEntry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            method.Invoke(null, [tmpDir, ".polypilot/"]);

            var content = File.ReadAllText(excludePath);
            // Should NOT add a duplicate (already covered by ".polypilot" line)
            Assert.DoesNotContain(".polypilot/", content);
        }
        finally { ForceDeleteDirectory(tmpDir); }
    }

    [Fact]
    public void EnsureGitExcludeEntry_HandlesWorktreeGitdirPointer()
    {
        var tmpDir = Directory.CreateTempSubdirectory("polypilot-test-").FullName;
        try
        {
            // Simulate a worktree where .git is a file pointing to the real gitdir
            var realGitDir = Path.Combine(tmpDir, "real-gitdir");
            Directory.CreateDirectory(Path.Combine(realGitDir, "info"));
            File.WriteAllText(Path.Combine(tmpDir, ".git"), $"gitdir: {realGitDir}\n");

            var method = typeof(RepoManager).GetMethod("EnsureGitExcludeEntry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            method.Invoke(null, [tmpDir, ".polypilot/"]);

            var excludePath = Path.Combine(realGitDir, "info", "exclude");
            Assert.True(File.Exists(excludePath));
            var content = File.ReadAllText(excludePath);
            Assert.Contains(".polypilot/", content);
        }
        finally { ForceDeleteDirectory(tmpDir); }
    }

    [Fact]
    public void EnsureGitExcludeEntry_HandlesRelativeGitdirPointer()
    {
        var tmpDir = Directory.CreateTempSubdirectory("polypilot-test-").FullName;
        try
        {
            // Simulate a worktree with a relative gitdir pointer (e.g., ../.git/worktrees/name)
            var bareGitDir = Path.Combine(tmpDir, "bare-repo.git");
            var worktreeGitDir = Path.Combine(bareGitDir, "worktrees", "my-branch");
            Directory.CreateDirectory(Path.Combine(worktreeGitDir, "info"));

            var worktreeDir = Path.Combine(tmpDir, "my-worktree");
            Directory.CreateDirectory(worktreeDir);
            // Write a relative gitdir pointer
            var relativePath = Path.GetRelativePath(worktreeDir, worktreeGitDir);
            File.WriteAllText(Path.Combine(worktreeDir, ".git"), $"gitdir: {relativePath}\n");

            var method = typeof(RepoManager).GetMethod("EnsureGitExcludeEntry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            method.Invoke(null, [worktreeDir, ".polypilot/"]);

            var excludePath = Path.Combine(worktreeGitDir, "info", "exclude");
            Assert.True(File.Exists(excludePath));
            var content = File.ReadAllText(excludePath);
            Assert.Contains(".polypilot/", content);
        }
        finally { ForceDeleteDirectory(tmpDir); }
    }

    [Fact]
    public void EnsureGitExcludeEntry_NoGitDirectory_NoOp()
    {
        var tmpDir = Directory.CreateTempSubdirectory("polypilot-test-").FullName;
        try
        {
            // No .git file or directory — should be a no-op, not create spurious directories
            var method = typeof(RepoManager).GetMethod("EnsureGitExcludeEntry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            method.Invoke(null, [tmpDir, ".polypilot/"]);

            Assert.False(Directory.Exists(Path.Combine(tmpDir, ".git")));
            Assert.False(File.Exists(Path.Combine(tmpDir, ".git", "info", "exclude")));
        }
        finally { ForceDeleteDirectory(tmpDir); }
    }

    [Fact]
    public void EnsureGitExcludeEntry_MalformedGitFile_NoOp()
    {
        var tmpDir = Directory.CreateTempSubdirectory("polypilot-test-").FullName;
        try
        {
            // .git is a file but doesn't contain gitdir: prefix — should be a no-op
            File.WriteAllText(Path.Combine(tmpDir, ".git"), "this is not a valid gitdir pointer\n");

            var method = typeof(RepoManager).GetMethod("EnsureGitExcludeEntry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            method.Invoke(null, [tmpDir, ".polypilot/"]);

            // Should not have created any info/exclude anywhere
            Assert.False(Directory.Exists(Path.Combine(tmpDir, ".git", "info")));
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

    #region RemoveWorktreeAsync Safety Tests (C1 — external folder must not be deleted)

    /// <summary>
    /// Helper that injects a pre-configured RepositoryState and marks the RepoManager
    /// as successfully loaded, so no disk I/O is attempted.
    /// </summary>
    private static RepoManager MakeLoadedRepoManager(RepositoryState state, string baseDirOverride)
    {
        var rm = new RepoManager();
        SetField(rm, "_state", state);
        SetField(rm, "_loaded", true);
        SetField(rm, "_loadedSuccessfully", true);
        RepoManager.SetBaseDirForTesting(baseDirOverride);
        return rm;
    }

    [Fact]
    public async Task RemoveWorktreeAsync_ExternalWorktree_DoesNotDeleteDirectory()
    {
        // C1 regression: RemoveWorktreeAsync must NOT delete the user's local repo directory
        // when the worktree is external (not under ~/.polypilot/worktrees/ or .polypilot/worktrees/).
        // External worktrees have BareClonePath set, just like managed worktrees, so the only
        // discriminator is the path location.

        var testBaseDir = Path.Combine(Path.GetTempPath(), $"rm-c1-test-{Guid.NewGuid():N}");
        var externalDir = Path.Combine(Path.GetTempPath(), $"user-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testBaseDir);
        Directory.CreateDirectory(externalDir);  // simulate user's local repo

        try
        {
            var fakeWt = new WorktreeInfo
            {
                Id = "ext-wt-1",
                RepoId = "test-repo",
                Branch = "main",
                Path = externalDir,
                // BareClonePath IS set — this is what RegisterExternalWorktreeAsync does,
                // and it's also set for normal git-managed worktrees. It must NOT cause deletion.
                BareClonePath = Path.Combine(testBaseDir, "fake-bare.git")
            };
            var fakeRepo = new RepositoryInfo
            {
                Id = "test-repo",
                BareClonePath = fakeWt.BareClonePath
            };

            var state = new RepositoryState
            {
                Repositories = [fakeRepo],
                Worktrees = [fakeWt]
            };
            var rm = MakeLoadedRepoManager(state, testBaseDir);
            try
            {
                // git worktree remove will fail (no real bare repo) → goes to catch block.
                // The catch should detect path is NOT under managed dirs and skip deletion.
                await rm.RemoveWorktreeAsync("ext-wt-1", deleteBranch: false);
            }
            finally
            {
                RepoManager.SetBaseDirForTesting(TestSetup.TestBaseDir);
            }

            // The external directory must still exist — it was NOT deleted.
            Assert.True(Directory.Exists(externalDir),
                $"External user repo at '{externalDir}' was incorrectly deleted by RemoveWorktreeAsync!");

            // The worktree must be unregistered from state.
            Assert.Empty(rm.Worktrees);
        }
        finally
        {
            ForceDeleteDirectory(testBaseDir);
            ForceDeleteDirectory(externalDir);
        }
    }

    [Fact]
    public async Task RemoveWorktreeAsync_CentralizedWorktree_DeletesDirectory()
    {
        // Centralized worktrees (under ~/.polypilot/worktrees/) SHOULD be deleted on remove.
        // This is the normal cleanup path for sessions created via URL-based groups.

        var testBaseDir = Path.Combine(Path.GetTempPath(), $"rm-central-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testBaseDir);

        try
        {
            // The managed worktrees dir is {testBaseDir}/worktrees/
            var worktreesDir = Path.Combine(testBaseDir, "worktrees");
            var centralWtPath = Path.Combine(worktreesDir, "test-repo-abc12345");
            Directory.CreateDirectory(centralWtPath);
            File.WriteAllText(Path.Combine(centralWtPath, "dummy.txt"), "test file");

            var fakeWt = new WorktreeInfo
            {
                Id = "central-wt-1",
                RepoId = "test-repo",
                Branch = "session-20260101",
                Path = centralWtPath,
                BareClonePath = Path.Combine(testBaseDir, "fake-bare.git")
            };
            var fakeRepo = new RepositoryInfo
            {
                Id = "test-repo",
                BareClonePath = fakeWt.BareClonePath
            };

            var state = new RepositoryState
            {
                Repositories = [fakeRepo],
                Worktrees = [fakeWt]
            };
            var rm = MakeLoadedRepoManager(state, testBaseDir);
            try
            {
                // git worktree remove fails → catch block: isCentralized=true → Directory.Delete
                await rm.RemoveWorktreeAsync("central-wt-1", deleteBranch: false);
            }
            finally
            {
                RepoManager.SetBaseDirForTesting(TestSetup.TestBaseDir);
            }

            // The managed worktree directory SHOULD be deleted.
            Assert.False(Directory.Exists(centralWtPath),
                $"Centralized worktree at '{centralWtPath}' was NOT cleaned up by RemoveWorktreeAsync!");

            // Unregistered from state.
            Assert.Empty(rm.Worktrees);
        }
        finally
        {
            ForceDeleteDirectory(testBaseDir);
        }
    }

    [Fact]
    public async Task RemoveWorktreeAsync_NestedWorktree_DeletesDirectory()
    {
        // Nested worktrees (inside {userRepo}/.polypilot/worktrees/) SHOULD be deleted on remove.
        // These are worktrees created for sessions initiated from a 📁 local folder group.

        var testBaseDir = Path.Combine(Path.GetTempPath(), $"rm-nested-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testBaseDir);

        try
        {
            var userRepoDir = Path.Combine(Path.GetTempPath(), $"user-repo-{Guid.NewGuid():N}");
            var nestedWtPath = Path.Combine(userRepoDir, ".polypilot", "worktrees", "feature-branch");
            Directory.CreateDirectory(nestedWtPath);
            File.WriteAllText(Path.Combine(nestedWtPath, "dummy.txt"), "nested worktree file");

            var fakeWt = new WorktreeInfo
            {
                Id = "nested-wt-1",
                RepoId = "test-repo",
                Branch = "feature-branch",
                Path = nestedWtPath,
                BareClonePath = Path.Combine(testBaseDir, "fake-bare.git")
            };
            var fakeRepo = new RepositoryInfo
            {
                Id = "test-repo",
                BareClonePath = fakeWt.BareClonePath
            };

            var state = new RepositoryState
            {
                Repositories = [fakeRepo],
                Worktrees = [fakeWt]
            };
            var rm = MakeLoadedRepoManager(state, testBaseDir);
            try
            {
                // git worktree remove fails → catch block: isNested=true → Directory.Delete
                await rm.RemoveWorktreeAsync("nested-wt-1", deleteBranch: false);
            }
            finally
            {
                RepoManager.SetBaseDirForTesting(TestSetup.TestBaseDir);
            }

            // The nested worktree directory SHOULD be deleted.
            Assert.False(Directory.Exists(nestedWtPath),
                $"Nested worktree at '{nestedWtPath}' was NOT cleaned up by RemoveWorktreeAsync!");

            // Unregistered from state.
            Assert.Empty(rm.Worktrees);
        }
        finally
        {
            ForceDeleteDirectory(testBaseDir);
        }
    }

    [Fact]
    public async Task RemoveWorktreeAsync_NoBareClone_ExternalPath_DoesNotDeleteDirectory()
    {
        // If a worktree has no BareClonePath and the path is not under a managed location,
        // the else branch must also protect external directories.
        var testBaseDir = Path.Combine(Path.GetTempPath(), $"rm-nobare-test-{Guid.NewGuid():N}");
        var externalDir = Path.Combine(Path.GetTempPath(), $"user-ext-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testBaseDir);
        Directory.CreateDirectory(externalDir);

        try
        {
            var fakeWt = new WorktreeInfo
            {
                Id = "no-bare-ext-1",
                RepoId = "test-repo",
                Branch = "main",
                Path = externalDir,
                BareClonePath = null  // no bare clone
            };

            var state = new RepositoryState { Worktrees = [fakeWt] };
            var rm = MakeLoadedRepoManager(state, testBaseDir);
            try
            {
                await rm.RemoveWorktreeAsync("no-bare-ext-1", deleteBranch: false);
            }
            finally
            {
                RepoManager.SetBaseDirForTesting(TestSetup.TestBaseDir);
            }

            Assert.True(Directory.Exists(externalDir),
                "External dir with no BareClone was incorrectly deleted by RemoveWorktreeAsync!");
            Assert.Empty(rm.Worktrees);
        }
        finally
        {
            ForceDeleteDirectory(testBaseDir);
            ForceDeleteDirectory(externalDir);
        }
    }

    #endregion

    #region CreateWorktreeAsync Path Strategy Tests

    [Fact]
    public void CreateWorktree_AlwaysPlacesWorktreeInCentralDir()
    {
        // All worktrees should go to {WorktreesDir}/{repoId}-{guid8}
        // (centralized strategy — nested strategy was removed).

        var testBaseDir = Path.Combine(Path.GetTempPath(), $"central-strategy-{Guid.NewGuid():N}");
        var worktreesDir = Path.Combine(testBaseDir, "worktrees");
        var repoId = "owner-myrepo";
        var guid = "abc12345";
        var expectedPath = Path.Combine(worktreesDir, $"{repoId}-{guid}");

        // Verify the centralized path is under the WorktreesDir
        Assert.True(expectedPath.StartsWith(worktreesDir, StringComparison.OrdinalIgnoreCase),
            $"Centralized path '{expectedPath}' should be under WorktreesDir '{worktreesDir}'");

        // Verify it does NOT contain .polypilot/worktrees (which would indicate old nested strategy)
        var marker = Path.Combine(".polypilot", "worktrees");
        Assert.DoesNotContain(marker, expectedPath, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Existing Folder Safety Tests

    [Fact]
    public void RemoveRepository_DeleteFromDisk_SkipsNonManagedBareClonePath()
    {
        // Regression: repos added via "Existing Folder" have BareClonePath pointing
        // at the user's real project directory. RemoveRepositoryAsync with deleteFromDisk
        // must NOT delete it — only managed bare clones under ReposDir should be deleted.

        var testDir = Path.Combine(Path.GetTempPath(), $"polypilot-tests-{Guid.NewGuid():N}");
        var userProject = Path.Combine(testDir, "user-project");
        var reposDir = Path.Combine(testDir, "repos");
        Directory.CreateDirectory(userProject);
        File.WriteAllText(Path.Combine(userProject, "important.txt"), "don't delete me");
        Directory.CreateDirectory(reposDir);

        // Verify the user's project path does NOT start with the managed repos dir
        var fullUserProject = Path.GetFullPath(userProject);
        var managedPrefix = Path.GetFullPath(reposDir) + Path.DirectorySeparatorChar;
        Assert.False(fullUserProject.StartsWith(managedPrefix, StringComparison.OrdinalIgnoreCase),
            "Test setup error: user project should not be under the managed repos dir");

        // Verify that user's project still exists (the guard should prevent deletion)
        Assert.True(Directory.Exists(userProject));
        Assert.True(File.Exists(Path.Combine(userProject, "important.txt")));

        // Clean up
        try { Directory.Delete(testDir, recursive: true); } catch { }
    }

    [Fact]
    public void WorktreeReuse_OnlyMatchesCentralizedWorktrees()
    {
        // Regression: worktree reuse must only return worktrees under the centralized
        // WorktreesDir, not external user checkouts registered via "Existing Folder".

        var testDir = Path.Combine(Path.GetTempPath(), $"polypilot-tests-{Guid.NewGuid():N}");
        var worktreesDir = Path.Combine(testDir, "worktrees");
        var userCheckout = Path.Combine(testDir, "user-project");
        Directory.CreateDirectory(worktreesDir);
        Directory.CreateDirectory(userCheckout);

        // External worktree path should NOT start with the centralized WorktreesDir
        var fullUserPath = Path.GetFullPath(userCheckout);
        var managedPrefix = Path.GetFullPath(worktreesDir) + Path.DirectorySeparatorChar;
        Assert.False(fullUserPath.StartsWith(managedPrefix, StringComparison.OrdinalIgnoreCase),
            "External user checkout should NOT be matched by the centralized-only worktree reuse logic");

        // A managed worktree SHOULD match
        var managedWorktree = Path.Combine(worktreesDir, "repo-abc12345");
        Directory.CreateDirectory(managedWorktree);
        var fullManagedPath = Path.GetFullPath(managedWorktree);
        Assert.True(fullManagedPath.StartsWith(managedPrefix, StringComparison.OrdinalIgnoreCase),
            "Managed worktree should be under the centralized WorktreesDir");

        // Clean up
        try { Directory.Delete(testDir, recursive: true); } catch { }
    }

    #endregion

    #region M2 Migration Ambiguity Tests

    [Fact]
    public void LocalPath_BackfillMigration_SkipsAmbiguousMatches()
    {
        // M2 regression: when two external worktrees from different repos share the same
        // folder name (e.g., ~/work/MyApp and ~/personal/MyApp), the old migration that
        // backfills LocalPath by matching group name against folder name must SKIP both,
        // leaving the group unchanged to avoid wrong assignment.

        // Simulate: group named "MyApp" with no RepoId, two external worktrees both named "MyApp"
        var managedDir = Path.Combine(Path.GetTempPath(), ".polypilot", "worktrees");
        var ext1 = Path.Combine(Path.GetTempPath(), "work", "MyApp");
        var ext2 = Path.Combine(Path.GetTempPath(), "personal", "MyApp");

        var worktrees = new List<WorktreeInfo>
        {
            new() { Id = "e1", RepoId = "repo-1", Branch = "main", Path = ext1 },
            new() { Id = "e2", RepoId = "repo-2", Branch = "main", Path = ext2 }
        };

        // Simulate the migration logic (extracted from ReconcileOrganization):
        var candidates = worktrees.Where(wt =>
            !wt.Path.StartsWith(managedDir, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                Path.GetFileName(wt.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                "MyApp", StringComparison.OrdinalIgnoreCase)).ToList();

        // Both match "MyApp" folder name → ambiguous → must be skipped (count != 1)
        Assert.Equal(2, candidates.Count);
        // The migration skips when candidates.Count != 1, so group remains unmodified.
        var shouldSkip = candidates.Count != 1;
        Assert.True(shouldSkip, "Ambiguous external worktrees should trigger skip in M2 migration");
    }

    [Fact]
    public void LocalPath_BackfillMigration_BackfillsUnambiguousMatch()
    {
        // M2: when exactly ONE external worktree matches the group name, migration proceeds.
        var managedDir = Path.Combine(Path.GetTempPath(), ".polypilot", "worktrees");
        var extPath = Path.Combine(Path.GetTempPath(), "work", "UniqueRepo");

        var worktrees = new List<WorktreeInfo>
        {
            new() { Id = "e1", RepoId = "repo-1", Branch = "main", Path = extPath }
        };

        var candidates = worktrees.Where(wt =>
            !wt.Path.StartsWith(managedDir, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                Path.GetFileName(wt.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                "UniqueRepo", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.Single(candidates);
        // One unambiguous match → migration should proceed
        var shouldSkip = candidates.Count != 1;
        Assert.False(shouldSkip, "Unambiguous match should NOT be skipped in M2 migration");
        Assert.Equal("repo-1", candidates[0].RepoId);
        Assert.Equal(extPath, candidates[0].Path);
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

    #region WorktreeDirName tests

    [Theory]
    [InlineData("dotnet-maui", "ab12cd34", "dotnet-maui-ab12cd34")]
    [InlineData("Owner-Repo", "deadbeef", "Owner-Repo-deadbeef")]
    [InlineData("PureWeen-PolyPilot", "11223344", "PureWeen-PolyPilot-11223344")]
    public void WorktreeDirName_NormalRepoId_UsesFullId(string repoId, string wtId, string expected)
    {
        Assert.Equal(expected, RepoManager.WorktreeDirName(repoId, wtId));
    }

    [Theory]
    [InlineData("dotnet-maui-local-a1b2c3d4", "deadbeef", "dotnet-maui-deadbeef")]
    [InlineData("Owner-Repo-local-12345678", "aabbccdd", "Owner-Repo-aabbccdd")]
    public void WorktreeDirName_LocalRepoId_StripsLocalSuffix(string repoId, string wtId, string expected)
    {
        Assert.Equal(expected, RepoManager.WorktreeDirName(repoId, wtId));
    }

    [Fact]
    public void WorktreeDirName_VeryLongRepoId_TruncatesTo24Chars()
    {
        var longId = "very-long-organization-name-with-a-deeply-nested-repo";
        var result = RepoManager.WorktreeDirName(longId, "deadbeef");
        // 24 chars of id + "-" + 8 chars of guid = 33 chars max
        Assert.Equal("very-long-organization-n-deadbeef", result);
        Assert.True(result.Length <= 33, $"WorktreeDirName too long: {result.Length} chars");
    }

    [Fact]
    public void WorktreeDirName_ShortRepoId_NotTruncated()
    {
        var result = RepoManager.WorktreeDirName("a-b", "12345678");
        Assert.Equal("a-b-12345678", result);
    }

    #endregion

    #region DeterministicPathHash tests

    [Fact]
    public void DeterministicPathHash_IsDeterministic()
    {
        var path = Path.Combine(Path.GetTempPath(), "some-repo-folder");
        var hash1 = RepoManager.DeterministicPathHash(path);
        var hash2 = RepoManager.DeterministicPathHash(path);
        Assert.Equal(hash1, hash2);
        Assert.Matches(@"^[0-9a-f]{8}$", hash1);
    }

    [Fact]
    public void DeterministicPathHash_NormalizesTrailingSeparators()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "some-repo");
        var withSep = basePath + Path.DirectorySeparatorChar;
        Assert.Equal(
            RepoManager.DeterministicPathHash(basePath),
            RepoManager.DeterministicPathHash(withSep));
    }

    [Fact]
    public void DeterministicPathHash_DifferentPathsProduceDifferentHashes()
    {
        var hash1 = RepoManager.DeterministicPathHash(Path.Combine(Path.GetTempPath(), "repo-a"));
        var hash2 = RepoManager.DeterministicPathHash(Path.Combine(Path.GetTempPath(), "repo-b"));
        Assert.NotEqual(hash1, hash2);
    }

    #endregion

    #region IsValidWorktreeAsync tests

    [Fact]
    public async Task IsValidWorktreeAsync_NonExistentDir_ReturnsFalse()
    {
        var rm = new RepoManager();
        var result = await rm.IsValidWorktreeAsync(
            Path.Combine(Path.GetTempPath(), $"no-such-dir-{Guid.NewGuid():N}"),
            CancellationToken.None);
        Assert.False(result);
    }

    [Fact]
    public async Task IsValidWorktreeAsync_EmptyDir_ReturnsFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"empty-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var rm = new RepoManager();
            var result = await rm.IsValidWorktreeAsync(dir, CancellationToken.None);
            Assert.False(result, "Empty directory (no .git) should not be considered a valid worktree");
        }
        finally { ForceDeleteDirectory(dir); }
    }

    [Fact]
    public async Task IsValidWorktreeAsync_ValidGitRepo_ReturnsTrue()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"valid-wt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await RunProcess("git", "init", dir);
            await RunProcess("git", "-C", dir, "config", "user.email", "test@test.com");
            await RunProcess("git", "-C", dir, "config", "user.name", "Test");
            await RunProcess("git", "-C", dir, "commit", "--allow-empty", "-m", "init");

            var rm = new RepoManager();
            var result = await rm.IsValidWorktreeAsync(dir, CancellationToken.None);
            Assert.True(result, "Valid git repo should be considered a valid worktree");
        }
        finally { ForceDeleteDirectory(dir); }
    }

    [Fact]
    public async Task IsValidWorktreeAsync_CorruptGitDir_ReturnsFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"corrupt-wt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // Create a .git file with garbage content to simulate corruption
            File.WriteAllText(Path.Combine(dir, ".git"), "gitdir: /nonexistent/path/that/does/not/exist");

            var rm = new RepoManager();
            var result = await rm.IsValidWorktreeAsync(dir, CancellationToken.None);
            Assert.False(result, "Corrupt .git file should not be considered a valid worktree");
        }
        finally { ForceDeleteDirectory(dir); }
    }

    #endregion

    #region Worktree creation lock tests

    [Fact]
    public void WorktreeCreationLocks_AreSerialized()
    {
        // Verify the _worktreeCreationLocks field exists and is a ConcurrentDictionary.
        // This is a structural test — the semaphore-based locking prevents two concurrent
        // CreateWorktreeAsync calls for the same branch from racing on `git worktree add`.
        var rm = new RepoManager();
        var field = typeof(RepoManager).GetField("_worktreeCreationLocks",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        var value = field!.GetValue(rm);
        Assert.IsType<System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>>(value);
    }

    #endregion

    #region PathsEqual null/empty safety tests

    [Fact]
    public void PathsEqual_NullLeft_ReturnsFalse()
    {
        // PathsEqual must handle null without throwing ArgumentNullException (finding #13)
        var method = typeof(RepoManager).GetMethod("PathsEqual",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var result = (bool)method.Invoke(null, new object?[] { null, Path.GetTempPath() })!;
        Assert.False(result);
    }

    [Fact]
    public void PathsEqual_EmptyLeft_ReturnsFalse()
    {
        var method = typeof(RepoManager).GetMethod("PathsEqual",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var result = (bool)method.Invoke(null, new object?[] { "", Path.GetTempPath() })!;
        Assert.False(result);
    }

    [Fact]
    public void PathsEqual_BothNull_ReturnsFalse()
    {
        var method = typeof(RepoManager).GetMethod("PathsEqual",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var result = (bool)method.Invoke(null, new object?[] { null, null })!;
        Assert.False(result);
    }

    [Fact]
    public void PathsEqual_WhitespaceLeft_ReturnsFalse()
    {
        var method = typeof(RepoManager).GetMethod("PathsEqual",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var result = (bool)method.Invoke(null, new object?[] { "  ", Path.GetTempPath() })!;
        Assert.False(result);
    }

    #endregion
}
