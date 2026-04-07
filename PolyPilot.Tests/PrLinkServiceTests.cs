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
}
