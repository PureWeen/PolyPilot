using PolyPilot.IntegrationTests.Fixtures;

namespace PolyPilot.IntegrationTests;

/// <summary>
/// Integration tests verifying that the app starts successfully after the
/// orphaned temp session sweep runs on startup.
/// </summary>
[Collection("PolyPilot")]
[Trait("Category", "TempSessionSweep")]
public class TempSessionSweepTests : IntegrationTestBase
{
    public TempSessionSweepTests(AppFixture app, ITestOutputHelper output)
        : base(app, output) { }

    [Fact]
    public async Task AppStartsSuccessfully_AfterSweep()
    {
        // The sweep runs during RestorePreviousSessionsAsync on startup.
        // Verify the app loaded the dashboard without crashing.
        await WaitForCdpReadyAsync();
        var dashboardVisible = await WaitForAsync("#dashboard-page, .dashboard, .session-list");
        Assert.True(dashboardVisible, "Dashboard should be visible after startup with sweep");
    }

    [Fact]
    public async Task NoErrorBanners_AfterSweep()
    {
        await WaitForCdpReadyAsync();
        // Check that no error banners or fallback notices appeared
        var hasError = await ExistsAsync(".error-banner, .fallback-notice, .fatal-error");
        Assert.False(hasError, "No error banners should appear after normal startup with sweep");
    }
}
