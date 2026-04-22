using PolyPilot.IntegrationTests.Fixtures;

namespace PolyPilot.IntegrationTests;

/// <summary>
/// Integration tests for the Scheduled Tasks feature.
/// Launches PolyPilot, navigates to /scheduled-tasks, and exercises
/// the full create → verify → toggle → validate → delete lifecycle
/// through the live Blazor UI via DevFlow CDP.
/// </summary>
[Collection("PolyPilot")]
[Trait("Category", "ScheduledTasks")]
public class ScheduledTaskTests : IntegrationTestBase
{
    public ScheduledTaskTests(AppFixture app, ITestOutputHelper output)
        : base(app, output) { }

    [Fact]
    public async Task Navigate_ToScheduledTasksPage()
    {
        await WaitForCdpReadyAsync();
        var navigated = await NavigateToAsync("Scheduled Tasks", "#scheduled-tasks-page");
        Assert.True(navigated, "Should navigate to /scheduled-tasks page");
    }

    [Fact]
    public async Task CreateTask_AppearsInList()
    {
        await WaitForCdpReadyAsync();
        await NavigateToAsync("Scheduled Tasks", "#scheduled-tasks-page");

        // Open create form
        var clickNew = await ClickAsync("#scheduled-task-new");
        Assert.Equal("clicked", clickNew);

        var formVisible = await WaitForAsync("#scheduled-task-form");
        Assert.True(formVisible, "Task creation form should open");

        // Fill form
        var taskName = $"CI-Test-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        await FillInputAsync("#scheduled-task-name", taskName);
        await FillInputAsync("#scheduled-task-prompt", "echo hello from integration test");
        await SelectAsync("#scheduled-task-schedule", "Interval");
        await Task.Delay(500);
        await FillInputAsync("#scheduled-task-interval", "60");

        await ScreenshotAsync("form-filled");

        // Submit
        await ClickAsync("#scheduled-task-save");
        await Task.Delay(2000);

        await ScreenshotAsync("after-save");

        // Verify card appeared
        var cardSelector = $".task-card[data-task-name=\"{taskName}\"]";
        var cardVisible = await WaitForAsync(cardSelector);
        Assert.True(cardVisible, $"Task card for '{taskName}' should appear after creation");

        // Cleanup
        await DeleteTaskAsync(taskName);
    }

    [Fact]
    public async Task CreateTask_ShowsCorrectDetails()
    {
        await WaitForCdpReadyAsync();
        await NavigateToAsync("Scheduled Tasks", "#scheduled-tasks-page");

        var taskName = $"Detail-Test-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        await CreateIntervalTaskAsync(taskName, "check build status", 120);

        // Verify schedule description (UI shows human-friendly text like "Every 2 hours")
        var scheduleText = await GetTextAsync($".task-card[data-task-name=\"{taskName}\"] .task-schedule");
        Output.WriteLine($"Schedule text: '{scheduleText}'");
        Assert.False(string.IsNullOrWhiteSpace(scheduleText), "Schedule description should not be empty");
        Assert.Contains("every", scheduleText, StringComparison.OrdinalIgnoreCase);

        // Verify prompt preview
        var promptText = await GetTextAsync($".task-card[data-task-name=\"{taskName}\"] .task-prompt-preview");
        Output.WriteLine($"Prompt text: '{promptText}'");
        Assert.Contains("check build", promptText, StringComparison.OrdinalIgnoreCase);

        await DeleteTaskAsync(taskName);
    }

    [Fact]
    public async Task ToggleEnabled_ChangesVisualState()
    {
        await WaitForCdpReadyAsync();
        await NavigateToAsync("Scheduled Tasks", "#scheduled-tasks-page");

        var taskName = $"Toggle-Test-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        await CreateIntervalTaskAsync(taskName, "toggle test", 60);

        var card = $".task-card[data-task-name=\"{taskName}\"]";

        // Disable
        await ClickAsync($"{card} [data-task-action=\"toggle-enabled\"]");
        await Task.Delay(1000);

        var isDisabled = await ExistsAsync($"{card}.disabled");
        Assert.True(isDisabled, "Task should be visually disabled");

        // Re-enable
        await ClickAsync($"{card} [data-task-action=\"toggle-enabled\"]");
        await Task.Delay(1000);

        var isEnabled = await ExistsAsync($"{card}:not(.disabled)");
        Assert.True(isEnabled, "Task should be re-enabled");

        await DeleteTaskAsync(taskName);
    }

    [Fact]
    public async Task FormValidation_InvalidCron_ShowsError()
    {
        await WaitForCdpReadyAsync();
        await NavigateToAsync("Scheduled Tasks", "#scheduled-tasks-page");

        await ClickAsync("#scheduled-task-new");
        await WaitForAsync("#scheduled-task-form");

        await FillInputAsync("#scheduled-task-name", "invalid-cron-test");
        await FillInputAsync("#scheduled-task-prompt", "test");
        await SelectAsync("#scheduled-task-schedule", "Cron");
        await Task.Delay(500);
        await FillInputAsync("#scheduled-task-cron", "not a valid cron");

        await ClickAsync("#scheduled-task-save");
        await Task.Delay(1000);

        var errorText = await GetTextAsync("#scheduled-task-form-error");
        Output.WriteLine($"Validation error: '{errorText}'");
        Assert.True(
            errorText.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
            errorText.Contains("cron", StringComparison.OrdinalIgnoreCase),
            $"Should show validation error, got: '{errorText}'");

        // Cancel and verify no task created
        await ClickAsync("#scheduled-task-cancel");
        await Task.Delay(500);

        var taskCreated = await ExistsAsync(".task-card[data-task-name=\"invalid-cron-test\"]");
        Assert.False(taskCreated, "Invalid task should not be created");
    }

    [Fact]
    public async Task DeleteTask_RemovesFromList()
    {
        await WaitForCdpReadyAsync();
        await NavigateToAsync("Scheduled Tasks", "#scheduled-tasks-page");

        var taskName = $"Delete-Test-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        await CreateIntervalTaskAsync(taskName, "delete me", 60);

        // Verify it exists
        Assert.True(await ExistsAsync($".task-card[data-task-name=\"{taskName}\"]"));

        // Delete
        await DeleteTaskAsync(taskName);

        // Verify gone
        var stillExists = await ExistsAsync($".task-card[data-task-name=\"{taskName}\"]");
        Assert.False(stillExists, "Task should be removed after deletion");
    }

    [Fact]
    public async Task AgentStatus_ReportsRunning()
    {
        var status = await GetJsonAsync("/api/status");
        Assert.True(status.TryGetProperty("running", out var running) && running.GetBoolean(),
            "Agent should report running=true");
    }

    [Fact]
    public async Task ScheduledTasksPage_HasCorrectStructure()
    {
        await WaitForCdpReadyAsync();
        await NavigateToAsync("Scheduled Tasks", "#scheduled-tasks-page");

        // Page should have a "New" button
        Assert.True(await ExistsAsync("#scheduled-task-new"),
            "Page should have a 'New Task' button");

        // Page title should be visible
        var pageText = await CdpEvalAsync("document.querySelector('#scheduled-tasks-page')?.innerText?.substring(0, 100) || ''");
        Output.WriteLine($"Page content: {pageText}");
        Assert.False(string.IsNullOrWhiteSpace(pageText), "Page should have visible content");
    }

    // ─── Helpers ───

    private async Task CreateIntervalTaskAsync(string name, string prompt, int intervalMinutes)
    {
        await ClickAsync("#scheduled-task-new");
        await WaitForAsync("#scheduled-task-form");

        await FillInputAsync("#scheduled-task-name", name);
        await FillInputAsync("#scheduled-task-prompt", prompt);
        await SelectAsync("#scheduled-task-schedule", "Interval");
        await Task.Delay(500);
        await FillInputAsync("#scheduled-task-interval", intervalMinutes.ToString());

        await ClickAsync("#scheduled-task-save");

        var created = await WaitForAsync($".task-card[data-task-name=\"{name}\"]", TimeSpan.FromSeconds(10));
        if (!created)
            Output.WriteLine($"Warning: Task '{name}' may not have been created");
    }

    private async Task DeleteTaskAsync(string taskName)
    {
        var card = $".task-card[data-task-name=\"{taskName}\"]";
        await ClickAsync($"{card} [data-task-action=\"delete\"]");
        await Task.Delay(1000);

        if (await ExistsAsync($"{card} .delete-confirm-bar"))
        {
            await ClickAsync($"{card} [data-task-action=\"confirm-delete\"]");
            await Task.Delay(2000);
        }
    }
}
