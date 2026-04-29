using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the session.shutdown pre-check in SendPromptAsync (Issue #397).
/// Before sending a prompt, SendPromptAsync checks if events.jsonl ends with
/// session.shutdown and forces a reconnect instead of sending to a dead session.
/// </summary>
public class ShutdownPreCheckTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public ShutdownPreCheckTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    // --- GetLastEventType detection tests ---

    [Fact]
    public void GetLastEventType_DetectsSessionShutdown()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "polypilot-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        var eventsFile = Path.Combine(tmpDir, "events.jsonl");

        try
        {
            // Write events ending with session.shutdown
            File.WriteAllText(eventsFile, string.Join("\n",
                """{"type":"session.start","data":{}}""",
                """{"type":"user.message","data":{"content":"hello"}}""",
                """{"type":"assistant.message","data":{"content":"hi"}}""",
                """{"type":"session.shutdown","data":{}}"""
            ));

            var lastEvent = CopilotService.GetLastEventType(eventsFile);
            Assert.Equal("session.shutdown", lastEvent);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void GetLastEventType_NonShutdownEvent_DoesNotTrigger()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "polypilot-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        var eventsFile = Path.Combine(tmpDir, "events.jsonl");

        try
        {
            // Write events ending with a normal event (not shutdown)
            File.WriteAllText(eventsFile, string.Join("\n",
                """{"type":"session.start","data":{}}""",
                """{"type":"user.message","data":{"content":"hello"}}""",
                """{"type":"assistant.message","data":{"content":"hi"}}"""
            ));

            var lastEvent = CopilotService.GetLastEventType(eventsFile);
            Assert.NotEqual("session.shutdown", lastEvent);
            Assert.Equal("assistant.message", lastEvent);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void GetLastEventType_EmptyFile_ReturnsNull()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "polypilot-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        var eventsFile = Path.Combine(tmpDir, "events.jsonl");

        try
        {
            File.WriteAllText(eventsFile, "");
            var lastEvent = CopilotService.GetLastEventType(eventsFile);
            Assert.Null(lastEvent);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void GetLastEventType_MissingFile_ReturnsNull()
    {
        var lastEvent = CopilotService.GetLastEventType("/tmp/nonexistent-file-" + Guid.NewGuid().ToString("N"));
        Assert.Null(lastEvent);
    }

    [Fact]
    public void GetLastEventType_TrailingWhitespace_IgnoresBlankLines()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "polypilot-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        var eventsFile = Path.Combine(tmpDir, "events.jsonl");

        try
        {
            // session.shutdown followed by trailing whitespace/newlines
            File.WriteAllText(eventsFile,
                """{"type":"session.shutdown","data":{}}""" + "\n\n  \n");

            var lastEvent = CopilotService.GetLastEventType(eventsFile);
            Assert.Equal("session.shutdown", lastEvent);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    // --- Structural tests: verify the pre-check code path in SendPromptAsync ---

    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root");
    }

    [Fact]
    public void ShutdownPreCheck_OperationCanceledException_NotWrapped()
    {
        // The pre-check catch block must re-throw OperationCanceledException directly
        // (not wrapped in InvalidOperationException) to preserve cancellation semantics.
        var servicePath = Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs");
        var source = File.ReadAllText(servicePath);

        // Find the pre-check block
        var preCheckIdx = source.IndexOf("[SEND-SHUTDOWN-PRECHECK]", StringComparison.Ordinal);
        Assert.True(preCheckIdx >= 0, "SEND-SHUTDOWN-PRECHECK diagnostic tag must exist");

        // Find the surrounding try-catch region (search from the PRECHECK tag backwards/forwards)
        var catchOceIdx = source.IndexOf("catch (OperationCanceledException)", preCheckIdx - 500, StringComparison.Ordinal);
        Assert.True(catchOceIdx >= 0,
            "Pre-check must have a separate catch for OperationCanceledException before the generic catch (Exception)");

        // Verify it rethrows without wrapping
        var catchOceBlock = source.Substring(catchOceIdx, Math.Min(300, source.Length - catchOceIdx));
        Assert.Contains("throw;", catchOceBlock);
        Assert.DoesNotContain("throw new", catchOceBlock);
    }

    [Fact]
    public void ShutdownPreCheck_SendingFlagReleasedOnCancellation()
    {
        // Both catch blocks (OCE and generic) must release SendingFlag before rethrowing
        var servicePath = Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs");
        var source = File.ReadAllText(servicePath);

        var preCheckIdx = source.IndexOf("[SEND-SHUTDOWN-PRECHECK]", StringComparison.Ordinal);
        Assert.True(preCheckIdx >= 0);

        // Find the OCE catch
        var catchOceIdx = source.IndexOf("catch (OperationCanceledException)", preCheckIdx - 500, StringComparison.Ordinal);
        Assert.True(catchOceIdx >= 0, "Must have OperationCanceledException catch");
        var catchOceBlock = source.Substring(catchOceIdx, Math.Min(300, source.Length - catchOceIdx));
        Assert.Contains("Interlocked.Exchange(ref state.SendingFlag, 0)", catchOceBlock);

        // Find the generic catch
        var catchGenericIdx = source.IndexOf("catch (Exception ex)", catchOceIdx, StringComparison.Ordinal);
        Assert.True(catchGenericIdx >= 0, "Must have generic Exception catch after OCE catch");
        var catchGenericBlock = source.Substring(catchGenericIdx, Math.Min(400, source.Length - catchGenericIdx));
        Assert.Contains("Interlocked.Exchange(ref state.SendingFlag, 0)", catchGenericBlock);
    }

    [Fact]
    public void ShutdownPreCheck_SkippedAfterLazyResume()
    {
        // When lazy-resume fires (Session was null), the pre-check must be skipped
        // to avoid a redundant double-reconnect on stale events.jsonl.
        var servicePath = Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs");
        var source = File.ReadAllText(servicePath);

        // The lazy-resume block must set a flag
        var sendIdx = source.IndexOf("async Task<string> SendPromptAsync(", StringComparison.Ordinal);
        Assert.True(sendIdx >= 0);

        // Find the lazy-resume flag (wasLazyResumed or similar) between SendPromptAsync and the pre-check
        var preCheckIdx = source.IndexOf("[SEND-SHUTDOWN-PRECHECK]", sendIdx, StringComparison.Ordinal);
        Assert.True(preCheckIdx >= 0);

        var regionBetween = source.Substring(sendIdx, preCheckIdx - sendIdx);
        Assert.Contains("wasLazyResumed", regionBetween);

        // The pre-check must be guarded by !wasLazyResumed
        Assert.Contains("!wasLazyResumed", regionBetween);
    }

    // --- Behavioral detection tests ---

    [Fact]
    public void ShutdownPreCheck_SessionWithShutdownEvent_IsDetected()
    {
        var baseDir = TestSetup.TestBaseDir;
        var sessionStatePath = Path.Combine(baseDir, "session-state");
        var sessionId = Guid.NewGuid().ToString();
        var sessionDir = Path.Combine(sessionStatePath, sessionId);
        Directory.CreateDirectory(sessionDir);
        var eventsFile = Path.Combine(sessionDir, "events.jsonl");

        try
        {
            File.WriteAllText(eventsFile, string.Join("\n",
                """{"type":"session.start","data":{}}""",
                """{"type":"user.message","data":{"content":"test"}}""",
                """{"type":"session.shutdown","data":{}}"""
            ));

            var lastEvent = CopilotService.GetLastEventType(eventsFile);
            Assert.Equal("session.shutdown", lastEvent);

            bool shouldForceReconnect = lastEvent == "session.shutdown";
            Assert.True(shouldForceReconnect, "Should detect server-shutdown session and force reconnect");
        }
        finally
        {
            if (Directory.Exists(sessionDir))
                Directory.Delete(sessionDir, true);
        }
    }

    [Fact]
    public void ShutdownPreCheck_ActiveSession_NoReconnectNeeded()
    {
        var baseDir = TestSetup.TestBaseDir;
        var sessionStatePath = Path.Combine(baseDir, "session-state");
        var sessionId = Guid.NewGuid().ToString();
        var sessionDir = Path.Combine(sessionStatePath, sessionId);
        Directory.CreateDirectory(sessionDir);
        var eventsFile = Path.Combine(sessionDir, "events.jsonl");

        try
        {
            File.WriteAllText(eventsFile, string.Join("\n",
                """{"type":"session.start","data":{}}""",
                """{"type":"user.message","data":{"content":"test"}}""",
                """{"type":"assistant.message","data":{"content":"response"}}""",
                """{"type":"session.idle","data":{}}"""
            ));

            var lastEvent = CopilotService.GetLastEventType(eventsFile);
            bool shouldForceReconnect = lastEvent == "session.shutdown";
            Assert.False(shouldForceReconnect, "Active session should not trigger shutdown pre-check");
        }
        finally
        {
            if (Directory.Exists(sessionDir))
                Directory.Delete(sessionDir, true);
        }
    }

    [Fact]
    public void ShutdownPreCheck_ToolExecutionSession_NoReconnectNeeded()
    {
        var baseDir = TestSetup.TestBaseDir;
        var sessionStatePath = Path.Combine(baseDir, "session-state");
        var sessionId = Guid.NewGuid().ToString();
        var sessionDir = Path.Combine(sessionStatePath, sessionId);
        Directory.CreateDirectory(sessionDir);
        var eventsFile = Path.Combine(sessionDir, "events.jsonl");

        try
        {
            File.WriteAllText(eventsFile, string.Join("\n",
                """{"type":"session.start","data":{}}""",
                """{"type":"user.message","data":{"content":"fix this"}}""",
                """{"type":"tool.execution_start","data":{"name":"edit"}}"""
            ));

            var lastEvent = CopilotService.GetLastEventType(eventsFile);
            bool shouldForceReconnect = lastEvent == "session.shutdown";
            Assert.False(shouldForceReconnect, "Session with active tool execution should not trigger shutdown pre-check");
        }
        finally
        {
            if (Directory.Exists(sessionDir))
                Directory.Delete(sessionDir, true);
        }
    }

    [Fact]
    public void ShutdownPreCheck_NoEventsFile_NoReconnectNeeded()
    {
        var lastEvent = CopilotService.GetLastEventType("/tmp/nonexistent-" + Guid.NewGuid().ToString("N"));
        bool shouldForceReconnect = lastEvent == "session.shutdown";
        Assert.False(shouldForceReconnect, "Missing events file should not trigger shutdown pre-check");
    }
}
