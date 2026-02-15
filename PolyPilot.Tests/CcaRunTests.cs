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

    [Fact]
    public void IsPrCompleted_Merged_ReturnsTrue()
    {
        var run = new CcaRun { PrState = "merged" };
        Assert.True(run.IsPrCompleted);
    }

    [Fact]
    public void IsPrCompleted_Closed_ReturnsTrue()
    {
        var run = new CcaRun { PrState = "closed" };
        Assert.True(run.IsPrCompleted);
    }

    [Fact]
    public void IsPrCompleted_Open_ReturnsFalse()
    {
        var run = new CcaRun { PrState = "open" };
        Assert.False(run.IsPrCompleted);
    }

    [Fact]
    public void IsPrCompleted_NoPr_ReturnsFalse()
    {
        var run = new CcaRun { PrState = null };
        Assert.False(run.IsPrCompleted);
    }

    [Fact]
    public void ClickUrl_WithPrUrl_ReturnsPrUrl()
    {
        var run = new CcaRun
        {
            HtmlUrl = "https://github.com/owner/repo/actions/runs/123",
            PrUrl = "https://github.com/owner/repo/pull/42"
        };
        Assert.Equal("https://github.com/owner/repo/pull/42", run.ClickUrl);
    }

    [Fact]
    public void ClickUrl_WithoutPrUrl_ReturnsHtmlUrl()
    {
        var run = new CcaRun
        {
            HtmlUrl = "https://github.com/owner/repo/actions/runs/123",
            PrUrl = null
        };
        Assert.Equal("https://github.com/owner/repo/actions/runs/123", run.ClickUrl);
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
