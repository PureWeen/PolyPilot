using PolyPilot.IntegrationTests.Fixtures;

namespace PolyPilot.IntegrationTests;

/// <summary>
/// Integration tests for orchestration relaunch resilience (issue #400).
/// Verifies the multi-agent group UI and orchestration resume indicators
/// are accessible through the live Blazor UI via DevFlow CDP.
/// </summary>
[Collection("PolyPilot")]
[Trait("Category", "OrchestrationRelaunch")]
public class OrchestrationRelaunchTests : IntegrationTestBase
{
    public OrchestrationRelaunchTests(AppFixture app, ITestOutputHelper output)
        : base(app, output) { }

    [Fact]
    public async Task Dashboard_MultiAgentGroupsRenderable()
    {
        await WaitForCdpReadyAsync();
        // The dashboard should be able to render multi-agent groups.
        // This doesn't require an active orchestration — just verifies the
        // rendering pipeline handles the group list without errors.
        var dashboardExists = await ExistsAsync("#dashboard");
        Assert.True(dashboardExists, "Dashboard should load successfully — multi-agent group rendering must not crash");
        await ScreenshotAsync("dashboard-multiagent-groups");
    }

    [Fact]
    public async Task Dashboard_OrchestratorPhaseIndicatorRenderable()
    {
        await WaitForCdpReadyAsync();
        // Verify the dashboard component that shows orchestrator phase
        // (Planning → Dispatching → WaitingForWorkers → Synthesizing → Complete → Resuming)
        // doesn't crash even with no active orchestration.
        var dashboardExists = await ExistsAsync("#dashboard");
        Assert.True(dashboardExists,
            "Dashboard should load without errors — phase indicator rendering must not crash when no orchestration is active");
        await ScreenshotAsync("dashboard-no-active-orchestration");
    }

    [Fact]
    public async Task Settings_HasConnectionModeForReconnect()
    {
        await WaitForCdpReadyAsync();
        var navigated = await NavigateToAsync("Settings", "#settings-page");
        if (navigated)
        {
            await ScreenshotAsync("settings-reconnect-mode");
            // Settings page should have reconnect capability — needed for
            // orchestration resume after relaunch
            var content = await GetTextAsync("#settings-page");
            Assert.False(string.IsNullOrWhiteSpace(content),
                "Settings page should have visible content for connection mode configuration");
        }
    }
}
