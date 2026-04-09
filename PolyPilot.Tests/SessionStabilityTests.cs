using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for session stability hardening from PR #373:
/// - IsOrphaned guards on all event/timer entry points
/// - ForceCompleteProcessingAsync INV-1 compliance
/// - Mixed worker success/failure in synthesis prompt
/// - TryUpdate concurrency guard on reconnect
/// - Sibling TCS cancellation on orphan
/// - MCP servers reload on reconnect
/// - Collection snapshots before Task.Run
/// </summary>
[Collection("BaseDir")]
public class SessionStabilityTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly IServiceProvider _serviceProvider;

    public SessionStabilityTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new(_chatDb, _serverManager, _bridgeClient, new RepoManager(), _serviceProvider, _demoService);

    // ─── IsOrphaned Guard Tests (source verification) ───

    [Fact]
    public void HandleSessionEvent_ChecksIsOrphaned_BeforeProcessing()
    {
        var source = File.ReadAllText(TestPaths.EventsCs);
        var handleMethod = ExtractMethod(source, "void HandleSessionEvent");
        Assert.Contains("IsOrphaned", handleMethod);
        // The orphan check should guard with an immediate return
        Assert.Contains("if (state.IsOrphaned)", handleMethod);
    }

    [Fact]
    public void CompleteResponse_ChecksIsOrphaned_AndCancelsTcs()
    {
        var source = File.ReadAllText(TestPaths.EventsCs);
        var method = ExtractMethod(source, "void CompleteResponse");
        Assert.Contains("IsOrphaned", method);
        Assert.Contains("TrySetCanceled", method,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WatchdogLoop_ChecksIsOrphaned_AndExits()
    {
        var source = File.ReadAllText(TestPaths.EventsCs);
        var method = ExtractMethod(source, "RunProcessingWatchdogAsync");
        Assert.Contains("IsOrphaned", method);
    }

    [Fact]
    public void IsOrphaned_IsVolatile()
    {
        var source = File.ReadAllText(TestPaths.CopilotServiceCs);
        // SessionState must declare IsOrphaned as volatile for cross-thread visibility
        Assert.Contains("volatile bool IsOrphaned", source);
    }

    // ─── ForceCompleteProcessingAsync INV-1 Tests ───

    [Fact]
    public void ForceCompleteProcessing_ClearsAllInv1Fields()
    {
        var source = File.ReadAllText(TestPaths.OrganizationCs);
        var method = ExtractMethod(source, "Task ForceCompleteProcessingAsync");

        // ForceCompleteProcessingAsync must call ClearProcessingState (which atomically
        // clears all INV-1 fields) and the other required operations
        var requiredPatterns = new[]
        {
            "ClearProcessingState(state",  // Atomic INV-1 field clearing
            "FlushCurrentResponse",         // INV-1 field 9
            "OnSessionComplete",            // INV-1 field 10
            "TrySetResult",                 // Resolves the worker TCS
            "AllowTurnStartRearm = false",  // Explicit recovery terminal
        };

        foreach (var field in requiredPatterns)
        {
            Assert.True(method.Contains(field, StringComparison.Ordinal),
                $"ForceCompleteProcessingAsync must contain '{field}'");
        }
    }

    [Fact]
    public void ForceCompleteProcessing_CancelsTimersBeforeUiThreadWork()
    {
        var source = File.ReadAllText(TestPaths.OrganizationCs);
        var method = ExtractMethod(source, "Task ForceCompleteProcessingAsync");

        // Timer cancellation must happen BEFORE InvokeOnUI (thread-safe operations first)
        var cancelIdx = method.IndexOf("CancelProcessingWatchdog", StringComparison.Ordinal);
        var invokeIdx = method.IndexOf("InvokeOnUI", StringComparison.Ordinal);
        Assert.True(cancelIdx >= 0, "CancelProcessingWatchdog must be present in ForceCompleteProcessingAsync");
        Assert.True(invokeIdx >= 0, "InvokeOnUI must be present in ForceCompleteProcessingAsync");
        Assert.True(cancelIdx < invokeIdx,
            "Timer cancellation must happen before InvokeOnUI in ForceCompleteProcessingAsync");
    }

    [Fact]
    public void ForceCompleteProcessing_SkipsIfNotProcessing()
    {
        var source = File.ReadAllText(TestPaths.OrganizationCs);
        var method = ExtractMethod(source, "Task ForceCompleteProcessingAsync");

        // Must early-return if already not processing (idempotent)
        Assert.Contains("!state.Info.IsProcessing", method);
    }

    [Fact]
    public void ForceCompleteProcessing_BoundsAbortAsyncTimeout()
    {
        var source = File.ReadAllText(TestPaths.OrganizationCs);
        var method = ExtractMethod(source, "Task ForceCompleteProcessingAsync");

        Assert.Contains("ForceCompleteAbortTimeoutSeconds", source);
        Assert.Contains("new CancellationTokenSource(TimeSpan.FromSeconds(ForceCompleteAbortTimeoutSeconds))", method);
        Assert.Contains("await session.AbortAsync(abortCts.Token);", method);
        Assert.Contains("OperationCanceledException", method);
    }

    [Fact]
    public void ForceCompleteProcessing_UsesClearProcessingState()
    {
        // ForceCompleteProcessingAsync must delegate to ClearProcessingState rather than
        // manually clearing fields. ClearProcessingState calls ClearDeferredIdleTracking
        // with preserveCarryOver: true so stale shell fingerprints survive across turns.
        var source = File.ReadAllText(TestPaths.OrganizationCs);
        var method = ExtractMethod(source, "Task ForceCompleteProcessingAsync");

        Assert.Contains("ClearProcessingState(state", method);
    }

    [Fact]
    public void OrchestratorTimeout_ResultCollection_PreservesWorkerNames()
    {
        var source = File.ReadAllText(TestPaths.OrganizationCs);

        Assert.Contains("var workerName = i < assignments.Count ? assignments[i].WorkerName : \"unknown\";", source);
        Assert.DoesNotContain("new WorkerResult(\"unknown\", null, false", source);
    }

    // ─── Mixed Worker Success/Failure Synthesis Tests ───

    [Fact]
    public void BuildSynthesisPrompt_IncludesBothSuccessAndFailure()
    {
        var source = File.ReadAllText(TestPaths.OrganizationCs);
        var method = ExtractMethod(source, "string BuildSynthesisPrompt");

        // Must include success indicator
        Assert.Contains("completed", method);
        // Must include failure indicator
        Assert.Contains("failed", method);
        // Must include error text for failed workers
        Assert.Contains("result.Error", method);
    }

    [Fact]
    public void BuildSynthesisPrompt_SanitizesReflectSentinel()
    {
        var source = File.ReadAllText(TestPaths.OrganizationCs);
        var method = ExtractMethod(source, "string BuildSynthesisPrompt");

        // Worker responses containing the reflect complete sentinel must be sanitized
        // to prevent the orchestrator from echoing it and causing false loop termination
        Assert.Contains("GROUP_REFLECT_COMPLETE", method);
        Assert.Contains("WORKER_APPROVED", method);
    }

    // ─── Sibling Re-Resume Safety Tests (source verification) ───

    [Fact]
    public void SiblingReResume_OrphansOldState_BeforeCreatingNew()
    {
        var source = File.ReadAllText(TestPaths.CopilotServiceCs);

        // Must set IsOrphaned = true on old state during reconnect
        Assert.Contains("IsOrphaned = true", source);
        // Must cancel old TCS
        Assert.Contains("TrySetCanceled", source);
        // Must set ProcessingGeneration to max to prevent stale callbacks
        Assert.Contains("long.MaxValue", source);
    }

    [Fact]
    public void SiblingReResume_UsesTryUpdate_NotIndexAssignment()
    {
        var source = File.ReadAllText(TestPaths.CopilotServiceCs);

        // Reconnect sibling path must use TryUpdate for atomic swap (prevents stale Task.Run overwrite)
        // The sibling re-resume code lives inside SendPromptAsync's reconnect-on-failure path
        var sendMethod = ExtractMethod(source, "Task<string> SendPromptAsync(");
        Assert.False(string.IsNullOrEmpty(sendMethod), "SendPromptAsync method must exist");
        Assert.Contains("TryUpdate", sendMethod);
    }

    [Fact]
    public void SiblingReResume_SnapshotsCollections_BeforeTaskRun()
    {
        var source = File.ReadAllText(TestPaths.CopilotServiceCs);

        // Must snapshot Organization.Sessions and Groups before Task.Run
        // (List<T> is not thread-safe for concurrent reads during modification)
        // The sibling re-resume code lives inside SendPromptAsync's reconnect-on-failure path
        var sendMethod = ExtractMethod(source, "Task<string> SendPromptAsync(");
        Assert.False(string.IsNullOrEmpty(sendMethod), "SendPromptAsync method must exist");
        Assert.Contains("Sessions.ToList()", sendMethod);
        Assert.Contains("Groups.ToList()", sendMethod);
    }

    [Fact]
    public void SiblingReResume_RegistersHandler_BeforePublishing()
    {
        var source = File.ReadAllText(TestPaths.CopilotServiceCs);

        // Handler registration (HandleSessionEvent(siblingState)) must appear
        // in the SendPromptAsync reconnect path — paired with TryUpdate for correct ordering
        var sendMethod = ExtractMethod(source, "Task<string> SendPromptAsync(");
        Assert.False(string.IsNullOrEmpty(sendMethod), "SendPromptAsync method must exist");
        Assert.Contains("HandleSessionEvent(siblingState", sendMethod);

        // Handler must appear BEFORE TryUpdate (register before publishing)
        var handlerIdx = sendMethod.IndexOf("HandleSessionEvent(siblingState", StringComparison.Ordinal);
        var tryUpdateIdx = sendMethod.IndexOf("TryUpdate", StringComparison.Ordinal);
        Assert.True(handlerIdx >= 0, "HandleSessionEvent(siblingState must be present in reconnect path");
        Assert.True(tryUpdateIdx >= 0, "TryUpdate must be present in reconnect path");
        Assert.True(handlerIdx < tryUpdateIdx,
            "Handler registration must happen BEFORE TryUpdate (no window where events arrive with no handler)");
    }

    [Fact]
    public void ReconnectConfig_LoadsMcpServers_BothPaths()
    {
        var source = File.ReadAllText(TestPaths.CopilotServiceCs);

        // Both sibling and primary reconnect paths must reload MCP servers
        var mcpCount = CountOccurrences(source, "LoadMcpServers");
        Assert.True(mcpCount >= 2,
            $"Expected LoadMcpServers in both sibling and primary reconnect paths, found {mcpCount} occurrences");
    }

    [Fact]
    public void ReconnectConfig_LoadsSkillDirectories_BothPaths()
    {
        var source = File.ReadAllText(TestPaths.CopilotServiceCs);

        var skillCount = CountOccurrences(source, "LoadSkillDirectories");
        Assert.True(skillCount >= 2,
            $"Expected LoadSkillDirectories in both sibling and primary reconnect paths, found {skillCount} occurrences");
    }

    // ─── Diagnostic Log Tag Completeness ───

    [Fact]
    public void AllIsProcessingFalsePaths_HaveDiagnosticLogEntry()
    {
        // Verify that every IsProcessing = false has a nearby Debug() call
        var eventsSource = File.ReadAllText(TestPaths.EventsCs);
        var serviceSource = File.ReadAllText(TestPaths.CopilotServiceCs);

        // Events.cs paths
        var eventsFalseCount = CountOccurrences(eventsSource, "IsProcessing = false");
        var eventsDebugTagCount = CountPatterns(eventsSource, new[] {
            "[COMPLETE]", "[ERROR]", "[WATCHDOG]", "[BRIDGE-COMPLETE]", "[INTERRUPTED]"
        });
        Assert.True(eventsDebugTagCount >= eventsFalseCount,
            $"Events.cs has {eventsFalseCount} IsProcessing=false paths but only {eventsDebugTagCount} diagnostic tags");

        // CopilotService.cs paths (ABORT, ERROR, SEND-fail)
        var serviceFalseCount = CountOccurrences(serviceSource, "IsProcessing = false");
        var serviceDebugTagCount = CountPatterns(serviceSource, new[] {
            "[ABORT]", "[ERROR]", "[SEND]"
        });
        // At least the abort paths should have tags
        Assert.True(serviceDebugTagCount >= 2,
            "CopilotService.cs must have diagnostic tags for abort and send-failure paths");
    }

    // ─── Processing Watchdog Orphan Guard ───

    [Fact]
    public void WatchdogCrashRecovery_ClearsAllCompanionFields()
    {
        var source = File.ReadAllText(TestPaths.EventsCs);
        var watchdogMethod = ExtractMethod(source, "RunProcessingWatchdogAsync");

        // The crash recovery block must call ClearProcessingState (which atomically
        // clears IsProcessing, ProcessingPhase, ProcessingStartedAt, ToolCallCount, etc.)
        Assert.True(watchdogMethod.Contains("ClearProcessingState(state", StringComparison.Ordinal),
            "Watchdog crash recovery must call ClearProcessingState to atomically clear all companion fields");
        // Must also set AllowTurnStartRearm = false (terminal forced stop)
        Assert.True(watchdogMethod.Contains("AllowTurnStartRearm = false", StringComparison.Ordinal),
            "Watchdog crash recovery must set AllowTurnStartRearm = false");
    }

    // ─── Multi-Agent Fix Prompt Enhancement ───

    [Fact]
    public void BuildCopilotPrompt_IncludesMultiAgentSection_InSource()
    {
        var source = File.ReadAllText(TestPaths.SessionSidebarRazor);

        // Fix prompt must include multi-agent testing requirements when session is in a group
        Assert.Contains("Multi-Agent Testing Requirements", source);
        Assert.Contains("IsSessionInMultiAgentGroup", source);
    }

    [Fact]
    public void GetBugReportDebugInfo_IncludesMultiAgentContext_InSource()
    {
        var source = File.ReadAllText(TestPaths.SessionSidebarRazor);

        // Bug report debug info must include multi-agent context
        Assert.Contains("AppendMultiAgentDebugInfo", source);
    }

    [Fact]
    public void AppendMultiAgentDebugInfo_IncludesEventDiagnostics()
    {
        var source = File.ReadAllText(TestPaths.SessionSidebarRazor);

        // Multi-agent debug info must include:
        Assert.Contains("OrchestratorMode", source);     // Group mode
        Assert.Contains("event-diagnostics", source);    // Recent events
        Assert.Contains("pending-orchestration", source); // Pending state
    }

    // ─── IsOrphaned Reset on Lazy-Resume (PR #522) ───

    [Fact]
    public void EnsureSessionConnected_ClearsIsOrphaned()
    {
        // The lazy-resume path (EnsureSessionConnectedAsync) must reset IsOrphaned = false
        // when it creates/resumes a fresh SDK session. Without this, a sibling reconnect
        // failure can permanently orphan a SessionState, causing HandleSessionEvent to
        // silently drop all events and leaving the session stuck.
        var source = File.ReadAllText(TestPaths.PersistenceCs);
        var method = ExtractMethod(source, "Task EnsureSessionConnectedAsync");
        Assert.Contains("IsOrphaned = false", method);
    }

    [Fact]
    public void EnsureSessionConnected_ClearsIsOrphaned_BeforeHandlerRegistration()
    {
        // IsOrphaned must be cleared BEFORE copilotSession.On(HandleSessionEvent) —
        // if cleared after, early SDK replay events (session.resume, buffered idle) would
        // be silently dropped by the IsOrphaned guard, causing the exact stuck-session
        // symptom this fix addresses.
        var source = File.ReadAllText(TestPaths.PersistenceCs);
        var method = ExtractMethod(source, "Task EnsureSessionConnectedAsync");

        var orphanResetIdx = method.IndexOf("IsOrphaned = false", StringComparison.Ordinal);
        var handlerIdx = method.IndexOf("HandleSessionEvent(state", StringComparison.Ordinal);
        Assert.True(orphanResetIdx >= 0, "EnsureSessionConnectedAsync must contain 'IsOrphaned = false'");
        Assert.True(handlerIdx >= 0, "EnsureSessionConnectedAsync must register HandleSessionEvent");
        Assert.True(orphanResetIdx < handlerIdx,
            "IsOrphaned = false must appear BEFORE HandleSessionEvent registration to prevent event-drop window");
    }

    // ─── StartLanBridge Tunnel Fallback (PR #522) ───

    [Fact]
    public void StartLanBridge_DoesNotCheckDirectSharingEnabled()
    {
        // StartLanBridge intentionally bypasses DirectSharingEnabled — when the user
        // enabled AutoStartTunnel they opted into network access, so a failed tunnel
        // should degrade to LAN rather than leaving the bridge unreachable.
        var source = File.ReadAllText(TestPaths.DashboardRazor);
        var method = ExtractMethod(source, "void StartLanBridge");
        Assert.DoesNotContain("DirectSharingEnabled", method);
    }

    [Fact]
    public void StartLanBridge_RequiresServerPassword()
    {
        // Even though DirectSharingEnabled is bypassed, ServerPassword is still required
        // for security — the bridge must always be password-protected.
        var source = File.ReadAllText(TestPaths.DashboardRazor);
        var method = ExtractMethod(source, "void StartLanBridge");
        Assert.Contains("ServerPassword", method);
    }

    [Fact]
    public void TunnelFailureFallback_CallsStartLanBridge_NotDirectSharing()
    {
        // After tunnel failure, the fallback must call StartLanBridge (unconditional LAN),
        // not StartDirectSharingIfEnabled (which checks DirectSharingEnabled flag).
        var source = File.ReadAllText(TestPaths.DashboardRazor);

        // Find the tunnel failure handler (the !success branch inside the Task.Run)
        var tunnelFailIdx = source.IndexOf("Tunnel failed, falling back to LAN bridge", StringComparison.Ordinal);
        Assert.True(tunnelFailIdx >= 0, "Tunnel failure log message must exist");

        // The next method call after the log should be StartLanBridge, not StartDirectSharingIfEnabled
        var afterFail = source[tunnelFailIdx..];
        var lanBridgeIdx = afterFail.IndexOf("StartLanBridge", StringComparison.Ordinal);
        var directSharingIdx = afterFail.IndexOf("StartDirectSharingIfEnabled", StringComparison.Ordinal);
        Assert.True(lanBridgeIdx >= 0, "StartLanBridge must be called after tunnel failure");
        Assert.True(lanBridgeIdx < directSharingIdx,
            "StartLanBridge must be called (not StartDirectSharingIfEnabled) on tunnel failure path");
    }

    // ─── Helpers ───

    private static string ExtractMethod(string source, string methodSignature)
    {
        var idx = source.IndexOf(methodSignature, StringComparison.Ordinal);
        if (idx < 0) return "";
        var braceIdx = source.IndexOf('{', idx);
        if (braceIdx < 0) return "";
        return source[idx..FindEndOfBlock(source, braceIdx)];
    }

    private static int FindEndOfBlock(string source, int openBraceIdx)
    {
        int depth = 0;
        for (int i = openBraceIdx; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}') { depth--; if (depth == 0) return i + 1; }
        }
        return source.Length;
    }

    private static int FindMatchingBrace(string text)
    {
        var braceIdx = text.IndexOf('{');
        if (braceIdx < 0) return text.Length;
        return FindEndOfBlock(text, braceIdx);
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        { count++; idx += pattern.Length; }
        return count;
    }

    private static int CountPatterns(string text, string[] patterns)
    {
        return patterns.Sum(p => CountOccurrences(text, p));
    }

    /// <summary>
    /// Centralized source file paths to avoid repetition.
    /// </summary>
    private static class TestPaths
    {
        private static readonly string ProjectRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "PolyPilot"));

        public static string CopilotServiceCs => Path.Combine(ProjectRoot, "Services", "CopilotService.cs");
        public static string EventsCs => Path.Combine(ProjectRoot, "Services", "CopilotService.Events.cs");
        public static string OrganizationCs => Path.Combine(ProjectRoot, "Services", "CopilotService.Organization.cs");
        public static string PersistenceCs => Path.Combine(ProjectRoot, "Services", "CopilotService.Persistence.cs");
        public static string SessionSidebarRazor => Path.Combine(ProjectRoot, "Components", "Layout", "SessionSidebar.razor");
        public static string DashboardRazor => Path.Combine(ProjectRoot, "Components", "Pages", "Dashboard.razor");
    }
}
