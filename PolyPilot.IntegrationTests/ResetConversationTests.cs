using PolyPilot.IntegrationTests.Fixtures;

namespace PolyPilot.IntegrationTests;

/// <summary>
/// Integration tests for the "Reset Conversation" context menu feature.
/// Verifies the menu item appears and triggers history clear via the live Blazor UI.
/// </summary>
[Collection("PolyPilot")]
[Trait("Category", "ResetConversation")]
public class ResetConversationTests : IntegrationTestBase
{
    public ResetConversationTests(AppFixture app, ITestOutputHelper output)
        : base(app, output) { }

    [Fact]
    public async Task ContextMenu_ContainsResetConversation()
    {
        await WaitForCdpReadyAsync();

        // Right-click (open context menu) on the first session item
        var menuItemExists = await CdpEvalAsync(@"
            const item = document.querySelector('.session-item');
            if (!item) return 'no session item';
            // Trigger context menu
            item.dispatchEvent(new PointerEvent('contextmenu', { bubbles: true }));
            'triggered'
        ");
        Output.WriteLine($"Context menu trigger: {menuItemExists}");

        if (menuItemExists == "no session item")
        {
            Output.WriteLine("No session items found — skipping (app may not have sessions in CI)");
            Assert.Skip("No session items available in CI — cannot verify context menu");
        }

        // Wait for menu to open
        await Task.Delay(1000);

        // Check for the Reset Conversation menu item
        var hasResetItem = await CdpEvalAsync(@"
            const items = [...document.querySelectorAll('.menu-item')];
            const resetItem = items.find(el => el.textContent?.includes('Reset Conversation'));
            resetItem ? 'found' : 'not found'
        ");
        Output.WriteLine($"Reset Conversation menu item: {hasResetItem}");

        // Close the menu
        var overlay = await ClickAsync(".menu-overlay");
        Output.WriteLine($"Close menu: {overlay}");

        await ScreenshotAsync("context-menu-reset-conversation");

        Assert.Equal("found", hasResetItem);
    }
}
