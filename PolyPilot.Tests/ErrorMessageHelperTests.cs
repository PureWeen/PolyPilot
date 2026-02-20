using PolyPilot.Models;

namespace PolyPilot.Tests;

public class ErrorMessageHelperTests
{
    // --- HumanizeMessage tests (string input) ---

    [Fact]
    public void ConnectFailure_ReturnsHumanReadableMessage()
    {
        var result = ErrorMessageHelper.HumanizeMessage("net_webstatus_ConnectFailure");
        Assert.Contains("Connection refused", result);
        Assert.Contains("not reachable", result);
    }

    [Fact]
    public void ConnectFailure_CaseInsensitive()
    {
        var result = ErrorMessageHelper.HumanizeMessage("NET_WEBSTATUS_CONNECTFAILURE");
        Assert.Contains("Connection refused", result);
    }

    [Fact]
    public void ConnectFailure_EmbeddedInLongerMessage()
    {
        var result = ErrorMessageHelper.HumanizeMessage("Unable to connect (net_webstatus_ConnectFailure)");
        Assert.Contains("Connection refused", result);
    }

    [Fact]
    public void NameResolutionFailure_ReturnsHumanReadableMessage()
    {
        var result = ErrorMessageHelper.HumanizeMessage("net_webstatus_NameResolutionFailure");
        Assert.Contains("resolve", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Timeout_ReturnsHumanReadableMessage()
    {
        var result = ErrorMessageHelper.HumanizeMessage("net_webstatus_Timeout");
        Assert.Contains("timed out", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SecureChannelFailure_ReturnsHumanReadableMessage()
    {
        var result = ErrorMessageHelper.HumanizeMessage("net_webstatus_SecureChannelFailure");
        Assert.Contains("SSL", result);
    }

    [Fact]
    public void TrustFailure_ReturnsHumanReadableMessage()
    {
        var result = ErrorMessageHelper.HumanizeMessage("net_webstatus_TrustFailure");
        Assert.Contains("certificate", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReceiveFailure_ReturnsHumanReadableMessage()
    {
        var result = ErrorMessageHelper.HumanizeMessage("net_webstatus_ReceiveFailure");
        Assert.Contains("lost", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SendFailure_ReturnsHumanReadableMessage()
    {
        var result = ErrorMessageHelper.HumanizeMessage("net_webstatus_SendFailure");
        Assert.Contains("send", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConnectionRefused_Generic_ReturnsHumanReadableMessage()
    {
        var result = ErrorMessageHelper.HumanizeMessage("Connection refused");
        Assert.Contains("not reachable", result);
    }

    [Fact]
    public void NoSuchHost_ReturnsHumanReadableMessage()
    {
        var result = ErrorMessageHelper.HumanizeMessage("No such host is known");
        Assert.Contains("resolve", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NetworkUnreachable_ReturnsHumanReadableMessage()
    {
        var result = ErrorMessageHelper.HumanizeMessage("Network is unreachable");
        Assert.Contains("internet", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalMessage_PassesThrough()
    {
        var result = ErrorMessageHelper.HumanizeMessage("Something went wrong");
        Assert.Equal("Something went wrong", result);
    }

    [Fact]
    public void NullOrEmpty_ReturnsFallback()
    {
        Assert.Equal("An unknown error occurred", ErrorMessageHelper.HumanizeMessage(""));
        Assert.Equal("An unknown error occurred", ErrorMessageHelper.HumanizeMessage("   "));
        Assert.Equal("An unknown error occurred", ErrorMessageHelper.HumanizeMessage(null!));
    }

    // --- Humanize tests (Exception input) ---

    [Fact]
    public void Humanize_Exception_WithPlatformMessage()
    {
        var ex = new Exception("net_webstatus_ConnectFailure");
        var result = ErrorMessageHelper.Humanize(ex);
        Assert.Contains("Connection refused", result);
    }

    [Fact]
    public void Humanize_Exception_WithInnerSocketException()
    {
        var inner = new System.Net.Sockets.SocketException(61); // Connection refused on macOS
        var outer = new Exception("An error occurred", inner);
        var result = ErrorMessageHelper.Humanize(outer);
        // Should get a meaningful message, not a raw exception type
        Assert.DoesNotContain("net_webstatus_", result);
    }

    [Fact]
    public void Humanize_Exception_PrefersOuterWhenContainsPlatformCode()
    {
        var inner = new Exception("some detail");
        var outer = new Exception("net_webstatus_ConnectFailure", inner);
        var result = ErrorMessageHelper.Humanize(outer);
        Assert.Contains("Connection refused", result);
    }

    [Fact]
    public void Humanize_Exception_UsesInnerWhenMoreSpecific()
    {
        var inner = new Exception("Connection refused");
        var outer = new Exception("An error occurred while connecting", inner);
        var result = ErrorMessageHelper.Humanize(outer);
        Assert.Contains("not reachable", result);
    }

    [Fact]
    public void MacOsDnsFailure_ReturnsHumanReadableMessage()
    {
        var result = ErrorMessageHelper.HumanizeMessage("nodename nor servname provided, or not known");
        Assert.Contains("resolve", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LinuxDnsFailure_ReturnsHumanReadableMessage()
    {
        var result = ErrorMessageHelper.HumanizeMessage("Name or service not known");
        Assert.Contains("resolve", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoRouteToHost_ReturnsHumanReadableMessage()
    {
        var result = ErrorMessageHelper.HumanizeMessage("No route to host");
        Assert.Contains("reach", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HostIsDown_ReturnsHumanReadableMessage()
    {
        var result = ErrorMessageHelper.HumanizeMessage("Host is down");
        Assert.Contains("down", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownNetWebStatus_ReturnsCatchAllNetworkError()
    {
        var result = ErrorMessageHelper.HumanizeMessage("net_webstatus_SomethingUnexpected");
        Assert.Equal("A network error occurred. Check your connection and try again.", result);
    }
}
