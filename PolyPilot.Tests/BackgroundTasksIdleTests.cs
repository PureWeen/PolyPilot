using GitHub.Copilot.SDK;
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
}
