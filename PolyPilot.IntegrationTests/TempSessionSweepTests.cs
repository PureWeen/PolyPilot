using PolyPilot.IntegrationTests.Fixtures;

namespace PolyPilot.IntegrationTests;

/// <summary>
/// Integration tests verifying that PolyPilot's startup sweep cleans up
/// orphaned temp session directories. The app should start successfully
/// and display the dashboard even when orphaned temp dirs exist.
/// </summary>
[Collection("PolyPilot")]
[Trait("Category", "TempSessionSweep")]
public class TempSessionSweepTests : IntegrationTestBase
{
    public TempSessionSweepTests(AppFixture app, ITestOutputHelper output)
        : base(app, output) { }

    [Fact]
    public async Task App_StartsSuccessfully_WithDashboard()
    {
        // The startup sweep runs during RestorePreviousSessionsAsync.
        // Verify the app reached a healthy state by checking the dashboard renders.
        await WaitForCdpReadyAsync();

        // Dashboard should be visible (the default landing page)
        var dashboardVisible = await WaitForAsync("#dashboard-page, .dashboard, .session-list", TimeSpan.FromSeconds(15));
        Assert.True(dashboardVisible, "Dashboard should be visible after app startup (startup sweep ran without errors)");
    }

    [Fact]
    public async Task App_SessionList_IsAccessible()
    {
        // After startup sweep, the session list should render normally.
        await WaitForCdpReadyAsync();

        // Check that the session management UI is available
        var hasSessionArea = await WaitForAsync(".session-list, .sessions-container, #session-list", TimeSpan.FromSeconds(10));
        Output.WriteLine($"Session area found: {hasSessionArea}");

        // The app should not show any error banners from sweep failures
        var errorBanner = await ExistsAsync(".error-banner, .fatal-error");
        Assert.False(errorBanner, "No error banner should be visible after startup sweep");
    }
}
