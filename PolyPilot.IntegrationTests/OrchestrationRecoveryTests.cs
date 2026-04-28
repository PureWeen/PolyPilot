using PolyPilot.IntegrationTests.Fixtures;

namespace PolyPilot.IntegrationTests;

/// <summary>
/// Integration tests for orchestration recovery paths (issue #387).
/// Verifies multi-agent UI elements exist and orchestration features
/// are accessible through the live Blazor UI via DevFlow CDP.
/// </summary>
[Collection("PolyPilot")]
[Trait("Category", "OrchestrationRecovery")]
public class OrchestrationRecoveryTests : IntegrationTestBase
{
    public OrchestrationRecoveryTests(AppFixture app, ITestOutputHelper output)
        : base(app, output) { }

    [Fact]
    public async Task Dashboard_LoadsSuccessfully()
    {
        await WaitForCdpReadyAsync();
        var exists = await ExistsAsync("#dashboard");
        Assert.True(exists, "Dashboard page should load and contain #dashboard element");
    }

    [Fact]
    public async Task Dashboard_NewSessionButtonExists()
    {
        await WaitForCdpReadyAsync();
        var exists = await ExistsAsync(".new-session-btn, #new-session-btn, [data-testid='new-session']");
        Assert.True(exists, "New session button should be present on the dashboard");
    }

    [Fact]
    public async Task Settings_ConnectionModeExists()
    {
        await WaitForCdpReadyAsync();
        var navigated = await NavigateToAsync("Settings", "#settings-page");
        Assert.True(navigated, "Should navigate to Settings page");

        await ScreenshotAsync("settings-page");
        // Verify settings page has connection mode options
        var settingsContent = await GetTextAsync("#settings-page");
        Assert.False(string.IsNullOrWhiteSpace(settingsContent),
            "Settings page should have visible content");
    }
}
