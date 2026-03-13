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

    // ===== ExtractErrorMessage =====

    [Fact]
    public void ExtractErrorMessage_Null_ReturnsNull()
    {
        Assert.Null(CopilotService.ExtractErrorMessage(null));
    }

    [Fact]
    public void ExtractErrorMessage_ObjectWithMessageAndCode_ReturnsCombined()
    {
        var error = new { Message = "Permission denied", Code = "denied" };
        var result = CopilotService.ExtractErrorMessage(error);
        Assert.Contains("Permission denied", result);
        Assert.Contains("denied", result);
    }

    [Fact]
    public void ExtractErrorMessage_ObjectWithMessageOnly_ReturnsMessage()
    {
        var error = new { Message = "Something went wrong" };
        var result = CopilotService.ExtractErrorMessage(error);
        Assert.Equal("Something went wrong", result);
    }

    [Fact]
    public void ExtractErrorMessage_ObjectWithNoProperties_ReturnsToString()
    {
        var error = "plain string error";
        var result = CopilotService.ExtractErrorMessage(error);
        Assert.Equal("plain string error", result);
    }

    [Fact]
    public void ExtractErrorMessage_SdkStyle_PermissionDenial_IsDetectedByIsPermissionDenialText()
    {
        // Simulates the real SDK ToolExecutionCompleteDataError object
        var error = new { Message = "Permission denied and could not request permission from user", Code = "denied" };
        var errorStr = CopilotService.ExtractErrorMessage(error);
        Assert.True(CopilotService.IsPermissionDenialText(errorStr),
            $"Expected permission denial detection for: '{errorStr}'");
    }

    [Fact]
    public void ExtractErrorMessage_SdkTypeNameString_WouldNotBeDetected()
    {
        // This is what the OLD code produced — verifies the bug existed
        var fakeTypeName = "GitHub.Copilot.SDK.ToolExecutionCompleteDataError";
        Assert.False(CopilotService.IsPermissionDenialText(fakeTypeName),
            "SDK type name should NOT match permission denial patterns");
    }

    // ===== Shell environment failure detection =====

    [Theory]
    [InlineData("posix_spawn failed: No such file or directory")]
    [InlineData("Error: posix_spawn failed")]
    [InlineData("POSIX_SPAWN FAILED: some reason")]
    public void IsShellEnvironmentFailure_PosixSpawnErrors_ReturnsTrue(string text)
    {
        Assert.True(CopilotService.IsShellEnvironmentFailure(text));
    }

    [Fact]
    public void IsShellEnvironmentFailure_Null_ReturnsFalse()
    {
        Assert.False(CopilotService.IsShellEnvironmentFailure(null));
    }

    [Fact]
    public void IsShellEnvironmentFailure_Empty_ReturnsFalse()
    {
        Assert.False(CopilotService.IsShellEnvironmentFailure(""));
    }

    [Theory]
    [InlineData("Connection refused")]
    [InlineData("Permission denied")]
    [InlineData("File not found")]
    public void IsShellEnvironmentFailure_UnrelatedErrors_ReturnsFalse(string text)
    {
        Assert.False(CopilotService.IsShellEnvironmentFailure(text));
    }

    [Fact]
    public void IsShellEnvironmentFailure_NotConfusedWithPermissionDenial()
    {
        // posix_spawn errors should be detected by shell failure, not permission denial
        var text = "posix_spawn failed: No such file or directory";
        Assert.True(CopilotService.IsShellEnvironmentFailure(text));
        Assert.False(CopilotService.IsPermissionDenialText(text));
    }
}
