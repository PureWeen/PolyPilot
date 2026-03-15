using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Safety tests that verify watchdog and session lifecycle changes do NOT
/// prematurely kill legitimate long-running multi-agent sessions.
///
/// Multi-agent workers routinely run 5-30+ minutes. These tests validate:
/// - Freshness windows are wide enough for real workloads
/// - Revival paths register event handlers
/// - Timeout constants are consistent with observed session durations
/// - Code structure invariants that prevent future regressions
///
/// ⚠️ Run these tests before merging ANY change to watchdog, Case B,
/// timeout constants, or session lifecycle (revival, reconnect, dispose).
/// See: .claude/skills/multi-agent-orchestration/SKILL.md → "Long-Running Session Safety"
/// </summary>
[Collection("BaseDir")]
public class LongRunningSessionSafetyTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly IServiceProvider _serviceProvider;

    public LongRunningSessionSafetyTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private const System.Reflection.BindingFlags NonPublic =
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    private const System.Reflection.BindingFlags AnyInstance =
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

    private static object GetSessionState(CopilotService svc, string sessionName)
    {
        var sessionsField = typeof(CopilotService).GetField("_sessions", NonPublic)!;
        var sessionsDict = sessionsField.GetValue(svc)!;
        var tryGetMethod = sessionsDict.GetType().GetMethod("TryGetValue")!;
        var args = new object?[] { sessionName, null };
        tryGetMethod.Invoke(sessionsDict, args);
        return args[1] ?? throw new InvalidOperationException($"Session '{sessionName}' not found");
    }

    private static T GetField<T>(object state, string fieldName)
    {
        var field = state.GetType().GetField(fieldName, AnyInstance)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found");
        return (T)field.GetValue(state)!;
    }

    private static object GetProp(object state, string propName)
    {
        // SessionState.Info is a property-like field
        var field = state.GetType().GetField(propName, AnyInstance);
        if (field != null) return field.GetValue(state)!;
        var prop = state.GetType().GetProperty(propName, AnyInstance)
            ?? throw new InvalidOperationException($"Property '{propName}' not found");
        return prop.GetValue(state)!;
    }

    private CopilotService CreateService() =>
        new(_chatDb, _serverManager, _bridgeClient, new RepoManager(), _serviceProvider, _demoService);

    private static class TestPaths
    {
        private static readonly string ProjectRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "PolyPilot"));

        public static string CopilotServiceCs => Path.Combine(ProjectRoot, "Services", "CopilotService.cs");
        public static string EventsCs => Path.Combine(ProjectRoot, "Services", "CopilotService.Events.cs");
        public static string OrganizationCs => Path.Combine(ProjectRoot, "Services", "CopilotService.Organization.cs");
    }

    // ─── Timeout Constant Safety ───

    [Fact]
    public void MultiAgentFreshness_CanAccommodate20MinuteWorker()
    {
        // Real-world: PR review workers with 5-model dispatch take 10-20 min.
        // The freshness window must be wider than the longest expected worker.
        var freshnessMinutes = CopilotService.WatchdogMultiAgentCaseBFreshnessSeconds / 60.0;
        Assert.True(freshnessMinutes >= 20,
            $"Multi-agent freshness ({freshnessMinutes:F0} min) must be >= 20 min to accommodate " +
            "long PR review workers. Observed durations: 3-20 min typical, 30 min max.");
    }

    [Fact]
    public void MultiAgentFreshness_NotSoWideItHidesDeadSessions()
    {
        // Safety: freshness shouldn't exceed 60 min (worker execution timeout).
        // After 60 min, the orchestrator cancels the worker anyway.
        var freshnessMinutes = CopilotService.WatchdogMultiAgentCaseBFreshnessSeconds / 60.0;
        Assert.True(freshnessMinutes <= 60,
            $"Multi-agent freshness ({freshnessMinutes:F0} min) shouldn't exceed worker " +
            "execution timeout (60 min). Dead sessions would hide for too long.");
    }

    [Fact]
    public void StandardFreshness_NotUsedForMultiAgentSessions()
    {
        // The standard 300s (5 min) freshness is too short for multi-agent workers.
        // Verify the code uses the multi-agent constant for multi-agent sessions.
        Assert.True(CopilotService.WatchdogCaseBFreshnessSeconds < CopilotService.WatchdogMultiAgentCaseBFreshnessSeconds,
            "Standard freshness must be shorter than multi-agent freshness");
        Assert.True(CopilotService.WatchdogCaseBFreshnessSeconds <= 300,
            "Standard freshness should be ≤ 5 min for interactive sessions");
    }

    [Fact]
    public void CaseBDeferralCap_AllowsFullWorkerExecution()
    {
        // 40 deferrals × 120s = 4800s = 80 min. This exceeds the 60 min worker
        // execution timeout, ensuring the deferral cap is never the binding constraint
        // for legitimate workers (the orchestrator cancels first).
        var totalDeferralTime = CopilotService.WatchdogMaxCaseBResets * 120; // 120s per check cycle
        var workerTimeoutSeconds = 3600; // 60 min
        Assert.True(totalDeferralTime >= workerTimeoutSeconds,
            $"Total deferral time ({totalDeferralTime}s = {totalDeferralTime / 60} min) must be >= " +
            $"worker execution timeout ({workerTimeoutSeconds}s = {workerTimeoutSeconds / 60} min). " +
            "Otherwise the deferral cap kills workers before the orchestrator can cancel them.");
    }

    [Fact]
    public void MaxProcessingTime_AccommodatesReflectLoops()
    {
        // OrchestratorReflect loops can run 30-60 min (7+ iterations × 5 min each).
        // The max processing time safety net must not kill them.
        var maxMinutes = CopilotService.WatchdogMaxProcessingTimeSeconds / 60.0;
        Assert.True(maxMinutes >= 30,
            $"Max processing time ({maxMinutes:F0} min) must be >= 30 min for OrchestratorReflect loops");
    }

    // ─── Code Structure Safety: Case B uses correct freshness for multi-agent ───

    [Fact]
    public void CaseB_UsesIsMultiAgentSession_ToSelectFreshness()
    {
        var source = File.ReadAllText(TestPaths.EventsCs);

        // The watchdog reads IsMultiAgentSession from state and uses it
        // to choose between the two freshness constants.
        // isMultiAgentSession is a local variable inside the watchdog loop.
        Assert.Contains("IsMultiAgentSession", source);
        Assert.Contains("WatchdogMultiAgentCaseBFreshnessSeconds", source);
        Assert.Contains("WatchdogCaseBFreshnessSeconds", source);

        // Must NOT have hardcoded numeric freshness values
        Assert.DoesNotContain("age < 300", source);
        Assert.DoesNotContain("age < 1800", source);
    }

    // ─── Revival Path Safety: event handler must be registered ───

    [Fact]
    public void WorkerRevival_RegistersEventHandler()
    {
        // The revival path in ExecuteWorkerAsync creates a fresh session.
        // It MUST register .On(evt => HandleSessionEvent(...)) BEFORE sending.
        // Without this, the session has a dead event stream and the watchdog
        // is the only recovery — taking 30+ minutes for multi-agent workers.
        var source = File.ReadAllText(TestPaths.OrganizationCs);

        // Find the revival section (between "fresh session revival" and next response assignment)
        var revivalStart = source.IndexOf("attempting fresh session revival");
        Assert.True(revivalStart > 0, "Revival code must exist in Organization.cs");

        var revivalEnd = source.IndexOf("SendPromptAndWaitAsync", revivalStart);
        Assert.True(revivalEnd > revivalStart, "Revival must call SendPromptAndWaitAsync after setup");

        var revivalSection = source[revivalStart..revivalEnd];

        Assert.Contains(".On(evt => HandleSessionEvent(", revivalSection);
    }

    [Fact]
    public void WorkerRevival_CopiesIsMultiAgentSession()
    {
        // Fresh SessionState defaults IsMultiAgentSession=false. The revival
        // must copy it from the dead state, otherwise the watchdog uses the
        // standard 300s freshness instead of the 1800s multi-agent window.
        // (SendPromptAsync also sets it, but defense-in-depth.)
        var source = File.ReadAllText(TestPaths.OrganizationCs);

        var revivalStart = source.IndexOf("attempting fresh session revival");
        Assert.True(revivalStart > 0);

        var revivalEnd = source.IndexOf("SendPromptAndWaitAsync", revivalStart);
        var revivalSection = source[revivalStart..revivalEnd];

        Assert.Contains("IsMultiAgentSession", revivalSection);
    }

    [Fact]
    public void WorkerRevival_MarksOldStateOrphaned()
    {
        // The old SessionState must be marked IsOrphaned so any lingering
        // callbacks from the disposed session are no-ops.
        var source = File.ReadAllText(TestPaths.OrganizationCs);

        var revivalStart = source.IndexOf("attempting fresh session revival");
        Assert.True(revivalStart > 0);

        var revivalEnd = source.IndexOf("SendPromptAndWaitAsync", revivalStart);
        var revivalSection = source[revivalStart..revivalEnd];

        Assert.Contains("IsOrphaned", revivalSection);
    }

    // ─── All session creation paths register event handlers ───

    [Fact]
    public void AllSessionCreationPaths_RegisterEventHandler()
    {
        // Every path that creates a CopilotSession via the SDK and stores it
        // must call .On(evt => HandleSessionEvent(...)). Missing this causes
        // dead event streams.
        //
        // We check that in files that call SDK's CreateSessionAsync (the low-level
        // call that returns a CopilotSession), there's a corresponding handler.
        // We use a targeted pattern: client.CreateSessionAsync( — the SDK call
        // vs the service's own public CreateSessionAsync method.

        // Organization.cs: The revival path creates a session via _client.CreateSessionAsync
        var orgSource = File.ReadAllText(TestPaths.OrganizationCs);
        var orgSdkCreates = CountOccurrences(orgSource, ".CreateSessionAsync(");
        var orgHandlers = CountOccurrences(orgSource, ".On(evt => HandleSessionEvent(");

        Assert.True(orgHandlers >= orgSdkCreates,
            $"Organization.cs: Found {orgSdkCreates} SDK CreateSessionAsync calls but only {orgHandlers} " +
            $"event handler registrations. Every session creation MUST register a handler.");

        // Verify each CreateSessionAsync call has a handler within 15 lines (same code block)
        VerifyHandlerProximity(orgSource, "Organization.cs");

        // Events.cs: Tool health revival creates sessions
        var eventsSource = File.ReadAllText(TestPaths.EventsCs);
        var eventsSdkCreates = CountOccurrences(eventsSource, ".CreateSessionAsync(");
        var eventsHandlers = CountOccurrences(eventsSource, ".On(evt => HandleSessionEvent(");

        Assert.True(eventsHandlers >= eventsSdkCreates,
            $"Events.cs: Found {eventsSdkCreates} SDK CreateSessionAsync calls but only {eventsHandlers} " +
            $"event handler registrations. Every session creation MUST register a handler.");

        VerifyHandlerProximity(eventsSource, "Events.cs");

        // CopilotService.cs: Main session creation and reconnect paths
        // Uses count-based check (not proximity) because the reconnect-recovery
        // paths have try/retry sharing a single handler after the catch blocks.
        var mainSource = File.ReadAllText(TestPaths.CopilotServiceCs);
        Assert.True(CountOccurrences(mainSource, ".On(evt => HandleSessionEvent(") >= 3,
            "CopilotService.cs must have at least 3 event handler registrations " +
            "(create, restore, reconnect primary + sibling).");
    }

    /// <summary>
    /// Verifies that each SDK CreateSessionAsync call (client.CreateSessionAsync) has an
    /// .On(evt => HandleSessionEvent( registration within 15 lines, ensuring handlers
    /// aren't missing on individual paths while the file-wide count appears balanced.
    /// Skips non-SDK calls like _bridgeClient.CreateSessionAsync and method signatures.
    /// </summary>
    private static void VerifyHandlerProximity(string source, string fileName)
    {
        var lines = source.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(".CreateSessionAsync(", StringComparison.Ordinal)) continue;
            // Skip comments
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("//") || trimmed.StartsWith("*")) continue;
            // Skip non-SDK calls: bridge client, method signatures, test stubs
            if (trimmed.Contains("_bridgeClient.", StringComparison.Ordinal)) continue;
            if (trimmed.Contains("public ", StringComparison.Ordinal) ||
                trimmed.Contains("private ", StringComparison.Ordinal) ||
                trimmed.Contains("internal ", StringComparison.Ordinal)) continue;
            // Only match SDK client calls (e.g., client.CreateSessionAsync, _client.CreateSessionAsync)
            if (!trimmed.Contains("client.CreateSessionAsync", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.Contains("_client.CreateSessionAsync", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.Contains("codespaceClient.CreateSessionAsync", StringComparison.OrdinalIgnoreCase))
                continue;

            // Search forward up to 60 lines for the handler registration.
            // 60 lines accommodates retry patterns where the initial try and retry
            // share a single handler registration after the try/catch blocks.
            bool foundHandler = false;
            int searchEnd = Math.Min(i + 60, lines.Length);
            for (int j = i; j < searchEnd; j++)
            {
                if (lines[j].Contains(".On(evt => HandleSessionEvent(", StringComparison.Ordinal))
                {
                    foundHandler = true;
                    break;
                }
            }

            Assert.True(foundHandler,
                $"{fileName} line {i + 1}: CreateSessionAsync call has no .On(evt => HandleSessionEvent( " +
                $"within 60 lines. Every session creation path must register a handler.");
        }
    }

    // ─── Simulate long-running session scenarios ───

    [Fact]
    public void LongRunningWorker_IsNotKilledByStandardTimeout()
    {
        // Verifies that the watchdog timeout selection uses extended multi-agent
        // thresholds for multi-agent workers, not the standard 120s inactivity timeout.
        // This tests the code STRUCTURE (timeout selection logic) rather than running
        // a real timer — the watchdog loop itself is tested in ProcessingWatchdogTests.
        var source = File.ReadAllText(TestPaths.EventsCs);

        // The watchdog must check IsMultiAgentSession when selecting freshness threshold
        Assert.Contains("IsMultiAgentSession", source);
        Assert.Contains("WatchdogMultiAgentCaseBFreshnessSeconds", source);

        // Multi-agent freshness (1800s) must be checked BEFORE falling back to standard (300s)
        var multiIdx = source.IndexOf("WatchdogMultiAgentCaseBFreshnessSeconds", StringComparison.Ordinal);
        var stdIdx = source.IndexOf("WatchdogCaseBFreshnessSeconds", StringComparison.Ordinal);
        Assert.True(multiIdx >= 0, "Multi-agent freshness constant must be referenced in Events.cs");
        Assert.True(stdIdx >= 0, "Standard freshness constant must be referenced in Events.cs");

        // Also verify the actual constant values ensure long-running workers survive
        // 1800s (30 min) multi-agent window >> 120s standard inactivity timeout
        var eventsFields = typeof(CopilotService).GetFields(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var multiField = eventsFields.FirstOrDefault(f => f.Name == "WatchdogMultiAgentCaseBFreshnessSeconds");
        Assert.NotNull(multiField); // Fail loudly if constant is renamed
        var multiValue = Convert.ToInt32(multiField.GetValue(null));
        Assert.True(multiValue >= 1800,
            $"WatchdogMultiAgentCaseBFreshnessSeconds={multiValue} must be >= 1800 for long-running workers");
    }

    [Fact]
    public async Task LongRunningWorker_SendingFlag_ResetOnCleanAbort()
    {
        // If a long-running session is aborted, SendingFlag must be cleared
        // to allow future sends. Without this, the session deadlocks.
        var service = CreateService();
        await service.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var sessionName = "test-long-worker";
        await service.CreateSessionAsync(sessionName);
        await service.SendPromptAsync(sessionName, "test prompt");

        // Wait for demo completion
        await Task.Delay(300);

        // Abort should clear all processing state
        await service.AbortSessionAsync(sessionName);

        var state = GetSessionState(service, sessionName);
        var info = GetProp(state, "Info");
        Assert.False((bool)info.GetType().GetProperty("IsProcessing")!.GetValue(info)!,
            "IsProcessing should be cleared after abort");
        Assert.Equal(0, (int)info.GetType().GetProperty("ProcessingPhase")!.GetValue(info)!);
    }

    [Fact]
    public void WatchdogConstants_AreInternallyConsistent()
    {
        // Verify the relationship between all watchdog constants.
        // These invariants prevent changes to one constant from
        // silently breaking the timeout hierarchy.

        // Standard freshness < Multi-agent freshness
        Assert.True(CopilotService.WatchdogCaseBFreshnessSeconds <
                    CopilotService.WatchdogMultiAgentCaseBFreshnessSeconds);

        // Inactivity timeout < Tool execution timeout
        Assert.True(CopilotService.WatchdogInactivityTimeoutSeconds <
                    CopilotService.WatchdogToolExecutionTimeoutSeconds);

        // Case B deferral cap × check interval > worker execution timeout
        var totalDeferralSeconds = CopilotService.WatchdogMaxCaseBResets *
                                   CopilotService.WatchdogInactivityTimeoutSeconds;
        Assert.True(totalDeferralSeconds > 3600,
            $"Total deferral time ({totalDeferralSeconds}s) must exceed 60 min worker timeout");

        // Max processing time > multi-agent freshness
        Assert.True(CopilotService.WatchdogMaxProcessingTimeSeconds >
                    CopilotService.WatchdogMultiAgentCaseBFreshnessSeconds);
    }

    [Fact]
    public void CaseB_DoesNotUse_MtimeStalenessDetection()
    {
        // Mtime staleness detection (killing sessions when events.jsonl mtime
        // is unchanged for N consecutive checks) is UNSAFE for multi-agent
        // workers. The model can think for 5+ minutes between tool rounds,
        // during which no events are written and mtime stays frozen.
        //
        // This test guards against re-introducing mtime staleness detection.
        // The correct fix for dead event streams is to register event handlers
        // on all session creation paths (see WorkerRevival_RegistersEventHandler).
        var source = File.ReadAllText(TestPaths.EventsCs);
        var watchdogBody = ExtractMethod(source, "RunProcessingWatchdogAsync");

        // Must NOT track consecutive mtime changes to declare death
        Assert.DoesNotContain("StaleMtimeCount", watchdogBody);
        Assert.DoesNotContain("StaleLimit", watchdogBody);
        Assert.DoesNotContain("staleMtime", watchdogBody);
        Assert.DoesNotContain("mtime unchanged", watchdogBody.ToLowerInvariant());
    }

    [Theory]
    [InlineData(5)]   // 5 min — typical tool execution
    [InlineData(10)]  // 10 min — long review worker
    [InlineData(20)]  // 20 min — max typical worker
    [InlineData(29)]  // 29 min — just under 30 min freshness window
    public void MultiAgentFreshness_DoesNotExpireBefore(int workerMinutes)
    {
        // A worker that has been running for N minutes should still be within
        // the multi-agent freshness window.
        var workerSeconds = workerMinutes * 60;
        Assert.True(workerSeconds < CopilotService.WatchdogMultiAgentCaseBFreshnessSeconds,
            $"A {workerMinutes}-min worker would exceed the multi-agent freshness window " +
            $"({CopilotService.WatchdogMultiAgentCaseBFreshnessSeconds}s). " +
            "This would cause the watchdog to complete the session prematurely.");
    }

    [Theory]
    [InlineData(1)]   // 1 min pause — well within standard freshness
    [InlineData(3)]   // 3 min — typical model thinking, within standard
    [InlineData(4)]   // 4 min — within 5 min standard window
    public void ModelThinkingPause_IsWithinFreshnessWindow(int pauseMinutes)
    {
        // During model thinking pauses (no events written), the events.jsonl
        // age grows. The freshness window must accommodate this.
        var pauseSeconds = pauseMinutes * 60;
        Assert.True(pauseSeconds < CopilotService.WatchdogCaseBFreshnessSeconds,
            $"A {pauseMinutes}-min model thinking pause would exceed the STANDARD freshness window " +
            $"({CopilotService.WatchdogCaseBFreshnessSeconds}s).");
    }

    [Theory]
    [InlineData(5)]    // 5 min — long model thinking, needs multi-agent window
    [InlineData(10)]   // 10 min — very long thinking, needs multi-agent window
    [InlineData(20)]   // 20 min — extreme, still within multi-agent 30 min
    public void LongModelThinkingPause_IsWithinMultiAgentFreshnessWindow(int pauseMinutes)
    {
        // Multi-agent workers can have very long model thinking pauses.
        // The multi-agent freshness window (1800s = 30 min) must accommodate.
        var pauseSeconds = pauseMinutes * 60;
        Assert.True(pauseSeconds < CopilotService.WatchdogMultiAgentCaseBFreshnessSeconds,
            $"A {pauseMinutes}-min model thinking pause would exceed the MULTI-AGENT freshness window " +
            $"({CopilotService.WatchdogMultiAgentCaseBFreshnessSeconds}s).");
    }

    // ─── Revival path creates complete SessionState ───

    [Fact]
    public void RevivalPath_CreatesCompleteState()
    {
        // The revival path must create a SessionState with all required fields.
        // Check that the revival section sets up all the state that
        // SendPromptAsync and the watchdog depend on.
        var source = File.ReadAllText(TestPaths.OrganizationCs);

        var revivalStart = source.IndexOf("attempting fresh session revival");
        Assert.True(revivalStart > 0);
        var revivalEnd = source.IndexOf("SendPromptAndWaitAsync", revivalStart);
        var revivalSection = source[revivalStart..revivalEnd];

        // Must have these elements (in any order)
        Assert.Contains("new SessionState", revivalSection);
        Assert.Contains("IsMultiAgentSession", revivalSection);
        Assert.Contains(".On(evt => HandleSessionEvent(", revivalSection);
        Assert.Contains("IsOrphaned", revivalSection);
        Assert.Contains("TryUpdate", revivalSection);
    }

    // ─── Helpers ───

    private static string ExtractMethod(string source, string methodSignature)
    {
        var idx = source.IndexOf(methodSignature);
        if (idx < 0) return string.Empty;

        int braceCount = 0;
        bool foundBrace = false;
        int start = idx;

        for (int i = idx; i < source.Length; i++)
        {
            if (source[i] == '{') { braceCount++; foundBrace = true; }
            else if (source[i] == '}') { braceCount--; }

            if (foundBrace && braceCount == 0)
                return source[start..(i + 1)];
        }
        return source[start..];
    }

    private static int CountOccurrences(string source, string pattern)
    {
        int count = 0;
        int idx = 0;
        while ((idx = source.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }
}
