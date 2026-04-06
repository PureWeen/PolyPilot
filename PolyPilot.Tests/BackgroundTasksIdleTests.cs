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

    [Fact]
    public void ProactiveIdleDefer_SubagentDeferStartedAtTicks_StampedOnBackgroundTasksChanged()
    {
        // SessionBackgroundTasksChangedEvent should proactively stamp SubagentDeferStartedAtTicks
        // so the zombie expiry timer starts as early as possible — without waiting for session.idle.
        // See issue #518.
        var source = File.ReadAllText(Path.Combine(GetRepoRoot(),
            "PolyPilot", "Services", "CopilotService.Events.cs"));

        var handlerStart = source.IndexOf("case SessionBackgroundTasksChangedEvent:");
        Assert.True(handlerStart >= 0, "SessionBackgroundTasksChangedEvent handler not found");

        // Find the next case or closing brace to bound the handler
        var handlerEnd = source.IndexOf("case System", handlerStart + 1);
        if (handlerEnd < 0) handlerEnd = source.Length;
        var handler = source.Substring(handlerStart, handlerEnd - handlerStart);

        // Must stamp SubagentDeferStartedAtTicks via CompareExchange
        Assert.Contains("SubagentDeferStartedAtTicks", handler);
        Assert.Contains("CompareExchange", handler);
    }

    [Fact]
    public void ProactiveIdleDefer_Handler_DoesNotSetIsProcessingFalse()
    {
        // The SessionBackgroundTasksChangedEvent handler must NOT set IsProcessing=false.
        // It should only update IDLE-DEFER tracking fields. See issue #518.
        var source = File.ReadAllText(Path.Combine(GetRepoRoot(),
            "PolyPilot", "Services", "CopilotService.Events.cs"));

        var handlerStart = source.IndexOf("case SessionBackgroundTasksChangedEvent:");
        Assert.True(handlerStart >= 0, "SessionBackgroundTasksChangedEvent handler not found");

        var handlerEnd = source.IndexOf("case System", handlerStart + 1);
        if (handlerEnd < 0) handlerEnd = source.Length;
        var handler = source.Substring(handlerStart, handlerEnd - handlerStart);

        Assert.DoesNotContain("IsProcessing = false", handler);
        Assert.DoesNotContain("IsProcessing=false", handler);
    }

    [Fact]
    public void ProactiveIdleDefer_CompareExchange_PreservesExistingTimestamp()
    {
        // CompareExchange(0 → now) should only set the timestamp if it's currently 0.
        // If a previous IDLE-DEFER already stamped it, the proactive stamp should not overwrite.
        long existingTicks = DateTime.UtcNow.AddMinutes(-5).Ticks;
        long field = existingTicks;

        // Simulate what the handler does: CompareExchange(ref field, newValue, 0L)
        Interlocked.CompareExchange(ref field, DateTime.UtcNow.Ticks, 0L);

        // Should NOT have changed — existing value was non-zero
        Assert.Equal(existingTicks, field);
    }

    [Fact]
    public void ProactiveIdleDefer_CompareExchange_SetsWhenZero()
    {
        // When SubagentDeferStartedAtTicks is 0 (no prior IDLE-DEFER), CompareExchange should set it.
        long field = 0L;
        var now = DateTime.UtcNow.Ticks;

        Interlocked.CompareExchange(ref field, now, 0L);

        Assert.Equal(now, field);
    }

    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root");
    }
}
