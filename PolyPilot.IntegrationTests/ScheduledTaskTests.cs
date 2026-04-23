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

    // ─── Execution Tests ───

    [Fact]
    [Trait("Category", "ScheduledTaskExecution")]
    public async Task RunNow_CreatesRunHistory()
    {
        await WaitForCdpReadyAsync();
        await NavigateToAsync("Scheduled Tasks", "#scheduled-tasks-page");

        var taskName = $"RunNow-Test-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        await CreateIntervalTaskAsync(taskName, "echo run now test", 60);

        var card = $".task-card[data-task-name=\"{taskName}\"]";

        // Click Run Now
        var runResult = await ClickAsync($"{card} [data-task-action=\"run-now\"]");
        Output.WriteLine($"Run Now click: {runResult}");

        // Wait for the run to complete — poll for run history to appear
        // The task creates a new session, sends the prompt, and waits up to 11 minutes.
        // For a simple "echo" prompt, it should complete in ~30 seconds.
        var hasHistory = false;
        for (var i = 0; i < 90; i++) // 90 seconds max
        {
            // Check if a run-status indicator appeared
            var statusExists = await ExistsAsync($"{card} .run-status");
            var lastRunText = await GetTextAsync($"{card} .last-run");
            Output.WriteLine($"Poll {i}: statusExists={statusExists}, lastRun='{lastRunText}'");

            if (statusExists && !string.IsNullOrWhiteSpace(lastRunText))
            {
                hasHistory = true;
                break;
            }
            await Task.Delay(2000);
        }

        await ScreenshotAsync("after-run-now");

        Assert.True(hasHistory, "Run Now should produce a run history entry with status indicator");

        // Expand history and verify run entry
        await ClickAsync($"{card} .history-toggle");
        await Task.Delay(1000);

        var historyVisible = await ExistsAsync($"{card} .run-history");
        if (historyVisible)
        {
            var runEntryExists = await ExistsAsync($"{card} .run-entry");
            Assert.True(runEntryExists, "Run history should contain at least one run entry");

            var sessionName = await GetTextAsync($"{card} .run-entry .run-session");
            Output.WriteLine($"Run session name: '{sessionName}'");
            Assert.False(string.IsNullOrWhiteSpace(sessionName), "Run entry should show session name");
        }

        await DeleteTaskAsync(taskName);
    }

    [Fact]
    [Trait("Category", "ScheduledTaskExecution")]
    public async Task ScheduledExecution_TaskFiresAutomatically()
    {
        await WaitForCdpReadyAsync();
        await NavigateToAsync("Scheduled Tasks", "#scheduled-tasks-page");

        // Create a 1-minute interval task
        var taskName = $"AutoRun-Test-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        await CreateIntervalTaskAsync(taskName, "echo scheduled execution test", 1);

        var card = $".task-card[data-task-name=\"{taskName}\"]";
        Output.WriteLine("Waiting up to 120s for the scheduled task to fire automatically...");

        // Wait for the task to fire — the scheduler evaluates every 30 seconds,
        // so a 1-minute interval task should fire within ~90 seconds.
        var hasFired = false;
        for (var i = 0; i < 60; i++) // 120 seconds max (2s intervals)
        {
            var lastRunText = await GetTextAsync($"{card} .last-run");
            var statusExists = await ExistsAsync($"{card} .run-status");

            if (i % 10 == 0)
                Output.WriteLine($"Poll {i * 2}s: lastRun='{lastRunText}', status={statusExists}");

            if (statusExists && !string.IsNullOrWhiteSpace(lastRunText) && lastRunText.Contains("Last run"))
            {
                hasFired = true;
                Output.WriteLine($"Task fired! lastRun='{lastRunText}'");
                break;
            }
            await Task.Delay(2000);
        }

        await ScreenshotAsync("after-scheduled-execution");

        Assert.True(hasFired, "1-minute interval task should fire automatically within 120 seconds");

        // Verify the next-run timer is shown
        var nextRun = await GetTextAsync($"{card} .next-run");
        Output.WriteLine($"Next run: '{nextRun}'");

        await DeleteTaskAsync(taskName);
    }

    [Fact]
    [Trait("Category", "ScheduledTaskExecution")]
    public async Task RunNow_TwiceCreatesUniqueSessionNames()
    {
        await WaitForCdpReadyAsync();
        await NavigateToAsync("Scheduled Tasks", "#scheduled-tasks-page");

        var taskName = $"Multi-Run-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        await CreateIntervalTaskAsync(taskName, "echo multi run test", 60);

        var card = $".task-card[data-task-name=\"{taskName}\"]";

        // Run Now — first execution
        await ClickAsync($"{card} [data-task-action=\"run-now\"]");
        Output.WriteLine("First Run Now triggered, waiting for completion...");

        // Wait for first run
        for (var i = 0; i < 45; i++)
        {
            if (await ExistsAsync($"{card} .run-status"))
                break;
            await Task.Delay(2000);
        }
        Assert.True(await ExistsAsync($"{card} .run-status"), "First run should complete");

        // Run Now — second execution
        await ClickAsync($"{card} [data-task-action=\"run-now\"]");
        Output.WriteLine("Second Run Now triggered, waiting for completion...");

        // Wait for second run to appear in history
        await Task.Delay(30_000); // Give it 30 seconds

        // Expand history
        await ClickAsync($"{card} .history-toggle");
        await Task.Delay(1000);

        // Count run entries
        var runCount = await CdpEvalAsync(
            $"document.querySelectorAll(\"{EscapeForJs(card)} .run-entry\").length.toString()");
        Output.WriteLine($"Run entries: {runCount}");

        var count = int.TryParse(runCount, out var c) ? c : 0;
        Assert.True(count >= 2, $"Should have at least 2 run entries after running twice, got {count}");

        // Verify session names are different
        var sessions = await CdpEvalAsync(
            $"[...document.querySelectorAll(\"{EscapeForJs(card)} .run-entry .run-session\")].map(s => s.textContent.trim()).join('|')");
        Output.WriteLine($"Session names: {sessions}");

        var names = sessions.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (names.Length >= 2)
            Assert.NotEqual(names[0], names[1]);

        await DeleteTaskAsync(taskName);
    }

    // ─── Helpers ───

    private static string EscapeForJs(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");


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
