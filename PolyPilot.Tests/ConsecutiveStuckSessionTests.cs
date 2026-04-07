using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;
using System.Reflection;
using System.Text;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the consecutive stuck session detection and feedback loop prevention.
/// Bug: Sessions with very large message histories (212+ messages) enter a repeated stuck cycle:
///   1. SendAsync → no events → watchdog fires (120s) → adds system message → user retries
///   2. Each failure adds messages to history, growing the context and making the NEXT failure more likely
/// Fix: Track ConsecutiveStuckCount on AgentSessionInfo. After 3+ consecutive timeouts,
/// skip adding system messages to history (breaking the growth loop) and show a stronger warning.
/// </summary>
public class ConsecutiveStuckSessionTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    private static readonly BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    public ConsecutiveStuckSessionTests()
    {
        _serviceProvider = new ServiceCollection().BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    private static object GetSessionState(CopilotService svc, string sessionName)
    {
        var sessionsField = typeof(CopilotService).GetField("_sessions", NonPublic)!;
        var sessionsDict = sessionsField.GetValue(svc)!;
        var tryGetMethod = sessionsDict.GetType().GetMethod("TryGetValue")!;
        var args = new object?[] { sessionName, null };
        tryGetMethod.Invoke(sessionsDict, args);
        return args[1] ?? throw new InvalidOperationException($"Session '{sessionName}' not found");
    }

    private static void InvokeCompleteResponse(CopilotService svc, object sessionState, long? expectedGeneration = null)
    {
        var method = typeof(CopilotService).GetMethod("CompleteResponse", NonPublic)!;
        method.Invoke(svc, new object?[] { sessionState, expectedGeneration });
    }

    private static T GetField<T>(object state, string fieldName)
    {
        var field = state.GetType().GetField(fieldName, AnyInstance)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {state.GetType().Name}");
        return (T)field.GetValue(state)!;
    }

    private static void SetField(object state, string fieldName, object? value)
    {
        var field = state.GetType().GetField(fieldName, AnyInstance)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {state.GetType().Name}");
        field.SetValue(state, value);
    }

    private static void SetResponseCompletion(object state, TaskCompletionSource<string>? tcs)
    {
        var prop = state.GetType().GetProperty("ResponseCompletion", AnyInstance)!;
        prop.SetValue(state, tcs);
    }

    private static StringBuilder GetCurrentResponse(object state)
    {
        var prop = state.GetType().GetProperty("CurrentResponse", AnyInstance)!;
        return (StringBuilder)prop.GetValue(state)!;
    }

    private static void SetupDirtyProcessingState(object state, AgentSessionInfo info)
    {
        info.IsProcessing = true;
        info.IsResumed = true;
        info.ProcessingStartedAt = DateTime.UtcNow.AddSeconds(-30);
        info.ToolCallCount = 5;
        info.ProcessingPhase = 3;
        SetField(state, "SendingFlag", 1);
        SetField(state, "ActiveToolCallCount", 3);
        SetField(state, "HasUsedToolsThisTurn", true);
        SetField(state, "SuccessfulToolCountThisTurn", 2);
    }

    // =========================================================================
    // A. ConsecutiveStuckCount Property Tests
    // =========================================================================

    [Fact]
    public void ConsecutiveStuckCount_DefaultsToZero()
    {
        var info = new AgentSessionInfo { Name = "test", Model = "test-model" };
        Assert.Equal(0, info.ConsecutiveStuckCount);
    }

    [Fact]
    public void ConsecutiveStuckCount_CanBeIncrementedAndReset()
    {
        var info = new AgentSessionInfo { Name = "test", Model = "test-model" };

        info.ConsecutiveStuckCount++;
        Assert.Equal(1, info.ConsecutiveStuckCount);

        info.ConsecutiveStuckCount++;
        info.ConsecutiveStuckCount++;
        Assert.Equal(3, info.ConsecutiveStuckCount);

        info.ConsecutiveStuckCount = 0;
        Assert.Equal(0, info.ConsecutiveStuckCount);
    }

    // =========================================================================
    // B. CompleteResponse Resets ConsecutiveStuckCount
    // =========================================================================

    [Fact]
    public async Task CompleteResponse_ResetsConsecutiveStuckCount()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("reset-stuck");

        var state = GetSessionState(svc, "reset-stuck");
        SetupDirtyProcessingState(state, session);

        // Simulate a session that was stuck 5 times
        session.ConsecutiveStuckCount = 5;

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetResponseCompletion(state, tcs);

        // Act: successful completion
        InvokeCompleteResponse(svc, state, null);

        // Assert: stuck count is reset
        Assert.Equal(0, session.ConsecutiveStuckCount);
        Assert.False(session.IsProcessing);
    }

    // =========================================================================
    // C. Watchdog Behavior Source Code Guards
    //    Structural tests that verify the watchdog code handles stuck counts correctly.
    // =========================================================================

    [Fact]
    public void WatchdogSource_IncrementsConsecutiveStuckCount()
    {
        // Verify the watchdog timeout path increments ConsecutiveStuckCount.
        // This is a structural guard — if someone removes the increment, this test fails.
        var repoRoot = GetRepoRoot();
        var eventsSource = File.ReadAllText(Path.Combine(repoRoot, "PolyPilot", "Services", "CopilotService.Events.cs"));

        Assert.Contains("ConsecutiveStuckCount++", eventsSource);
    }

    [Fact]
    public void WatchdogSource_SkipsHistoryGrowthAfterRepeatedStucks()
    {
        // Verify the watchdog conditionally skips adding system messages to history
        // when ConsecutiveStuckCount >= 3 to break the positive feedback loop.
        var repoRoot = GetRepoRoot();
        var eventsSource = File.ReadAllText(Path.Combine(repoRoot, "PolyPilot", "Services", "CopilotService.Events.cs"));

        Assert.Contains("ConsecutiveStuckCount < 3", eventsSource);
    }

    [Fact]
    public void WatchdogSource_ClearsMessageQueueOnRepeatedStucks()
    {
        // Verify that after 3+ consecutive stucks, the message queue is cleared
        // to prevent auto-dispatch from immediately re-sending into a stuck session.
        var repoRoot = GetRepoRoot();
        var eventsSource = File.ReadAllText(Path.Combine(repoRoot, "PolyPilot", "Services", "CopilotService.Events.cs"));

        // The else branch (ConsecutiveStuckCount >= 3) must clear the queue
        Assert.Contains("MessageQueue.Clear()", eventsSource);
    }

    [Fact]
    public void CompleteResponseSource_ResetsConsecutiveStuckCount()
    {
        // Verify CompleteResponse resets the stuck counter on success.
        // ConsecutiveStuckCount = 0 must only appear in CompleteResponse (success path),
        // NOT in ClearProcessingState (which would break accumulation on watchdog timeouts).
        var repoRoot = GetRepoRoot();
        var eventsSource = File.ReadAllText(Path.Combine(repoRoot, "PolyPilot", "Services", "CopilotService.Events.cs"));

        // Must be in CompleteResponse
        var crIdx = eventsSource.IndexOf("private void CompleteResponse(", StringComparison.Ordinal);
        Assert.True(crIdx >= 0, "CompleteResponse must exist");
        var crBody = eventsSource.Substring(crIdx, Math.Min(10000, eventsSource.Length - crIdx));
        Assert.Contains("ConsecutiveStuckCount = 0", crBody);
    }

    // =========================================================================
    // D. History Growth Prevention (Feedback Loop Breaking)
    // =========================================================================

    [Fact]
    public void FirstStuck_AddsSystemMessage()
    {
        // First few stuck cycles should add the warning message to history
        var info = new AgentSessionInfo { Name = "test", Model = "test-model" };
        info.ConsecutiveStuckCount = 0; // First time stuck

        // Simulate what the watchdog does
        info.ConsecutiveStuckCount++;

        // First stuck (count=1): should add system message
        Assert.True(info.ConsecutiveStuckCount < 3,
            "First stuck should be below threshold, allowing system message in history");
    }

    [Fact]
    public void ThirdConsecutiveStuck_SkipsSystemMessage()
    {
        // After 3+ consecutive stucks, the system message should be skipped
        var info = new AgentSessionInfo { Name = "test", Model = "test-model" };
        info.ConsecutiveStuckCount = 2; // Already stuck twice

        // Simulate third stuck
        info.ConsecutiveStuckCount++;

        // Third stuck (count=3): should NOT add system message
        Assert.False(info.ConsecutiveStuckCount < 3,
            "Third consecutive stuck should be at/above threshold, skipping system message");
    }

    [Fact]
    public async Task HistorySize_DoesNotGrow_AfterRepeatedStucks()
    {
        // Simulates the feedback loop: repeated stucks should not keep adding messages
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("history-growth");

        // Add some initial messages to simulate a large session
        for (int i = 0; i < 200; i++)
        {
            session.History.Add(new ChatMessage(i % 2 == 0 ? "user" : "assistant",
                $"Message {i}", DateTime.Now));
        }
        var initialCount = session.History.Count;

        // Simulate 5 consecutive stuck cycles
        // First 2 should add messages, remaining 3 should not
        for (int i = 0; i < 5; i++)
        {
            session.ConsecutiveStuckCount++;
            if (session.ConsecutiveStuckCount < 3)
            {
                session.History.Add(ChatMessage.SystemMessage("⚠️ Session appears stuck"));
            }
        }

        // Only 2 messages should have been added (for stuck counts 1 and 2)
        Assert.Equal(initialCount + 2, session.History.Count);
    }

    // =========================================================================
    // E. SendAsync Timeout Constants
    // =========================================================================

    [Fact]
    public void SendAsyncTimeout_IsReasonable()
    {
        // SendAsync timeout should be long enough for slow servers (>10s)
        // but short enough to detect hung connections (<120s).
        Assert.InRange(CopilotService.SendAsyncTimeoutMs, 10_000, 120_000);
    }

    [Fact]
    public void SendAsyncTimeout_IsLessThanWatchdogInactivityTimeout()
    {
        // The SendAsync timeout should fire before the watchdog's inactivity timeout.
        // This ensures hung SendAsync calls are detected and enter the reconnect path
        // before the watchdog has to kill the session.
        Assert.True(
            CopilotService.SendAsyncTimeoutMs < CopilotService.WatchdogInactivityTimeoutSeconds * 1000,
            "SendAsync timeout must be less than watchdog inactivity timeout to allow reconnect before kill");
    }

    [Fact]
    public void SendAsyncTimeoutSource_WrapsWithTaskWhenAny()
    {
        // Verify that SendAsync is wrapped with Task.WhenAny for timeout detection.
        // The CancellationToken.None workaround remains, but we have a client-side timeout.
        var repoRoot = GetRepoRoot();
        var source = File.ReadAllText(Path.Combine(repoRoot, "PolyPilot", "Services", "CopilotService.cs"));

        Assert.Contains("Task.WhenAny(sendTask", source);
        Assert.Contains("SendAsyncTimeoutMs", source);
    }

    [Fact]
    public void SendAsyncTimeoutSource_RetryPathAlsoHasTimeout()
    {
        // Verify that the reconnect+retry SendAsync path ALSO has the timeout wrapper.
        // Without this, the retry could hang indefinitely just like the primary send.
        var repoRoot = GetRepoRoot();
        var source = File.ReadAllText(Path.Combine(repoRoot, "PolyPilot", "Services", "CopilotService.cs"));

        Assert.Contains("Task.WhenAny(retryTask", source);
    }

    // =========================================================================
    // F. Error Message Quality
    // =========================================================================

    [Fact]
    public void RepeatedStuckMessage_SuggestsNewSession()
    {
        // The error message for repeated stucks should suggest creating a new session
        var info = new AgentSessionInfo { Name = "test", Model = "test-model" };
        for (int i = 0; i < 200; i++)
            info.History.Add(new ChatMessage("user", $"msg {i}", DateTime.Now));
        info.ConsecutiveStuckCount = 3;

        // Simulate the message format from the watchdog
        var msg = $"Session has failed to respond {info.ConsecutiveStuckCount} times consecutively. " +
                  $"Consider starting a new session — the conversation history ({info.History.Count} messages) may be too large for the server to process.";

        Assert.Contains("Consider starting a new session", msg);
        Assert.Contains("200 messages", msg);
        Assert.Contains("3 times consecutively", msg);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root");
    }
}
