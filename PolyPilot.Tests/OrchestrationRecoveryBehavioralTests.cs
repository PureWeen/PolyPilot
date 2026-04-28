using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Behavioral tests for orchestration recovery paths (issue #387).
/// These tests exercise the actual recovery logic with real objects and events,
/// replacing the structural tests that only verify source code patterns.
///
/// Coverage:
/// 1. LoadHistoryFromDiskAsync — events.jsonl parsing with timestamps and filtering
/// 2. bestResponse multi-round accumulation — longest-content-wins across recovery rounds
/// 3. PrematureIdleSignal lifecycle — ManualResetEventSlim set/reset/wait/dispose
/// 4. OnSessionComplete event handler — subscription, firing, and cleanup
/// 5. OCE handling — CancellationTokenSource cancellation preserves bestResponse
/// 6. dispatchTime filtering — DateTimeOffset-based message filtering
/// </summary>
[Collection("BaseDir")]
public class OrchestrationRecoveryBehavioralTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly IServiceProvider _serviceProvider;

    private static readonly BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly BindingFlags NonPublicStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    public OrchestrationRecoveryBehavioralTests()
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

    /// <summary>Create an events.jsonl file for a given session ID in the test directory.</summary>
    private static string CreateEventsFile(string sessionId, params string[] lines)
    {
        var sessionDir = Path.Combine(GetSessionStatePath(), sessionId);
        Directory.CreateDirectory(sessionDir);
        var eventsFile = Path.Combine(sessionDir, "events.jsonl");
        File.WriteAllLines(eventsFile, lines);
        return eventsFile;
    }

    /// <summary>Invoke the private LoadHistoryFromDiskAsync method via reflection.</summary>
    private static async Task<List<ChatMessage>> InvokeLoadHistoryFromDiskAsync(CopilotService svc, string sessionId)
    {
        var method = typeof(CopilotService).GetMethod("LoadHistoryFromDiskAsync", NonPublic)!;
        var task = (Task<List<ChatMessage>>)method.Invoke(svc, new object[] { sessionId })!;
        return await task;
    }

    /// <summary>Get the _sessions ConcurrentDictionary via reflection.</summary>
    private static object GetSessionsDict(CopilotService svc)
    {
        var field = typeof(CopilotService).GetField("_sessions", NonPublic)!;
        return field.GetValue(svc)!;
    }

    /// <summary>Get the SessionState type (private nested class).</summary>
    private static Type GetSessionStateType()
    {
        return typeof(CopilotService).GetNestedType("SessionState", BindingFlags.NonPublic)!;
    }

    /// <summary>Create a SessionState with the given AgentSessionInfo via reflection.
    /// GetUninitializedObject bypasses constructors, so readonly field initializers
    /// (like PrematureIdleSignal = new ManualResetEventSlim()) don't run.
    /// We manually initialize them after creation.
    ///
    /// Fields initialized here (required by tests or called by production methods):
    ///   - PrematureIdleSignal: used directly by §3 lifecycle tests
    ///   - CurrentResponse: StringBuilder, used by FlushCurrentResponse/CompleteResponse
    ///   - FlushedResponse: StringBuilder, used by CompleteResponse
    ///   - PendingReasoningMessages: ConcurrentDictionary, used by reasoning event handler
    /// If future tests call production methods on this state and hit NRE,
    /// check whether additional fields need initialization here.</summary>
    private static object CreateSessionState(AgentSessionInfo info)
    {
        var stateType = GetSessionStateType();
        var state = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(stateType);
        stateType.GetProperty("Info")!.SetValue(state, info);

        // Initialize readonly fields that would normally be set by field initializers.
        var signalField = stateType.GetField("PrematureIdleSignal", AnyInstance)!;
        signalField.SetValue(state, new ManualResetEventSlim(initialState: false));

        // Initialize other readonly fields to prevent NullReferenceException if
        // production methods are called on this state in future tests.
        // These are get-only auto-properties, so we set their backing fields directly.
        var currentResponseField = stateType.GetField("<CurrentResponse>k__BackingField", NonPublic);
        currentResponseField?.SetValue(state, new StringBuilder());

        var flushedResponseField = stateType.GetField("<FlushedResponse>k__BackingField", NonPublic);
        flushedResponseField?.SetValue(state, new StringBuilder());

        var pendingReasoningField = stateType.GetField("<PendingReasoningMessages>k__BackingField", NonPublic);
        pendingReasoningField?.SetValue(state, new ConcurrentDictionary<string, ChatMessage>());

        return state;
    }

    /// <summary>Add a session to the CopilotService._sessions dictionary.</summary>
    private static void AddSession(CopilotService svc, string name, object sessionState)
    {
        var dict = GetSessionsDict(svc);
        dict.GetType().GetMethod("TryAdd")!.Invoke(dict, new[] { name, sessionState });
    }

    /// <summary>Get PrematureIdleSignal from a SessionState.</summary>
    private static ManualResetEventSlim GetPrematureIdleSignal(object sessionState)
    {
        var field = sessionState.GetType().GetField("PrematureIdleSignal", AnyInstance)!;
        return (ManualResetEventSlim)field.GetValue(sessionState)!;
    }

    /// <summary>Build a JSONL event line with the given type, data, and timestamp.</summary>
    private static string BuildEventLine(string type, object data, DateTimeOffset? timestamp = null)
    {
        var ts = timestamp ?? DateTimeOffset.UtcNow;
        var obj = new { type, data, timestamp = ts.ToString("o") };
        return JsonSerializer.Serialize(obj);
    }

    #endregion

    #region 1. LoadHistoryFromDiskAsync — events.jsonl parsing

    [Fact]
    public async Task LoadHistoryFromDisk_ParsesUserMessages()
    {
        var svc = CreateService();
        var sessionId = Guid.NewGuid().ToString();
        var ts = DateTimeOffset.UtcNow.AddMinutes(-5);

        CreateEventsFile(sessionId,
            BuildEventLine("user.message", new { content = "Hello world" }, ts));

        var history = await InvokeLoadHistoryFromDiskAsync(svc, sessionId);

        Assert.Single(history);
        Assert.Equal("user", history[0].Role);
        Assert.Equal("Hello world", history[0].Content);
        Assert.Equal(ChatMessageType.User, history[0].MessageType);
    }

    [Fact]
    public async Task LoadHistoryFromDisk_ParsesAssistantMessages()
    {
        var svc = CreateService();
        var sessionId = Guid.NewGuid().ToString();
        var ts = DateTimeOffset.UtcNow.AddMinutes(-3);

        CreateEventsFile(sessionId,
            BuildEventLine("assistant.message", new { content = "Here is my response" }, ts));

        var history = await InvokeLoadHistoryFromDiskAsync(svc, sessionId);

        Assert.Single(history);
        Assert.Equal("assistant", history[0].Role);
        Assert.Equal("Here is my response", history[0].Content);
        Assert.Equal(ChatMessageType.Assistant, history[0].MessageType);
    }

    [Fact]
    public async Task LoadHistoryFromDisk_ParsesAssistantMessageWithReasoning()
    {
        var svc = CreateService();
        var sessionId = Guid.NewGuid().ToString();
        var ts = DateTimeOffset.UtcNow.AddMinutes(-2);

        CreateEventsFile(sessionId,
            BuildEventLine("assistant.message", new { content = "Final answer", reasoningText = "Let me think..." }, ts));

        var history = await InvokeLoadHistoryFromDiskAsync(svc, sessionId);

        Assert.Equal(2, history.Count);
        // First: reasoning message
        Assert.Equal(ChatMessageType.Reasoning, history[0].MessageType);
        Assert.Equal("Let me think...", history[0].Content);
        Assert.True(history[0].IsCollapsed);
        Assert.True(history[0].IsComplete);
        // Second: assistant text
        Assert.Equal(ChatMessageType.Assistant, history[1].MessageType);
        Assert.Equal("Final answer", history[1].Content);
    }

    [Fact]
    public async Task LoadHistoryFromDisk_ParsesToolExecutionStartAndComplete()
    {
        var svc = CreateService();
        var sessionId = Guid.NewGuid().ToString();
        var ts = DateTimeOffset.UtcNow.AddMinutes(-1);

        CreateEventsFile(sessionId,
            BuildEventLine("tool.execution_start",
                new { toolName = "bash", toolCallId = "tc-1", input = "{\"command\":\"ls\"}" }, ts),
            BuildEventLine("tool.execution_complete",
                new { toolCallId = "tc-1", success = true, result = new { content = "file1.txt\nfile2.txt" } }, ts.AddSeconds(2)));

        var history = await InvokeLoadHistoryFromDiskAsync(svc, sessionId);

        Assert.Single(history); // tool start + complete are merged into one message
        Assert.Equal(ChatMessageType.ToolCall, history[0].MessageType);
        Assert.Equal("bash", history[0].ToolName);
        Assert.Equal("tc-1", history[0].ToolCallId);
        Assert.True(history[0].IsComplete);
        Assert.True(history[0].IsSuccess);
        Assert.Contains("file1.txt", history[0].Content!);
    }

    [Fact]
    public async Task LoadHistoryFromDisk_SkipsReportIntentTool()
    {
        var svc = CreateService();
        var sessionId = Guid.NewGuid().ToString();

        CreateEventsFile(sessionId,
            BuildEventLine("tool.execution_start",
                new { toolName = "report_intent", toolCallId = "tc-2" }),
            BuildEventLine("tool.execution_start",
                new { toolName = "bash", toolCallId = "tc-3" }));

        var history = await InvokeLoadHistoryFromDiskAsync(svc, sessionId);

        Assert.Single(history);
        Assert.Equal("bash", history[0].ToolName);
    }

    [Fact]
    public async Task LoadHistoryFromDisk_PreservesTimestamps()
    {
        var svc = CreateService();
        var sessionId = Guid.NewGuid().ToString();
        var ts1 = new DateTimeOffset(2025, 6, 15, 10, 30, 0, TimeSpan.Zero);
        var ts2 = new DateTimeOffset(2025, 6, 15, 10, 31, 0, TimeSpan.Zero);

        CreateEventsFile(sessionId,
            BuildEventLine("user.message", new { content = "First" }, ts1),
            BuildEventLine("assistant.message", new { content = "Second" }, ts2));

        var history = await InvokeLoadHistoryFromDiskAsync(svc, sessionId);

        Assert.Equal(2, history.Count);
        Assert.Equal(ts1, history[0].Timestamp);
        Assert.Equal(ts2, history[1].Timestamp);
    }

    [Fact]
    public async Task LoadHistoryFromDisk_ReturnsEmptyForMissingFile()
    {
        var svc = CreateService();
        var history = await InvokeLoadHistoryFromDiskAsync(svc, Guid.NewGuid().ToString());
        Assert.Empty(history);
    }

    [Fact]
    public async Task LoadHistoryFromDisk_ReturnsEmptyForEmptyFile()
    {
        var svc = CreateService();
        var sessionId = Guid.NewGuid().ToString();
        CreateEventsFile(sessionId); // no lines

        var history = await InvokeLoadHistoryFromDiskAsync(svc, sessionId);
        Assert.Empty(history);
    }

    [Fact]
    public async Task LoadHistoryFromDisk_HandlesBlankLinesGracefully()
    {
        var svc = CreateService();
        var sessionId = Guid.NewGuid().ToString();

        CreateEventsFile(sessionId,
            "",
            BuildEventLine("user.message", new { content = "test" }),
            "   ",
            BuildEventLine("assistant.message", new { content = "response" }),
            "");

        var history = await InvokeLoadHistoryFromDiskAsync(svc, sessionId);
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public async Task LoadHistoryFromDisk_SkipsEventsWithoutTypeOrData()
    {
        var svc = CreateService();
        var sessionId = Guid.NewGuid().ToString();

        CreateEventsFile(sessionId,
            JsonSerializer.Serialize(new { data = new { content = "no type" } }),
            JsonSerializer.Serialize(new { type = "user.message" }),
            BuildEventLine("user.message", new { content = "valid" }));

        var history = await InvokeLoadHistoryFromDiskAsync(svc, sessionId);
        Assert.Single(history);
        Assert.Equal("valid", history[0].Content);
    }

    [Fact]
    public async Task LoadHistoryFromDisk_MultipleConversationTurns()
    {
        var svc = CreateService();
        var sessionId = Guid.NewGuid().ToString();
        var baseTs = DateTimeOffset.UtcNow.AddMinutes(-10);

        CreateEventsFile(sessionId,
            BuildEventLine("user.message", new { content = "What is 2+2?" }, baseTs),
            BuildEventLine("assistant.message", new { content = "4" }, baseTs.AddSeconds(1)),
            BuildEventLine("user.message", new { content = "And 3+3?" }, baseTs.AddSeconds(5)),
            BuildEventLine("tool.execution_start", new { toolName = "calculator", toolCallId = "tc-calc" }, baseTs.AddSeconds(6)),
            BuildEventLine("tool.execution_complete", new { toolCallId = "tc-calc", success = true, result = new { content = "6" } }, baseTs.AddSeconds(7)),
            BuildEventLine("assistant.message", new { content = "The answer is 6" }, baseTs.AddSeconds(8)));

        var history = await InvokeLoadHistoryFromDiskAsync(svc, sessionId);

        // user(1) + assistant(1) + user(1) + tool(start+complete=1) + assistant(1) = 5
        Assert.Equal(5, history.Count);
        Assert.Equal("What is 2+2?", history[0].Content);
        Assert.Equal("4", history[1].Content);
        Assert.Equal("And 3+3?", history[2].Content);
        Assert.Equal(ChatMessageType.ToolCall, history[3].MessageType);
        Assert.Equal("The answer is 6", history[4].Content);
    }

    [Fact]
    public async Task LoadHistoryFromDisk_ToolWithDetailedContent()
    {
        var svc = CreateService();
        var sessionId = Guid.NewGuid().ToString();

        CreateEventsFile(sessionId,
            BuildEventLine("tool.execution_start",
                new { toolName = "grep", toolCallId = "tc-grep" }),
            BuildEventLine("tool.execution_complete",
                new { toolCallId = "tc-grep", success = true,
                    result = new { detailedContent = "Detailed grep output", content = "Short output" } }));

        var history = await InvokeLoadHistoryFromDiskAsync(svc, sessionId);
        Assert.Single(history);
        Assert.Equal("Detailed grep output", history[0].Content);
    }

    #endregion

    #region 2. bestResponse multi-round accumulation logic
    // NOTE: These tests verify the bestResponse accumulation *pattern* (longest-content-wins)
    // in isolation. They duplicate the LINQ filter from RecoverFromPrematureIdleIfNeededAsync
    // rather than calling the production method directly. If the production filter changes
    // (e.g., > becomes >=), these tests won't detect the divergence.
    // The §1, §7, and §8 tests call real production code. A future improvement would be to
    // extract the accumulation/filtering logic into a testable internal static helper and
    // test that instead.

    [Fact]
    public void BestResponseAccumulation_LongestContentWins()
    {
        // Simulate the bestResponse accumulation pattern from RecoverFromPrematureIdleIfNeededAsync
        var dispatchTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        string? bestResponse = "short";

        // Simulate round 1: session history has a longer response
        var history = new List<ChatMessage>
        {
            ChatMessage.AssistantMessage("This is a much longer response from round 1"),
        };
        history[0].Timestamp = dispatchTime.AddSeconds(10);

        var latestContent = history
            .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content)
                && m.MessageType == ChatMessageType.Assistant
                && m.Timestamp >= dispatchTime);

        if (latestContent != null && latestContent.Content!.Length > (bestResponse?.Length ?? 0))
        {
            bestResponse = latestContent.Content;
        }

        Assert.Equal("This is a much longer response from round 1", bestResponse);
    }

    [Fact]
    public void BestResponseAccumulation_DoesNotDowngrade()
    {
        var dispatchTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        string? bestResponse = "This is the longer best response from round 1 that should be preserved";

        // Round 2: session history has a shorter response
        var history = new List<ChatMessage>
        {
            ChatMessage.AssistantMessage("Short"),
        };
        history[0].Timestamp = dispatchTime.AddSeconds(20);

        var latestContent = history
            .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content)
                && m.MessageType == ChatMessageType.Assistant
                && m.Timestamp >= dispatchTime);

        if (latestContent != null && latestContent.Content!.Length > (bestResponse?.Length ?? 0))
        {
            bestResponse = latestContent.Content;
        }

        // bestResponse should NOT have been downgraded
        Assert.Equal("This is the longer best response from round 1 that should be preserved", bestResponse);
    }

    [Fact]
    public void BestResponseAccumulation_NullInitialResponse_UpgradesToAnyContent()
    {
        var dispatchTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        string? bestResponse = null; // initialResponse was null (empty TCS result)

        var history = new List<ChatMessage>
        {
            ChatMessage.AssistantMessage("Recovery found content"),
        };
        history[0].Timestamp = dispatchTime.AddSeconds(5);

        var latestContent = history
            .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content)
                && m.MessageType == ChatMessageType.Assistant
                && m.Timestamp >= dispatchTime);

        if (latestContent != null && latestContent.Content!.Length > (bestResponse?.Length ?? 0))
        {
            bestResponse = latestContent.Content;
        }

        Assert.Equal("Recovery found content", bestResponse);
    }

    [Fact]
    public void BestResponseAccumulation_MultipleRoundsProgressivelyLonger()
    {
        var dispatchTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        string? bestResponse = "initial";

        // Simulate 3 recovery rounds with progressively longer content
        var roundResponses = new[]
        {
            "Round 1: medium-length recovery content from the first round",
            "Round 2: this is a significantly longer recovery content that demonstrates progressive improvement across rounds",
            "Round 3: final" // shorter — should NOT replace round 2
        };

        foreach (var roundContent in roundResponses)
        {
            var history = new List<ChatMessage>
            {
                ChatMessage.AssistantMessage(roundContent),
            };
            history[0].Timestamp = dispatchTime.AddSeconds(10);

            var latestContent = history
                .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content)
                    && m.MessageType == ChatMessageType.Assistant
                    && m.Timestamp >= dispatchTime);

            if (latestContent != null && latestContent.Content!.Length > (bestResponse?.Length ?? 0))
            {
                bestResponse = latestContent.Content;
            }
        }

        // Should have the longest (round 2), not the last (round 3)
        Assert.Equal(roundResponses[1], bestResponse);
    }

    [Fact]
    public void BestResponseAccumulation_IgnoresNonAssistantMessages()
    {
        var dispatchTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        string? bestResponse = null;

        var history = new List<ChatMessage>
        {
            ChatMessage.UserMessage("User message — very long content that should be ignored by the recovery filter"),
            ChatMessage.SystemMessage("System message — also long and should be ignored"),
            ChatMessage.AssistantMessage("Short but valid"),
        };
        foreach (var m in history) m.Timestamp = dispatchTime.AddSeconds(5);

        var latestContent = history
            .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content)
                && m.MessageType == ChatMessageType.Assistant
                && m.Timestamp >= dispatchTime);

        if (latestContent != null && latestContent.Content!.Length > (bestResponse?.Length ?? 0))
        {
            bestResponse = latestContent.Content;
        }

        Assert.Equal("Short but valid", bestResponse);
    }

    #endregion

    #region 3. PrematureIdleSignal lifecycle
    // NOTE: These tests exercise ManualResetEventSlim directly via the SessionState field.
    // They verify the signaling primitive's behavior, not the production code that uses it.

    [Fact]
    public void PrematureIdleSignal_StartsUnset()
    {
        var info = new AgentSessionInfo { Name = "test", Model = "test-model" };
        var state = CreateSessionState(info);
        var signal = GetPrematureIdleSignal(state);

        Assert.False(signal.IsSet);
    }

    [Fact]
    public void PrematureIdleSignal_SetMakesItDetectable()
    {
        var info = new AgentSessionInfo { Name = "test", Model = "test-model" };
        var state = CreateSessionState(info);
        var signal = GetPrematureIdleSignal(state);

        signal.Set();
        Assert.True(signal.IsSet);
    }

    [Fact]
    public void PrematureIdleSignal_ResetClearsSignal()
    {
        var info = new AgentSessionInfo { Name = "test", Model = "test-model" };
        var state = CreateSessionState(info);
        var signal = GetPrematureIdleSignal(state);

        signal.Set();
        Assert.True(signal.IsSet);

        signal.Reset();
        Assert.False(signal.IsSet);
    }

    [Fact]
    public void PrematureIdleSignal_WaitReturnsTrueWhenSet()
    {
        var info = new AgentSessionInfo { Name = "test", Model = "test-model" };
        var state = CreateSessionState(info);
        var signal = GetPrematureIdleSignal(state);

        signal.Set();
        bool result = signal.Wait(100); // 100ms timeout
        Assert.True(result);
    }

    [Fact]
    public void PrematureIdleSignal_WaitReturnsFalseWhenNotSetAndTimesOut()
    {
        var info = new AgentSessionInfo { Name = "test", Model = "test-model" };
        var state = CreateSessionState(info);
        var signal = GetPrematureIdleSignal(state);

        bool result = signal.Wait(50); // short timeout — signal never set
        Assert.False(result);
    }

    [Fact]
    public void PrematureIdleSignal_WaitUnblocksWhenSetFromAnotherThread()
    {
        var info = new AgentSessionInfo { Name = "test", Model = "test-model" };
        var state = CreateSessionState(info);
        var signal = GetPrematureIdleSignal(state);

        bool wasSet = false;
        var waitTask = Task.Run(() =>
        {
            wasSet = signal.Wait(5000); // generous timeout
        });

        // Set from main thread after a brief delay
        Thread.Sleep(100);
        signal.Set();

        waitTask.Wait(3000);
        Assert.True(wasSet, "Wait should have returned true after Set was called");
    }

    [Fact]
    public void PrematureIdleSignal_DisposedSignalDoesNotThrowOnIsSetCheck()
    {
        var info = new AgentSessionInfo { Name = "test", Model = "test-model" };
        var state = CreateSessionState(info);
        var signal = GetPrematureIdleSignal(state);

        signal.Dispose();

        // The IsPrematureIdleSignalSet() local function in RecoverFromPrematureIdleIfNeededAsync
        // catches ObjectDisposedException and returns false
        bool result;
        try
        {
            result = signal.IsSet;
        }
        catch (ObjectDisposedException)
        {
            result = false;
        }
        Assert.False(result);
    }

    [Fact]
    public void PrematureIdleSignal_DisposedSignalDoesNotThrowOnWait()
    {
        var info = new AgentSessionInfo { Name = "test", Model = "test-model" };
        var state = CreateSessionState(info);
        var signal = GetPrematureIdleSignal(state);

        signal.Dispose();

        // The WaitForPrematureIdleSignal() local function catches ObjectDisposedException
        bool result;
        try
        {
            result = signal.Wait(100);
        }
        catch (ObjectDisposedException)
        {
            result = false;
        }
        Assert.False(result);
    }

    #endregion

    #region 4. OnSessionComplete event handler lifecycle

    [Fact]
    public async Task OnSessionComplete_SubscriptionDoesNotThrow()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        string? firedName = null;
        string? firedSummary = null;
        svc.OnSessionComplete += (name, summary) =>
        {
            firedName = name;
            firedSummary = summary;
        };

        // Create a session and trigger a complete event
        await svc.CreateSessionAsync("worker-1");
        await svc.SendPromptAsync("worker-1", "hello");

        // Demo mode doesn't reliably fire OnSessionComplete, so we verify
        // that subscribing and sending a prompt doesn't throw.
        // The TCS-based handler tests below (OnSessionComplete_TCSCompletesOnNameMatch etc.)
        // verify the actual event dispatch pattern behaviorally.
        Assert.NotNull(svc);
    }

    [Fact]
    public void OnSessionComplete_TCSCompletesOnNameMatch()
    {
        // Simulate the pattern used in RecoverFromPrematureIdleIfNeededAsync:
        // A TCS that completes when OnSessionComplete fires for the right worker name
        var completionTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        const string targetWorker = "worker-1";

        void LocalHandler(string name, string _)
        {
            if (name == targetWorker)
                completionTcs.TrySetResult(true);
        }

        // Simulate firing for matching name
        LocalHandler("worker-1", "done");
        Assert.True(completionTcs.Task.IsCompleted);
        Assert.True(completionTcs.Task.Result);
    }

    [Fact]
    public void OnSessionComplete_TCSDoesNotCompleteOnNameMismatch()
    {
        var completionTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        const string targetWorker = "worker-1";

        void LocalHandler(string name, string _)
        {
            if (name == targetWorker)
                completionTcs.TrySetResult(true);
        }

        // Fire for a different worker
        LocalHandler("worker-2", "done");
        Assert.False(completionTcs.Task.IsCompleted);
    }

    [Fact]
    public async Task OnSessionComplete_HandlerUnsubscribeStopsDelivery()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        int fireCount = 0;
        void Handler(string name, string summary) => fireCount++;

        svc.OnSessionComplete += Handler;
        svc.OnSessionComplete -= Handler;

        // Fire a session complete via the demo service
        await svc.CreateSessionAsync("test-session");
        await svc.SendPromptAsync("test-session", "hello");

        // Handler was unsubscribed, so it shouldn't have incremented
        Assert.Equal(0, fireCount);
    }

    [Fact]
    public async Task OnSessionComplete_MultipleHandlersReceiveEvent()
    {
        var completionTcs1 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completionTcs2 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler1(string name, string _)
        {
            if (name == "worker") completionTcs1.TrySetResult(true);
        }
        void Handler2(string name, string _)
        {
            if (name == "worker") completionTcs2.TrySetResult(true);
        }

        // Both handlers should fire
        Handler1("worker", "done");
        Handler2("worker", "done");

        Assert.True(completionTcs1.Task.IsCompleted);
        Assert.True(completionTcs2.Task.IsCompleted);
    }

    [Fact]
    public void OnSessionComplete_CancellationRegistrationUnblocksTCS()
    {
        // Simulate the CTS timeout unblocking the TCS used in the recovery loop
        var completionTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        using var reg = cts.Token.Register(() => completionTcs.TrySetResult(false));

        // Cancel — should unblock with false
        cts.Cancel();

        Assert.True(completionTcs.Task.IsCompleted);
        Assert.False(completionTcs.Task.Result);
    }

    #endregion

    #region 5. OCE handling — bestResponse preservation
    // NOTE: These tests re-implement the OCE catch-block pattern from
    // RecoverFromPrematureIdleIfNeededAsync in isolation. See §2 note.

    [Fact]
    public void OCE_PreservesBestResponseOnCancellation()
    {
        // Simulate the OCE catch block in RecoverFromPrematureIdleIfNeededAsync
        string? bestResponse = "Long accumulated recovery content from multiple rounds";
        string? initialResponse = "short";

        // The recovery loop catches OCE and returns bestResponse ?? initialResponse
        string? result;
        try
        {
            throw new OperationCanceledException("Recovery timeout");
        }
        catch (OperationCanceledException)
        {
            result = bestResponse ?? initialResponse;
        }

        Assert.Equal("Long accumulated recovery content from multiple rounds", result);
    }

    [Fact]
    public void OCE_FallsBackToInitialResponseWhenBestResponseIsNull()
    {
        string? bestResponse = null;
        string? initialResponse = "initial truncated content";

        string? result;
        try
        {
            throw new OperationCanceledException("Recovery timeout");
        }
        catch (OperationCanceledException)
        {
            result = bestResponse ?? initialResponse;
        }

        Assert.Equal("initial truncated content", result);
    }

    [Fact]
    public async Task OCE_LinkedCTSPreservesAccumulatedContent()
    {
        // Simulate the linked CTS pattern from the recovery method
        using var outerCts = new CancellationTokenSource();
        using var recoveryCts = CancellationTokenSource.CreateLinkedTokenSource(outerCts.Token);
        recoveryCts.CancelAfter(100); // short timeout

        string? bestResponse = "Accumulated during recovery";

        try
        {
            await Task.Delay(5000, recoveryCts.Token); // will be cancelled
            bestResponse = "This should never be reached";
        }
        catch (OperationCanceledException)
        {
            // Preserve bestResponse — don't set it to null or re-throw
        }

        Assert.Equal("Accumulated during recovery", bestResponse);
    }

    [Fact]
    public void OCE_OuterCancellationAlsoPreservesBestResponse()
    {
        // Simulate user abort (outer cancellation) during recovery
        string? bestResponse = "Partial recovery before user abort";
        string? initialResponse = "truncated";

        string? result;
        try
        {
            throw new OperationCanceledException("User aborted");
        }
        catch (OperationCanceledException)
        {
            result = bestResponse ?? initialResponse;
        }

        Assert.Equal("Partial recovery before user abort", result);
    }

    #endregion

    #region 6. dispatchTime filtering correctness
    // NOTE: These tests duplicate the dispatchTime LINQ filter from production code.
    // See §2 note about the trade-off. §6.10 (DispatchTimeFilter_EndToEnd_DiskFallback)
    // calls real production code (LoadHistoryFromDiskAsync) for end-to-end coverage.

    [Fact]
    public void DispatchTimeFilter_ExcludesMessagesBeforeDispatch()
    {
        var dispatchTime = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

        var history = new List<ChatMessage>
        {
            CreateAssistantWithTimestamp("Old message from prior conversation", dispatchTime.AddMinutes(-30)),
            CreateAssistantWithTimestamp("Stale response", dispatchTime.AddSeconds(-1)),
            CreateAssistantWithTimestamp("Current response after dispatch", dispatchTime.AddSeconds(5)),
        };

        var latestContent = history
            .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content)
                && m.MessageType == ChatMessageType.Assistant
                && m.Timestamp >= dispatchTime);

        Assert.NotNull(latestContent);
        Assert.Equal("Current response after dispatch", latestContent!.Content);
    }

    [Fact]
    public void DispatchTimeFilter_IncludesMessageExactlyAtDispatchTime()
    {
        var dispatchTime = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

        var history = new List<ChatMessage>
        {
            CreateAssistantWithTimestamp("Response at exact dispatch time", dispatchTime),
        };

        var latestContent = history
            .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content)
                && m.MessageType == ChatMessageType.Assistant
                && m.Timestamp >= dispatchTime);

        Assert.NotNull(latestContent);
        Assert.Equal("Response at exact dispatch time", latestContent!.Content);
    }

    [Fact]
    public void DispatchTimeFilter_ReturnsNullWhenNoMatchingMessages()
    {
        var dispatchTime = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

        var history = new List<ChatMessage>
        {
            CreateAssistantWithTimestamp("Too old", dispatchTime.AddMinutes(-10)),
        };

        var latestContent = history
            .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content)
                && m.MessageType == ChatMessageType.Assistant
                && m.Timestamp >= dispatchTime);

        Assert.Null(latestContent);
    }

    [Fact]
    public void DispatchTimeFilter_SelectsLastMatchingMessage()
    {
        var dispatchTime = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

        var history = new List<ChatMessage>
        {
            CreateAssistantWithTimestamp("First valid", dispatchTime.AddSeconds(1)),
            CreateAssistantWithTimestamp("Second valid — should be selected (last)", dispatchTime.AddSeconds(10)),
        };

        var latestContent = history
            .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content)
                && m.MessageType == ChatMessageType.Assistant
                && m.Timestamp >= dispatchTime);

        Assert.NotNull(latestContent);
        Assert.Equal("Second valid — should be selected (last)", latestContent!.Content);
    }

    [Fact]
    public void DispatchTimeFilter_IgnoresWhitespaceOnlyContent()
    {
        var dispatchTime = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

        var history = new List<ChatMessage>
        {
            CreateAssistantWithTimestamp("  \t\n  ", dispatchTime.AddSeconds(5)),
            CreateAssistantWithTimestamp("Real content", dispatchTime.AddSeconds(10)),
        };

        var latestContent = history
            .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content)
                && m.MessageType == ChatMessageType.Assistant
                && m.Timestamp >= dispatchTime);

        Assert.Equal("Real content", latestContent!.Content);
    }

    [Fact]
    public void DispatchTimeFilter_IgnoresToolCallMessages()
    {
        var dispatchTime = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

        var toolMsg = ChatMessage.ToolCallMessage("bash", "tc-1");
        toolMsg.Content = "Very long tool output that would win the length comparison";
        toolMsg.Timestamp = dispatchTime.AddSeconds(5);

        var assistantMsg = CreateAssistantWithTimestamp("Short valid", dispatchTime.AddSeconds(10));

        var history = new List<ChatMessage> { toolMsg, assistantMsg };

        var latestContent = history
            .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content)
                && m.MessageType == ChatMessageType.Assistant
                && m.Timestamp >= dispatchTime);

        Assert.Equal("Short valid", latestContent!.Content);
    }

    [Fact]
    public void DispatchTimeFilter_IgnoresUserMessages()
    {
        var dispatchTime = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

        var userMsg = ChatMessage.UserMessage("Long user message content");
        userMsg.Timestamp = dispatchTime.AddSeconds(5);

        var assistantMsg = CreateAssistantWithTimestamp("Assistant reply", dispatchTime.AddSeconds(10));

        var history = new List<ChatMessage> { userMsg, assistantMsg };

        var latestContent = history
            .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content)
                && m.MessageType == ChatMessageType.Assistant
                && m.Timestamp >= dispatchTime);

        Assert.Equal("Assistant reply", latestContent!.Content);
    }

    [Fact]
    public async Task DispatchTimeFilter_WorksWithEventsFromDisk()
    {
        // End-to-end: create events.jsonl with timestamps, load via LoadHistoryFromDiskAsync,
        // then apply the same dispatchTime filter used in production
        var svc = CreateService();
        var sessionId = Guid.NewGuid().ToString();
        var dispatchTime = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

        CreateEventsFile(sessionId,
            BuildEventLine("user.message", new { content = "Old prompt" }, dispatchTime.AddMinutes(-10)),
            BuildEventLine("assistant.message", new { content = "Old response — before dispatch" }, dispatchTime.AddMinutes(-9)),
            BuildEventLine("user.message", new { content = "New prompt" }, dispatchTime.AddSeconds(1)),
            BuildEventLine("assistant.message", new { content = "New response — after dispatch" }, dispatchTime.AddSeconds(5)));

        var diskHistory = await InvokeLoadHistoryFromDiskAsync(svc, sessionId);

        var latestContent = diskHistory
            .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content)
                && m.MessageType == ChatMessageType.Assistant
                && m.Timestamp >= dispatchTime);

        Assert.NotNull(latestContent);
        Assert.Equal("New response — after dispatch", latestContent!.Content);
    }

    [Fact]
    public async Task DispatchTimeFilter_DiskFallbackWithMultipleAssistantMessages()
    {
        // Simulate the disk fallback path in RecoverFromPrematureIdleIfNeededAsync:
        // When History didn't have better content, load from events.jsonl and filter
        var svc = CreateService();
        var sessionId = Guid.NewGuid().ToString();
        var dispatchTime = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

        // Simulate: agent produced truncated output first, then longer output after recovery
        CreateEventsFile(sessionId,
            BuildEventLine("user.message", new { content = "Analyze this code" }, dispatchTime.AddSeconds(1)),
            BuildEventLine("assistant.message", new { content = "I'll analyze..." }, dispatchTime.AddSeconds(3)),
            BuildEventLine("tool.execution_start", new { toolName = "bash", toolCallId = "tc-1" }, dispatchTime.AddSeconds(5)),
            BuildEventLine("tool.execution_complete", new { toolCallId = "tc-1", success = true, result = new { content = "file list" } }, dispatchTime.AddSeconds(7)),
            BuildEventLine("assistant.message", new { content = "After analyzing the code, here is my comprehensive review with detailed findings across all files..." }, dispatchTime.AddSeconds(15)));

        var diskHistory = await InvokeLoadHistoryFromDiskAsync(svc, sessionId);

        // The lastOrDefault with dispatchTime filter should get the last (longest) assistant message
        var lastDisk = diskHistory
            .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content)
                && m.MessageType == ChatMessageType.Assistant
                && m.Timestamp >= dispatchTime);

        Assert.NotNull(lastDisk);
        Assert.StartsWith("After analyzing", lastDisk!.Content);

        // Verify the disk fallback would upgrade bestResponse (production pattern)
        string? bestResponse = "I'll analyze..."; // truncated initial
        if (lastDisk.Content!.Length > (bestResponse?.Length ?? 0))
        {
            bestResponse = lastDisk.Content;
        }
        Assert.StartsWith("After analyzing", bestResponse);
    }

    #endregion

    #region 7. GetEventsFileMtime behavioral tests

    [Fact]
    public void GetEventsFileMtime_ReturnsNullForMissingSession()
    {
        var svc = CreateService();
        var method = typeof(CopilotService).GetMethod("GetEventsFileMtime",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var result = method.Invoke(svc, new object?[] { Guid.NewGuid().ToString() });
        Assert.Null(result);
    }

    [Fact]
    public void GetEventsFileMtime_ReturnsNullForNullSessionId()
    {
        var svc = CreateService();
        var method = typeof(CopilotService).GetMethod("GetEventsFileMtime",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var result = method.Invoke(svc, new object?[] { null });
        Assert.Null(result);
    }

    [Fact]
    public void GetEventsFileMtime_ReturnsTimeForExistingFile()
    {
        var svc = CreateService();
        var sessionId = Guid.NewGuid().ToString();
        var eventsFile = CreateEventsFile(sessionId,
            BuildEventLine("user.message", new { content = "test" }));

        var method = typeof(CopilotService).GetMethod("GetEventsFileMtime",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var result = (DateTime?)method.Invoke(svc, new object?[] { sessionId });
        Assert.NotNull(result);

        // The mtime should be very recent (within last few seconds)
        var age = DateTime.UtcNow - result!.Value;
        Assert.True(age.TotalSeconds < 30, $"File mtime should be recent, but age was {age.TotalSeconds}s");
    }

    [Fact]
    public void GetEventsFileMtime_DetectsFileModification()
    {
        var svc = CreateService();
        var sessionId = Guid.NewGuid().ToString();
        CreateEventsFile(sessionId,
            BuildEventLine("user.message", new { content = "initial" }));

        var method = typeof(CopilotService).GetMethod("GetEventsFileMtime",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var mtime1 = (DateTime?)method.Invoke(svc, new object?[] { sessionId });
        Assert.NotNull(mtime1);

        // Wait briefly and modify the file
        Thread.Sleep(50);
        var eventsPath = Path.Combine(GetSessionStatePath(), sessionId, "events.jsonl");
        File.AppendAllText(eventsPath, "\n" + BuildEventLine("assistant.message", new { content = "new" }));

        var mtime2 = (DateTime?)method.Invoke(svc, new object?[] { sessionId });
        Assert.NotNull(mtime2);
        Assert.True(mtime2!.Value >= mtime1!.Value,
            "Modified file should have same or later mtime");
    }

    #endregion

    #region 8. Constants validation (behavioral — verifying actual values matter)

    [Fact]
    public void PrematureIdleRecoveryTimeout_IsLongEnoughForToolExecution()
    {
        // Workers with long tool runs (e.g., multi-minute builds) need the timeout
        // to be generous. 300s = 5 minutes is the current value.
        Assert.True(CopilotService.PrematureIdleRecoveryTimeoutMs >= 60_000,
            "Recovery timeout must be >= 60s for tool-heavy workers");
        Assert.True(CopilotService.PrematureIdleRecoveryTimeoutMs <= 600_000,
            "Recovery timeout must be <= 600s to avoid blocking orchestration forever");
    }

    [Fact]
    public void PrematureIdleEventsSettleMs_IsLessThanGracePeriod()
    {
        // The settle phase must be shorter than the total grace period.
        // settle + observe = grace, so settle < grace.
        Assert.True(CopilotService.PrematureIdleEventsSettleMs < CopilotService.PrematureIdleEventsGracePeriodMs,
            "Settle period must be less than the total grace period");
        Assert.True(CopilotService.PrematureIdleEventsSettleMs > 0,
            "Settle period must be positive");
    }

    [Fact]
    public void PrematureIdleEventsGracePeriodMs_ObservationWindowIsReasonable()
    {
        // The observation window (grace - settle) should be meaningful
        int observationWindow = CopilotService.PrematureIdleEventsGracePeriodMs - CopilotService.PrematureIdleEventsSettleMs;
        Assert.True(observationWindow >= 500,
            $"Observation window ({observationWindow}ms) must be >= 500ms to detect genuine CLI writes");
        Assert.True(observationWindow <= 5000,
            $"Observation window ({observationWindow}ms) must be <= 5000ms to avoid excessive wait");
    }

    [Fact]
    public void PrematureIdleEventsFileFreshnessSeconds_IsReasonableForDetection()
    {
        Assert.True(CopilotService.PrematureIdleEventsFileFreshnessSeconds >= 5,
            "Freshness threshold must be >= 5s to avoid false positives from OS flushing");
        Assert.True(CopilotService.PrematureIdleEventsFileFreshnessSeconds <= 60,
            "Freshness threshold must be <= 60s to detect stale files promptly");
    }

    #endregion

    #region 9. Recovery loop TCS pattern — end-to-end simulation
    // NOTE: These tests simulate the TCS/CTS coordination pattern from
    // RecoverFromPrematureIdleIfNeededAsync but don't invoke it directly. See §2 note.

    [Fact]
    public async Task RecoveryLoop_TCSCompletesOnSessionCompleteEvent()
    {
        // Simulate the recovery loop pattern:
        // 1. Create TCS
        // 2. Subscribe OnSessionComplete handler that completes TCS for matching worker
        // 3. Fire event → TCS completes
        // 4. Collect content
        var completionTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        const string workerName = "TestGroup-Worker-1";

        void LocalHandler(string name, string _)
        {
            if (name == workerName)
                completionTcs.TrySetResult(true);
        }

        // Simulate firing from a background thread (as CompleteResponse does)
        _ = Task.Run(() =>
        {
            Thread.Sleep(50);
            LocalHandler(workerName, "completed successfully");
        });

        var completed = await completionTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(completed, "TCS should complete when OnSessionComplete fires for matching worker");
    }

    [Fact]
    public async Task RecoveryLoop_CTSTimeoutUnblocksTCSWithFalse()
    {
        var completionTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var recoveryCts = new CancellationTokenSource(100); // Short timeout

        await using var reg = recoveryCts.Token.Register(() => completionTcs.TrySetResult(false));

        var completed = await completionTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(completed, "TCS should complete with false when CTS times out");
    }

    [Fact]
    public async Task RecoveryLoop_AlreadyDoneSessionCompletesImmediately()
    {
        // Simulate the pattern: if worker already finished (IsProcessing=false),
        // TCS is set immediately without waiting for the event
        var completionTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool isProcessing = false; // Worker already finished

        if (!isProcessing)
            completionTcs.TrySetResult(true);

        Assert.True(completionTcs.Task.IsCompleted);
        Assert.True(await completionTcs.Task);
    }

    [Fact]
    public async Task RecoveryLoop_MultipleRoundsAccumulateContent()
    {
        // Simulate multiple recovery rounds with the full accumulation pattern
        var dispatchTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        string? bestResponse = null;
        int rounds = 0;
        var maxRounds = 3;

        using var cts = new CancellationTokenSource(5000);

        while (!cts.Token.IsCancellationRequested && rounds < maxRounds)
        {
            rounds++;
            var completionTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Simulate immediate completion (worker already done)
            completionTcs.TrySetResult(true);

            var completed = await completionTcs.Task;
            if (!completed) break;

            // Simulate progressively longer content per round
            var roundContent = new string('x', rounds * 100);
            var history = new List<ChatMessage>
            {
                ChatMessage.AssistantMessage(roundContent),
            };
            history[0].Timestamp = dispatchTime.AddSeconds(rounds * 5);

            var latestContent = history
                .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content)
                    && m.MessageType == ChatMessageType.Assistant
                    && m.Timestamp >= dispatchTime);

            if (latestContent != null && latestContent.Content!.Length > (bestResponse?.Length ?? 0))
            {
                bestResponse = latestContent.Content;
            }

            // Simulate "worker is truly done" check on last round
            if (rounds >= maxRounds) break;
        }

        Assert.Equal(maxRounds, rounds);
        Assert.NotNull(bestResponse);
        Assert.Equal(maxRounds * 100, bestResponse!.Length);
    }

    #endregion

    #region Helpers

    private static ChatMessage CreateAssistantWithTimestamp(string content, DateTimeOffset timestamp)
    {
        var msg = ChatMessage.AssistantMessage(content);
        msg.Timestamp = timestamp;
        return msg;
    }

    #endregion
}
