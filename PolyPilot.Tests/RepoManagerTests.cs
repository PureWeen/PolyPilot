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
}
