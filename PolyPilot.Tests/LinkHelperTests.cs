using PolyPilot.Models;

namespace PolyPilot.Tests;

public class LinkHelperTests
{
    [Theory]
    [InlineData("https://github.com", true)]
    [InlineData("http://example.com", true)]
    [InlineData("https://example.com/path?q=1&r=2#frag", true)]
    [InlineData("HTTP://EXAMPLE.COM", true)]
    [InlineData("HTTPS://EXAMPLE.COM", true)]
    public void IsValidExternalUrl_AcceptsHttpAndHttps(string url, bool expected)
    {
        Assert.Equal(expected, LinkHelper.IsValidExternalUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://example.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<h1>hi</h1>")]
    [InlineData("not-a-url")]
    [InlineData("relative/path")]
    [InlineData("/absolute/path")]
    public void IsValidExternalUrl_RejectsInvalidUrls(string? url)
    {
        Assert.False(LinkHelper.IsValidExternalUrl(url));
    }

    [Fact]
    public void OpenInBackground_RejectsInvalidUrl()
    {
        // Should not throw for any invalid input
        LinkHelper.OpenInBackground(null!);
        LinkHelper.OpenInBackground("");
        LinkHelper.OpenInBackground("javascript:alert(1)");
        LinkHelper.OpenInBackground("file:///etc/passwd");
    }

    [Fact]
    public void OpenInBackground_AcceptsValidUrl()
    {
        // On non-Mac platforms this is a no-op; just verify it doesn't throw
        LinkHelper.OpenInBackground("https://github.com");
    }
}
