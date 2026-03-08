using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for CopilotService.IsPermissionDenialText — the helper that detects
/// permission denial messages from the Copilot SDK (issue #300 / PR #305).
/// </summary>
public class PermissionDenialDetectionTests
{
    // ===== Null / empty → false =====

    [Fact]
    public void IsPermissionDenialText_Null_ReturnsFalse()
    {
        Assert.False(CopilotService.IsPermissionDenialText(null));
    }

    [Fact]
    public void IsPermissionDenialText_Empty_ReturnsFalse()
    {
        Assert.False(CopilotService.IsPermissionDenialText(""));
    }

    // ===== Human-readable message variants → true =====

    [Theory]
    [InlineData("Permission denied")]
    [InlineData("Permission denied and could not request permission from user")]
    [InlineData("PERMISSION DENIED")]                // case-insensitive
    [InlineData("Error: permission denied (code 403)")]
    public void IsPermissionDenialText_PermissionDeniedVariants_ReturnsTrue(string text)
    {
        Assert.True(CopilotService.IsPermissionDenialText(text));
    }

    // ===== SDK internal error code → true =====

    [Theory]
    [InlineData("denied-no-approval-rule-and-could-not-request-from-user")]
    [InlineData("denied-no-approval-rule")]          // prefix match
    [InlineData("Error: DENIED-NO-APPROVAL-RULE")]   // case-insensitive
    public void IsPermissionDenialText_DeniedNoApprovalRule_ReturnsTrue(string text)
    {
        Assert.True(CopilotService.IsPermissionDenialText(text));
    }

    // ===== "could not request permission" variant → true =====

    [Theory]
    [InlineData("could not request permission from user")]
    [InlineData("Could Not Request Permission")]     // case-insensitive
    public void IsPermissionDenialText_CouldNotRequestPermission_ReturnsTrue(string text)
    {
        Assert.True(CopilotService.IsPermissionDenialText(text));
    }

    // ===== Unrelated errors → false =====

    [Theory]
    [InlineData("Connection refused")]
    [InlineData("Session not found")]
    [InlineData("Timeout waiting for response")]
    [InlineData("Tool execution failed: file not found")]
    [InlineData("Access denied")]                    // similar but not matching
    [InlineData("denied")]                           // partial — doesn't match any pattern alone
    public void IsPermissionDenialText_UnrelatedText_ReturnsFalse(string text)
    {
        Assert.False(CopilotService.IsPermissionDenialText(text));
    }
}
