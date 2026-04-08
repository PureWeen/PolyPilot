using PolyPilot.Services;

namespace PolyPilot.Tests;

public class PrLinkServiceTests
{
    [Theory]
    [InlineData("https://github.com/PureWeen/PolyPilot/pull/507", 507)]
    [InlineData("https://github.com/PureWeen/PolyPilot/pull/507/files", 507)]
    [InlineData("https://github.contoso.com/org/repo/pulls/42", 42)]
    public void ExtractPrNumber_ValidUrls_ReturnsPrNumber(string url, int expected)
    {
        Assert.Equal(expected, PrLinkService.ExtractPrNumber(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("https://github.com/PureWeen/PolyPilot/issues/507")]
    [InlineData("https://github.com/PureWeen/PolyPilot/pull/not-a-number")]
    public void ExtractPrNumber_InvalidUrls_ReturnsNull(string? url)
    {
        Assert.Null(PrLinkService.ExtractPrNumber(url));
    }

    [Fact]
    public async Task GetPrDiffAsync_Success_ReturnsTrimmedDiffAndUsesExpectedArgs()
    {
        var service = new TestPrLinkService((workingDirectory, _, args) =>
            Task.FromResult<(string Output, string Error, int ExitCode)>(("""
                diff --git a/test.cs b/test.cs
                --- a/test.cs
                +++ b/test.cs
                @@ -1 +1 @@
                -old
                +new
                """, "", 0)));

        var diff = await service.GetPrDiffAsync("/tmp/repo", 507);

        Assert.Contains("diff --git a/test.cs b/test.cs", diff);
        Assert.Equal("/tmp/repo", service.LastWorkingDirectory);
        Assert.Equal(["pr", "diff", "507", "--color", "never"], service.LastArgs);
    }

    [Fact]
    public async Task GetPrDiffAsync_GhFailure_ThrowsMeaningfulError()
    {
        var service = new TestPrLinkService((_, _, _) =>
            Task.FromResult<(string Output, string Error, int ExitCode)>(("", "gh: not logged in", 1)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetPrDiffAsync("/tmp/repo", 507));

        Assert.Contains("not logged in", ex.Message);
    }

    [Fact]
    public async Task GetPrDiffAsync_EmptyDiff_Throws()
    {
        var service = new TestPrLinkService((_, _, _) =>
            Task.FromResult<(string Output, string Error, int ExitCode)>(("   ", "", 0)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetPrDiffAsync("/tmp/repo", 507));

        Assert.Contains("does not have any diff content", ex.Message);
    }

    [Theory]
    [InlineData("", 507, "working directory")]
    [InlineData("/tmp/repo", 0, "greater than zero")]
    public async Task GetPrDiffAsync_InvalidArguments_Throw(string workingDirectory, int prNumber, string expected)
    {
        var service = new TestPrLinkService((_, _, _) =>
            Task.FromResult<(string Output, string Error, int ExitCode)>(("", "", 0)));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => service.GetPrDiffAsync(workingDirectory, prNumber));

        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestPrLinkService(
        Func<string, CancellationToken, string[], Task<(string Output, string Error, int ExitCode)>> runner) : PrLinkService
    {
        public string? LastWorkingDirectory { get; private set; }
        public string[]? LastArgs { get; private set; }

        protected override Task<(string Output, string Error, int ExitCode)> RunGhAsync(
            string workingDirectory,
            CancellationToken cancellationToken,
            params string[] args)
        {
            LastWorkingDirectory = workingDirectory;
            LastArgs = args;
            return runner(workingDirectory, cancellationToken, args);
        }
    }
}
