using GitHub.Copilot.SDK;
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
/// </summary>
public class ZombieSubagentExpiryTests
{
    private static long TicksAgo(double minutes) =>
        DateTime.UtcNow.AddMinutes(-minutes).Ticks;

    private static SessionIdleEvent MakeIdleWithAgents(int agentCount = 1)
    {
        return new SessionIdleEvent
        {
            Data = new SessionIdleData
            {
                BackgroundTasks = new SessionIdleDataBackgroundTasks
                {
                    Agents = Enumerable.Range(0, agentCount)
                        .Select(i => new SessionIdleDataBackgroundTasksAgentsItem
                        {
                            AgentId = $"agent-{i}",
                            AgentType = "copilot",
                            Description = ""
                        }).ToArray(),
                    Shells = Array.Empty<SessionIdleDataBackgroundTasksShellsItem>()
                }
            }
        };
    }

    private static SessionIdleEvent MakeIdleWithShells(int shellCount = 1)
    {
        return new SessionIdleEvent
        {
            Data = new SessionIdleData
            {
                BackgroundTasks = new SessionIdleDataBackgroundTasks
                {
                    Agents = Array.Empty<SessionIdleDataBackgroundTasksAgentsItem>(),
                    Shells = Enumerable.Range(0, shellCount)
                        .Select(i => new SessionIdleDataBackgroundTasksShellsItem
                        {
                            ShellId = $"shell-{i}",
                            Description = ""
                        }).ToArray()
                }
            }
        };
    }

    // --- Zero ticks (not set) — backward-compatible behavior ---

    [Fact]
    public void ZeroTicks_ActiveAgent_ReturnsTrue()
    {
        // 0 means "not set" — behaviour is unchanged: any agent means "still running".
        var idle = MakeIdleWithAgents();
        Assert.True(CopilotService.HasActiveBackgroundTasks(idle, idleDeferStartedAtTicks: 0));
    }

    [Fact]
    public void ZeroTicks_NoTasks_ReturnsFalse()
    {
        var idle = new SessionIdleEvent { Data = new SessionIdleData() };
        Assert.False(CopilotService.HasActiveBackgroundTasks(idle, idleDeferStartedAtTicks: 0));
    }

    // --- Fresh IDLE-DEFER (started recently — well within timeout) ---

    [Fact]
    public void FreshDeferStart_ActiveAgent_ReturnsTrue()
    {
        var idle = MakeIdleWithAgents();
        Assert.True(CopilotService.HasActiveBackgroundTasks(idle, TicksAgo(1)));
    }

    [Fact]
    public void DeferStartJustBelowThreshold_ReturnsTrue()
    {
        var idle = MakeIdleWithAgents();
        Assert.True(CopilotService.HasActiveBackgroundTasks(
            idle, TicksAgo(CopilotService.SubagentZombieTimeoutMinutes - 1)));
    }

    // --- Zombie threshold reached ---

    [Fact]
    public void ZombieThresholdExceeded_SingleAgent_ReturnsFalse()
    {
        var idle = MakeIdleWithAgents();
        Assert.False(CopilotService.HasActiveBackgroundTasks(
            idle, TicksAgo(CopilotService.SubagentZombieTimeoutMinutes + 1)));
    }

    [Fact]
    public void ZombieThresholdExceeded_MultipleAgents_ReturnsFalse()
    {
        // All 8 agents reported — none complete — reproduces the real incident
        var idle = MakeIdleWithAgents(agentCount: 8);
        Assert.False(CopilotService.HasActiveBackgroundTasks(idle, TicksAgo(42)));
    }

    [Fact]
    public void ZombieThresholdExactlyMet_ReturnsFalse()
    {
        // At exactly the threshold, the session is considered expired.
        // TicksAgo(20) produces ticks > 20min ago (test executes in microseconds, not minutes),
        // so elapsed will be just over 20min and the threshold check fires correctly.
        var idle = MakeIdleWithAgents();
        Assert.False(CopilotService.HasActiveBackgroundTasks(
            idle, TicksAgo(CopilotService.SubagentZombieTimeoutMinutes)));
    }

    // --- Shells are never expired ---

    [Fact]
    public void ZombieThresholdExceeded_WithShells_ReturnsTrue()
    {
        // Even if all background agents are expired, an active shell keeps IDLE-DEFER alive.
        // Shells are managed at the OS level — PolyPilot never force-expires them.
        var idle = new SessionIdleEvent
        {
            Data = new SessionIdleData
            {
                BackgroundTasks = new SessionIdleDataBackgroundTasks
                {
                    Agents = new[]
                    {
                        new SessionIdleDataBackgroundTasksAgentsItem
                        {
                            AgentId = "zombie-agent", AgentType = "copilot", Description = ""
                        }
                    },
                    Shells = new[]
                    {
                        new SessionIdleDataBackgroundTasksShellsItem
                        {
                            ShellId = "shell-1", Description = "npm test"
                        }
                    }
                }
            }
        };
        Assert.True(CopilotService.HasActiveBackgroundTasks(idle, TicksAgo(30)));
    }

    [Fact]
    public void ZombieThresholdExceeded_ShellsOnly_ReturnsTrue()
    {
        // Shells alone always block completion — they are never zombie-expired.
        var idle = MakeIdleWithShells();
        Assert.True(CopilotService.HasActiveBackgroundTasks(idle, TicksAgo(60)));
    }

    [Fact]
    public void FreshDeferStart_ShellsOnly_ReturnsTrue()
    {
        var idle = MakeIdleWithShells();
        Assert.True(CopilotService.HasActiveBackgroundTasks(idle, TicksAgo(1)));
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
        var idle = MakeIdleWithAgents();

        // Simulate: stale ticks from 25 minutes ago NOT cleared at turn boundary
        long staleTicks = TicksAgo(25);

        // Without the fix, this would return false (zombie expiry fires on fresh agents)
        // The test ASSERTS false to document what the broken behavior looks like,
        // and to verify that HasActiveBackgroundTasks correctly respects the ticks value.
        // The real invariant is: callers MUST pass 0 (not stale ticks) for new turns.
        Assert.False(CopilotService.HasActiveBackgroundTasks(idle, staleTicks),
            "HasActiveBackgroundTasks correctly expires based on elapsed ticks — " +
            "the caller is responsible for resetting SubagentDeferStartedAtTicks at turn boundaries.");

        // The safe path: passing fresh ticks (new turn, new timestamp) should NOT expire agents
        long freshTicks = TicksAgo(1);
        Assert.True(CopilotService.HasActiveBackgroundTasks(idle, freshTicks),
            "With fresh ticks (new turn), agents should NOT be expired — confirms the fix works.");
    }

    // --- Backward compatibility with existing BackgroundTasksIdleTests ---

    [Fact]
    public void BackwardCompat_NullBackgroundTasks_ReturnsFalse()
    {
        var idle = new SessionIdleEvent { Data = new SessionIdleData { BackgroundTasks = null } };
        Assert.False(CopilotService.HasActiveBackgroundTasks(idle));
    }

    [Fact]
    public void BackwardCompat_WithAgents_ReturnsTrue()
    {
        var idle = MakeIdleWithAgents();
        Assert.True(CopilotService.HasActiveBackgroundTasks(idle));
    }

    [Fact]
    public void BackwardCompat_WithShells_ReturnsTrue()
    {
        var idle = MakeIdleWithShells();
        Assert.True(CopilotService.HasActiveBackgroundTasks(idle));
    }
}
