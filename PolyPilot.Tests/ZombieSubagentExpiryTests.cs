using GitHub.Copilot.SDK;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the zombie subagent expiry mechanism in HasActiveBackgroundTasks.
///
/// Problem: The Copilot CLI has no per-subagent timeout. A crashed or orphaned subagent
/// never fires SubagentCompleted/SubagentFailed, so IDLE-DEFER blocks session completion
/// indefinitely. PolyPilot tracks when IDLE-DEFER first started and expires the background
/// agent block after SubagentZombieTimeoutMinutes, allowing the session to complete normally.
/// See: issue #509 (expose CancelBackgroundTaskAsync via SDK).
/// </summary>
public class ZombieSubagentExpiryTests
{
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

    // --- No defer start time (null) — backward-compatible behavior ---

    [Fact]
    public void NoDeferStartTime_ActiveAgent_ReturnsTrue()
    {
        // When no deferStartedAt is provided, behaviour is unchanged:
        // any agent in the idle payload means "still running".
        var idle = MakeIdleWithAgents();
        Assert.True(CopilotService.HasActiveBackgroundTasks(idle, idleDeferStartedAt: null));
    }

    [Fact]
    public void NoDeferStartTime_NoTasks_ReturnsFalse()
    {
        var idle = new SessionIdleEvent { Data = new SessionIdleData() };
        Assert.False(CopilotService.HasActiveBackgroundTasks(idle, idleDeferStartedAt: null));
    }

    // --- Fresh IDLE-DEFER (started recently — well within timeout) ---

    [Fact]
    public void FreshDeferStart_ActiveAgent_ReturnsTrue()
    {
        var idle = MakeIdleWithAgents();
        var deferStartedAt = DateTime.UtcNow.AddMinutes(-1); // deferred 1 minute ago
        Assert.True(CopilotService.HasActiveBackgroundTasks(idle, deferStartedAt));
    }

    [Fact]
    public void DeferStartJustBelowThreshold_ReturnsTrue()
    {
        var idle = MakeIdleWithAgents();
        var deferStartedAt = DateTime.UtcNow.AddMinutes(-(CopilotService.SubagentZombieTimeoutMinutes - 1));
        Assert.True(CopilotService.HasActiveBackgroundTasks(idle, deferStartedAt));
    }

    // --- Zombie threshold reached ---

    [Fact]
    public void ZombieThresholdExceeded_SingleAgent_ReturnsFalse()
    {
        var idle = MakeIdleWithAgents();
        var deferStartedAt = DateTime.UtcNow.AddMinutes(-(CopilotService.SubagentZombieTimeoutMinutes + 1));
        Assert.False(CopilotService.HasActiveBackgroundTasks(idle, deferStartedAt));
    }

    [Fact]
    public void ZombieThresholdExceeded_MultipleAgents_ReturnsFalse()
    {
        // All 8 agents reported — none complete — reproduces the real incident
        var idle = MakeIdleWithAgents(agentCount: 8);
        var deferStartedAt = DateTime.UtcNow.AddMinutes(-42); // stuck for 42 minutes
        Assert.False(CopilotService.HasActiveBackgroundTasks(idle, deferStartedAt));
    }

    [Fact]
    public void ZombieThresholdExactlyMet_ReturnsFalse()
    {
        // At exactly the threshold, the session is considered expired.
        var idle = MakeIdleWithAgents();
        var deferStartedAt = DateTime.UtcNow.AddMinutes(-CopilotService.SubagentZombieTimeoutMinutes);
        Assert.False(CopilotService.HasActiveBackgroundTasks(idle, deferStartedAt));
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
        var deferStartedAt = DateTime.UtcNow.AddMinutes(-30);
        Assert.True(CopilotService.HasActiveBackgroundTasks(idle, deferStartedAt));
    }

    [Fact]
    public void ZombieThresholdExceeded_ShellsOnly_ReturnsTrue()
    {
        // Shells alone always block completion — they are never zombie-expired.
        var idle = MakeIdleWithShells();
        var deferStartedAt = DateTime.UtcNow.AddMinutes(-60);
        Assert.True(CopilotService.HasActiveBackgroundTasks(idle, deferStartedAt));
    }

    [Fact]
    public void FreshDeferStart_ShellsOnly_ReturnsTrue()
    {
        var idle = MakeIdleWithShells();
        var deferStartedAt = DateTime.UtcNow.AddMinutes(-1);
        Assert.True(CopilotService.HasActiveBackgroundTasks(idle, deferStartedAt));
    }

    // --- Existing BackgroundTasksIdleTests cases remain unaffected with null deferStartedAt ---

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
