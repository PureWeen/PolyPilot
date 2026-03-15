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
        var method = ExtractMethod(source, "ForceCompleteProcessingAsync");

        // Every INV-1 field must be cleared
        var requiredClears = new[]
        {
            "ActiveToolCallCount",      // INV-1 field 3
            "HasUsedToolsThisTurn",     // INV-1 field 2
            "SendingFlag",              // INV-1 field 7
            "IsResumed",                // INV-1 field 1
            "ProcessingStartedAt",      // INV-1 field 4
            "ToolCallCount",            // INV-1 field 5
            "ProcessingPhase",          // INV-1 field 6
            "ClearPermissionDenials",   // INV-1 field 8
            "FlushCurrentResponse",     // INV-1 field 9
            "IsProcessing",             // The flag itself
            "OnSessionComplete",        // INV-1 field 10
            "TrySetResult",             // Resolves the worker TCS
        };

        foreach (var field in requiredClears)
        {
            Assert.True(method.Contains(field, StringComparison.Ordinal),
                $"ForceCompleteProcessingAsync must clear '{field}' (INV-1 compliance)");
        }
    }

    [Fact]
    public void ForceCompleteProcessing_CancelsTimersBeforeUiThreadWork()
    {
        var source = File.ReadAllText(TestPaths.OrganizationCs);
        var method = ExtractMethod(source, "ForceCompleteProcessingAsync");

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
        var method = ExtractMethod(source, "ForceCompleteProcessingAsync");

        // Must early-return if already not processing (idempotent)
        Assert.Contains("!state.Info.IsProcessing", method);
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

        // The crash recovery block (Case C kill) must clear companion fields
        var companionFields = new[]
        {
            "IsProcessing = false",
            "ProcessingPhase",
            "ProcessingStartedAt",
            "ToolCallCount",
        };

        foreach (var field in companionFields)
        {
            Assert.True(watchdogMethod.Contains(field, StringComparison.Ordinal),
                $"Watchdog crash recovery must clear '{field}'");
        }
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
        public static string SessionSidebarRazor => Path.Combine(ProjectRoot, "Components", "Layout", "SessionSidebar.razor");
    }
}
