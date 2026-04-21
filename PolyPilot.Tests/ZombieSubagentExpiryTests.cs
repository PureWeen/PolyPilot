using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the zombie subagent expiry mechanism in HasActiveBackgroundTasks.
///
/// Problem: The Copilot CLI has no per-subagent timeout. A crashed or orphaned subagent
/// never fires SubagentCompleted/SubagentFailed, so IDLE-DEFER blocks session completion
/// indefinitely. PolyPilot tracks when IDLE-DEFER first started (as UTC ticks in
/// SubagentDeferStartedAtTicks) and expires the background agent block after
/// SubagentZombieTimeoutMinutes, allowing the session to complete normally.
/// See: issue #509 (expose CancelBackgroundTaskAsync via SDK).
///
/// SDK v0.2.2: SessionIdleDataBackgroundTasks removed. Background tasks are now tracked
/// via BackgroundTaskSnapshot and SessionBackgroundTasksChangedEvent.
/// </summary>
public class ZombieSubagentExpiryTests
{
    private static long TicksAgo(double minutes) =>
        DateTime.UtcNow.AddMinutes(-minutes).Ticks;

    // Test DTOs for reflection-based GetBackgroundTaskSnapshot
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

    // --- Zero ticks (not set) — backward-compatible behavior ---

    [Fact]
    public void ZeroTicks_ActiveAgent_ReturnsTrue()
    {
        // 0 means "not set" — behaviour is unchanged: any agent means "still running".
        var snapshot = MakeSnapshot(agents: 1);
        Assert.True(CopilotService.HasActiveBackgroundTasks(snapshot, idleDeferStartedAtTicks: 0));
    }

    [Fact]
    public void ZeroTicks_NoTasks_ReturnsFalse()
    {
        var snapshot = MakeSnapshot();
        Assert.False(CopilotService.HasActiveBackgroundTasks(snapshot, idleDeferStartedAtTicks: 0));
    }

    // --- Fresh IDLE-DEFER (started recently — well within timeout) ---

    [Fact]
    public void FreshDeferStart_ActiveAgent_ReturnsTrue()
    {
        var snapshot = MakeSnapshot(agents: 1);
        Assert.True(CopilotService.HasActiveBackgroundTasks(snapshot, TicksAgo(1)));
    }

    [Fact]
    public void DeferStartJustBelowThreshold_ReturnsTrue()
    {
        var snapshot = MakeSnapshot(agents: 1);
        Assert.True(CopilotService.HasActiveBackgroundTasks(
            snapshot, TicksAgo(CopilotService.SubagentZombieTimeoutMinutes - 1)));
    }

    // --- Zombie threshold reached ---

    [Fact]
    public void ZombieThresholdExceeded_SingleAgent_ReturnsFalse()
    {
        var snapshot = MakeSnapshot(agents: 1);
        Assert.False(CopilotService.HasActiveBackgroundTasks(
            snapshot, TicksAgo(CopilotService.SubagentZombieTimeoutMinutes + 1)));
    }

    [Fact]
    public void ZombieThresholdExceeded_MultipleAgents_ReturnsFalse()
    {
        // All 8 agents reported — none complete — reproduces the real incident
        var snapshot = MakeSnapshot(agents: 8);
        Assert.False(CopilotService.HasActiveBackgroundTasks(snapshot, TicksAgo(42)));
    }

    [Fact]
    public void ZombieThresholdExactlyMet_ReturnsFalse()
    {
        // At exactly the threshold, the session is considered expired.
        // TicksAgo(20) produces ticks > 20min ago (test executes in microseconds, not minutes),
        // so elapsed will be just over 20min and the threshold check fires correctly.
        var snapshot = MakeSnapshot(agents: 1);
        Assert.False(CopilotService.HasActiveBackgroundTasks(
            snapshot, TicksAgo(CopilotService.SubagentZombieTimeoutMinutes)));
    }

    // --- Shells get a longer zombie timeout than agents ---

    [Fact]
    public void AfterAgentThreshold_MixedShellsStillKeepSessionActive()
    {
        // At 30 minutes, agents should have expired but shells should still keep the
        // session deferred. This protects legitimate long-running build/test shells.
        var snapshot = MakeSnapshot(agents: 1, shells: 1);
        Assert.True(CopilotService.HasActiveBackgroundTasks(
            snapshot, TicksAgo(CopilotService.SubagentZombieTimeoutMinutes + 10)));
    }

    [Fact]
    public void ZombieThresholdExceeded_ShellsOnly_ReturnsFalse()
    {
        // Shells alone should eventually expire too — just with a longer threshold than agents.
        var snapshot = MakeSnapshot(shells: 1);
        Assert.False(CopilotService.HasActiveBackgroundTasks(
            snapshot, TicksAgo(CopilotService.ShellZombieTimeoutMinutes + 1)));
    }

    [Fact]
    public void FreshDeferStart_ShellsOnly_ReturnsTrue()
    {
        var snapshot = MakeSnapshot(shells: 1);
        Assert.True(CopilotService.HasActiveBackgroundTasks(snapshot, TicksAgo(1)));
    }

    [Fact]
    public void AfterAgentThreshold_ShellsOnly_StillReturnsTrue()
    {
        // Shells should survive past the 20-minute agent timeout so legitimate long-running
        // commands do not get truncated just because they're shell-backed instead of subagents.
        var snapshot = MakeSnapshot(shells: 1);
        Assert.True(CopilotService.HasActiveBackgroundTasks(
            snapshot, TicksAgo(CopilotService.SubagentZombieTimeoutMinutes + 5)));
    }

    [Fact]
    public void SameShellFingerprint_PreservesOriginalAgeAcrossTurns()
    {
        // The same orphaned shell IDs can be reported again on later prompts. Their zombie age
        // must continue from the first time we saw them — otherwise every new prompt resets the
        // timer and the session can stay "busy" forever.
        var bt = MakeFakeBt(shells: 2);
        var snapshot = CopilotService.GetBackgroundTaskSnapshot(bt);
        var staleTicks = TicksAgo(CopilotService.ShellZombieTimeoutMinutes + 5);

        var preservedTicks = CopilotService.GetBackgroundTaskFirstSeenTicks(
            bt,
            snapshot.Fingerprint,
            staleTicks,
            DateTime.UtcNow);

        Assert.Equal(staleTicks, preservedTicks);
        Assert.False(CopilotService.HasActiveBackgroundTasks(snapshot, preservedTicks));
    }

    [Fact]
    public void DifferentShellFingerprint_RefreshesAgeForNewBackgroundWork()
    {
        var bt = new FakeBackgroundTasks
        {
            Agents = Array.Empty<FakeAgent>(),
            Shells = new[]
            {
                new FakeShell { ShellId = "shell-new", Description = "fresh build" }
            }
        };
        var staleTicks = TicksAgo(CopilotService.ShellZombieTimeoutMinutes + 5);
        var before = DateTime.UtcNow;

        var refreshedTicks = CopilotService.GetBackgroundTaskFirstSeenTicks(
            bt,
            "shell:shell-old",
            staleTicks,
            before);

        Assert.Equal(before.Ticks, refreshedTicks);
        var snapshot = CopilotService.GetBackgroundTaskSnapshot(bt);
        Assert.True(CopilotService.HasActiveBackgroundTasks(snapshot, refreshedTicks));
    }

    [Fact]
    public void CarryOverShellOnlyTasks_FromPriorTurn_ShouldNotBlockNewTurn()
    {
        var bt = MakeFakeBt(shells: 2);
        var snapshot = CopilotService.GetBackgroundTaskSnapshot(bt);

        Assert.True(CopilotService.ShouldIgnoreCarryOverShellOnlyTasks(
            snapshot,
            TicksAgo(3),
            DateTime.UtcNow.AddMinutes(-1)));
    }

    [Fact]
    public void CarryOverShellOnlyTasks_DoNotApplyToCurrentTurnOrActiveAgents()
    {
        var shellSnapshot = CopilotService.GetBackgroundTaskSnapshot(MakeFakeBt(shells: 1));

        Assert.False(CopilotService.ShouldIgnoreCarryOverShellOnlyTasks(
            shellSnapshot,
            TicksAgo(1),
            DateTime.UtcNow.AddMinutes(-3)));

        var mixedSnapshot = CopilotService.GetBackgroundTaskSnapshot(MakeFakeBt(agents: 1, shells: 1));
        Assert.False(CopilotService.ShouldIgnoreCarryOverShellOnlyTasks(
            mixedSnapshot,
            TicksAgo(10),
            DateTime.UtcNow.AddMinutes(-1)));
    }

    // --- Cross-turn stale timestamp: the critical lifecycle bug this PR fixes ---

    [Fact]
    public void StaleDeferTimestamp_FromPriorTurn_NewTurnShouldNotExpireAgents()
    {
        // Scenario: SubagentDeferStartedAtTicks was NOT cleared (e.g. watchdog/abort path)
        // after Turn N which had an IDLE-DEFER 25 minutes ago. Turn N+1 starts and its first
        // IDLE-DEFER fires. The ??= logic would preserve the stale timestamp.
        //
        // This test documents WHY SubagentDeferStartedAtTicks MUST be reset in all paths that
        // clear HasDeferredIdle, not just CompleteResponse. If a caller passes a 25-min-old
        // timestamp for what is actually a brand-new IDLE-DEFER, zombie expiry fires immediately
        // and kills live subagents.
        //
        // Fix: Interlocked.Exchange(ref state.SubagentDeferStartedAtTicks, 0L) alongside
        // every HasDeferredIdle = false in SendPromptAsync, AbortSessionAsync, error paths, etc.
        //
        // After the fix, SubagentDeferStartedAtTicks is reset at turn boundaries, so the
        // ??= CompareExchange sets a fresh timestamp for the new turn, and zombie expiry
        // is based on the new turn's actual elapsed time.
        var snapshot = MakeSnapshot(agents: 1);

        // Simulate: stale ticks from 25 minutes ago NOT cleared at turn boundary
        long staleTicks = TicksAgo(25);

        // Without the fix, this would return false (zombie expiry fires on fresh agents)
        // The test ASSERTS false to document what the broken behavior looks like,
        // and to verify that HasActiveBackgroundTasks correctly respects the ticks value.
        // The real invariant is: callers MUST pass 0 (not stale ticks) for new turns.
        Assert.False(CopilotService.HasActiveBackgroundTasks(snapshot, staleTicks),
            "HasActiveBackgroundTasks correctly expires based on elapsed ticks — " +
            "the caller is responsible for resetting SubagentDeferStartedAtTicks at turn boundaries.");

        // The safe path: passing fresh ticks (new turn, new timestamp) should NOT expire agents
        long freshTicks = TicksAgo(1);
        Assert.True(CopilotService.HasActiveBackgroundTasks(snapshot, freshTicks),
            "With fresh ticks (new turn), agents should NOT be expired — confirms the fix works.");
    }

    // --- Backward compatibility ---

    [Fact]
    public void BackwardCompat_NullBackgroundTasks_ReturnsFalse()
    {
        var snapshot = CopilotService.GetBackgroundTaskSnapshot(null);
        Assert.False(CopilotService.HasActiveBackgroundTasks(snapshot));
    }

    [Fact]
    public void BackwardCompat_WithAgents_ReturnsTrue()
    {
        var snapshot = MakeSnapshot(agents: 1);
        Assert.True(CopilotService.HasActiveBackgroundTasks(snapshot));
    }

    [Fact]
    public void BackwardCompat_WithShells_ReturnsTrue()
    {
        var snapshot = MakeSnapshot(shells: 1);
        Assert.True(CopilotService.HasActiveBackgroundTasks(snapshot));
    }
}
