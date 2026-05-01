using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Behavioral tests for the orchestration relaunch fix (issue #400).
/// Verifies that MonitorAndSynthesizeAsync correctly detects active workers
/// that are lazy placeholders (Session == null) after app relaunch, instead of
/// prematurely considering them idle due to the InvokeOnUI race condition.
/// </summary>
[Collection("BaseDir")]
public class OrchestrationRelaunchTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly IServiceProvider _serviceProvider;

    private static readonly BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly BindingFlags NonPublicStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    public OrchestrationRelaunchTests()
    {
        _serviceProvider = new ServiceCollection().BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, new RepoManager(), _serviceProvider, _demoService);

    #region Helpers

    /// <summary>Get the SessionStatePath used by CopilotService (redirected to test temp dir).</summary>
    private static string GetSessionStatePath()
    {
        var prop = typeof(CopilotService).GetProperty("SessionStatePath", NonPublicStatic)!;
        return (string)prop.GetValue(null)!;
    }

    /// <summary>Get the SessionState type (private nested class).</summary>
    private static Type GetSessionStateType()
    {
        return typeof(CopilotService).GetNestedType("SessionState", BindingFlags.NonPublic)!;
    }

    /// <summary>Create a SessionState with the given AgentSessionInfo via reflection.
    /// GetUninitializedObject bypasses constructors, so Session is null (lazy placeholder).
    /// We manually initialize fields that would be set by field initializers.</summary>
    private static object CreateSessionState(AgentSessionInfo info)
    {
        var stateType = GetSessionStateType();
        var state = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(stateType);
        stateType.GetProperty("Info")!.SetValue(state, info);

        // Initialize readonly field that would normally be set by the field initializer
        var signalField = stateType.GetField("PrematureIdleSignal", AnyInstance);
        signalField?.SetValue(state, new ManualResetEventSlim(initialState: false));

        return state;
    }

    /// <summary>Add a session to the CopilotService._sessions dictionary.</summary>
    private static void AddSession(CopilotService svc, string name, object sessionState)
    {
        var field = typeof(CopilotService).GetField("_sessions", NonPublic)!;
        var dict = field.GetValue(svc)!;
        dict.GetType().GetMethod("TryAdd")!.Invoke(dict, new[] { name, sessionState });
    }

    /// <summary>Create an events.jsonl file for a given session ID in the test directory.</summary>
    private static string CreateEventsFile(string sessionId, params string[] lines)
    {
        var sessionDir = Path.Combine(GetSessionStatePath(), sessionId);
        Directory.CreateDirectory(sessionDir);
        var eventsFile = Path.Combine(sessionDir, "events.jsonl");
        File.WriteAllLines(eventsFile, lines);
        return eventsFile;
    }

    /// <summary>Build a JSONL event line with the given type.</summary>
    private static string BuildEventLine(string type, object data)
    {
        var obj = new { type, data, timestamp = DateTimeOffset.UtcNow.ToString("o") };
        return JsonSerializer.Serialize(obj);
    }

    #endregion

    #region IsWorkerIdleForMonitor behavioral tests

    [Fact]
    public void IsWorkerIdleForMonitor_SessionNotFound_ReturnsTrue()
    {
        // Worker name not in _sessions → treat as idle (handled in result collection)
        var svc = CreateService();
        Assert.True(svc.IsWorkerIdleForMonitor("nonexistent-worker"));
    }

    [Fact]
    public void IsWorkerIdleForMonitor_ConnectedAndProcessing_ReturnsFalse()
    {
        // Worker has IsProcessing=true → not idle regardless of Session state
        var svc = CreateService();
        var sessionId = Guid.NewGuid().ToString();
        var info = new AgentSessionInfo
        {
            Name = "worker-1",
            Model = "gpt-4",
            SessionId = sessionId,
            WorkingDirectory = "/tmp"
        };
        info.IsProcessing = true;

        var state = CreateSessionState(info);
        AddSession(svc, "worker-1", state);

        Assert.False(svc.IsWorkerIdleForMonitor("worker-1"));
    }

    [Fact]
    public void IsWorkerIdleForMonitor_ConnectedAndIdle_ReturnsTrue()
    {
        // Worker has IsProcessing=false and Session would be connected → idle
        var svc = CreateService();
        var sessionId = Guid.NewGuid().ToString();
        var info = new AgentSessionInfo
        {
            Name = "worker-1",
            Model = "gpt-4",
            SessionId = sessionId,
            WorkingDirectory = "/tmp"
        };
        info.IsProcessing = false;

        var state = CreateSessionState(info);
        AddSession(svc, "worker-1", state);

        // No events.jsonl at all → IsSessionStillProcessing returns false
        Assert.True(svc.IsWorkerIdleForMonitor("worker-1"));
    }

    [Fact]
    public void IsWorkerIdleForMonitor_LazyPlaceholderWithActiveCli_ReturnsFalse()
    {
        // KEY FIX TEST: Worker is a lazy placeholder (Session == null) with
        // IsProcessing=false (InvokeOnUI hasn't fired yet), but the CLI
        // events.jsonl shows active tool execution → NOT idle.
        var svc = CreateService();
        var sessionId = Guid.NewGuid().ToString();
        var info = new AgentSessionInfo
        {
            Name = "worker-1",
            Model = "gpt-4",
            SessionId = sessionId,
            WorkingDirectory = "/tmp"
        };
        info.IsProcessing = false; // InvokeOnUI hasn't set this yet

        var state = CreateSessionState(info);
        // Session is null (lazy placeholder) — GetUninitializedObject doesn't call constructor
        AddSession(svc, "worker-1", state);

        // Create events.jsonl showing active tool execution
        CreateEventsFile(sessionId,
            BuildEventLine("assistant.turn_start", new { }),
            BuildEventLine("tool.execution_start", new { toolCallId = "tc1", name = "bash" }));

        Assert.False(svc.IsWorkerIdleForMonitor("worker-1"));
    }

    [Fact]
    public void IsWorkerIdleForMonitor_LazyPlaceholderWithIdleCli_ReturnsTrue()
    {
        // Worker is a lazy placeholder but CLI has completed (terminal event) → idle
        var svc = CreateService();
        var sessionId = Guid.NewGuid().ToString();
        var info = new AgentSessionInfo
        {
            Name = "worker-1",
            Model = "gpt-4",
            SessionId = sessionId,
            WorkingDirectory = "/tmp"
        };
        info.IsProcessing = false;

        var state = CreateSessionState(info);
        AddSession(svc, "worker-1", state);

        // Create events.jsonl showing session shutdown (terminal)
        CreateEventsFile(sessionId,
            BuildEventLine("assistant.turn_start", new { }),
            BuildEventLine("assistant.message", new { content = "Done!" }),
            BuildEventLine("session.shutdown", new { }));

        Assert.True(svc.IsWorkerIdleForMonitor("worker-1"));
    }

    [Fact]
    public void IsWorkerIdleForMonitor_LazyPlaceholderWithNoEvents_ReturnsTrue()
    {
        // Worker is a lazy placeholder with no events.jsonl → idle
        var svc = CreateService();
        var sessionId = Guid.NewGuid().ToString();
        var info = new AgentSessionInfo
        {
            Name = "worker-1",
            Model = "gpt-4",
            SessionId = sessionId,
            WorkingDirectory = "/tmp"
        };
        info.IsProcessing = false;

        var state = CreateSessionState(info);
        AddSession(svc, "worker-1", state);

        // No events.jsonl created → IsSessionStillProcessing returns false
        Assert.True(svc.IsWorkerIdleForMonitor("worker-1"));
    }

    [Fact]
    public void IsWorkerIdleForMonitor_LazyPlaceholderWithStaleEvents_ReturnsTrue()
    {
        // Worker is a lazy placeholder but events.jsonl is old → CLI finished long ago → idle
        var svc = CreateService();
        var sessionId = Guid.NewGuid().ToString();
        var info = new AgentSessionInfo
        {
            Name = "worker-1",
            Model = "gpt-4",
            SessionId = sessionId,
            WorkingDirectory = "/tmp"
        };
        info.IsProcessing = false;

        var state = CreateSessionState(info);
        AddSession(svc, "worker-1", state);

        // Create events.jsonl showing active event BUT backdate the file
        var eventsFile = CreateEventsFile(sessionId,
            BuildEventLine("tool.execution_start", new { toolCallId = "tc1", name = "bash" }));

        // Backdate to be older than the staleness threshold
        var staleTime = DateTime.UtcNow.AddSeconds(-(CopilotService.WatchdogToolExecutionTimeoutSeconds + 60));
        File.SetLastWriteTimeUtc(eventsFile, staleTime);

        Assert.True(svc.IsWorkerIdleForMonitor("worker-1"));
    }

    [Fact]
    public void IsWorkerIdleForMonitor_LazyPlaceholderNoSessionId_ReturnsTrue()
    {
        // Worker is a lazy placeholder with no SessionId → can't check events → idle
        var svc = CreateService();
        var info = new AgentSessionInfo
        {
            Name = "worker-1",
            Model = "gpt-4",
            SessionId = null!,
            WorkingDirectory = "/tmp"
        };
        info.IsProcessing = false;

        var state = CreateSessionState(info);
        AddSession(svc, "worker-1", state);

        Assert.True(svc.IsWorkerIdleForMonitor("worker-1"));
    }

    #endregion

    #region MonitorAndSynthesizeAsync source structure (supplementary invariant guards)

    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root");
    }

    [Fact]
    public void MonitorAndSynthesizeAsync_CallsIsWorkerIdleForMonitor()
    {
        // Structural guard: MonitorAndSynthesizeAsync must use IsWorkerIdleForMonitor
        // instead of directly checking state.Info.IsProcessing.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Organization.cs"));

        var methodStart = source.IndexOf("private async Task MonitorAndSynthesizeAsync");
        Assert.True(methodStart >= 0, "MonitorAndSynthesizeAsync method not found");

        var methodBody = source.Substring(methodStart, Math.Min(2000, source.Length - methodStart));

        Assert.Contains("IsWorkerIdleForMonitor", methodBody);
    }

    [Fact]
    public void MonitorAndSynthesizeAsync_WaitsForRestoreToComplete()
    {
        // Structural guard: MonitorAndSynthesizeAsync must wait for IsRestoring to become false
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Organization.cs"));

        var methodStart = source.IndexOf("private async Task MonitorAndSynthesizeAsync");
        Assert.True(methodStart >= 0, "MonitorAndSynthesizeAsync method not found");

        var methodBody = source.Substring(methodStart, Math.Min(2000, source.Length - methodStart));

        Assert.Contains("IsRestoring", methodBody);
    }

    #endregion
}
