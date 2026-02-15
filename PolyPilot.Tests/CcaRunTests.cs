using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

public class CcaRunTests
{
    [Fact]
    public void IsCodingAgent_RunningCopilotCodingAgent_ReturnsTrue()
    {
        var run = new CcaRun { Name = "Running Copilot coding agent" };
        Assert.True(run.IsCodingAgent);
    }

    [Fact]
    public void IsCodingAgent_AddressingComment_ReturnsFalse()
    {
        var run = new CcaRun { Name = "Addressing comment on PR #106" };
        Assert.False(run.IsCodingAgent);
    }

    [Fact]
    public void IsCodingAgent_EmptyName_ReturnsFalse()
    {
        var run = new CcaRun { Name = "" };
        Assert.False(run.IsCodingAgent);
    }

    [Fact]
    public void IsCodingAgent_CaseInsensitive()
    {
        var run = new CcaRun { Name = "running copilot coding agent" };
        Assert.True(run.IsCodingAgent);
    }

    [Fact]
    public void IsActive_InProgress_ReturnsTrue()
    {
        var run = new CcaRun { Status = "in_progress" };
        Assert.True(run.IsActive);
    }

    [Fact]
    public void IsActive_Completed_ReturnsFalse()
    {
        var run = new CcaRun { Status = "completed" };
        Assert.False(run.IsActive);
    }

    [Theory]
    [InlineData("https://github.com/owner/repo.git", "owner/repo")]
    [InlineData("https://github.com/owner/repo", "owner/repo")]
    [InlineData("git@github.com:owner/repo.git", "owner/repo")]
    [InlineData("git@github.com:owner/repo", "owner/repo")]
    [InlineData("owner/repo", "owner/repo")]
    public void ExtractOwnerRepo_ValidUrls(string gitUrl, string expected)
    {
        Assert.Equal(expected, CcaRunService.ExtractOwnerRepo(gitUrl));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("https://github.com/onlyone")]
    public void ExtractOwnerRepo_InvalidUrls_ReturnsNull(string gitUrl)
    {
        Assert.Null(CcaRunService.ExtractOwnerRepo(gitUrl));
    }
}
