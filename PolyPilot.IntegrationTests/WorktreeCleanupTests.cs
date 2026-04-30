using PolyPilot.IntegrationTests.Fixtures;

namespace PolyPilot.IntegrationTests;

/// <summary>
/// Integration tests verifying that orphaned worktree pruning runs on startup
/// without crashing the app. The pruning logic is fire-and-forget in
/// RestoreSessionsInBackgroundAsync, so we verify the app loads normally.
/// </summary>
[Collection("PolyPilot")]
[Trait("Category", "WorktreeCleanup")]
public class WorktreeCleanupTests : IntegrationTestBase
{
    public WorktreeCleanupTests(AppFixture app, ITestOutputHelper output)
        : base(app, output) { }

    [Fact]
    public async Task AppStartsSuccessfully_WithWorktreePruning()
    {
        // Worktree pruning runs as fire-and-forget during startup.
        // Verify the app loaded the dashboard without crashing.
        await WaitForCdpReadyAsync();
        var dashboardVisible = await WaitForAsync("#dashboard-page, .dashboard, .session-list");
        Assert.True(dashboardVisible, "Dashboard should be visible after startup with worktree pruning");
    }

    [Fact]
    public async Task AgentStatus_ReportsRunning_AfterPruning()
    {
        var status = await GetJsonAsync("/api/status");
        Assert.True(status.TryGetProperty("running", out var running) && running.GetBoolean(),
            "Agent should report running=true after startup with worktree pruning");
    }
}
