using PolyPilot.IntegrationTests.Fixtures;

namespace PolyPilot.IntegrationTests;

/// <summary>
/// Integration smoke tests for session management UI (baseline for Issue #397).
/// These verify the dashboard and prompt input are accessible — prerequisite paths
/// for the shutdown pre-check flow. Full shutdown scenario testing requires a live
/// CLI server and is covered by manual/E2E tests.
/// </summary>
[Collection("PolyPilot")]
[Trait("Category", "SessionManagement")]
public class SessionManagementSmokeTests : IntegrationTestBase
{
    public SessionManagementSmokeTests(AppFixture app, ITestOutputHelper output)
        : base(app, output) { }

    [Fact]
    public async Task Dashboard_SessionList_IsAccessible()
    {
        // Verify the app is running and the dashboard loads — baseline smoke test.
        await WaitForCdpReadyAsync();

        // Dashboard should be the default page
        var dashboardExists = await ExistsAsync("#dashboard, .sessions-list, .dashboard-container");
        Assert.True(dashboardExists, "Dashboard should be accessible for session management");

        await ScreenshotAsync("dashboard-baseline-smoke");
    }

    [Fact]
    public async Task PromptInput_IsAvailable()
    {
        // Verify that the prompt input area is present on the dashboard.
        await WaitForCdpReadyAsync();

        var inputExists = await ExistsAsync("#prompt-input, .prompt-input, textarea[id*='prompt']");
        Assert.True(inputExists, "Prompt input should be visible on dashboard");

        await ScreenshotAsync("prompt-input-available");
    }
}
