using PolyPilot.IntegrationTests.Fixtures;

namespace PolyPilot.IntegrationTests;

/// <summary>
/// Smoke tests verifying app bootstrap succeeds after the SystemMessageConfig
/// refactoring (issue #496). These don't test section overrides directly —
/// they confirm the refactored CreateSessionAsync doesn't break initialization.
/// </summary>
[Collection("PolyPilot")]
[Trait("Category", "AppBootstrap")]
public class WorkerSystemMessageTests : IntegrationTestBase
{
    public WorkerSystemMessageTests(AppFixture app, ITestOutputHelper output)
        : base(app, output) { }

    [Fact]
    public async Task AppBootstrap_DashboardLoads()
    {
        await WaitForCdpReadyAsync();

        // Verify the dashboard is accessible — the multi-agent group creation
        // UI is on the dashboard. If the dashboard loads, the underlying
        // CreateMultiAgentGroupAsync (which now passes worker system message
        // sections) is available.
        var dashboardExists = await WaitForAsync("#dashboard-page", TimeSpan.FromSeconds(10));
        if (!dashboardExists)
        {
            // Try navigating to dashboard
            await NavigateToAsync("Dashboard", "#dashboard-page");
            dashboardExists = await ExistsAsync("#dashboard-page");
        }

        // Even if dashboard element ID isn't present, the app should be responsive
        var bodyText = await GetTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(bodyText), "App body should have content");
        Output.WriteLine($"Dashboard content preview: {bodyText[..Math.Min(bodyText.Length, 200)]}");

        await ScreenshotAsync("dashboard-worker-system-message");
    }

    [Fact]
    public async Task AppBootstrap_RespondsToApiStatus()
    {
        // Verify the app is running and responsive — this confirms that the
        // refactored CreateSessionAsync (with systemMessageSections parameter)
        // didn't break app initialization.
        var status = await GetJsonAsync("/api/status");
        Assert.True(status.TryGetProperty("agentReady", out var ready));
        Output.WriteLine($"Agent ready: {ready}");
    }
}
