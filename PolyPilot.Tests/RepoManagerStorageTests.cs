using System.Reflection;
using System.Text.Json;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

public class RepoManagerStorageTests
{
    private static RepoManager CreateRepoManagerWithState(RepositoryState state)
    {
        var rm = new RepoManager();
        var stateField = typeof(RepoManager).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var loadedField = typeof(RepoManager).GetField("_loaded", BindingFlags.NonPublic | BindingFlags.Instance)!;
        stateField.SetValue(rm, state);
        loadedField.SetValue(rm, true);
        return rm;
    }

    private static void InvokeBackfillWorktreeClonePaths(RepoManager rm, RepositoryInfo repo)
    {
        var method = typeof(RepoManager).GetMethod("BackfillWorktreeClonePaths", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(rm, new object[] { repo });
    }

    [Fact]
    public void BackfillWorktreeClonePaths_FillsMissingPaths()
    {
        var repo = new RepositoryInfo { Id = "owner-repo", BareClonePath = "/old/repos/owner-repo.git" };
        var state = new RepositoryState
        {
            Repositories = new List<RepositoryInfo> { repo },
            Worktrees = new List<WorktreeInfo>
            {
                new() { Id = "wt1", RepoId = "owner-repo", Path = "/old/worktrees/owner-repo-a1b2c3d4" },
                new() { Id = "wt2", RepoId = "other-repo", Path = "/old/worktrees/other-repo-e5f6g7h8" }
            }
        };
        var rm = CreateRepoManagerWithState(state);

        InvokeBackfillWorktreeClonePaths(rm, repo);

        Assert.Equal("/old/repos/owner-repo.git", state.Worktrees[0].BareClonePath);
        Assert.Null(state.Worktrees[1].BareClonePath);
    }

    [Fact]
    public void BackfillWorktreeClonePaths_DoesNotOverwriteExistingPath()
    {
        var repo = new RepositoryInfo { Id = "owner-repo", BareClonePath = "/new/repos/owner-repo.git" };
        var state = new RepositoryState
        {
            Repositories = new List<RepositoryInfo> { repo },
            Worktrees = new List<WorktreeInfo>
            {
                new()
                {
                    Id = "wt1",
                    RepoId = "owner-repo",
                    Path = "/old/worktrees/owner-repo-a1b2c3d4",
                    BareClonePath = "/old/repos/owner-repo.git"
                }
            }
        };
        var rm = CreateRepoManagerWithState(state);

        InvokeBackfillWorktreeClonePaths(rm, repo);

        Assert.Equal("/old/repos/owner-repo.git", state.Worktrees[0].BareClonePath);
    }

    [Fact]
    public void RepositoryState_WorktreeBareClonePath_RoundTripsJson()
    {
        var original = new RepositoryState
        {
            Repositories = new List<RepositoryInfo>
            {
                new() { Id = "owner-repo", BareClonePath = "/repos/owner-repo.git", Url = "https://github.com/owner/repo" }
            },
            Worktrees = new List<WorktreeInfo>
            {
                new() { Id = "wt1", RepoId = "owner-repo", Path = "/worktrees/owner-repo-1234", BareClonePath = "/repos/owner-repo.git" }
            }
        };

        var json = JsonSerializer.Serialize(original);
        var roundTrip = JsonSerializer.Deserialize<RepositoryState>(json);

        Assert.NotNull(roundTrip);
        Assert.Single(roundTrip!.Worktrees);
        Assert.Equal("/repos/owner-repo.git", roundTrip.Worktrees[0].BareClonePath);
    }

    [Fact]
    public async Task RecloneAllRepositoriesToCurrentRootAsync_NoRepos_Completes()
    {
        var rm = CreateRepoManagerWithState(new RepositoryState());
        await rm.RecloneAllRepositoriesToCurrentRootAsync();
        Assert.Empty(rm.Repositories);
    }

    [Fact]
    public async Task RecloneAllRepositoriesToCurrentRootAsync_NullBareClonePath_DoesNotThrow()
    {
        // Repos with null/empty BareClonePath should not crash EnsureRepoCloneInCurrentRootAsync.
        // They'll attempt a fresh clone (which will fail without network), but the error is caught
        // and remaining repos continue processing.
        var repoWithNullPath = new RepositoryInfo { Id = "owner-repo", BareClonePath = null!, Url = "https://github.com/owner/repo" };
        var state = new RepositoryState { Repositories = new List<RepositoryInfo> { repoWithNullPath } };
        var rm = CreateRepoManagerWithState(state);
        var progressMessages = new List<string>();

        // Should not throw ArgumentNullException even with null BareClonePath
        await rm.RecloneAllRepositoriesToCurrentRootAsync(msg => progressMessages.Add(msg));

        // A warning progress message is emitted when the clone fails (network unavailable in tests)
        Assert.Contains(progressMessages, m => m.StartsWith("⚠") || m.Contains("owner-repo") || m.Contains("[1/1]"));
    }

    [Fact]
    public void PathsEqual_IgnoresTrailingSeparator()
    {
        var method = typeof(RepoManager).GetMethod("PathsEqual", BindingFlags.NonPublic | BindingFlags.Static)!;
        var root = Path.Combine(Path.GetTempPath(), "polypilot-repo-root");
        var withTrailing = root + Path.DirectorySeparatorChar;
        var equal = (bool)method.Invoke(null, new object[] { withTrailing, root })!;
        Assert.True(equal);
    }
}
