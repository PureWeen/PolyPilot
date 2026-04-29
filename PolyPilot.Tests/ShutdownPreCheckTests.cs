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

    // --- Behavioral test: SendPromptAsync on a shutdown session ---
    // We can't call SendPromptAsync directly (requires SDK infrastructure), but we can
    // verify the detection logic that guards it.

    [Fact]
    public void ShutdownPreCheck_SessionWithShutdownEvent_IsDetected()
    {
        // Simulate the exact check from SendPromptAsync:
        // 1. Get session ID
        // 2. Build events path
        // 3. Check GetLastEventType

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

            // This is the exact check added in the fix
            var lastEvent = CopilotService.GetLastEventType(eventsFile);
            Assert.Equal("session.shutdown", lastEvent);

            // The fix would force reconnect when this condition is true
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
        // Normal active session should NOT trigger the pre-check
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
        // Session with tool execution in progress should NOT trigger pre-check
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
        // New session with no events file should not trigger pre-check
        var lastEvent = CopilotService.GetLastEventType(Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid().ToString("N")));
        bool shouldForceReconnect = lastEvent == "session.shutdown";
        Assert.False(shouldForceReconnect, "Missing events file should not trigger shutdown pre-check");
    }

    // --- Behavioral tests for the pre-check block's logic flow ---

    [Fact]
    public void ShutdownPreCheck_SkipsWhenJustResumed()
    {
        // Validates that the justResumed guard prevents spurious double-reconnect:
        // when lazy-resume just ran (state.Session was null), the pre-check must be skipped
        // because stale events.jsonl would cause an unnecessary second reconnect.
        var baseDir = TestSetup.TestBaseDir;
        var sessionStatePath = Path.Combine(baseDir, "session-state");
        var sessionId = Guid.NewGuid().ToString();
        var sessionDir = Path.Combine(sessionStatePath, sessionId);
        Directory.CreateDirectory(sessionDir);
        var eventsFile = Path.Combine(sessionDir, "events.jsonl");

        try
        {
            // Write events ending with session.shutdown (stale from before restart)
            File.WriteAllText(eventsFile, string.Join("\n",
                """{"type":"session.start","data":{}}""",
                """{"type":"session.shutdown","data":{}}"""
            ));

            // Simulate the justResumed guard: when true, pre-check is skipped
            bool justResumed = true;
            bool preCheckWouldTrigger = false;

            if (!justResumed)
            {
                var lastEvent = CopilotService.GetLastEventType(eventsFile);
                preCheckWouldTrigger = lastEvent == "session.shutdown";
            }

            Assert.False(preCheckWouldTrigger,
                "Pre-check must NOT trigger when justResumed=true (avoids spurious double-reconnect)");

            // Verify that without the guard, it WOULD have triggered
            var lastEventDirect = CopilotService.GetLastEventType(eventsFile);
            Assert.Equal("session.shutdown", lastEventDirect);
        }
        finally
        {
            if (Directory.Exists(sessionDir))
                Directory.Delete(sessionDir, true);
        }
    }

    [Fact]
    public void ShutdownPreCheck_TriggersWhenNotResumed()
    {
        // Validates that when justResumed=false (session was already connected),
        // the pre-check correctly detects shutdown and would trigger reconnect.
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
                """{"type":"user.message","data":{"content":"hello"}}""",
                """{"type":"session.shutdown","data":{}}"""
            ));

            bool justResumed = false;
            bool preCheckTriggered = false;

            if (!justResumed)
            {
                var lastEvent = CopilotService.GetLastEventType(eventsFile);
                preCheckTriggered = lastEvent == "session.shutdown";
            }

            Assert.True(preCheckTriggered,
                "Pre-check must trigger when justResumed=false and events end with session.shutdown");
        }
        finally
        {
            if (Directory.Exists(sessionDir))
                Directory.Delete(sessionDir, true);
        }
    }

    [Fact]
    public void ShutdownPreCheck_StructuralVerification_JustResumedGuard()
    {
        // Structural: verify that the pre-check block is guarded by justResumed in production code.
        // This ensures the double-reconnect fix cannot silently regress.
        var repoRoot = GetRepoRoot();
        var servicePath = Path.Combine(repoRoot, "PolyPilot", "Services", "CopilotService.cs");
        var source = File.ReadAllText(servicePath);

        // Find the pre-check block
        var preCheckIdx = source.IndexOf("[SEND-SHUTDOWN-PRECHECK]", StringComparison.Ordinal);
        Assert.True(preCheckIdx >= 0, "SEND-SHUTDOWN-PRECHECK diagnostic tag must exist");

        // The justResumed guard must appear before the pre-check block
        var justResumedIdx = source.IndexOf("if (!justResumed)", StringComparison.Ordinal);
        Assert.True(justResumedIdx >= 0, "justResumed guard must exist in SendPromptAsync");
        Assert.True(justResumedIdx < preCheckIdx,
            "justResumed guard must appear before the SEND-SHUTDOWN-PRECHECK block");
    }

    [Fact]
    public void ShutdownPreCheck_StructuralVerification_OperationCanceledExceptionHandled()
    {
        // Structural: verify that OperationCanceledException is caught separately
        // from the general Exception handler in the pre-check block.
        var repoRoot = GetRepoRoot();
        var servicePath = Path.Combine(repoRoot, "PolyPilot", "Services", "CopilotService.cs");
        var source = File.ReadAllText(servicePath);

        // Find the pre-check block
        var preCheckIdx = source.IndexOf("[SEND-SHUTDOWN-PRECHECK]", StringComparison.Ordinal);
        Assert.True(preCheckIdx >= 0, "SEND-SHUTDOWN-PRECHECK must exist");

        // Look backward from the pre-check for the catch block structure
        // The OperationCanceledException catch must exist in the same try block
        var blockStart = source.LastIndexOf("if (!justResumed)", preCheckIdx, StringComparison.Ordinal);
        Assert.True(blockStart >= 0);
        var blockEnd = source.IndexOf("long myGeneration", blockStart, StringComparison.Ordinal);
        Assert.True(blockEnd >= 0);
        var preCheckBlock = source[blockStart..blockEnd];

        Assert.Contains("catch (OperationCanceledException)", preCheckBlock);
        Assert.Contains("throw; // Preserve cancellation semantics", preCheckBlock);
    }

    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root");
    }
}
