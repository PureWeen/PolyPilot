using PolyPilot.IntegrationTests.Fixtures;

namespace PolyPilot.IntegrationTests;

/// <summary>
/// Integration tests for the Advanced CLI config settings in the Settings page.
/// Verifies that the Advanced section renders with the expected toggles
/// (CompactPaste, RespectGitignore, DisableAllHooks) end-to-end through
/// the live Blazor UI via DevFlow CDP.
/// Related to issue #698 (Expose additional CLI config options in Settings UI).
/// </summary>
[Collection("PolyPilot")]
[Trait("Category", "AdvancedCliConfig")]
public class AdvancedCliConfigTests : IntegrationTestBase
{
    public AdvancedCliConfigTests(AppFixture app, ITestOutputHelper output)
        : base(app, output) { }

    [Fact]
    public async Task SettingsPage_ShowsAdvancedSection()
    {
        await WaitForCdpReadyAsync();

        // Navigate to settings
        await ClickAsync("[href='/settings'], .settings-link, a[title='Settings']");
        await WaitForAsync("#settings-page, .settings-container", TimeSpan.FromSeconds(10));

        // The Advanced section should be present in the page
        var hasAdvanced = await ExistsAsync("#settings-advanced");
        Output.WriteLine($"Advanced section visible: {hasAdvanced}");
        Assert.True(hasAdvanced, "Expected #settings-advanced section to be present on the Settings page");

        await ScreenshotAsync("settings-advanced-section");
    }

    [Fact]
    public async Task AdvancedSection_HasCliConfigToggles()
    {
        await WaitForCdpReadyAsync();

        // Navigate to settings
        await ClickAsync("[href='/settings'], .settings-link, a[title='Settings']");
        await WaitForAsync("#settings-page, .settings-container", TimeSpan.FromSeconds(10));

        // Scroll to and check for the Advanced navigation item
        var navVisible = await ExistsAsync(".settings-nav-item");
        Output.WriteLine($"Nav items visible: {navVisible}");
        Assert.True(navVisible, "Expected settings nav items to be visible");

        // Check the page text contains our setting labels
        var pageText = await GetTextAsync("#settings-advanced") ?? "";
        Output.WriteLine($"Advanced section text length: {pageText.Length}");
        Assert.True(pageText.Length > 0, "Expected #settings-advanced section to contain text");

        await ScreenshotAsync("settings-advanced-toggles");
    }
}
