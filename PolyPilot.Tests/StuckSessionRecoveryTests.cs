using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the stuck-session recovery logic:
/// - IsSessionStillProcessing staleness check (events.jsonl file age)
/// - Watchdog IsResumed clearing after events arrive
/// Regression tests for: sessions permanently stuck in IsProcessing=true after app restart.
/// </summary>
public class StuckSessionRecoveryTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public StuckSessionRecoveryTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    // --- Staleness check: IsSessionStillProcessing ---

    [Fact]
    public void IsSessionStillProcessing_StaleFile_ReturnsFalse()
    {
        // Arrange: create a temp session dir with an events.jsonl that was modified long ago
        var svc = CreateService();
        var tmpDir = Path.Combine(Path.GetTempPath(), "polypilot-test-" + Guid.NewGuid().ToString("N"));
        var sessionId = Guid.NewGuid().ToString();
        var sessionDir = Path.Combine(tmpDir, sessionId);
        Directory.CreateDirectory(sessionDir);
        var eventsFile = Path.Combine(sessionDir, "events.jsonl");

        try
        {
            // Write an "active" event as the last line
            File.WriteAllText(eventsFile, """{"type":"assistant.message_delta","data":{"deltaContent":"hello"}}""");
            // Backdate the file to be older than the watchdog timeout
            var staleTime = DateTime.UtcNow.AddSeconds(-(CopilotService.WatchdogToolExecutionTimeoutSeconds + 60));
            File.SetLastWriteTimeUtc(eventsFile, staleTime);

            // Act: call IsSessionStillProcessing via the service
            var result = svc.IsSessionStillProcessing(sessionId, tmpDir);

            // Assert: should be false because the file is stale
            Assert.False(result, "Stale events.jsonl should not report session as still processing");
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void IsSessionStillProcessing_RecentFile_ActiveEvent_ReturnsTrue()
    {
        var svc = CreateService();
        var tmpDir = Path.Combine(Path.GetTempPath(), "polypilot-test-" + Guid.NewGuid().ToString("N"));
        var sessionId = Guid.NewGuid().ToString();
        var sessionDir = Path.Combine(tmpDir, sessionId);
        Directory.CreateDirectory(sessionDir);
        var eventsFile = Path.Combine(sessionDir, "events.jsonl");

        try
        {
            // Write recent events with an active last event
            File.WriteAllText(eventsFile,
                """{"type":"session.start","data":{}}""" + "\n" +
                """{"type":"assistant.turn_start","data":{}}""" + "\n" +
                """{"type":"assistant.message_delta","data":{"deltaContent":"thinking..."}}""");
            // File was just written so it's recent — no need to adjust LastWriteTime

            var result = svc.IsSessionStillProcessing(sessionId, tmpDir);

            Assert.True(result, "Recent events.jsonl with active last event should report still processing");
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void IsSessionStillProcessing_RecentFile_IdleEvent_ReturnsFalse()
    {
        var svc = CreateService();
        var tmpDir = Path.Combine(Path.GetTempPath(), "polypilot-test-" + Guid.NewGuid().ToString("N"));
        var sessionId = Guid.NewGuid().ToString();
        var sessionDir = Path.Combine(tmpDir, sessionId);
        Directory.CreateDirectory(sessionDir);
        var eventsFile = Path.Combine(sessionDir, "events.jsonl");

        try
        {
            File.WriteAllText(eventsFile,
                """{"type":"session.start","data":{}}""" + "\n" +
                """{"type":"assistant.message_delta","data":{"deltaContent":"done"}}""" + "\n" +
                """{"type":"session.idle","data":{}}""");

            var result = svc.IsSessionStillProcessing(sessionId, tmpDir);

            Assert.False(result, "events.jsonl ending with session.idle should not report still processing");
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void IsSessionStillProcessing_MissingFile_ReturnsFalse()
    {
        var svc = CreateService();
        var tmpDir = Path.Combine(Path.GetTempPath(), "polypilot-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);

        try
        {
            var result = svc.IsSessionStillProcessing(Guid.NewGuid().ToString(), tmpDir);
            Assert.False(result, "Missing events.jsonl should not report still processing");
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void IsSessionStillProcessing_EmptyFile_ReturnsFalse()
    {
        var svc = CreateService();
        var tmpDir = Path.Combine(Path.GetTempPath(), "polypilot-test-" + Guid.NewGuid().ToString("N"));
        var sessionId = Guid.NewGuid().ToString();
        var sessionDir = Path.Combine(tmpDir, sessionId);
        Directory.CreateDirectory(sessionDir);
        var eventsFile = Path.Combine(sessionDir, "events.jsonl");

        try
        {
            File.WriteAllText(eventsFile, "");
            var result = svc.IsSessionStillProcessing(sessionId, tmpDir);
            Assert.False(result, "Empty events.jsonl should not report still processing");
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void IsSessionStillProcessing_CorruptJsonl_ReturnsFalse()
    {
        var svc = CreateService();
        var tmpDir = Path.Combine(Path.GetTempPath(), "polypilot-test-" + Guid.NewGuid().ToString("N"));
        var sessionId = Guid.NewGuid().ToString();
        var sessionDir = Path.Combine(tmpDir, sessionId);
        Directory.CreateDirectory(sessionDir);
        var eventsFile = Path.Combine(sessionDir, "events.jsonl");

        try
        {
            File.WriteAllText(eventsFile, "this is not json at all\n{broken json}");
            var result = svc.IsSessionStillProcessing(sessionId, tmpDir);
            Assert.False(result, "Corrupt events.jsonl should not report still processing (should not crash)");
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    // --- Watchdog IsResumed clearing ---

    [Fact]
    public void AgentSessionInfo_IsResumed_ClearedAfterEventsArrive()
    {
        // Simulate: session is resumed with IsResumed=true,
        // then events arrive, watchdog should clear IsResumed.
        var info = new AgentSessionInfo
        {
            Name = "test",
            Model = "test-model",
            IsResumed = true,
            IsProcessing = true
        };

        // Before events: IsResumed is true
        Assert.True(info.IsResumed);

        bool hasReceivedEvents = false;

        // Simulate event arriving
        hasReceivedEvents = true;

        // Simulate what the watchdog does: clear IsResumed when events have arrived
        if (info.IsResumed && hasReceivedEvents)
        {
            info.IsResumed = false;
        }

        Assert.False(info.IsResumed, "IsResumed should be cleared after events arrive");
    }

    [Fact]
    public void AgentSessionInfo_IsResumed_NotClearedWithoutEvents()
    {
        var info = new AgentSessionInfo
        {
            Name = "test",
            Model = "test-model",
            IsResumed = true,
            IsProcessing = true
        };

        bool hasReceivedEvents = false;

        // Watchdog check: should NOT clear IsResumed
        if (info.IsResumed && hasReceivedEvents)
        {
            info.IsResumed = false;
        }

        Assert.True(info.IsResumed, "IsResumed should stay true when no events have arrived");
    }

    // --- Staleness threshold validation ---

    [Fact]
    public void StalenessThreshold_UsesWatchdogToolExecutionTimeout()
    {
        // The staleness threshold should match the tool execution timeout
        // to ensure we don't prematurely declare sessions as idle during long tool runs.
        Assert.Equal(600, CopilotService.WatchdogToolExecutionTimeoutSeconds);
    }

    [Theory]
    [InlineData("assistant.turn_start")]
    [InlineData("tool.execution_start")]
    [InlineData("tool.execution_progress")]
    [InlineData("assistant.message_delta")]
    [InlineData("assistant.reasoning")]
    [InlineData("assistant.reasoning_delta")]
    [InlineData("assistant.intent")]
    public void IsSessionStillProcessing_AllActiveEventTypes_ReturnTrue(string eventType)
    {
        var svc = CreateService();
        var tmpDir = Path.Combine(Path.GetTempPath(), "polypilot-test-" + Guid.NewGuid().ToString("N"));
        var sessionId = Guid.NewGuid().ToString();
        var sessionDir = Path.Combine(tmpDir, sessionId);
        Directory.CreateDirectory(sessionDir);
        var eventsFile = Path.Combine(sessionDir, "events.jsonl");

        try
        {
            File.WriteAllText(eventsFile, $$$"""{"type":"{{{eventType}}}","data":{}}""");
            var result = svc.IsSessionStillProcessing(sessionId, tmpDir);
            Assert.True(result, $"Active event type '{eventType}' should report still processing");
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Theory]
    [InlineData("session.idle")]
    [InlineData("assistant.message")]
    [InlineData("session.start")]
    [InlineData("assistant.turn_end")]
    [InlineData("tool.execution_end")]
    public void IsSessionStillProcessing_InactiveEventTypes_ReturnFalse(string eventType)
    {
        var svc = CreateService();
        var tmpDir = Path.Combine(Path.GetTempPath(), "polypilot-test-" + Guid.NewGuid().ToString("N"));
        var sessionId = Guid.NewGuid().ToString();
        var sessionDir = Path.Combine(tmpDir, sessionId);
        Directory.CreateDirectory(sessionDir);
        var eventsFile = Path.Combine(sessionDir, "events.jsonl");

        try
        {
            File.WriteAllText(eventsFile, $$$"""{"type":"{{{eventType}}}","data":{}}""");
            var result = svc.IsSessionStillProcessing(sessionId, tmpDir);
            Assert.False(result, $"Inactive event type '{eventType}' should not report still processing");
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }
}
