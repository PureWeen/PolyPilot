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
    [InlineData("owner/repo.js")]  // has a dot — not treated as shorthand
    [InlineData("a/b/c")]          // 3 segments — not shorthand
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
            Directory.Delete(tempDir, true);
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
            Directory.Delete(tempDir, true);
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
}
