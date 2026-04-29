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

    // --- Structural tests: verify SendPromptAsync shutdown pre-check code structure ---
    // These verify the actual fix code rather than just re-testing GetLastEventType.

    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root");
    }

    private static string GetSendPromptPreCheckBlock()
    {
        var servicePath = Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs");
        var source = File.ReadAllText(servicePath);

        // Find the shutdown pre-check block by its diagnostic tag
        var preCheckIdx = source.IndexOf("[SEND-SHUTDOWN-PRECHECK]", StringComparison.Ordinal);
        Assert.True(preCheckIdx >= 0, "SEND-SHUTDOWN-PRECHECK must exist in CopilotService.cs");

        // Extract a window around the pre-check for assertions
        var start = Math.Max(0, preCheckIdx - 2000);
        return source.Substring(start, Math.Min(4000, source.Length - start));
    }

    [Fact]
    public void ShutdownPreCheck_HasWasAlreadyConnectedGuard()
    {
        // The pre-check must be gated on wasAlreadyConnected to prevent spurious
        // double-reconnect when lazy-resume already reconnected the session.
        var block = GetSendPromptPreCheckBlock();
        Assert.Contains("wasAlreadyConnected", block);
        Assert.Contains("bool wasAlreadyConnected = state.Session != null", block);
        Assert.Contains("if (wasAlreadyConnected)", block);
    }

    [Fact]
    public void ShutdownPreCheck_HasOperationCanceledExceptionCatch()
    {
        // The pre-check must catch OperationCanceledException separately to preserve
        // cancellation semantics — wrapping it in InvalidOperationException loses the
        // cancellation identity for callers.
        var block = GetSendPromptPreCheckBlock();
        Assert.Contains("catch (OperationCanceledException)", block);
        Assert.Contains("throw; // Preserve cancellation semantics", block);
    }

    [Fact]
    public void ShutdownPreCheck_ReleasesSendingFlagOnBothCatchPaths()
    {
        // Both catch blocks (OperationCanceledException and general Exception) must
        // release SendingFlag to prevent session deadlock.
        var block = GetSendPromptPreCheckBlock();

        // Count SendingFlag releases in the pre-check block
        var flagReleaseCount = 0;
        var searchFrom = 0;
        while (true)
        {
            var idx = block.IndexOf("Interlocked.Exchange(ref state.SendingFlag, 0)", searchFrom, StringComparison.Ordinal);
            if (idx < 0) break;
            flagReleaseCount++;
            searchFrom = idx + 1;
        }

        Assert.True(flagReleaseCount >= 2, $"Expected at least 2 SendingFlag releases in pre-check catch blocks, found {flagReleaseCount}");
    }

    [Fact]
    public void ShutdownPreCheck_ErrorMessageIncludesInnerExceptionDetails()
    {
        // The error message must include the inner exception's message so the user
        // sees the actionable root cause (auth failure, network error, etc.),
        // not a generic "shut down by the server" message.
        var block = GetSendPromptPreCheckBlock();
        Assert.Contains("ex.Message", block);
        Assert.Contains("needs reconnection after detecting shutdown state", block);
    }

    [Fact]
    public void ShutdownPreCheck_UsesNullForgivingAssignment()
    {
        // state.Session is a non-nullable required property — assigning null
        // without ! produces CS8625. The pre-check must use null! for consistency
        // with the 12+ other occurrences in the codebase.
        var block = GetSendPromptPreCheckBlock();
        Assert.Contains("state.Session = null!", block);
        Assert.DoesNotContain("state.Session = null;", block);
    }

    // --- Detection logic tests (retained from original) ---

    [Fact]
    public void ShutdownPreCheck_SessionWithShutdownEvent_IsDetected()
    {
        var svc = CreateService();
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
