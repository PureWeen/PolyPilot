using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

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

    // ── FindOrphanedWorktrees ──────────────────────────────────────────────

    [Fact]
    public void FindOrphanedWorktrees_ReturnsWorktreesNotInActiveSet()
    {
        var worktrees = new[]
        {
            new WorktreeInfo { Id = "wt1", Branch = "feat-a", Path = "/tmp/wt1" },
            new WorktreeInfo { Id = "wt2", Branch = "feat-b", Path = "/tmp/wt2" },
            new WorktreeInfo { Id = "wt3", Branch = "feat-c", Path = "/tmp/wt3" },
        };
        var activeIds = new[] { "wt1", "wt3" };

        var orphans = RepoManager.FindOrphanedWorktrees(worktrees, activeIds).ToList();

        Assert.Single(orphans);
        Assert.Equal("wt2", orphans[0].Id);
    }

    [Fact]
    public void FindOrphanedWorktrees_AllActive_ReturnsEmpty()
    {
        var worktrees = new[]
        {
            new WorktreeInfo { Id = "wt1", Branch = "feat-a", Path = "/tmp/wt1" },
            new WorktreeInfo { Id = "wt2", Branch = "feat-b", Path = "/tmp/wt2" },
        };

        var orphans = RepoManager.FindOrphanedWorktrees(worktrees, new[] { "wt1", "wt2" }).ToList();

        Assert.Empty(orphans);
    }

    [Fact]
    public void FindOrphanedWorktrees_NullIdsIgnored()
    {
        var worktrees = new[]
        {
            new WorktreeInfo { Id = "wt1", Branch = "feat-a", Path = "/tmp/wt1" },
        };
        // null entries in the active list must not be treated as a match
        var activeIds = new string?[] { null, null };

        var orphans = RepoManager.FindOrphanedWorktrees(worktrees, activeIds).ToList();

        Assert.Single(orphans);
        Assert.Equal("wt1", orphans[0].Id);
    }

    [Fact]
    public void FindOrphanedWorktrees_EmptyWorktrees_ReturnsEmpty()
    {
        var orphans = RepoManager.FindOrphanedWorktrees(
            Array.Empty<WorktreeInfo>(), new[] { "wt1" }).ToList();

        Assert.Empty(orphans);
    }

    [Fact]
    public void FindOrphanedWorktrees_EmptyActiveSet_ReturnsAllWorktrees()
    {
        var worktrees = new[]
        {
            new WorktreeInfo { Id = "wt1", Branch = "feat-a", Path = "/tmp/wt1" },
            new WorktreeInfo { Id = "wt2", Branch = "feat-b", Path = "/tmp/wt2" },
        };

        var orphans = RepoManager.FindOrphanedWorktrees(worktrees, Array.Empty<string?>()).ToList();

        Assert.Equal(2, orphans.Count);
    }
}
