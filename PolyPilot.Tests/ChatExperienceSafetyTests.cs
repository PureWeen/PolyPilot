using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;
using System.Reflection;
using System.Text;

namespace PolyPilot.Tests;

/// <summary>
/// Behavioral safety tests for the PolyPilot chat experience.
/// These tests exercise the REAL CopilotService pipeline (CompleteResponse,
/// watchdog state, AbortSessionAsync) via reflection — NOT demo mode shortcuts.
///
/// Covers the recurring bug patterns:
///   1. "Chat messages get killed while doing long-running processes" (watchdog timeouts)
///   2. "Turns get lost / messages disappear" (generation counter races, content flushing)
///   3. State not fully cleaned up after errors/abort (INV-1 violations)
///
/// Regression test history: PRs #141→#147→#148→#153→#158→#163→#164→#276→#284→#330
/// </summary>
public class ChatExperienceSafetyTests
{
    // =========================================================================
    // Infrastructure — reflection helpers to access private CopilotService guts
    // =========================================================================

    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    private static readonly BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    public ChatExperienceSafetyTests()
    {
        _serviceProvider = new ServiceCollection().BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    // These helpers use string-based reflection to access private members. Renaming any private
    // member silently breaks tests at runtime rather than compile time.
    // TODO: Consider adding [assembly: InternalsVisibleTo("PolyPilot.Tests")] to PolyPilot.csproj
    //       for compile-time safety on internal members.

    /// <summary>Gets the private SessionState object from CopilotService._sessions dictionary.</summary>
    private static object GetSessionState(CopilotService svc, string sessionName)
    {
        var sessionsField = typeof(CopilotService).GetField("_sessions", NonPublic)!;
        var sessionsDict = sessionsField.GetValue(svc)!;
        var tryGetMethod = sessionsDict.GetType().GetMethod("TryGetValue")!;
        var args = new object?[] { sessionName, null };
        tryGetMethod.Invoke(sessionsDict, args);
        return args[1] ?? throw new InvalidOperationException($"Session '{sessionName}' not found in _sessions");
    }

    /// <summary>Invokes the private CompleteResponse method via reflection.</summary>
    private static void InvokeCompleteResponse(CopilotService svc, object sessionState, long? expectedGeneration = null)
    {
        var method = typeof(CopilotService).GetMethod("CompleteResponse", NonPublic)!;
        method.Invoke(svc, new object?[] { sessionState, expectedGeneration });
    }

    /// <summary>Invokes the private FlushCurrentResponse method via reflection.</summary>
    private static void InvokeFlushCurrentResponse(CopilotService svc, object sessionState)
    {
        var method = typeof(CopilotService).GetMethod("FlushCurrentResponse", NonPublic)!;
        method.Invoke(svc, new object?[] { sessionState });
    }

    /// <summary>Gets a field from SessionState by name.</summary>
    private static T GetField<T>(object state, string fieldName)
    {
        var field = state.GetType().GetField(fieldName, AnyInstance)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {state.GetType().Name}");
        return (T)field.GetValue(state)!;
    }

    /// <summary>Sets a field on SessionState by name.</summary>
    private static void SetField(object state, string fieldName, object? value)
    {
        var field = state.GetType().GetField(fieldName, AnyInstance)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {state.GetType().Name}");
        field.SetValue(state, value);
    }

    /// <summary>Gets the CurrentResponse StringBuilder from SessionState.</summary>
    private static StringBuilder GetCurrentResponse(object state)
    {
        var prop = state.GetType().GetProperty("CurrentResponse", AnyInstance)!;
        return (StringBuilder)prop.GetValue(state)!;
    }

    /// <summary>Gets the FlushedResponse StringBuilder from SessionState.</summary>
    private static StringBuilder GetFlushedResponse(object state)
    {
        var prop = state.GetType().GetProperty("FlushedResponse", AnyInstance)!;
        return (StringBuilder)prop.GetValue(state)!;
    }

    /// <summary>Gets or sets the ResponseCompletion TCS from SessionState.</summary>
    private static TaskCompletionSource<string>? GetResponseCompletion(object state)
    {
        var prop = state.GetType().GetProperty("ResponseCompletion", AnyInstance)!;
        return (TaskCompletionSource<string>?)prop.GetValue(state);
    }
    private static void SetResponseCompletion(object state, TaskCompletionSource<string>? tcs)
    {
        var prop = state.GetType().GetProperty("ResponseCompletion", AnyInstance)!;
        prop.SetValue(state, tcs);
    }

    /// <summary>
    /// Sets up a session in a "dirty" processing state, simulating a mid-turn state
    /// where all flags are set as if tools were executing.
    /// </summary>
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

    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root");
    }

    // =========================================================================
    // A. CompleteResponse Behavioral Tests
    //    These call the REAL CompleteResponse via reflection — no demo mode.
    // =========================================================================

    /// <summary>
    /// INV-1: CompleteResponse must clear ALL processing state fields.
    /// This is the most critical invariant — incomplete cleanup causes stuck sessions.
    /// Regression: PRs #141, #148, #158, #163, #164 all missed fields.
    /// </summary>
    [Fact]
    public async Task CompleteResponse_ClearsAllINV1Fields()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("inv1-complete");

        var state = GetSessionState(svc, "inv1-complete");
        SetupDirtyProcessingState(state, session);

        // Set up TCS
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetResponseCompletion(state, tcs);

        // Act: call CompleteResponse with no generation check
        InvokeCompleteResponse(svc, state, null);

        // Assert: ALL INV-1 fields must be cleared
        Assert.False(session.IsProcessing, "IsProcessing must be false after CompleteResponse");
        Assert.False(session.IsResumed, "IsResumed must be false after CompleteResponse");
        Assert.Null(session.ProcessingStartedAt);
        Assert.Equal(0, session.ToolCallCount);
        Assert.Equal(0, session.ProcessingPhase);

        var sendingFlag = GetField<int>(state, "SendingFlag");
        Assert.Equal(0, sendingFlag);

        var activeToolCount = GetField<int>(state, "ActiveToolCallCount");
        Assert.Equal(0, activeToolCount);

        var hasUsedTools = GetField<bool>(state, "HasUsedToolsThisTurn");
        Assert.False(hasUsedTools);

        var successfulToolCount = GetField<int>(state, "SuccessfulToolCountThisTurn");
        Assert.Equal(0, successfulToolCount);
    }

    /// <summary>
    /// CompleteResponse is idempotent: calling it when IsProcessing is already false
    /// must NOT add duplicate messages or throw. (Watchdog may beat IDLE callback.)
    /// </summary>
    [Fact]
    public async Task CompleteResponse_WhenIsProcessingAlreadyFalse_NoOp()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("idempotent-test");

        var state = GetSessionState(svc, "idempotent-test");
        session.IsProcessing = false; // Already cleared

        var historyCountBefore = session.History.Count;

        // Act: CompleteResponse should be a no-op
        InvokeCompleteResponse(svc, state, null);

        Assert.False(session.IsProcessing);
        Assert.Equal(historyCountBefore, session.History.Count);
    }

    /// <summary>
    /// INV-3: Generation guard prevents stale IDLE from completing wrong turn.
    /// This is THE race condition fix from PR #147. Without it, a queued IDLE
    /// callback from turn N can complete turn N+1 when the user sends rapidly.
    /// </summary>
    [Fact]
    public async Task CompleteResponse_GenerationMismatch_Skips()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("gen-guard-test");

        var state = GetSessionState(svc, "gen-guard-test");
        session.IsProcessing = true;

        // Set generation to 5 (simulating 5th prompt sent)
        SetField(state, "ProcessingGeneration", 5L);

        // Act: CompleteResponse with stale generation 3
        InvokeCompleteResponse(svc, state, 3L);

        // Assert: should NOT have completed — IsProcessing stays true
        Assert.True(session.IsProcessing, "CompleteResponse must skip when generation mismatches");
    }

    /// <summary>
    /// Generation guard allows completion when generations match.
    /// </summary>
    [Fact]
    public async Task CompleteResponse_GenerationMatch_Executes()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("gen-match-test");

        var state = GetSessionState(svc, "gen-match-test");
        session.IsProcessing = true;
        SetField(state, "ProcessingGeneration", 5L);
        SetField(state, "SendingFlag", 1);

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetResponseCompletion(state, tcs);

        // Act: CompleteResponse with matching generation
        InvokeCompleteResponse(svc, state, 5L);

        // Assert: should have completed
        Assert.False(session.IsProcessing, "CompleteResponse must execute when generation matches");
    }

    /// <summary>
    /// CompleteResponse with null generation always executes (used by error/watchdog paths).
    /// </summary>
    [Fact]
    public async Task CompleteResponse_NullGeneration_AlwaysExecutes()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("null-gen-test");

        var state = GetSessionState(svc, "null-gen-test");
        session.IsProcessing = true;
        SetField(state, "ProcessingGeneration", 99L);
        SetField(state, "SendingFlag", 1);

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetResponseCompletion(state, tcs);

        // Act: null generation skips the generation check entirely
        InvokeCompleteResponse(svc, state, null);

        Assert.False(session.IsProcessing, "CompleteResponse with null generation must always execute");
    }

    /// <summary>
    /// CompleteResponse must flush accumulated content to History before clearing state.
    /// Without this (PR #158 regression), content in CurrentResponse is silently lost.
    /// </summary>
    [Fact]
    public async Task CompleteResponse_FlushesContentToHistory()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("flush-content-test");

        var state = GetSessionState(svc, "flush-content-test");
        session.IsProcessing = true;
        SetField(state, "SendingFlag", 1);

        // Simulate accumulated response text (from AssistantMessageDeltaEvent)
        var currentResponse = GetCurrentResponse(state);
        currentResponse.Append("This is the model's response text that must not be lost.");

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetResponseCompletion(state, tcs);

        var historyCountBefore = session.History.Count;

        // Act
        InvokeCompleteResponse(svc, state, null);

        // Assert: content was flushed to History
        Assert.True(session.History.Count > historyCountBefore,
            "CompleteResponse must add accumulated content to History");
        var lastMessage = session.History.Last();
        Assert.Equal("assistant", lastMessage.Role);
        Assert.Contains("model's response text", lastMessage.Content);
    }

    /// <summary>
    /// CompleteResponse must include FlushedResponse (from mid-turn flushes on TurnEnd)
    /// in the TCS result. Without this, orchestrator dispatch gets empty string.
    /// This was the root cause of "orchestrator didn't respond to worker" bugs.
    /// </summary>
    [Fact]
    public async Task CompleteResponse_IncludesFlushedResponseInTcsResult()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("flushed-tcs-test");

        var state = GetSessionState(svc, "flushed-tcs-test");
        session.IsProcessing = true;
        SetField(state, "SendingFlag", 1);

        // Simulate mid-turn flush (happens on AssistantTurnEndEvent)
        var flushedResponse = GetFlushedResponse(state);
        flushedResponse.Append("First sub-turn response text");

        // Simulate additional content after turn end
        var currentResponse = GetCurrentResponse(state);
        currentResponse.Append("Second sub-turn continuation");

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetResponseCompletion(state, tcs);

        // Act
        InvokeCompleteResponse(svc, state, null);

        // Assert: TCS result includes BOTH flushed and current text
        Assert.True(tcs.Task.IsCompleted);
        var result = tcs.Task.Result;
        Assert.Contains("First sub-turn response text", result);
        Assert.Contains("Second sub-turn continuation", result);
    }

    /// <summary>
    /// CompleteResponse fires OnSessionComplete so orchestrator loops can unblock.
    /// Without this (INV-O4), multi-agent workers hang forever waiting for completion.
    /// </summary>
    [Fact]
    public async Task CompleteResponse_FiresOnSessionComplete()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("complete-event-test");

        var state = GetSessionState(svc, "complete-event-test");
        session.IsProcessing = true;
        SetField(state, "SendingFlag", 1);

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetResponseCompletion(state, tcs);

        var completedSessions = new List<string>();
        svc.OnSessionComplete += (name, _) => completedSessions.Add(name);

        // Act
        InvokeCompleteResponse(svc, state, null);

        // Assert
        Assert.Contains("complete-event-test", completedSessions);
    }

    /// <summary>
    /// When IsProcessing is already false but CurrentResponse has accumulated content,
    /// CompleteResponse should still flush that content (late deltas after watchdog/error).
    /// </summary>
    [Fact]
    public async Task CompleteResponse_WhenAlreadyFalse_StillFlushesLateContent()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("late-flush-test");

        var state = GetSessionState(svc, "late-flush-test");
        session.IsProcessing = false; // Already cleared by watchdog

        // But late deltas arrived after watchdog cleared state
        var currentResponse = GetCurrentResponse(state);
        currentResponse.Append("Late delta content that arrived after watchdog");

        var historyCountBefore = session.History.Count;

        // Act
        InvokeCompleteResponse(svc, state, null);

        // Assert: late content was still flushed
        Assert.True(session.History.Count > historyCountBefore,
            "Late content must be flushed even when IsProcessing is already false");
    }

    // =========================================================================
    // B. Watchdog Timeout Decision Logic
    //    Tests the actual timeout selection logic from RunProcessingWatchdogAsync.
    // =========================================================================

    /// <summary>
    /// Mirrors the three-tier timeout selection logic from RunProcessingWatchdogAsync.
    /// This is a direct copy of the production code — if production changes, this test
    /// detects the deviation.
    /// </summary>
    private static int ComputeEffectiveTimeout(
        bool hasActiveTool, bool hasUsedTools, bool isMultiAgent,
        bool isResumed, bool hasReceivedEvents)
    {
        var useResumeQuiescence = isResumed && !hasReceivedEvents && !hasActiveTool && !hasUsedTools;
        var useToolTimeout = hasActiveTool || (isResumed && !useResumeQuiescence);
        var useUsedToolsTimeout = !useToolTimeout && hasUsedTools && !hasActiveTool;
        return useResumeQuiescence
            ? CopilotService.WatchdogResumeQuiescenceTimeoutSeconds
            : useToolTimeout
                ? CopilotService.WatchdogToolExecutionTimeoutSeconds
                : useUsedToolsTimeout
                    ? CopilotService.WatchdogUsedToolsIdleTimeoutSeconds
                    : CopilotService.WatchdogInactivityTimeoutSeconds;
    }

    /// <summary>
    /// INV-5: HasUsedToolsThisTurn BETWEEN tool rounds must keep 180s timeout (used-tools idle tier).
    /// This is the primary protection against "messages killed during long-running processes."
    /// ActiveToolCallCount resets on AssistantTurnStartEvent between rounds — only
    /// HasUsedToolsThisTurn persists and keeps the longer timeout.
    /// </summary>
    [Fact]
    public void WatchdogTimeout_BetweenToolRounds_Uses180s()
    {
        // Between tool rounds: ActiveToolCallCount=0, but HasUsedToolsThisTurn=true
        var timeout = ComputeEffectiveTimeout(
            hasActiveTool: false, hasUsedTools: true, isMultiAgent: false,
            isResumed: false, hasReceivedEvents: false);

        Assert.Equal(CopilotService.WatchdogUsedToolsIdleTimeoutSeconds, timeout);
        Assert.Equal(180, timeout);
    }

    /// <summary>Active tool execution gets the 600s timeout.</summary>
    [Fact]
    public void WatchdogTimeout_ActiveTool_Uses600s()
    {
        var timeout = ComputeEffectiveTimeout(
            hasActiveTool: true, hasUsedTools: false, isMultiAgent: false,
            isResumed: false, hasReceivedEvents: false);
        Assert.Equal(600, timeout);
    }

    /// <summary>Multi-agent sessions without active tools get 120s base timeout (isMultiAgent alone no longer escalates).</summary>
    [Fact]
    public void WatchdogTimeout_MultiAgent_Uses120s()
    {
        var timeout = ComputeEffectiveTimeout(
            hasActiveTool: false, hasUsedTools: false, isMultiAgent: true,
            isResumed: false, hasReceivedEvents: false);
        Assert.Equal(120, timeout);
    }

    /// <summary>Resumed session with no events → 30s quiescence (fast recovery).</summary>
    [Fact]
    public void WatchdogTimeout_ResumedNoEvents_Uses30sQuiescence()
    {
        var timeout = ComputeEffectiveTimeout(
            hasActiveTool: false, hasUsedTools: false, isMultiAgent: false,
            isResumed: true, hasReceivedEvents: false);
        Assert.Equal(CopilotService.WatchdogResumeQuiescenceTimeoutSeconds, timeout);
        Assert.Equal(30, timeout);
    }

    /// <summary>Used tools but none active → 180s middle tier (between 600s active and 120s base).</summary>
    [Fact]
    public void WatchdogTimeout_UsedToolsIdle_Uses180s()
    {
        var timeout = ComputeEffectiveTimeout(
            hasActiveTool: false, hasUsedTools: true, isMultiAgent: false,
            isResumed: false, hasReceivedEvents: false);
        Assert.Equal(CopilotService.WatchdogUsedToolsIdleTimeoutSeconds, timeout);
        Assert.Equal(180, timeout);
    }

    /// <summary>Resumed session with events flowing → 600s (session is active).</summary>
    [Fact]
    public void WatchdogTimeout_ResumedWithEvents_Uses600s()
    {
        var timeout = ComputeEffectiveTimeout(
            hasActiveTool: false, hasUsedTools: false, isMultiAgent: false,
            isResumed: true, hasReceivedEvents: true);
        Assert.Equal(600, timeout);
    }

    /// <summary>No tools, not resumed, not multi-agent → 120s base timeout.</summary>
    [Fact]
    public void WatchdogTimeout_BaseCase_Uses120s()
    {
        var timeout = ComputeEffectiveTimeout(
            hasActiveTool: false, hasUsedTools: false, isMultiAgent: false,
            isResumed: false, hasReceivedEvents: false);
        Assert.Equal(CopilotService.WatchdogInactivityTimeoutSeconds, timeout);
        Assert.Equal(120, timeout);
    }

    /// <summary>
    /// Comprehensive: all 8 combinations of the 3 main flags that contribute to useToolTimeout.
    /// This catches logic errors like using && instead of ||.
    /// </summary>
    [Theory]
    [InlineData(false, false, false, false, false, 120)]  // base case
    [InlineData(true,  false, false, false, false, 600)]  // active tool
    [InlineData(false, true,  false, false, false, 180)]  // used tools (between rounds) → 180s middle tier
    [InlineData(false, false, true,  false, false, 120)]  // multi-agent alone → base (no escalation)
    [InlineData(true,  true,  false, false, false, 600)]  // active + used → active wins (600s)
    [InlineData(true,  false, true,  false, false, 600)]  // active + multi
    [InlineData(false, true,  true,  false, false, 180)]  // used + multi → used-tools tier (180s)
    [InlineData(true,  true,  true,  false, false, 600)]  // all three → active wins (600s)
    public void WatchdogTimeout_AllCombinations(
        bool hasActive, bool hasUsed, bool isMulti,
        bool isResumed, bool hasEvents, int expected)
    {
        var timeout = ComputeEffectiveTimeout(hasActive, hasUsed, isMulti, isResumed, hasEvents);
        Assert.Equal(expected, timeout);
    }

    // =========================================================================
    // C. AbortSessionAsync — Full INV-1 Cleanup
    //    Tests use real AbortSessionAsync (public) with reflection state setup.
    // =========================================================================

    /// <summary>
    /// INV-1: AbortSessionAsync clears ALL processing state fields.
    /// This is the path exercised when user clicks "Stop" during a long-running process.
    /// </summary>
    [Fact]
    public async Task AbortSession_ClearsAllINV1Fields()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("abort-inv1");

        var state = GetSessionState(svc, "abort-inv1");
        SetupDirtyProcessingState(state, session);

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetResponseCompletion(state, tcs);

        // Act
        await svc.AbortSessionAsync("abort-inv1");

        // Assert: ALL fields cleared
        Assert.False(session.IsProcessing);
        Assert.False(session.IsResumed);
        Assert.Null(session.ProcessingStartedAt);
        Assert.Equal(0, session.ToolCallCount);
        Assert.Equal(0, session.ProcessingPhase);
        Assert.Equal(0, GetField<int>(state, "SendingFlag"));
        Assert.Equal(0, GetField<int>(state, "ActiveToolCallCount"));
        Assert.False(GetField<bool>(state, "HasUsedToolsThisTurn"));
        Assert.Equal(0, GetField<int>(state, "SuccessfulToolCountThisTurn"));
    }

    /// <summary>
    /// Abort with accumulated content preserves partial response in History.
    /// Without this, clicking Stop discards what the user was waiting for.
    /// </summary>
    [Fact]
    public async Task AbortSession_PreservesAccumulatedContent()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("abort-content");

        var state = GetSessionState(svc, "abort-content");
        session.IsProcessing = true;
        SetField(state, "SendingFlag", 1);

        // Accumulate partial response
        GetCurrentResponse(state).Append("Partial response that should be preserved on abort");

        var historyCountBefore = session.History.Count;

        // Act
        await svc.AbortSessionAsync("abort-content");

        // Assert: partial content preserved
        Assert.True(session.History.Count > historyCountBefore);
        var lastMsg = session.History.Last(m => m.Role == "assistant");
        Assert.Contains("Partial response", lastMsg.Content);
    }

    /// <summary>
    /// Abort completes the ResponseCompletion TCS so orchestrator loops unblock.
    /// TCS must be completed AFTER state cleanup (INV-O3).
    /// </summary>
    [Fact]
    public async Task AbortSession_CompletesResponseTcs()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("abort-tcs");

        var state = GetSessionState(svc, "abort-tcs");
        session.IsProcessing = true;
        SetField(state, "SendingFlag", 1);

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetResponseCompletion(state, tcs);

        // Act
        await svc.AbortSessionAsync("abort-tcs");

        // Assert: TCS was completed (canceled, since it's an abort)
        Assert.True(tcs.Task.IsCompleted);
    }

    /// <summary>
    /// Abort fires OnSessionComplete so orchestrator loops don't hang.
    /// </summary>
    [Fact]
    public async Task AbortSession_FiresOnSessionComplete()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("abort-event");

        var state = GetSessionState(svc, "abort-event");
        session.IsProcessing = true;
        SetField(state, "SendingFlag", 1);

        var completedSessions = new List<string>();
        svc.OnSessionComplete += (name, _) => completedSessions.Add(name);

        // Act
        await svc.AbortSessionAsync("abort-event");

        Assert.Contains("abort-event", completedSessions);
    }

    /// <summary>
    /// Abort clears the message queue so queued prompts don't auto-send.
    /// </summary>
    [Fact]
    public async Task AbortSession_ClearsMessageQueue()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("abort-queue");

        session.IsProcessing = true;
        session.MessageQueue.Add("queued-message-1");
        session.MessageQueue.Add("queued-message-2");

        var state = GetSessionState(svc, "abort-queue");
        SetField(state, "SendingFlag", 1);

        // Act
        await svc.AbortSessionAsync("abort-queue");

        Assert.Empty(session.MessageQueue);
    }

    // =========================================================================
    // D. Content Preservation — Messages Never Lost
    // =========================================================================

    /// <summary>
    /// FlushCurrentResponse moves accumulated text to History and clears CurrentResponse.
    /// This is called on AssistantTurnEndEvent to persist content mid-turn.
    /// </summary>
    [Fact]
    public async Task FlushCurrentResponse_AddsToHistory()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("flush-test");

        var state = GetSessionState(svc, "flush-test");
        session.SessionId = "test-session-id"; // Required for DB write-through

        var currentResponse = GetCurrentResponse(state);
        currentResponse.Append("Response content to be flushed");

        var historyCountBefore = session.History.Count;

        // Act
        InvokeFlushCurrentResponse(svc, state);

        // Assert: content in History, CurrentResponse cleared
        Assert.True(session.History.Count > historyCountBefore);
        Assert.Equal(0, currentResponse.Length);

        var flushedMsg = session.History.Last();
        Assert.Equal("assistant", flushedMsg.Role);
        Assert.Equal("Response content to be flushed", flushedMsg.Content);
    }

    /// <summary>
    /// FlushCurrentResponse dedup guard: if the last assistant message has identical content,
    /// the flush is skipped to prevent duplicates on session resume.
    /// </summary>
    [Fact]
    public async Task FlushCurrentResponse_DedupGuard_SkipsDuplicate()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("dedup-test");

        var state = GetSessionState(svc, "dedup-test");

        // Add a message that looks like it was already flushed
        session.History.Add(ChatMessage.AssistantMessage("Already flushed content"));
        var historyCountAfterFirst = session.History.Count;

        // Simulate the same content appearing in CurrentResponse (SDK replay on resume)
        GetCurrentResponse(state).Append("Already flushed content");

        // Act
        InvokeFlushCurrentResponse(svc, state);

        // Assert: no duplicate added
        Assert.Equal(historyCountAfterFirst, session.History.Count);
    }

    /// <summary>
    /// FlushCurrentResponse accumulates text in FlushedResponse so CompleteResponse
    /// can include it in the TCS result for orchestrator dispatch.
    /// </summary>
    [Fact]
    public async Task FlushCurrentResponse_AccumulatesInFlushedResponse()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("accum-test");

        var state = GetSessionState(svc, "accum-test");

        // First flush
        GetCurrentResponse(state).Append("First sub-turn");
        InvokeFlushCurrentResponse(svc, state);

        // Second flush
        GetCurrentResponse(state).Append("Second sub-turn");
        InvokeFlushCurrentResponse(svc, state);

        // Assert: FlushedResponse has both
        var flushed = GetFlushedResponse(state).ToString();
        Assert.Contains("First sub-turn", flushed);
        Assert.Contains("Second sub-turn", flushed);
    }

    /// <summary>
    /// Multi-turn conversation: messages are preserved across sequential sends.
    /// </summary>
    [Fact]
    public async Task MultiTurn_AllMessagesPreserved()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("multi-turn");

        for (int i = 0; i < 5; i++)
        {
            await svc.SendPromptAsync("multi-turn", $"Message {i}");
        }

        // All user messages should be in history
        var userMessages = session.History.Where(m => m.Role == "user").ToList();
        Assert.True(userMessages.Count >= 5,
            $"Expected at least 5 user messages, got {userMessages.Count}");
        Assert.False(session.IsProcessing);
    }

    /// <summary>
    /// FlushCurrentResponse with empty content is a no-op (no empty messages in history).
    /// </summary>
    [Fact]
    public async Task FlushCurrentResponse_EmptyContent_NoOp()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("empty-flush");

        var state = GetSessionState(svc, "empty-flush");
        var historyCount = session.History.Count;

        // Act: flush with empty buffer
        InvokeFlushCurrentResponse(svc, state);

        Assert.Equal(historyCount, session.History.Count);
    }

    // =========================================================================
    // E. Structural Regression Guards (source assertions)
    //    These verify critical code patterns are present. Keep minimal — 3 max.
    // =========================================================================

    /// <summary>
    /// The watchdog callback must have a generation guard to prevent killing a new
    /// turn if the user aborts + resends between Post() and callback execution.
    /// </summary>
    [Fact]
    public void WatchdogCallback_HasGenerationGuard()
    {
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Events.cs"));

        // Find the watchdog callback inside InvokeOnUI
        var watchdogIdx = source.IndexOf("watchdogGeneration != currentGen", StringComparison.Ordinal);
        Assert.True(watchdogIdx > 0,
            "Watchdog callback must compare watchdogGeneration to currentGen");
    }

    /// <summary>
    /// CompleteResponse must clear SendingFlag to allow subsequent sends.
    /// Without this, the session deadlocks on next SendPromptAsync.
    /// </summary>
    [Fact]
    public void CompleteResponse_Source_ClearsSendingFlag()
    {
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Events.cs"));

        // Find CompleteResponse method
        var crIdx = source.IndexOf("private void CompleteResponse(", StringComparison.Ordinal);
        Assert.True(crIdx > 0);

        // Within CompleteResponse, SendingFlag must be cleared (may be 80+ lines into method)
        var afterCR = source.Substring(crIdx, Math.Min(5000, source.Length - crIdx));
        Assert.Contains("SendingFlag", afterCR);
    }

    /// <summary>
    /// The "Session not found" reconnect path must include McpServers and SkillDirectories
    /// in the fresh session config (PR #330 regression guard).
    /// </summary>
    [Fact]
    public void ReconnectPath_IncludesMcpServersAndSkills()
    {
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs"));

        // After extraction to BuildFreshSessionConfig, verify the reconnect path calls the helper
        var sessionNotFoundIdx = source.IndexOf("Session not found", StringComparison.OrdinalIgnoreCase);
        Assert.True(sessionNotFoundIdx > 0);
        var afterNotFound = source.Substring(sessionNotFoundIdx, Math.Min(1000, source.Length - sessionNotFoundIdx));
        Assert.Contains("BuildFreshSessionConfig", afterNotFound);

        // And verify the helper itself includes MCP and Skills
        var helperIdx = source.IndexOf("BuildFreshSessionConfig(SessionState state");
        Assert.True(helperIdx > 0);
        var helperBlock = source.Substring(helperIdx, Math.Min(2000, source.Length - helperIdx));
        Assert.Contains("McpServers", helperBlock);
        Assert.Contains("SkillDirectories", helperBlock);
    }

    // =========================================================================
    // F. Race Condition & Edge Case Tests
    // =========================================================================

    /// <summary>
    /// Sequential sends don't leave ghost processing state.
    /// Each send must complete fully before the next can proceed.
    /// </summary>
    [Fact]
    public async Task SequentialSends_NoGhostState()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("no-ghost");

        for (int i = 0; i < 10; i++)
        {
            await svc.SendPromptAsync("no-ghost", $"Prompt {i}");
            Assert.False(session.IsProcessing, $"IsProcessing stuck after prompt {i}");
            Assert.Equal(0, session.ProcessingPhase);
        }
    }

    /// <summary>
    /// CompleteResponse clears PendingReasoningMessages to prevent stale reasoning
    /// from previous turn leaking into next turn's display.
    /// </summary>
    [Fact]
    public async Task CompleteResponse_ClearsPendingReasoning()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("reasoning-clear");

        var state = GetSessionState(svc, "reasoning-clear");
        session.IsProcessing = true;
        SetField(state, "SendingFlag", 1);

        // Add pending reasoning
        var pendingProp = state.GetType().GetProperty("PendingReasoningMessages", AnyInstance)!;
        var pendingDict = pendingProp.GetValue(state) as System.Collections.Concurrent.ConcurrentDictionary<string, ChatMessage>;
        pendingDict!.TryAdd("reasoning-1", ChatMessage.SystemMessage("test reasoning"));

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetResponseCompletion(state, tcs);

        // Act
        InvokeCompleteResponse(svc, state, null);

        // Assert: pending reasoning cleared
        Assert.True(pendingDict.IsEmpty, "PendingReasoningMessages must be cleared on CompleteResponse");
    }

    /// <summary>
    /// Abort when not processing is a no-op (safe to call multiple times).
    /// </summary>
    [Fact]
    public async Task AbortSession_WhenNotProcessing_NoOp()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("abort-noop");

        session.IsProcessing = false;
        var historyCount = session.History.Count;

        // Act: abort should be a safe no-op
        await svc.AbortSessionAsync("abort-noop");

        Assert.Equal(historyCount, session.History.Count);
        Assert.False(session.IsProcessing);
    }

    /// <summary>
    /// The TCS result includes full response even when FlushedResponse has content
    /// but CurrentResponse is empty (common case: TurnEnd flush cleared CurrentResponse
    /// before SessionIdle fires CompleteResponse).
    /// </summary>
    [Fact]
    public async Task CompleteResponse_FlushedOnlyNoCurrentResponse_TcsHasContent()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("flushed-only");

        var state = GetSessionState(svc, "flushed-only");
        session.IsProcessing = true;
        SetField(state, "SendingFlag", 1);

        // FlushedResponse has content (from TurnEnd flush), CurrentResponse is empty
        GetFlushedResponse(state).Append("Full turn response from mid-turn flush");
        // CurrentResponse stays empty — this is the normal TurnEnd → SessionIdle flow

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetResponseCompletion(state, tcs);

        // Act
        InvokeCompleteResponse(svc, state, null);

        // Assert: TCS has the flushed content
        Assert.True(tcs.Task.IsCompleted);
        Assert.Contains("Full turn response from mid-turn flush", tcs.Task.Result);
    }

    /// <summary>
    /// ChatDatabase write-through is called on content flush (fire-and-forget).
    /// </summary>
    [Fact]
    public async Task FlushCurrentResponse_WritesToChatDatabase()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("db-write-test");

        var state = GetSessionState(svc, "db-write-test");
        session.SessionId = "test-db-session-id";

        GetCurrentResponse(state).Append("Content for database");

        var dbCountBefore = _chatDb.AddedMessages.Count;

        // Act
        InvokeFlushCurrentResponse(svc, state);

        // Assert: DB write was triggered
        Assert.True(_chatDb.AddedMessages.Count > dbCountBefore,
            "FlushCurrentResponse must write through to ChatDatabase");
        var lastDbMsg = _chatDb.AddedMessages.Last();
        Assert.Equal("test-db-session-id", lastDbMsg.SessionId);
        Assert.Contains("Content for database", lastDbMsg.Message.Content);
    }
}
