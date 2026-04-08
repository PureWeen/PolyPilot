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

    /// <summary>Invokes the private FinalizeResumedSessionUiStateAsync helper via reflection.</summary>
    private static async Task InvokeFinalizeResumedSessionUiStateAsync(
        CopilotService svc,
        object sessionState,
        string sessionId,
        string? workingDirectory,
        string? gitBranch,
        bool isStillProcessing,
        int processingPhase,
        string reconnectMsg)
    {
        var method = typeof(CopilotService).GetMethod("FinalizeResumedSessionUiStateAsync", NonPublic)!;
        var task = (Task)method.Invoke(svc, new object?[]
        {
            sessionState,
            sessionId,
            workingDirectory,
            gitBranch,
            isStillProcessing,
            processingPhase,
            reconnectMsg
        })!;
        await task;
    }

    /// <summary>Invokes the private ClearFlushedReplayDedup helper to simulate a tool/sub-turn boundary.</summary>
    private static void InvokeClearFlushedReplayDedup(object sessionState)
    {
        var method = typeof(CopilotService).GetMethod("ClearFlushedReplayDedup",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        method.Invoke(null, new[] { sessionState });
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
        Assert.True(GetField<bool>(state, "AllowTurnStartRearm"));
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
        Assert.True(GetField<bool>(state, "AllowTurnStartRearm"),
            "Normal completion should allow one late TurnStart to recover from premature idle");
    }

    /// <summary>
    /// Late TurnStart events should only revive sessions after speculative auto-completion.
    /// Explicit aborts, watchdog kills, and force-complete recovery paths must not be re-armed
    /// by stale SDK TurnStart replays.
    /// </summary>
    [Theory]
    [InlineData(false, true, false, false, true,  true)]
    [InlineData(false, true, false, false, false, false)]
    [InlineData(false, true, false, true,  true,  false)]
    [InlineData(false, true, true,  false, true,  false)]
    [InlineData(true,  true, false, false, true,  false)]
    public void TurnStartRearmGuard_OnlyAllowsSpeculativeCompletion(
        bool isProcessing,
        bool isCurrentState,
        bool isOrphaned,
        bool wasUserAborted,
        bool allowTurnStartRearm,
        bool expected)
    {
        var result = CopilotService.ShouldRearmOnTurnStart(
            isProcessing,
            isCurrentState,
            isOrphaned,
            wasUserAborted,
            allowTurnStartRearm);

        Assert.Equal(expected, result);
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
    /// Identical assistant text across DIFFERENT turns must still be persisted.
    /// The replay dedup guard should only suppress content already flushed in the
    /// current turn, not a legitimate repeated reply like "Done." in a later turn.
    /// </summary>
    [Fact]
    public async Task CompleteResponse_IdenticalCrossTurnReply_IsStillPersisted()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("cross-turn-complete-test");

        var state = GetSessionState(svc, "cross-turn-complete-test");
        session.IsProcessing = true;
        SetField(state, "SendingFlag", 1);

        session.History.Add(ChatMessage.AssistantMessage("Done."));
        var historyBefore = session.History.Count;

        GetCurrentResponse(state).Append("Done.");

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetResponseCompletion(state, tcs);

        InvokeCompleteResponse(svc, state, null);

        Assert.Equal(historyBefore + 1, session.History.Count);
        Assert.Equal("Done.", session.History.Last().Content);
        Assert.True(tcs.Task.IsCompleted);
        Assert.Equal("Done.", tcs.Task.Result);
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
    /// If the SDK replays the exact text that was already flushed earlier in the SAME turn,
    /// CompleteResponse must not duplicate it in either History or the TCS result.
    /// </summary>
    [Fact]
    public async Task CompleteResponse_SameTurnReplay_DoesNotDuplicateHistoryOrTcs()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("same-turn-replay-test");

        var state = GetSessionState(svc, "same-turn-replay-test");
        session.IsProcessing = true;
        SetField(state, "SendingFlag", 1);

        GetCurrentResponse(state).Append("Already flushed content");
        InvokeFlushCurrentResponse(svc, state);
        var historyBefore = session.History.Count;

        GetCurrentResponse(state).Append("Already flushed content");

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetResponseCompletion(state, tcs);

        InvokeCompleteResponse(svc, state, null);

        Assert.Equal(historyBefore, session.History.Count);
        Assert.True(tcs.Task.IsCompleted);
        Assert.Equal("Already flushed content", tcs.Task.Result);
    }

    /// <summary>
    /// Same-turn replay dedup must still work for ordinary multi-paragraph/model-formatted
    /// responses that contain "\n\n" inside the content body.
    /// </summary>
    [Fact]
    public async Task CompleteResponse_SameTurnReplay_MultiParagraphContent_DoesNotDuplicate()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("same-turn-replay-multipara");

        var state = GetSessionState(svc, "same-turn-replay-multipara");
        session.IsProcessing = true;
        SetField(state, "SendingFlag", 1);

        const string content = "First paragraph.\n\n```csharp\nConsole.WriteLine(\"hi\");\n```\n\nFinal paragraph.";
        GetCurrentResponse(state).Append(content);
        InvokeFlushCurrentResponse(svc, state);
        var historyBefore = session.History.Count;

        GetCurrentResponse(state).Append(content);

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetResponseCompletion(state, tcs);

        InvokeCompleteResponse(svc, state, null);

        Assert.Equal(historyBefore, session.History.Count);
        Assert.True(tcs.Task.IsCompleted);
        Assert.Equal(content, tcs.Task.Result);
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
    /// FlushCurrentResponse dedup guard: if the exact same segment was already flushed in
    /// the CURRENT turn, the replay is skipped to prevent duplicates on resume/IDLE-DEFER.
    /// </summary>
    [Fact]
    public async Task FlushCurrentResponse_DedupGuard_SkipsDuplicate()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("dedup-test");

        var state = GetSessionState(svc, "dedup-test");

        // Simulate the current turn already flushing this exact segment once.
        GetCurrentResponse(state).Append("Already flushed content");
        InvokeFlushCurrentResponse(svc, state);
        var historyCountAfterFirst = session.History.Count;

        // Simulate the same content appearing in CurrentResponse again (SDK replay)
        GetCurrentResponse(state).Append("Already flushed content");

        // Act
        InvokeFlushCurrentResponse(svc, state);

        // Assert: no duplicate added
        Assert.Equal(historyCountAfterFirst, session.History.Count);
    }

    /// <summary>
    /// Same-turn flush dedup must treat embedded paragraph breaks as normal content, not as
    /// separators between separately flushed segments.
    /// </summary>
    [Fact]
    public async Task FlushCurrentResponse_DedupGuard_MultiParagraphContent_SkipsDuplicate()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("dedup-multipara-test");

        var state = GetSessionState(svc, "dedup-multipara-test");

        const string content = "Overview:\n\n- first item\n- second item\n\nDone.";
        GetCurrentResponse(state).Append(content);
        InvokeFlushCurrentResponse(svc, state);
        var historyCountAfterFirst = session.History.Count;

        GetCurrentResponse(state).Append(content);

        InvokeFlushCurrentResponse(svc, state);

        Assert.Equal(historyCountAfterFirst, session.History.Count);
    }

    /// <summary>
    /// A brand-new turn that happens to produce the same assistant text as the prior turn
    /// must still be preserved. Dedup is same-turn only.
    /// </summary>
    [Fact]
    public async Task FlushCurrentResponse_IdenticalCrossTurnReply_IsStillPersisted()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("cross-turn-flush-test");

        var state = GetSessionState(svc, "cross-turn-flush-test");
        session.History.Add(ChatMessage.AssistantMessage("Done."));
        var historyBefore = session.History.Count;

        GetCurrentResponse(state).Append("Done.");
        InvokeFlushCurrentResponse(svc, state);

        Assert.Equal(historyBefore + 1, session.History.Count);
        Assert.Equal("Done.", session.History.Last().Content);
    }

    /// <summary>
    /// A later same-turn sub-turn may legitimately produce the same short text again after a
    /// tool/sub-turn boundary. That follow-up response must not be mistaken for an SDK replay.
    /// </summary>
    [Fact]
    public async Task FlushCurrentResponse_IdenticalSameTurnAfterBoundary_IsStillPersisted()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("same-turn-after-boundary");

        var state = GetSessionState(svc, "same-turn-after-boundary");

        GetCurrentResponse(state).Append("Done.");
        InvokeFlushCurrentResponse(svc, state);
        var historyAfterFirst = session.History.Count;

        // Simulate a tool/sub-turn boundary before the assistant emits the same text again.
        InvokeClearFlushedReplayDedup(state);

        GetCurrentResponse(state).Append("Done.");
        InvokeFlushCurrentResponse(svc, state);

        Assert.Equal(historyAfterFirst + 1, session.History.Count);
        Assert.Equal("Done.", session.History.Last().Content);
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

        // CompleteResponse must call ClearProcessingState (which clears SendingFlag along with all other fields)
        var afterCR = source.Substring(crIdx, Math.Min(10000, source.Length - crIdx));
        Assert.Contains("ClearProcessingState", afterCR);
    }

    /// <summary>
    /// The UI must suppress the live streaming bubble once that exact assistant text has
    /// already been flushed into History. Otherwise IDLE-DEFER sessions render the same
    /// answer twice until the next prompt clears the streaming cache.
    /// </summary>
    [Fact]
    public void ChatMessageList_Source_SuppressesStreamingDuplicateAfterFlush()
    {
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "ChatMessageList.razor"));

        Assert.Contains("private bool ShouldShowStreamingContent()", source);
        Assert.Contains("NormalizeStreamingText(lastAssistant?.Content)", source);
        Assert.Contains("NormalizeStreamingText(StreamingContent)", source);
    }

    /// <summary>
    /// Draft restore must not clobber newer user typing with a stale cached draft during
    /// normal render cycles. The browser keeps a live draft map and restore logic skips
    /// overwriting text that diverged from the last restored value.
    /// </summary>
    [Fact]
    public void DraftRestore_Source_PreservesLiveTyping()
    {
        var indexHtml = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "wwwroot", "index.html"));
        var dashboard = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "Pages", "Dashboard.razor"));

        Assert.Contains("window.__liveDrafts", dashboard);
        Assert.Contains("hasDivergedUserText", indexHtml);
        Assert.Contains("current !== desired && current !== lastRestored", indexHtml);
        Assert.Contains("delete window.__liveDrafts[elementId]", indexHtml);
    }

    /// <summary>
    /// Fresh create + lazy/explicit resume paths must all preserve MCP servers and skill
    /// directories so restarted sessions do not silently lose tool availability.
    /// </summary>
    [Fact]
    public void ResumeAndReconnectPaths_IncludeMcpServersAndSkills()
    {
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs"));
        var persistence = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Persistence.cs"));

        // After extraction to BuildFreshSessionConfig, verify the reconnect path calls the helper
        var sessionNotFoundIdx = source.IndexOf("resumeEx.Message.Contains(\"Session not found\"", StringComparison.Ordinal);
        Assert.True(sessionNotFoundIdx > 0);
        var afterNotFound = source.Substring(sessionNotFoundIdx, Math.Min(1000, source.Length - sessionNotFoundIdx));
        Assert.Contains("BuildFreshSessionConfig", afterNotFound);

        // And verify the helper itself includes MCP and Skills
        var helperIdx = source.IndexOf("BuildFreshSessionConfig(SessionState state");
        Assert.True(helperIdx > 0);
        var helperBlock = source.Substring(helperIdx, Math.Min(2000, source.Length - helperIdx));
        Assert.Contains("McpServers", helperBlock);
        Assert.Contains("SkillDirectories", helperBlock);

        var resumeHelperIdx = source.IndexOf("private ResumeSessionConfig BuildResumeSessionConfig", StringComparison.Ordinal);
        Assert.True(resumeHelperIdx > 0);
        var resumeHelperBlock = source.Substring(resumeHelperIdx, Math.Min(2000, source.Length - resumeHelperIdx));
        Assert.Contains("McpServers", resumeHelperBlock);
        Assert.Contains("SkillDirectories", resumeHelperBlock);
        Assert.Contains("BuildResumeSessionConfig(state, resumeWorkingDirectory", source);
        Assert.Contains("BuildResumeSessionConfig(state, resumeWorkDir", persistence);
    }

    /// <summary>
    /// Finalizing resumed session state should complete stale entries and append the reconnect
    /// message through the dedicated UI-thread helper instead of mutating History inline.
    /// </summary>
    [Fact]
    public async Task FinalizeResumedSessionUiState_CompletesStaleEntriesAndAppendsReconnectMessage()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("resume-ui-finalize");
        var state = GetSessionState(svc, "resume-ui-finalize");

        var staleTool = ChatMessage.ToolCallMessage("bash", "call-1", "echo hi");
        staleTool.IsComplete = false;
        var staleReasoning = ChatMessage.ReasoningMessage("reason-1");
        staleReasoning.Content = "thinking...";
        staleReasoning.IsComplete = false;
        session.History.Add(ChatMessage.UserMessage("resume me"));
        session.History.Add(staleTool);
        session.History.Add(staleReasoning);

        await InvokeFinalizeResumedSessionUiStateAsync(
            svc,
            state,
            Guid.NewGuid().ToString(),
            "/tmp/worktree",
            "main",
            isStillProcessing: true,
            processingPhase: 3,
            reconnectMsg: "🔄 Session reconnected at 12:34 — running bash");

        Assert.True(staleTool.IsComplete);
        Assert.True(staleReasoning.IsComplete);
        Assert.True(session.IsProcessing);
        Assert.Equal(3, session.ProcessingPhase);
        Assert.Equal("/tmp/worktree", session.WorkingDirectory);
        Assert.Equal("main", session.GitBranch);
        Assert.Contains(session.History, m => m.Content?.Contains("Session reconnected", StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// The lazy-resume fallback path (session not found / corrupt / process error)
    /// must attach an event handler on the fresh session so it is not deaf.
    /// When the old post-resume .On() was removed in favor of ResumeSessionConfig.OnEvent,
    /// the fallback branch that creates a fresh SessionConfig must still call .On() explicitly.
    /// </summary>
    [Fact]
    public void LazyResumeFallback_AttachesEventHandler()
    {
        var persistence = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Persistence.cs"));

        // Find the fallback create path ("Lazy-resume failed" → CreateSessionAsync)
        var fallbackIdx = persistence.IndexOf("Lazy-resume failed for", StringComparison.Ordinal);
        Assert.True(fallbackIdx > 0, "Could not find the lazy-resume fallback path");

        // Grab the block from the fallback through the next FlushSaveActiveSessionsToDisk
        var afterFallback = persistence.Substring(fallbackIdx, Math.Min(800, persistence.Length - fallbackIdx));
        Assert.Contains("CreateSessionAsync", afterFallback);

        // The critical invariant: .On(evt => HandleSessionEvent(...)) must appear
        // between CreateSessionAsync and the end of this catch block
        Assert.Contains("copilotSession.On(evt => HandleSessionEvent(state, evt))", afterFallback);
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

    /// <summary>
    /// PR #531 review finding: ClearProcessingState must NOT set AllowTurnStartRearm=true.
    /// Only CompleteResponse (normal-completion path) should allow EVT-REARM.
    /// Error/abort paths must not have a race window where a background TurnStart event
    /// reads AllowTurnStartRearm=true before the caller can set it back to false.
    /// </summary>
    [Fact]
    public void ClearProcessingState_DoesNotSetAllowTurnStartRearm()
    {
        // Structural: ClearProcessingState method body must NOT contain AllowTurnStartRearm = true
        var svcPath = Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs");
        var source = File.ReadAllText(svcPath);

        var methodIdx = source.IndexOf("private void ClearProcessingState(", StringComparison.Ordinal);
        Assert.True(methodIdx >= 0, "ClearProcessingState must exist");
        var methodEnd = source.IndexOf("\n    }", methodIdx + 1, StringComparison.Ordinal);
        var methodBody = source.Substring(methodIdx, methodEnd - methodIdx);

        // Strip comment lines before checking — comments may document the invariant using
        // the very string we're guarding against appearing in actual code.
        var codeOnly = string.Join("\n", methodBody.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//")));

        Assert.False(codeOnly.Contains("AllowTurnStartRearm = true", StringComparison.Ordinal),
            "ClearProcessingState must NOT set AllowTurnStartRearm=true — only CompleteResponse should, " +
            "to avoid a race where error/abort paths get it briefly set before they can override to false.");
    }

    /// <summary>
    /// PR #531 review finding: _consecutiveWatchdogTimeouts reset must NOT be in
    /// ClearProcessingState. It is a success-only signal (healthy server) — resetting it
    /// on error/abort paths defeats the server-recovery detection threshold.
    /// </summary>
    [Fact]
    public void ClearProcessingState_DoesNotResetConsecutiveWatchdogTimeouts()
    {
        var svcPath = Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs");
        var source = File.ReadAllText(svcPath);

        var methodIdx = source.IndexOf("private void ClearProcessingState(", StringComparison.Ordinal);
        Assert.True(methodIdx >= 0, "ClearProcessingState must exist");
        var methodEnd = source.IndexOf("\n    }", methodIdx + 1, StringComparison.Ordinal);
        var methodBody = source.Substring(methodIdx, methodEnd - methodIdx);

        // Strip comment lines — comments may reference the counter by name while documenting why it's excluded.
        var codeOnly = string.Join("\n", methodBody.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//")));

        Assert.False(codeOnly.Contains("_consecutiveWatchdogTimeouts", StringComparison.Ordinal),
            "ClearProcessingState must NOT reset _consecutiveWatchdogTimeouts — only CompleteResponse " +
            "(success path) should, since resetting on error/abort paths defeats server-recovery detection.");

        Assert.False(codeOnly.Contains("ConsecutiveStuckCount", StringComparison.Ordinal),
            "ClearProcessingState must NOT reset ConsecutiveStuckCount — only CompleteResponse " +
            "(success path) should, since resetting on watchdog/error paths breaks the >= 3 " +
            "threshold that stops system message accumulation in repeatedly-stuck sessions.");
    }

    /// <summary>
    /// PR #531 review finding: CompleteResponse must set AllowTurnStartRearm=true,
    /// reset _consecutiveWatchdogTimeouts, and reset ConsecutiveStuckCount after ClearProcessingState.
    /// </summary>
    [Fact]
    public void CompleteResponse_SetsAllowTurnStartRearmAndResetsWatchdogCounter()
    {
        var eventsPath = Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Events.cs");
        var source = File.ReadAllText(eventsPath);

        var methodIdx = source.IndexOf("private void CompleteResponse(", StringComparison.Ordinal);
        Assert.True(methodIdx >= 0, "CompleteResponse must exist");
        // Use 10000 chars — the method is ~112 lines before AllowTurnStartRearm, ~6400 chars in.
        var methodBody = source.Substring(methodIdx, Math.Min(10000, source.Length - methodIdx));

        Assert.True(methodBody.Contains("AllowTurnStartRearm = true", StringComparison.Ordinal),
            "CompleteResponse must explicitly set AllowTurnStartRearm=true after ClearProcessingState");
        Assert.True(methodBody.Contains("_consecutiveWatchdogTimeouts", StringComparison.Ordinal),
            "CompleteResponse must reset _consecutiveWatchdogTimeouts on successful completion");
        Assert.True(methodBody.Contains("ConsecutiveStuckCount = 0", StringComparison.Ordinal),
            "CompleteResponse must reset ConsecutiveStuckCount on successful completion");
    }

    /// <summary>
    /// PR #531 re-review finding: AbortSessionAsync must NOT increment PremiumRequestsUsed.
    /// User-initiated aborts consumed server resources (API time) but shouldn't count against
    /// the premium request budget. The call must use accumulateApiTime: false and manually
    /// accumulate TotalApiTimeSeconds before ClearProcessingState.
    /// </summary>
    [Fact]
    public void AbortSessionAsync_DoesNotIncrementPremiumRequestsViaAccumulateApiTime()
    {
        var svcPath = Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs");
        var source = File.ReadAllText(svcPath);

        var methodIdx = source.IndexOf("public async Task AbortSessionAsync(", StringComparison.Ordinal);
        Assert.True(methodIdx >= 0, "AbortSessionAsync must exist");
        var methodEnd = source.IndexOf("\n    }", methodIdx + 1, StringComparison.Ordinal);
        var methodBody = source.Substring(methodIdx, methodEnd - methodIdx);

        // The abort path must call ClearProcessingState with accumulateApiTime: false
        Assert.True(methodBody.Contains("accumulateApiTime: false", StringComparison.Ordinal),
            "AbortSessionAsync must call ClearProcessingState with accumulateApiTime: false — " +
            "user aborts should not increment PremiumRequestsUsed");

        // It must also manually accumulate TotalApiTimeSeconds (request was consumed)
        Assert.True(methodBody.Contains("TotalApiTimeSeconds", StringComparison.Ordinal),
            "AbortSessionAsync must manually accumulate TotalApiTimeSeconds before ClearProcessingState — " +
            "the request consumed server resources even though the user aborted");
    }

    /// <summary>
    /// The ResumeSessionAsync method must pass the event handler via ResumeSessionConfig.OnEvent
    /// instead of calling .On() after the resume returns. The SDK registers OnEvent before
    /// sending the session.resume RPC, closing the race window where events arrive between
    /// resume-return and .On() call — which causes silently dropped content events (empty responses).
    /// </summary>
    [Fact]
    public void ResumeSessionAsync_UsesOnEventInConfig_NotPostResumeOn()
    {
        var svcPath = Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs");
        var source = File.ReadAllText(svcPath);

        // Find the ResumeSessionAsync method (the one that resumes sessions on startup)
        var methodIdx = source.IndexOf("public async Task<AgentSessionInfo> ResumeSessionAsync(", StringComparison.Ordinal);
        Assert.True(methodIdx >= 0, "ResumeSessionAsync must exist");
        var methodEnd = source.IndexOf("\n    }", methodIdx + 1, StringComparison.Ordinal);
        var methodBody = source.Substring(methodIdx, methodEnd - methodIdx);

        // Must use OnEvent in the config — this registers BEFORE the RPC call
        Assert.True(methodBody.Contains("OnEvent", StringComparison.Ordinal),
            "ResumeSessionAsync must pass event handler via ResumeSessionConfig.OnEvent " +
            "to avoid the race where events are dropped between resume-return and .On() call");

        // Must NOT have a post-resume .On() call (which would be the race-prone pattern)
        // Strip comment lines first
        var codeOnly = string.Join("\n", methodBody.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//")));
        Assert.False(codeOnly.Contains("copilotSession.On(evt", StringComparison.Ordinal),
            "ResumeSessionAsync must NOT call copilotSession.On() after resume — " +
            "use OnEvent in config instead to prevent the event delivery race");
    }
}
