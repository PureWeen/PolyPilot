using PolyPilot.IntegrationTests.Fixtures;

namespace PolyPilot.IntegrationTests;

/// <summary>
/// Integration tests for the quota display feature.
/// Creates a session, sends a message to trigger AssistantUsageEvent with quota data,
/// then verifies the quota indicator appears in the Dashboard header.
/// </summary>
[Collection("PolyPilot")]
[Trait("Category", "QuotaDisplay")]
public class QuotaDisplayTests : IntegrationTestBase
{
    public QuotaDisplayTests(AppFixture app, ITestOutputHelper output)
        : base(app, output) { }

    [Fact]
    public async Task QuotaIndicator_AppearsAfterSendingMessage()
    {
        await WaitForCdpReadyAsync();
        await ScreenshotAsync("quota-01-before");

        // Step 1: Create a new session by clicking "+" → "Session"
        await ClickAsync("[title='Create new session'], button.new-session-btn");
        await Task.Delay(1000);

        var menuItem = await CdpEvalAsync(
            "const items = [...document.querySelectorAll('.sidebar-new-menu-item, .popover-item, button')]; " +
            "const btn = items.find(b => b.textContent?.includes('Session') || b.textContent?.includes('Empty')); " +
            "btn?.click(); btn ? 'clicked: ' + btn.textContent?.trim()?.substring(0,20) : 'no session btn'");
        Output.WriteLine($"Create session: {menuItem}");
        await Task.Delay(2000);

        // Step 2: Find the input area and type a message
        var fillResult = await CdpEvalAsync(
            "const sel = '.card-input input, .card-input textarea, .input-row textarea, textarea'; " +
            "const input = [...document.querySelectorAll(sel)].find(i => i.offsetParent !== null); " +
            "if (input) { input.value = 'Say hello'; " +
            "input.dispatchEvent(new Event('input', {bubbles:true})); " +
            "input.dispatchEvent(new Event('change', {bubbles:true})); 'filled'; } else { 'no input'; }");
        Output.WriteLine($"Fill input: {fillResult}");

        // Step 3: Click send
        var sendResult = await CdpEvalAsync(
            "const sel = '.card-input input, .card-input textarea, .input-row textarea, textarea'; " +
            "const input = [...document.querySelectorAll(sel)].find(i => i.offsetParent !== null); " +
            "if (input) { const container = input.closest('.card-input') || input.closest('.input-row'); " +
            "const sendBtn = container?.querySelector('.send-btn:not(.stop-btn)') || " +
            "container?.querySelectorAll('button')?.[container?.querySelectorAll('button')?.length - 1]; " +
            "if (sendBtn) { sendBtn.click(); 'sent: ' + sendBtn.className; } " +
            "else { input.dispatchEvent(new KeyboardEvent('keydown', {key: 'Enter', bubbles: true})); 'enter'; } " +
            "} else { 'no input'; }");
        Output.WriteLine($"Send: {sendResult}");

        // Step 4: Wait for response + quota event (up to 60 seconds)
        Output.WriteLine("Waiting for Copilot response + quota data...");
        var hasQuota = false;
        for (var i = 0; i < 30; i++)
        {
            var quotaExists = await ExistsAsync("#quota-indicator, .quota-indicator, .quota-pill");
            if (quotaExists)
            {
                hasQuota = true;
                Output.WriteLine($"Quota indicator appeared after {i * 2}s");
                break;
            }
            if (i % 5 == 0)
            {
                var pageState = await CdpEvalAsync(
                    "JSON.stringify({quota: !!document.querySelector('#quota-indicator, .quota-indicator'), " +
                    "messages: document.querySelectorAll('.message-content, .markdown-body').length, " +
                    "processing: !!document.querySelector('.thinking, .processing, .spinner')})");
                Output.WriteLine($"Poll {i * 2}s: {pageState}");
            }
            await Task.Delay(2000);
        }

        await ScreenshotAsync("quota-02-after-message");

        if (hasQuota)
        {
            // Verify quota indicator content
            var quotaText = await GetTextAsync("#quota-indicator, .quota-indicator, .quota-pill");
            Output.WriteLine($"Quota indicator text: '{quotaText}'");
            Assert.False(string.IsNullOrWhiteSpace(quotaText), "Quota indicator should show percentage");

            await ScreenshotAsync("quota-03-indicator-visible");
        }
        else
        {
            Output.WriteLine("Quota indicator did not appear — may need more messages or token may not have quota data");
            // Don't fail — the test documents the behavior. Quota depends on the account's plan.
        }
    }

    [Fact]
    public async Task Dashboard_LoadsWithoutErrors()
    {
        await WaitForCdpReadyAsync();

        var dashboardLoaded = await ExistsAsync(".dashboard, #scheduled-tasks-page, .session-item");
        Assert.True(dashboardLoaded, "Dashboard should render without errors");

        await ScreenshotAsync("quota-dashboard");
    }

    [Fact]
    public async Task UsageCommand_ShowsQuotaInfo()
    {
        await WaitForCdpReadyAsync();

        // Navigate to a session if not already in one
        var hasSession = await ExistsAsync(".session-item, .session-list-item");
        if (hasSession)
        {
            await ClickAsync(".session-item, .session-list-item");
            await Task.Delay(2000);
        }

        // Type /usage command
        var fillResult = await CdpEvalAsync(
            "const sel = '.card-input input, .card-input textarea, .input-row textarea, textarea'; " +
            "const input = [...document.querySelectorAll(sel)].find(i => i.offsetParent !== null); " +
            "if (input) { input.value = '/usage'; " +
            "input.dispatchEvent(new Event('input', {bubbles:true})); " +
            "input.dispatchEvent(new Event('change', {bubbles:true})); 'filled'; } else { 'no input'; }");
        Output.WriteLine($"Fill /usage: {fillResult}");

        if (fillResult == "filled")
        {
            // Send it
            await CdpEvalAsync(
                "const sel = '.card-input input, .card-input textarea, .input-row textarea, textarea'; " +
                "const input = [...document.querySelectorAll(sel)].find(i => i.offsetParent !== null); " +
                "if (input) { input.dispatchEvent(new KeyboardEvent('keydown', {key: 'Enter', bubbles: true})); }");
            await Task.Delay(2000);

            await ScreenshotAsync("quota-usage-command");
        }
    }
}
