using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for HasActiveBackgroundTasks — the fix that prevents premature idle completion
/// when the SDK reports active background tasks (sub-agents, shells) in SessionIdleEvent.
/// See: session.idle with backgroundTasks means "foreground quiesced, background still running."
///
/// SDK v0.2.2: SessionIdleDataBackgroundTasks removed. Background tasks are now tracked
/// via BackgroundTaskSnapshot and SessionBackgroundTasksChangedEvent.
/// </summary>
public class BackgroundTasksIdleTests
{
    // Test DTOs for reflection-based GetBackgroundTaskSnapshot / GetBackgroundTaskFirstSeenTicks
    private class FakeBackgroundTasks
    {
        public FakeAgent[] Agents { get; set; } = Array.Empty<FakeAgent>();
        public FakeShell[] Shells { get; set; } = Array.Empty<FakeShell>();
    }
    private class FakeAgent
    {
        public string AgentId { get; set; } = "";
        public string AgentType { get; set; } = "";
        public string Description { get; set; } = "";
    }
    private class FakeShell
    {
        public string ShellId { get; set; } = "";
        public string Description { get; set; } = "";
    }

    private static CopilotService.BackgroundTaskSnapshot MakeSnapshot(int agents = 0, int shells = 0)
    {
        var parts = new List<string>();
        for (int i = 0; i < agents; i++) parts.Add($"agent:agent-{i}");
        for (int i = 0; i < shells; i++) parts.Add($"shell:shell-{i}");
        return new CopilotService.BackgroundTaskSnapshot(agents, shells, string.Join("|", parts), IsKnown: true);
    }

    private static FakeBackgroundTasks MakeFakeBt(int agents = 0, int shells = 0)
    {
        return new FakeBackgroundTasks
        {
            Agents = Enumerable.Range(0, agents)
                .Select(i => new FakeAgent { AgentId = $"agent-{i}", AgentType = "copilot", Description = "" })
                .ToArray(),
            Shells = Enumerable.Range(0, shells)
                .Select(i => new FakeShell { ShellId = $"shell-{i}", Description = "" })
                .ToArray()
        };
    }

    [Fact]
    public void HasActiveBackgroundTasks_NullBackgroundTasks_ReturnsFalse()
    {
        var snapshot = CopilotService.GetBackgroundTaskSnapshot(null);
        Assert.False(CopilotService.HasActiveBackgroundTasks(snapshot));
    }

    [Fact]
    public void HasActiveBackgroundTasks_EmptyBackgroundTasks_ReturnsFalse()
    {
        var snapshot = CopilotService.GetBackgroundTaskSnapshot(MakeFakeBt());
        Assert.False(CopilotService.HasActiveBackgroundTasks(snapshot));
    }

    [Fact]
    public void HasActiveBackgroundTasks_WithAgents_ReturnsTrue()
    {
        var snapshot = CopilotService.GetBackgroundTaskSnapshot(MakeFakeBt(agents: 1));
        Assert.True(CopilotService.HasActiveBackgroundTasks(snapshot));
    }

    [Fact]
    public void HasActiveBackgroundTasks_WithShells_ReturnsTrue()
    {
        var snapshot = CopilotService.GetBackgroundTaskSnapshot(MakeFakeBt(shells: 1));
        Assert.True(CopilotService.HasActiveBackgroundTasks(snapshot));
    }

    [Fact]
    public void HasActiveBackgroundTasks_WithBothAgentsAndShells_ReturnsTrue()
    {
        var snapshot = CopilotService.GetBackgroundTaskSnapshot(MakeFakeBt(agents: 1, shells: 1));
        Assert.True(CopilotService.HasActiveBackgroundTasks(snapshot));
    }

    [Fact]
    public void HasActiveBackgroundTasks_DefaultEmpty_ReturnsFalse()
    {
        // Empty snapshot — no agents, no shells
        var snapshot = new CopilotService.BackgroundTaskSnapshot(0, 0, string.Empty, IsKnown: true);
        Assert.False(CopilotService.HasActiveBackgroundTasks(snapshot));
    }

    [Fact]
    public void HasActiveBackgroundTasks_NullInput_ReturnsFalse()
    {
        var snapshot = CopilotService.GetBackgroundTaskSnapshot(null);
        Assert.False(CopilotService.HasActiveBackgroundTasks(snapshot));
    }

    // --- IDLE-DEFER-REARM tests (issue #403) ---
    // These test the expected re-arm conditions when session.idle arrives
    // with active background tasks but IsProcessing is already false.

    [Fact]
    public void IdleDeferRearm_ShouldRearm_WhenBackgroundTasksActiveAndNotProcessing()
    {
        // Scenario: watchdog or reconnect cleared IsProcessing,
        // then session.idle arrives with active background tasks.
        // Expected: should re-arm IsProcessing=true.
        var info = new AgentSessionInfo { Name = "test", Model = "test-model" };
        info.IsProcessing = false; // Cleared by watchdog

        var snapshot = MakeSnapshot(agents: 1);

        // Verify preconditions
        Assert.False(info.IsProcessing);
        Assert.True(CopilotService.HasActiveBackgroundTasks(snapshot));

        // The re-arm logic: if !IsProcessing && HasActiveBackgroundTasks && !WasUserAborted → re-arm
        bool shouldRearm = !info.IsProcessing && CopilotService.HasActiveBackgroundTasks(snapshot);
        Assert.True(shouldRearm, "Should re-arm when background tasks active and not processing");

        // Simulate re-arm
        info.IsProcessing = true;
        info.ProcessingPhase = 3; // Working (background tasks)
        info.ProcessingStartedAt ??= DateTime.UtcNow;

        Assert.True(info.IsProcessing);
        Assert.Equal(3, info.ProcessingPhase);
        Assert.NotNull(info.ProcessingStartedAt);
    }

    [Fact]
    public void IdleDeferRearm_ShouldNotRearm_WhenNoBackgroundTasks()
    {
        // Scenario: session.idle arrives with no background tasks and IsProcessing=false.
        // Expected: should NOT re-arm — this is a normal completion.
        var info = new AgentSessionInfo { Name = "test", Model = "test-model" };
        info.IsProcessing = false;

        var snapshot = CopilotService.GetBackgroundTaskSnapshot(null);

        bool shouldRearm = !info.IsProcessing && CopilotService.HasActiveBackgroundTasks(snapshot);
        Assert.False(shouldRearm, "Should NOT re-arm when no background tasks");
    }

    [Fact]
    public void IdleDeferRearm_ShouldNotRearm_WhenAlreadyProcessing()
    {
        // Scenario: session.idle with background tasks but IsProcessing is already true.
        // Expected: should NOT re-arm (already armed) — just defer.
        var info = new AgentSessionInfo { Name = "test", Model = "test-model" };
        info.IsProcessing = true; // Already processing

        var snapshot = MakeSnapshot(agents: 1);

        bool shouldRearm = !info.IsProcessing && CopilotService.HasActiveBackgroundTasks(snapshot);
        Assert.False(shouldRearm, "Should NOT re-arm when already processing");
    }

    [Fact]
    public void IdleDeferRearm_ShouldNotRearm_WhenUserAborted()
    {
        // Scenario: user clicked Stop, then session.idle with background tasks arrives.
        // Expected: should NOT re-arm — user explicitly stopped.
        var info = new AgentSessionInfo { Name = "test", Model = "test-model" };
        info.IsProcessing = false;
        bool wasUserAborted = true;

        var snapshot = MakeSnapshot(agents: 1);

        // The full re-arm condition includes WasUserAborted check
        bool shouldRearm = !info.IsProcessing && CopilotService.HasActiveBackgroundTasks(snapshot) && !wasUserAborted;
        Assert.False(shouldRearm, "Should NOT re-arm when user aborted");
    }

    [Fact]
    public void ProactiveIdleDefer_SubagentDeferStartedAtTicks_StampedOnBackgroundTasksChanged()
    {
        // SessionBackgroundTasksChangedEvent should proactively refresh deferred-idle tracking
        // so stale shell IDs keep their original age across turns instead of resetting forever.
        var source = File.ReadAllText(Path.Combine(GetRepoRoot(),
            "PolyPilot", "Services", "CopilotService.Events.cs"));

        var handlerStart = source.IndexOf("case SessionBackgroundTasksChangedEvent", StringComparison.Ordinal);
        Assert.True(handlerStart >= 0, "SessionBackgroundTasksChangedEvent handler not found");

        // Find the next case or closing brace to bound the handler
        var handlerEnd = source.IndexOf("case System", handlerStart + 1, StringComparison.Ordinal);
        if (handlerEnd < 0) handlerEnd = source.Length;
        var handler = source.Substring(handlerStart, handlerEnd - handlerStart);

        Assert.Contains("RefreshDeferredBackgroundTaskTracking", handler);
        Assert.Contains("SubagentDeferStartedAtTicks", handler);
    }

    [Fact]
    public void ProactiveIdleDefer_Handler_DoesNotSetIsProcessingFalse()
    {
        // The SessionBackgroundTasksChangedEvent handler must NOT set IsProcessing=false.
        // It should only update IDLE-DEFER tracking fields. See issue #518.
        var source = File.ReadAllText(Path.Combine(GetRepoRoot(),
            "PolyPilot", "Services", "CopilotService.Events.cs"));

        var handlerStart = source.IndexOf("case SessionBackgroundTasksChangedEvent", StringComparison.Ordinal);
        Assert.True(handlerStart >= 0, "SessionBackgroundTasksChangedEvent handler not found");

        var handlerEnd = source.IndexOf("case System", handlerStart + 1, StringComparison.Ordinal);
        if (handlerEnd < 0) handlerEnd = source.Length;
        var handler = source.Substring(handlerStart, handlerEnd - handlerStart);

        Assert.DoesNotContain("IsProcessing = false", handler);
        Assert.DoesNotContain("IsProcessing=false", handler);
    }

    [Fact]
    public void ProactiveIdleDefer_Handler_ResolvesDeferredIdleViaHelperWhenTasksClear()
    {
        var source = File.ReadAllText(Path.Combine(GetRepoRoot(),
            "PolyPilot", "Services", "CopilotService.Events.cs"));

        var handlerStart = source.IndexOf("case SessionBackgroundTasksChangedEvent", StringComparison.Ordinal);
        Assert.True(handlerStart >= 0, "SessionBackgroundTasksChangedEvent handler not found");

        var handlerEnd = source.IndexOf("case System", handlerStart + 1, StringComparison.Ordinal);
        if (handlerEnd < 0) handlerEnd = source.Length;
        var handler = source.Substring(handlerStart, handlerEnd - handlerStart);

        Assert.Contains("TryResolveDeferredIdleAfterBackgroundTaskChange", handler);
    }

    [Fact]
    public void GetBackgroundTaskFirstSeenTicks_PreservesExistingTimestampWhenFingerprintMatches()
    {
        var bt = MakeFakeBt(shells: 1);
        var snapshot = CopilotService.GetBackgroundTaskSnapshot(bt);
        var existingTicks = DateTime.UtcNow.AddMinutes(-5).Ticks;

        var preserved = CopilotService.GetBackgroundTaskFirstSeenTicks(
            bt,
            snapshot.Fingerprint,
            existingTicks,
            DateTime.UtcNow);

        Assert.Equal(existingTicks, preserved);
    }

    [Fact]
    public void GetBackgroundTaskFirstSeenTicks_RefreshesWhenFingerprintChanges()
    {
        var bt = new FakeBackgroundTasks
        {
            Agents = Array.Empty<FakeAgent>(),
            Shells = new[] { new FakeShell { ShellId = "shell-2", Description = "" } }
        };
        var before = DateTime.UtcNow;

        var refreshed = CopilotService.GetBackgroundTaskFirstSeenTicks(
            bt,
            "shell:shell-old",
            DateTime.UtcNow.AddMinutes(-30).Ticks,
            before);

        Assert.Equal(before.Ticks, refreshed);
    }

    [Fact]
    public void SessionIdle_StalePayload_NotDeferredWhenBgTasksAlreadyConfirmedEmpty()
    {
        // Regression: session.idle arrives with shells=2 but backgroundTasksChanged already
        // confirmed shells=0 (race — CLI snapshotted before completions landed). PolyPilot
        // must NOT defer in this case.
        //
        // The fix uses a sentinel: DeferredBackgroundTaskFingerprint == string.Empty means
        // "backgroundTasksChanged explicitly confirmed zero tasks this turn." null means
        // "no backgroundTasksChanged event has fired yet" (initial/reset state). Only the
        // empty-string sentinel triggers stale detection, preventing false positives when
        // session.idle arrives with genuine new tasks before backgroundTasksChanged fires.
        var source = File.ReadAllText(Path.Combine(GetRepoRoot(),
            "PolyPilot", "Services", "CopilotService.Events.cs"));

        var idleHandlerStart = source.IndexOf("case SessionIdleEvent idle:");
        Assert.True(idleHandlerStart >= 0, "SessionIdleEvent handler not found");
        var idleHandlerEnd = source.IndexOf("case SessionBackgroundTasks", idleHandlerStart + 1);
        if (idleHandlerEnd < 0) idleHandlerEnd = source.Length;
        var handler = source.Substring(idleHandlerStart, idleHandlerEnd - idleHandlerStart);

        // The handler must capture state before RefreshDeferredBackgroundTaskTracking
        Assert.Contains("preIdleFingerprint", handler);
        Assert.Contains("preIdleTicks", handler);
        // Staleness check uses string.Empty sentinel (not null) to distinguish confirmed-empty
        // from never-seen — guards against false positives on first idle with genuine tasks
        Assert.Contains("idlePayloadIsStale", handler);
        Assert.Contains("preIdleFingerprint == string.Empty", handler);
        Assert.Contains("preIdleTicks == 0", handler);
        // hasActiveTasks uses tracked fingerprint state (not null snapshot)
        Assert.Contains("hasTrackedTasks", handler);
        // hasActiveTasks must be guarded by !idlePayloadIsStale
        Assert.Contains("!idlePayloadIsStale", handler);
    }

    [Fact]
    public void RefreshDeferredBackgroundTaskTracking_SetsEmptyStringSentinel_WhenTasksConfirmedGone()
    {
        // When backgroundTasksChanged fires with no tasks, RefreshDeferredBackgroundTaskTracking
        // must set DeferredBackgroundTaskFingerprint = string.Empty (not null). This sentinel
        // is what distinguishes "confirmed empty" from "never seen" (null).
        var source = File.ReadAllText(Path.Combine(GetRepoRoot(),
            "PolyPilot", "Services", "CopilotService.Events.cs"));

        var methodStart = source.IndexOf("private static (BackgroundTaskSnapshot Snapshot, long FirstSeenTicks) RefreshDeferredBackgroundTaskTracking(");
        Assert.True(methodStart >= 0, "RefreshDeferredBackgroundTaskTracking not found");
        var methodEnd = source.IndexOf("\n    private ", methodStart + 1);
        if (methodEnd < 0) methodEnd = source.Length;
        var method = source.Substring(methodStart, methodEnd - methodStart);

        // Must set string.Empty sentinel (not null) when clearing after confirmed-empty event
        Assert.Contains("string.Empty", method);
        // Must NOT set null when clearing in this path (null is reserved for initial/reset state)
        Assert.DoesNotContain("DeferredBackgroundTaskFingerprint = null", method);
    }

    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root");
    }
}
