using GitHub.Copilot.SDK;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for HasActiveBackgroundTasks — the fix that prevents premature idle completion
/// when the SDK reports active background tasks (sub-agents, shells) in SessionIdleEvent.
/// See: session.idle with backgroundTasks means "foreground quiesced, background still running."
/// </summary>
public class BackgroundTasksIdleTests
{
    private static SessionIdleEvent MakeIdle(SessionIdleDataBackgroundTasks? bt = null)
    {
        return new SessionIdleEvent
        {
            Data = new SessionIdleData { BackgroundTasks = bt }
        };
    }

    [Fact]
    public void HasActiveBackgroundTasks_NullBackgroundTasks_ReturnsFalse()
    {
        var idle = MakeIdle(bt: null);
        Assert.False(CopilotService.HasActiveBackgroundTasks(idle));
    }

    [Fact]
    public void HasActiveBackgroundTasks_EmptyBackgroundTasks_ReturnsFalse()
    {
        var idle = MakeIdle(new SessionIdleDataBackgroundTasks
        {
            Agents = Array.Empty<SessionIdleDataBackgroundTasksAgentsItem>(),
            Shells = Array.Empty<SessionIdleDataBackgroundTasksShellsItem>()
        });
        Assert.False(CopilotService.HasActiveBackgroundTasks(idle));
    }

    [Fact]
    public void HasActiveBackgroundTasks_WithAgents_ReturnsTrue()
    {
        var idle = MakeIdle(new SessionIdleDataBackgroundTasks
        {
            Agents = new[]
            {
                new SessionIdleDataBackgroundTasksAgentsItem
                {
                    AgentId = "agent-42",
                    AgentType = "copilot",
                    Description = "PR reviewer"
                }
            },
            Shells = Array.Empty<SessionIdleDataBackgroundTasksShellsItem>()
        });
        Assert.True(CopilotService.HasActiveBackgroundTasks(idle));
    }

    [Fact]
    public void HasActiveBackgroundTasks_WithShells_ReturnsTrue()
    {
        var idle = MakeIdle(new SessionIdleDataBackgroundTasks
        {
            Agents = Array.Empty<SessionIdleDataBackgroundTasksAgentsItem>(),
            Shells = new[]
            {
                new SessionIdleDataBackgroundTasksShellsItem
                {
                    ShellId = "shell-1",
                    Description = "Running npm test"
                }
            }
        });
        Assert.True(CopilotService.HasActiveBackgroundTasks(idle));
    }

    [Fact]
    public void HasActiveBackgroundTasks_WithBothAgentsAndShells_ReturnsTrue()
    {
        var idle = MakeIdle(new SessionIdleDataBackgroundTasks
        {
            Agents = new[]
            {
                new SessionIdleDataBackgroundTasksAgentsItem
                {
                    AgentId = "a1", AgentType = "copilot", Description = ""
                }
            },
            Shells = new[]
            {
                new SessionIdleDataBackgroundTasksShellsItem
                {
                    ShellId = "s1", Description = ""
                }
            }
        });
        Assert.True(CopilotService.HasActiveBackgroundTasks(idle));
    }

    [Fact]
    public void HasActiveBackgroundTasks_DefaultIdle_ReturnsFalse()
    {
        // Default SessionIdleEvent — Data is auto-initialized but BackgroundTasks is null
        var idle = new SessionIdleEvent { Data = new SessionIdleData() };
        Assert.False(CopilotService.HasActiveBackgroundTasks(idle));
    }

    [Fact]
    public void HasActiveBackgroundTasks_DataNull_ReturnsFalse()
    {
        var idle = new SessionIdleEvent { Data = null! };
        Assert.False(CopilotService.HasActiveBackgroundTasks(idle));
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

        var idle = MakeIdle(new SessionIdleDataBackgroundTasks
        {
            Agents = new[]
            {
                new SessionIdleDataBackgroundTasksAgentsItem
                {
                    AgentId = "agent-1", AgentType = "copilot", Description = "worker"
                }
            },
            Shells = Array.Empty<SessionIdleDataBackgroundTasksShellsItem>()
        });

        // Verify preconditions
        Assert.False(info.IsProcessing);
        Assert.True(CopilotService.HasActiveBackgroundTasks(idle));

        // The re-arm logic: if !IsProcessing && HasActiveBackgroundTasks && !WasUserAborted → re-arm
        bool shouldRearm = !info.IsProcessing && CopilotService.HasActiveBackgroundTasks(idle);
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

        var idle = MakeIdle(bt: null);

        bool shouldRearm = !info.IsProcessing && CopilotService.HasActiveBackgroundTasks(idle);
        Assert.False(shouldRearm, "Should NOT re-arm when no background tasks");
    }

    [Fact]
    public void IdleDeferRearm_ShouldNotRearm_WhenAlreadyProcessing()
    {
        // Scenario: session.idle with background tasks but IsProcessing is already true.
        // Expected: should NOT re-arm (already armed) — just defer.
        var info = new AgentSessionInfo { Name = "test", Model = "test-model" };
        info.IsProcessing = true; // Already processing

        var idle = MakeIdle(new SessionIdleDataBackgroundTasks
        {
            Agents = new[]
            {
                new SessionIdleDataBackgroundTasksAgentsItem
                {
                    AgentId = "a1", AgentType = "copilot", Description = ""
                }
            },
            Shells = Array.Empty<SessionIdleDataBackgroundTasksShellsItem>()
        });

        bool shouldRearm = !info.IsProcessing && CopilotService.HasActiveBackgroundTasks(idle);
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

        var idle = MakeIdle(new SessionIdleDataBackgroundTasks
        {
            Agents = new[]
            {
                new SessionIdleDataBackgroundTasksAgentsItem
                {
                    AgentId = "a1", AgentType = "copilot", Description = ""
                }
            },
            Shells = Array.Empty<SessionIdleDataBackgroundTasksShellsItem>()
        });

        // The full re-arm condition includes WasUserAborted check
        bool shouldRearm = !info.IsProcessing && CopilotService.HasActiveBackgroundTasks(idle) && !wasUserAborted;
        Assert.False(shouldRearm, "Should NOT re-arm when user aborted");
    }
}
