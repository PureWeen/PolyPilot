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

    // ===== MCP server failure detection =====

    [Fact]
    public void IsMcpError_Null_ReturnsFalse()
    {
        Assert.False(CopilotService.IsMcpError(null));
    }

    [Fact]
    public void IsMcpError_Empty_ReturnsFalse()
    {
        Assert.False(CopilotService.IsMcpError(""));
    }

    [Theory]
    // Unambiguously MCP-specific patterns — match unconditionally
    [InlineData("MCP server 'workiq' failed to start")]
    [InlineData("Error connecting to MCP server: connection refused")]
    [InlineData("mcp_server_workiq: connection failed")]
    // Generic patterns — require "mcp" context
    [InlineData("connection refused while connecting to MCP")]
    [InlineData("Failed to start MCP process")]
    [InlineData("mcp server disconnected unexpectedly")]
    [InlineData("MCP transport error: connection reset")]
    [InlineData("mcp server process exited with code 1")]
    [InlineData("mcp_server_workiq: ECONNREFUSED 127.0.0.1:3000")]
    [InlineData("mcp server spawn ENOENT: npx not found")]
    public void IsMcpError_McpFailureVariants_ReturnsTrue(string text)
    {
        Assert.True(CopilotService.IsMcpError(text));
    }

    [Theory]
    [InlineData("Permission denied")]
    [InlineData("posix_spawn failed")]
    [InlineData("File not found")]
    [InlineData("Session not found")]
    [InlineData("Timeout waiting for response")]
    // Generic network/process errors without MCP context — must NOT trigger MCP recovery
    [InlineData("Connection refused")]
    [InlineData("ECONNREFUSED")]
    [InlineData("connect ECONNREFUSED 127.0.0.1:3000")]
    [InlineData("server disconnected unexpectedly")]
    [InlineData("transport error: connection reset")]
    [InlineData("server process exited with code 1")]
    [InlineData("failed to start")]
    [InlineData("spawn ENOENT: npx not found")]
    [InlineData("SSH connection refused")]
    [InlineData("docker container failed to start")]
    [InlineData("kubectl: connection refused to 10.0.0.1:6443")]
    [InlineData("git: spawn ENOENT: no such file or directory")]
    public void IsMcpError_UnrelatedErrors_ReturnsFalse(string text)
    {
        Assert.False(CopilotService.IsMcpError(text));
    }

    [Fact]
    public void IsMcpError_NotConfusedWithPermissionDenialOrShellFailure()
    {
        // MCP errors should be detected by IsMcpError, not by the other detectors
        var text = "MCP server 'workiq' failed to start: ECONNREFUSED";
        Assert.True(CopilotService.IsMcpError(text));
        Assert.False(CopilotService.IsPermissionDenialText(text));
        Assert.False(CopilotService.IsShellEnvironmentFailure(text));
    }

    // ===== Structural: MCP errors are recoverable =====

    [Fact]
    public void RecoverableError_IncludesMcpFailure()
    {
        // STRUCTURAL REGRESSION GUARD: isRecoverableError must include isMcpFailure
        // so that repeated MCP errors trigger auto-recovery (session reconnect).
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Events.cs"));
        Assert.Contains("var isRecoverableError = isPermissionDenial || isShellFailure || isMcpFailure;", source);
    }

    [Fact]
    public void McpRecoveryMessage_IncludesReloadHint()
    {
        // When MCP recovery triggers, the user message must mention /mcp reload
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Events.cs"));
        Assert.Contains("/mcp reload", source);
    }

    [Fact]
    public void ReloadMcpServersAsync_ExistsOnService_WithSessionNameParam()
    {
        // STRUCTURAL: The public API must accept a session name, not create a new session
        var method = typeof(CopilotService).GetMethod(
            "ReloadMcpServersAsync",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        var parameters = method.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(string), parameters[0].ParameterType);
        Assert.Equal("sessionName", parameters[0].Name);
    }

    [Fact]
    public void ReloadMcpServersAsync_PreservesHistoryInPlace()
    {
        // STRUCTURAL REGRESSION GUARD: ReloadMcpServersAsync must replace the SDK session
        // in-place using _sessions.TryUpdate (preserving AgentSessionInfo/history).
        // It must NOT call CreateSessionAsync with a renamed session (which loses history).
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs"));
        Assert.Contains("_sessions.TryUpdate(sessionName, newState, state)", source);
        Assert.Contains("Info = state.Info", source);
    }

    [Fact]
    public void ReloadMcpServersAsync_ThrowsOnConcurrentReload()
    {
        // STRUCTURAL: Concurrent reload must throw (not silently succeed) so the Dashboard
        // can display an honest error message instead of a false "✅ reloaded" confirmation.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs"));
        // The guard must throw, not return silently
        Assert.Contains("throw new InvalidOperationException(\"MCP reload is already in progress", source);
    }

    [Fact]
    public void ReloadMcpServersAsync_ClearsPermissionDenials_Unconditionally()
    {
        // STRUCTURAL: Reload must clear the sliding-window denial count UNCONDITIONALLY —
        // i.e. regardless of whether IsProcessing is true or false. The typical /mcp reload
        // scenario has the session idle when the user types the command, so any check gated
        // on IsProcessing would silently skip the clear and let stale denials carry into the
        // fresh SDK session, triggering an immediate TryRecoverPermissionAsync cascade.
        //
        // We verify the unconditional clear by checking for the unique marker comment that
        // accompanies the out-of-if-block call. If ClearPermissionDenials() were moved back
        // inside the if-block, this comment would not be present and the test would fail.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs"));

        // The unconditional call is preceded by a comment that is unique to that call site.
        Assert.Contains("Always clear the sliding-window denial queue regardless of processing state", source);

        // Verify the actual call follows that comment (not just the comment without the fix).
        var markerIdx = source.IndexOf("Always clear the sliding-window denial queue regardless of processing state", StringComparison.Ordinal);
        var snippetAfterMarker = source.Substring(markerIdx, Math.Min(500, source.Length - markerIdx));
        Assert.Contains("ClearPermissionDenials()", snippetAfterMarker);
    }

    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root");
    }
}

/// <summary>
/// Tests for CopilotService.IsInitializationError — the helper that detects
/// "Service not initialized" exceptions so worker dispatch can retry them
/// with a lazy re-init attempt instead of failing immediately (PR #422).
/// </summary>
public class InitializationErrorDetectionTests
{
    [Fact]
    public void IsInitializationError_ServiceNotInitialized_ReturnsTrue()
    {
        var ex = new InvalidOperationException("Service not initialized. Call InitializeAsync first.");
        Assert.True(CopilotService.IsInitializationError(ex));
    }

    [Fact]
    public void IsInitializationError_ClientNotInitialized_ReturnsTrue()
    {
        var ex = new InvalidOperationException("Client is not initialized");
        Assert.True(CopilotService.IsInitializationError(ex));
    }

    [Theory]
    [InlineData("Service not initialized. Call InitializeAsync first.")]
    [InlineData("Client is not initialized")]
    [InlineData("NOT INITIALIZED")]   // case-insensitive
    public void IsInitializationError_Variants_ReturnsTrue(string message)
    {
        var ex = new InvalidOperationException(message);
        Assert.True(CopilotService.IsInitializationError(ex));
    }

    [Fact]
    public void IsInitializationError_ConnectionRefused_ReturnsFalse()
    {
        // Connection errors are handled by IsConnectionError, not IsInitializationError
        var ex = new InvalidOperationException("connection refused");
        Assert.False(CopilotService.IsInitializationError(ex));
    }

    [Fact]
    public void IsInitializationError_WrongExceptionType_ReturnsFalse()
    {
        // Only InvalidOperationException matches — not base Exception
        var ex = new Exception("Service not initialized");
        Assert.False(CopilotService.IsInitializationError(ex));
    }

    [Fact]
    public void IsInitializationError_ArbitraryInvalidOperation_ReturnsFalse()
    {
        var ex = new InvalidOperationException("Session 'foo' already exists.");
        Assert.False(CopilotService.IsInitializationError(ex));
    }

    [Fact]
    public void IsInitializationError_HelperIsInternal_CanBeTestedViaPublicSurface()
    {
        // The helper is internal — verify it's accessible from tests (InternalsVisibleTo).
        // Also verify the helper exists in CopilotService.Utilities.cs at the right location.
        var repoRoot = GetRepoRoot();
        var source = File.ReadAllText(Path.Combine(repoRoot, "PolyPilot", "Services", "CopilotService.Utilities.cs"));
        Assert.Contains("internal static bool IsInitializationError(Exception ex)", source);
        Assert.Contains("not initialized", source);
    }

    [Fact]
    public void ExecuteWorkerAsync_RetryGate_IncludesInitializationError()
    {
        // Structural: the retry catch in ExecuteWorkerAsync must check IsInitializationError
        // so "Service not initialized" triggers a retry with lazy re-init, not an immediate fail.
        var repoRoot = GetRepoRoot();
        var source = File.ReadAllText(Path.Combine(repoRoot, "PolyPilot", "Services", "CopilotService.Organization.cs"));
        Assert.Contains("IsInitializationError(ex)", source);
        // The retry gate must combine both checks
        Assert.Contains("IsConnectionError(ex) || IsInitializationError(ex)", source);
    }

    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root");
    }
}
