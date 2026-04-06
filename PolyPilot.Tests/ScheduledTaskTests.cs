using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

public class ScheduledTaskTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public ScheduledTaskTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateCopilotService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    private ScheduledTaskService CreateService()
    {
        return new ScheduledTaskService(CreateCopilotService());
    }

    private static DateTime GetPastLocalSlot(DateTime utcNow)
    {
        var localNow = utcNow.ToLocalTime();
        var slotLocal = localNow.AddMinutes(-5);
        return slotLocal.Date < localNow.Date ? localNow.Date : slotLocal;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, string failureMessage)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
                return;

            await Task.Delay(25);
        }

        Assert.True(predicate(), failureMessage);
    }

    // ── Model tests ─────────────────────────────────────────────

    [Fact]
    public void ScheduledTask_DefaultValues()
    {
        var task = new ScheduledTask();

        Assert.False(string.IsNullOrEmpty(task.Id));
        Assert.Equal("", task.Name);
        Assert.Equal("", task.Prompt);
        Assert.Null(task.SessionName);
        Assert.Equal(ScheduleType.Daily, task.Schedule);
        Assert.Equal(60, task.IntervalMinutes);
        Assert.Equal("09:00", task.TimeOfDay);
        Assert.Equal(new List<int> { 1, 2, 3, 4, 5 }, task.DaysOfWeek);
        Assert.True(task.IsEnabled);
        Assert.Null(task.LastRunAt);
        Assert.Empty(task.RecentRuns);
    }

    [Fact]
    public void ScheduledTask_JsonRoundTrip()
    {
        var original = new ScheduledTask
        {
            Name = "Daily Standup",
            Prompt = "Give me a summary of yesterday's changes",
            Schedule = ScheduleType.Daily,
            TimeOfDay = "10:30",
            IsEnabled = true,
            SessionName = "my-session",
            Model = "claude-opus-4.6"
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<ScheduledTask>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Id, deserialized!.Id);
        Assert.Equal("Daily Standup", deserialized.Name);
        Assert.Equal("Give me a summary of yesterday's changes", deserialized.Prompt);
        Assert.Equal(ScheduleType.Daily, deserialized.Schedule);
        Assert.Equal("10:30", deserialized.TimeOfDay);
        Assert.True(deserialized.IsEnabled);
        Assert.Equal("my-session", deserialized.SessionName);
        Assert.Equal("claude-opus-4.6", deserialized.Model);
    }

    [Fact]
    public void ScheduledTask_JsonRoundTrip_List()
    {
        var tasks = new List<ScheduledTask>
        {
            new() { Name = "Task 1", Prompt = "Prompt 1", Schedule = ScheduleType.Interval, IntervalMinutes = 30 },
            new() { Name = "Task 2", Prompt = "Prompt 2", Schedule = ScheduleType.Weekly, DaysOfWeek = new() { 1, 3, 5 } }
        };

        var json = JsonSerializer.Serialize(tasks, new JsonSerializerOptions { WriteIndented = true });
        var deserialized = JsonSerializer.Deserialize<List<ScheduledTask>>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized!.Count);
        Assert.Equal("Task 1", deserialized[0].Name);
        Assert.Equal(30, deserialized[0].IntervalMinutes);
        Assert.Equal("Task 2", deserialized[1].Name);
        Assert.Equal(new List<int> { 1, 3, 5 }, deserialized[1].DaysOfWeek);
    }

    // ── Schedule description ────────────────────────────────────

    [Fact]
    public void ScheduleDescription_Interval()
    {
        var task = new ScheduledTask { Schedule = ScheduleType.Interval, IntervalMinutes = 30 };
        Assert.Equal("Every 30 minutes", task.ScheduleDescription);
    }

    [Fact]
    public void ScheduleDescription_Interval_Singular()
    {
        var task = new ScheduledTask { Schedule = ScheduleType.Interval, IntervalMinutes = 1 };
        Assert.Equal("Every 1 minute", task.ScheduleDescription);
    }

    [Fact]
    public void ScheduleDescription_Daily()
    {
        var task = new ScheduledTask { Schedule = ScheduleType.Daily, TimeOfDay = "14:00" };
        Assert.Equal("Daily at 14:00", task.ScheduleDescription);
    }

    [Fact]
    public void ScheduleDescription_Weekly()
    {
        var task = new ScheduledTask { Schedule = ScheduleType.Weekly, TimeOfDay = "09:00", DaysOfWeek = new() { 1, 3, 5 } };
        Assert.Equal("Weekly (Mon, Wed, Fri) at 09:00", task.ScheduleDescription);
    }

    // ── ParseTimeOfDay ──────────────────────────────────────────

    [Theory]
    [InlineData("09:00", 9, 0)]
    [InlineData("14:30", 14, 30)]
    [InlineData("00:00", 0, 0)]
    [InlineData("23:59", 23, 59)]
    public void ParseTimeOfDay_ValidInputs(string input, int expectedHours, int expectedMinutes)
    {
        var task = new ScheduledTask { TimeOfDay = input };
        var (h, m) = task.ParseTimeOfDay();
        Assert.Equal(expectedHours, h);
        Assert.Equal(expectedMinutes, m);
    }

    [Fact]
    public void ParseTimeOfDay_InvalidInput_ReturnsDefault()
    {
        var task = new ScheduledTask { TimeOfDay = "not-a-time" };
        var (h, m) = task.ParseTimeOfDay();
        Assert.Equal(9, h);
        Assert.Equal(0, m);
    }

    // ── IsDue ───────────────────────────────────────────────────

    [Fact]
    public void IsDue_DisabledTask_ReturnsFalse()
    {
        var task = new ScheduledTask
        {
            Schedule = ScheduleType.Interval,
            IntervalMinutes = 1,
            IsEnabled = false
        };
        Assert.False(task.IsDue(DateTime.UtcNow));
    }

    [Fact]
    public void IsDue_IntervalTask_NeverRun_ReturnsTrue()
    {
        var task = new ScheduledTask
        {
            Schedule = ScheduleType.Interval,
            IntervalMinutes = 60,
            IsEnabled = true,
            LastRunAt = null
        };
        Assert.True(task.IsDue(DateTime.UtcNow));
    }

    [Fact]
    public void IsDue_IntervalTask_RecentlyRun_ReturnsFalse()
    {
        var now = DateTime.UtcNow;
        var task = new ScheduledTask
        {
            Schedule = ScheduleType.Interval,
            IntervalMinutes = 60,
            IsEnabled = true,
            LastRunAt = now.AddMinutes(-10) // ran 10 min ago, interval is 60
        };
        Assert.False(task.IsDue(now));
    }

    [Fact]
    public void IsDue_IntervalTask_PastDue_ReturnsTrue()
    {
        var now = DateTime.UtcNow;
        var task = new ScheduledTask
        {
            Schedule = ScheduleType.Interval,
            IntervalMinutes = 60,
            IsEnabled = true,
            LastRunAt = now.AddMinutes(-65) // ran 65 min ago, interval is 60
        };
        Assert.True(task.IsDue(now));
    }

    // ── RecordRun ───────────────────────────────────────────────

    [Fact]
    public void RecordRun_AddsRunAndUpdatesLastRunAt()
    {
        var task = new ScheduledTask { Name = "test" };
        var run = new ScheduledTaskRun { StartedAt = DateTime.UtcNow, Success = true };

        task.RecordRun(run);

        Assert.Single(task.RecentRuns);
        Assert.Equal(run.StartedAt, task.LastRunAt);
    }

    [Fact]
    public void RecordRun_TrimsToTenEntries()
    {
        var task = new ScheduledTask { Name = "test" };
        for (int i = 0; i < 15; i++)
        {
            task.RecordRun(new ScheduledTaskRun
            {
                StartedAt = DateTime.UtcNow.AddMinutes(i),
                Success = true
            });
        }

        Assert.Equal(10, task.RecentRuns.Count);
    }

    // ── GetNextRunTimeUtc ───────────────────────────────────────

    [Fact]
    public void GetNextRunTimeUtc_IntervalZero_ReturnsNull()
    {
        var task = new ScheduledTask { Schedule = ScheduleType.Interval, IntervalMinutes = 0 };
        Assert.Null(task.GetNextRunTimeUtc(DateTime.UtcNow));
    }

    [Fact]
    public void GetNextRunTimeUtc_WeeklyNoDays_ReturnsNull()
    {
        var task = new ScheduledTask { Schedule = ScheduleType.Weekly, DaysOfWeek = new() };
        Assert.Null(task.GetNextRunTimeUtc(DateTime.UtcNow));
    }

    [Fact]
    public void GetNextRunTimeUtc_Weekly_MissedTodaySlot_StillDue()
    {
        // Regression test for: weekly task's slot time passed today but it hasn't run today —
        // should return today's slot (due), not skip to next week.
        var now = DateTime.UtcNow;
        var localNow = now.ToLocalTime();
        var slotLocal = GetPastLocalSlot(now);
        var timeStr = $"{slotLocal.Hour:D2}:{slotLocal.Minute:D2}";
        var todayDow = (int)localNow.DayOfWeek;

        var task = new ScheduledTask
        {
            Schedule = ScheduleType.Weekly,
            TimeOfDay = timeStr,
            DaysOfWeek = new List<int> { todayDow }, // today is a scheduled day
            IsEnabled = true,
            LastRunAt = now.AddDays(-7) // ran last week, not today
        };

        var next = task.GetNextRunTimeUtc(now);
        Assert.NotNull(next);
        // Should be today's slot (in the past) — meaning IsDue() is true
        Assert.True(next!.Value <= now, $"Expected past slot <= now, got {next.Value:O}");
        Assert.True(task.IsDue(now), "Task should be due — missed today's slot");
    }

    [Fact]
    public void GetNextRunTimeUtc_Weekly_AlreadyRanToday_NotDue()
    {
        // Task ran today at the scheduled slot — should NOT fire again today.
        var now = DateTime.UtcNow;
        var localNow = now.ToLocalTime();

        var slotLocal = GetPastLocalSlot(now);
        var timeStr = $"{slotLocal.Hour:D2}:{slotLocal.Minute:D2}";
        var todayDow = (int)localNow.DayOfWeek;

        var task = new ScheduledTask
        {
            Schedule = ScheduleType.Weekly,
            TimeOfDay = timeStr,
            DaysOfWeek = new List<int> { todayDow },
            IsEnabled = true,
            LastRunAt = slotLocal.AddMinutes(1).ToUniversalTime() // ran at today's slot
        };

        Assert.False(task.IsDue(now), "Task should not fire — already ran today");
    }

    [Fact]
    public void GetNextRunTimeUtc_Daily_FailedToday_StillDue()
    {
        // Regression test: a failed run today must not suppress the rest of today's schedule.
        var now = DateTime.UtcNow;
        var slotLocal = GetPastLocalSlot(now);
        var timeStr = $"{slotLocal.Hour:D2}:{slotLocal.Minute:D2}";

        var task = new ScheduledTask
        {
            Schedule = ScheduleType.Daily,
            TimeOfDay = timeStr,
            LastRunAt = slotLocal.ToUniversalTime()
        };
        task.RecentRuns.Add(new ScheduledTaskRun
        {
            StartedAt = slotLocal.ToUniversalTime(),
            CompletedAt = slotLocal.ToUniversalTime(),
            Success = false,
            Error = "Transient failure"
        });

        var next = task.GetNextRunTimeUtc(now);
        Assert.NotNull(next);
        Assert.True(next!.Value <= now, "Failed daily run today should remain due for retry");
        Assert.True(task.IsDue(now), "Task should still be due after a failed run today");
    }

    [Fact]
    public void GetNextRunTimeUtc_Weekly_FailedToday_StillDue()
    {
        // Regression test: weekly tasks should mirror daily behavior — a failed run today
        // must not skip directly to next week.
        var now = DateTime.UtcNow;
        var localNow = now.ToLocalTime();
        var slotLocal = GetPastLocalSlot(now);
        var timeStr = $"{slotLocal.Hour:D2}:{slotLocal.Minute:D2}";
        var todayDow = (int)localNow.DayOfWeek;

        var task = new ScheduledTask
        {
            Schedule = ScheduleType.Weekly,
            TimeOfDay = timeStr,
            DaysOfWeek = new List<int> { todayDow },
            LastRunAt = slotLocal.ToUniversalTime()
        };
        task.RecentRuns.Add(new ScheduledTaskRun
        {
            StartedAt = slotLocal.ToUniversalTime(),
            CompletedAt = slotLocal.ToUniversalTime(),
            Success = false,
            Error = "Transient failure"
        });

        var next = task.GetNextRunTimeUtc(now);
        Assert.NotNull(next);
        Assert.True(next!.Value <= now, "Failed weekly run today should remain due for retry");
        Assert.True(task.IsDue(now), "Weekly task should still be due after a failed run today");
    }

    [Fact]
    public void GetNextRunTimeUtc_IntervalNeverRun_ReturnsNow()
    {
        var now = DateTime.UtcNow;
        var task = new ScheduledTask
        {
            Schedule = ScheduleType.Interval,
            IntervalMinutes = 60,
            LastRunAt = null
        };
        var next = task.GetNextRunTimeUtc(now);
        Assert.NotNull(next);
        Assert.Equal(now, next!.Value);
    }

    [Fact]
    public void GetNextRunTimeUtc_IntervalAfterRun_ReturnsLastPlusInterval()
    {
        var now = DateTime.UtcNow;
        var lastRun = now.AddMinutes(-30);
        var task = new ScheduledTask
        {
            Schedule = ScheduleType.Interval,
            IntervalMinutes = 60,
            LastRunAt = lastRun
        };
        var next = task.GetNextRunTimeUtc(now);
        Assert.NotNull(next);
        Assert.Equal(lastRun.AddMinutes(60), next!.Value);
    }

    // ── ScheduleType enum ───────────────────────────────────────

    [Fact]
    public void ScheduleType_HasExpectedValues()
    {
        Assert.Equal(0, (int)ScheduleType.Interval);
        Assert.Equal(1, (int)ScheduleType.Daily);
        Assert.Equal(2, (int)ScheduleType.Weekly);
    }

    // ── Service persistence tests ───────────────────────────────

    [Fact]
    public void Service_SaveAndLoad_RoundTrips()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"polypilot-sched-test-{Guid.NewGuid():N}.json");
        ScheduledTaskService.SetTasksFilePathForTesting(tempFile);

        try
        {
            var svc = CreateService();
            svc.AddTask(new ScheduledTask { Name = "Test Task", Prompt = "Do something" });

            // Create a new service instance to verify it loads from disk
            var svc2 = CreateService();
            var loaded = svc2.GetTasks();

            Assert.Single(loaded);
            Assert.Equal("Test Task", loaded[0].Name);
            Assert.Equal("Do something", loaded[0].Prompt);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
            // Reset to test path
            ScheduledTaskService.SetTasksFilePathForTesting(
                Path.Combine(TestSetup.TestBaseDir, "scheduled-tasks.json"));
        }
    }

    [Fact]
    public void Service_DeleteTask_RemovesFromList()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"polypilot-sched-test-{Guid.NewGuid():N}.json");
        ScheduledTaskService.SetTasksFilePathForTesting(tempFile);

        try
        {
            var svc = CreateService();
            var task = new ScheduledTask { Name = "To Delete", Prompt = "test" };
            svc.AddTask(task);
            Assert.Single(svc.GetTasks());

            var result = svc.DeleteTask(task.Id);
            Assert.True(result);
            Assert.Empty(svc.GetTasks());
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
            ScheduledTaskService.SetTasksFilePathForTesting(
                Path.Combine(TestSetup.TestBaseDir, "scheduled-tasks.json"));
        }
    }

    [Fact]
    public void Service_SetEnabled_TogglesState()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"polypilot-sched-test-{Guid.NewGuid():N}.json");
        ScheduledTaskService.SetTasksFilePathForTesting(tempFile);

        try
        {
            var svc = CreateService();
            var task = new ScheduledTask { Name = "Toggle", Prompt = "test", IsEnabled = true };
            svc.AddTask(task);

            svc.SetEnabled(task.Id, false);
            Assert.False(svc.GetTask(task.Id)!.IsEnabled);

            svc.SetEnabled(task.Id, true);
            Assert.True(svc.GetTask(task.Id)!.IsEnabled);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
            ScheduledTaskService.SetTasksFilePathForTesting(
                Path.Combine(TestSetup.TestBaseDir, "scheduled-tasks.json"));
        }
    }

    [Fact]
    public void Service_UpdateTask_ModifiesExistingTask()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"polypilot-sched-test-{Guid.NewGuid():N}.json");
        ScheduledTaskService.SetTasksFilePathForTesting(tempFile);

        try
        {
            var svc = CreateService();
            var task = new ScheduledTask { Name = "Original", Prompt = "original" };
            svc.AddTask(task);

            var clone = svc.GetTask(task.Id)!;
            clone.Name = "Updated";
            clone.Prompt = "updated";
            svc.UpdateTask(clone);

            var loaded = svc.GetTask(task.Id);
            Assert.NotNull(loaded);
            Assert.Equal("Updated", loaded!.Name);
            Assert.Equal("updated", loaded.Prompt);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
            ScheduledTaskService.SetTasksFilePathForTesting(
                Path.Combine(TestSetup.TestBaseDir, "scheduled-tasks.json"));
        }
    }

    [Fact]
    public void Service_UpdateTask_PreservesRunHistoryAndLastRunAt()
    {
        // Regression test: editing a task while the timer ran must not erase run history.
        // UpdateTask merges user-editable fields onto the canonical instance,
        // never overwriting LastRunAt or RecentRuns.
        var tempFile = Path.Combine(Path.GetTempPath(), $"polypilot-sched-test-{Guid.NewGuid():N}.json");
        ScheduledTaskService.SetTasksFilePathForTesting(tempFile);

        try
        {
            var svc = CreateService();
            var task = new ScheduledTask { Name = "Original", Prompt = "original" };
            svc.AddTask(task);

            // Simulate: user opens edit form → gets a snapshot clone (no runs yet)
            var staleClone = svc.GetTask(task.Id)!;
            Assert.Empty(staleClone.RecentRuns);

            // Simulate: timer fires and records a run on the canonical instance.
            // AddTask stores the reference directly, so `task` IS the canonical object.
            task.RecordRun(new ScheduledTaskRun
            {
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
                Success = true,
                SessionName = "timer-session"
            });
            svc.SaveTasks(); // persist the run

            // Verify canonical now has a run
            var checkAfterRun = svc.GetTask(task.Id)!;
            Assert.Single(checkAfterRun.RecentRuns);
            Assert.NotNull(checkAfterRun.LastRunAt);

            // Now "user saves" using the stale clone (which has no runs)
            staleClone.Name = "Edited Name";
            staleClone.Prompt = "edited prompt";
            svc.UpdateTask(staleClone);

            // Verify: name/prompt updated, but runs preserved
            var result = svc.GetTask(task.Id)!;
            Assert.Equal("Edited Name", result.Name);
            Assert.Equal("edited prompt", result.Prompt);
            Assert.Single(result.RecentRuns); // run from timer still present
            Assert.NotNull(result.LastRunAt); // LastRunAt not wiped
            Assert.Equal("timer-session", result.RecentRuns[0].SessionName);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
            ScheduledTaskService.SetTasksFilePathForTesting(
                Path.Combine(TestSetup.TestBaseDir, "scheduled-tasks.json"));
        }
    }

    [Fact]
    public void Service_UpdateTask_DoesNotOverwriteIsEnabled_FromStaleEditSnapshot()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"polypilot-sched-test-{Guid.NewGuid():N}.json");
        ScheduledTaskService.SetTasksFilePathForTesting(tempFile);

        try
        {
            var svc = CreateService();
            var task = new ScheduledTask { Name = "Toggle Race", Prompt = "test", IsEnabled = true };
            svc.AddTask(task);

            var staleClone = svc.GetTask(task.Id)!; // edit form snapshot captured while enabled
            svc.SetEnabled(task.Id, false);         // user toggles off from the list

            staleClone.Name = "Edited Name";
            svc.UpdateTask(staleClone);

            var result = svc.GetTask(task.Id)!;
            Assert.Equal("Edited Name", result.Name);
            Assert.False(result.IsEnabled, "UpdateTask must preserve the latest toggle state");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
            ScheduledTaskService.SetTasksFilePathForTesting(
                Path.Combine(TestSetup.TestBaseDir, "scheduled-tasks.json"));
        }
    }

    [Fact]
    public async Task Service_EvaluateTasksAsync_ExecutesDueTasks()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"polypilot-sched-test-{Guid.NewGuid():N}.json");
        ScheduledTaskService.SetTasksFilePathForTesting(tempFile);

        try
        {
            var copilot = CreateCopilotService();
            var svc = new ScheduledTaskService(copilot);
            // Initialize CopilotService in demo mode so it can accept prompts
            await copilot.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
            await copilot.CreateSessionAsync("test-session");

            var task = new ScheduledTask
            {
                Name = "Due Task",
                Prompt = "Hello",
                Schedule = ScheduleType.Interval,
                IntervalMinutes = 1,
                IsEnabled = true,
                LastRunAt = DateTime.UtcNow.AddMinutes(-5),
                SessionName = "test-session"
            };
            svc.AddTask(task);

            await svc.EvaluateTasksAsync();
            await WaitUntilAsync(
                () => svc.GetTask(task.Id)?.RecentRuns.Count == 1,
                TimeSpan.FromSeconds(2),
                "Due task should be dispatched and record its completion.");

            var updated = svc.GetTask(task.Id);
            Assert.NotNull(updated);
            Assert.Single(updated!.RecentRuns);
            Assert.True(updated.RecentRuns[0].Success);

            var session = copilot.GetSession("test-session");
            Assert.NotNull(session);
            Assert.False(session!.IsProcessing, "ExecuteTaskAsync should not return before the target session is idle.");
            lock (session.HistoryLock)
            {
                Assert.Contains(session.History, m => m.Role == "assistant");
            }
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
            ScheduledTaskService.SetTasksFilePathForTesting(
                Path.Combine(TestSetup.TestBaseDir, "scheduled-tasks.json"));
        }
    }

    [Fact]
    public async Task Service_EvaluateTasksAsync_DoesNotBlockOtherDueTasksBehindLongRun()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"polypilot-sched-test-{Guid.NewGuid():N}.json");
        ScheduledTaskService.SetTasksFilePathForTesting(tempFile);
        var slowGate = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _demoService.BeforeCompleteAsync = (sessionName, _, ct) =>
            sessionName == "slow-session" ? slowGate.Task.WaitAsync(ct) : Task.Delay(10, ct);

        try
        {
            var copilot = CreateCopilotService();
            var svc = new ScheduledTaskService(copilot);
            await copilot.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
            await copilot.CreateSessionAsync("slow-session");
            await copilot.CreateSessionAsync("fast-session");

            var slowTask = new ScheduledTask
            {
                Name = "Slow Due Task",
                Prompt = "Wait here",
                Schedule = ScheduleType.Interval,
                IntervalMinutes = 1,
                LastRunAt = DateTime.UtcNow.AddMinutes(-5),
                SessionName = "slow-session",
                IsEnabled = true
            };
            var fastTask = new ScheduledTask
            {
                Name = "Fast Due Task",
                Prompt = "Respond immediately",
                Schedule = ScheduleType.Interval,
                IntervalMinutes = 1,
                LastRunAt = DateTime.UtcNow.AddMinutes(-5),
                SessionName = "fast-session",
                IsEnabled = true
            };

            svc.AddTask(slowTask);
            svc.AddTask(fastTask);

            var evaluateTask = svc.EvaluateTasksAsync();
            await WaitUntilAsync(
                () => svc.GetTask(fastTask.Id)?.RecentRuns.Count == 1,
                TimeSpan.FromSeconds(1),
                "A long-running task should not prevent other due tasks from running.");

            Assert.True(evaluateTask.IsCompleted,
                "EvaluateTasksAsync should dispatch due tasks without waiting for the slow run to finish.");
            Assert.Empty(svc.GetTask(slowTask.Id)!.RecentRuns);

            slowGate.TrySetResult(null);
            await WaitUntilAsync(
                () => svc.GetTask(slowTask.Id)?.RecentRuns.Count == 1,
                TimeSpan.FromSeconds(2),
                "The slow task should record its run after completion is released.");
        }
        finally
        {
            slowGate.TrySetResult(null);
            _demoService.BeforeCompleteAsync = null;
            try { File.Delete(tempFile); } catch { }
            ScheduledTaskService.SetTasksFilePathForTesting(
                Path.Combine(TestSetup.TestBaseDir, "scheduled-tasks.json"));
        }
    }

    [Fact]
    public async Task Service_ExecuteTask_NewSession_RecordsCompletionAndGeneratedSessionName()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"polypilot-sched-test-{Guid.NewGuid():N}.json");
        ScheduledTaskService.SetTasksFilePathForTesting(tempFile);

        try
        {
            var copilot = CreateCopilotService();
            var svc = new ScheduledTaskService(copilot);
            await copilot.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

            var task = new ScheduledTask
            {
                Name = "New Session Run",
                Prompt = "Hello from a freshly created scheduled session",
                Schedule = ScheduleType.Interval,
                IntervalMinutes = 5,
                IsEnabled = true
            };
            svc.AddTask(task);

            await svc.ExecuteTaskAsync(task, DateTime.UtcNow);

            var updated = svc.GetTask(task.Id);
            Assert.NotNull(updated);
            var run = Assert.Single(updated!.RecentRuns);
            Assert.True(run.Success);
            Assert.NotNull(run.CompletedAt);
            Assert.NotNull(run.SessionName);
            Assert.StartsWith("⏰ New Session Run", run.SessionName);
            var createdSession = Assert.Single(copilot.GetAllSessions(), s => s.Name == run.SessionName);
            Assert.False(createdSession.IsProcessing, "Scheduled task run should wait for the generated session to finish responding.");
            lock (createdSession.HistoryLock)
            {
                Assert.Contains(createdSession.History, m => m.Role == "assistant");
            }
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
            ScheduledTaskService.SetTasksFilePathForTesting(
                Path.Combine(TestSetup.TestBaseDir, "scheduled-tasks.json"));
        }
    }

    [Fact]
    public async Task Service_ExecuteTask_NewSession_ReusesTimestampButGeneratesUniqueName()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"polypilot-sched-test-{Guid.NewGuid():N}.json");
        ScheduledTaskService.SetTasksFilePathForTesting(tempFile);

        try
        {
            var copilot = CreateCopilotService();
            var svc = new ScheduledTaskService(copilot);
            await copilot.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

            var fixedNow = new DateTime(2026, 4, 6, 7, 7, 0, DateTimeKind.Utc);
            var task = new ScheduledTask
            {
                Name = "Duplicate Minute Run",
                Prompt = "Reply once",
                Schedule = ScheduleType.Interval,
                IntervalMinutes = 5,
                IsEnabled = true
            };
            svc.AddTask(task);

            await svc.ExecuteTaskAsync(task, fixedNow);
            await svc.ExecuteTaskAsync(task, fixedNow);

            var updated = svc.GetTask(task.Id);
            Assert.NotNull(updated);
            Assert.Equal(2, updated!.RecentRuns.Count);
            Assert.All(updated.RecentRuns, run => Assert.True(run.Success, run.Error));
            Assert.NotEqual(updated.RecentRuns[0].SessionName, updated.RecentRuns[1].SessionName);
            Assert.EndsWith("#2", updated.RecentRuns[1].SessionName);

            var createdSessionNames = copilot.GetAllSessions().Select(s => s.Name).ToList();
            Assert.Contains(updated.RecentRuns[0].SessionName!, createdSessionNames);
            Assert.Contains(updated.RecentRuns[1].SessionName!, createdSessionNames);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
            ScheduledTaskService.SetTasksFilePathForTesting(
                Path.Combine(TestSetup.TestBaseDir, "scheduled-tasks.json"));
        }
    }

    [Fact]
    public async Task Service_ExecuteTask_SessionClosedDuringRun_FailsFast()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"polypilot-sched-test-{Guid.NewGuid():N}.json");
        ScheduledTaskService.SetTasksFilePathForTesting(tempFile);
        var slowGate = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _demoService.BeforeCompleteAsync = (sessionName, _, ct) =>
            sessionName == "target-session" ? slowGate.Task.WaitAsync(ct) : Task.Delay(10, ct);

        try
        {
            var copilot = CreateCopilotService();
            var svc = new ScheduledTaskService(copilot);
            await copilot.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
            await copilot.CreateSessionAsync("target-session");

            var task = new ScheduledTask
            {
                Name = "Close Mid Run",
                Prompt = "Start and then close",
                SessionName = "target-session",
                Schedule = ScheduleType.Interval,
                IntervalMinutes = 1,
                IsEnabled = true
            };
            svc.AddTask(task);

            var executeTask = svc.ExecuteTaskAsync(task, DateTime.UtcNow);
            await WaitUntilAsync(
                () =>
                {
                    var session = copilot.GetSession("target-session");
                    if (session == null) return false;
                    lock (session.HistoryLock)
                    {
                        return session.History.Any(m => m.Role == "user");
                    }
                },
                TimeSpan.FromSeconds(1),
                "The scheduled run should dispatch its prompt before the session is closed.");

            var closed = await copilot.CloseSessionAsync("target-session");

            Assert.True(closed);
            await executeTask.WaitAsync(TimeSpan.FromSeconds(2));

            var updated = svc.GetTask(task.Id);
            Assert.NotNull(updated);
            var run = Assert.Single(updated!.RecentRuns);
            Assert.False(run.Success);
            Assert.Contains("closed during execution", run.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            slowGate.TrySetResult(null);
            _demoService.BeforeCompleteAsync = null;
            try { File.Delete(tempFile); } catch { }
            ScheduledTaskService.SetTasksFilePathForTesting(
                Path.Combine(TestSetup.TestBaseDir, "scheduled-tasks.json"));
        }
    }

    [Fact]
    public async Task Service_ExecuteTask_DailyFailure_StaysDueForRetryToday()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"polypilot-sched-test-{Guid.NewGuid():N}.json");
        ScheduledTaskService.SetTasksFilePathForTesting(tempFile);

        try
        {
            var copilot = CreateCopilotService();
            var svc = new ScheduledTaskService(copilot);
            await copilot.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

            var now = DateTime.UtcNow;
            var slotLocal = GetPastLocalSlot(now);
            var task = new ScheduledTask
            {
                Name = "Retry After Failure",
                Prompt = "This should fail because the target session does not exist",
                Schedule = ScheduleType.Daily,
                TimeOfDay = $"{slotLocal.Hour:D2}:{slotLocal.Minute:D2}",
                SessionName = "missing-session",
                IsEnabled = true
            };
            svc.AddTask(task);

            await svc.ExecuteTaskAsync(task, now);

            var updated = svc.GetTask(task.Id);
            Assert.NotNull(updated);
            var run = Assert.Single(updated!.RecentRuns);
            Assert.False(run.Success);
            Assert.Contains("not found", run.Error);
            Assert.True(updated.IsDue(now.AddMinutes(1)),
                "A failed daily run should remain due so it can retry later the same day.");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
            ScheduledTaskService.SetTasksFilePathForTesting(
                Path.Combine(TestSetup.TestBaseDir, "scheduled-tasks.json"));
        }
    }

    [Fact]
    public async Task Service_ExecuteTask_RecordsErrorWhenNotInitialized()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"polypilot-sched-test-{Guid.NewGuid():N}.json");
        ScheduledTaskService.SetTasksFilePathForTesting(tempFile);

        try
        {
            var svc = CreateService();
            // Do NOT initialize CopilotService
            var task = new ScheduledTask { Name = "Fail", Prompt = "test" };
            svc.AddTask(task);

            await svc.ExecuteTaskAsync(task, DateTime.UtcNow);

            var updated = svc.GetTask(task.Id);
            Assert.NotNull(updated);
            Assert.Single(updated!.RecentRuns);
            Assert.False(updated.RecentRuns[0].Success);
            Assert.Contains("not initialized", updated.RecentRuns[0].Error);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
            ScheduledTaskService.SetTasksFilePathForTesting(
                Path.Combine(TestSetup.TestBaseDir, "scheduled-tasks.json"));
        }
    }

    [Fact]
    public void Service_EvaluationIntervalSeconds_IsReasonable()
    {
        // Evaluation interval should be frequent enough to be useful but not too aggressive
        Assert.InRange(ScheduledTaskService.EvaluationIntervalSeconds, 10, 120);
    }

    [Fact]
    public void Service_Start_AfterDispose_DoesNotCreateTimer()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"polypilot-sched-test-{Guid.NewGuid():N}.json");
        ScheduledTaskService.SetTasksFilePathForTesting(tempFile);

        try
        {
            var svc = CreateService();
            svc.Dispose();
            // Should not throw or create a zombie timer
            svc.Start();
            // If Start created a timer, it would fire EvaluateTasksAsync — no way to
            // observe directly without reflection, but at least verify no exception.
        }
        finally
        {
            ScheduledTaskService.SetTasksFilePathForTesting(
                Path.Combine(TestSetup.TestBaseDir, "scheduled-tasks.json"));
        }
    }

    [Fact]
    public void Service_LoadTasks_HandlesCorruptFile()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"polypilot-sched-test-{Guid.NewGuid():N}.json");
        ScheduledTaskService.SetTasksFilePathForTesting(tempFile);

        try
        {
            // Write corrupt JSON
            Directory.CreateDirectory(Path.GetDirectoryName(tempFile)!);
            File.WriteAllText(tempFile, "{{not json}}");

            var svc = CreateService();
            Assert.Empty(svc.GetTasks());
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
            ScheduledTaskService.SetTasksFilePathForTesting(
                Path.Combine(TestSetup.TestBaseDir, "scheduled-tasks.json"));
        }
    }

    [Fact]
    public void Service_LoadTasks_HandlesNonexistentFile()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"polypilot-sched-test-{Guid.NewGuid():N}.json");
        ScheduledTaskService.SetTasksFilePathForTesting(tempFile);

        try
        {
            var svc = CreateService();
            Assert.Empty(svc.GetTasks());
        }
        finally
        {
            ScheduledTaskService.SetTasksFilePathForTesting(
                Path.Combine(TestSetup.TestBaseDir, "scheduled-tasks.json"));
        }
    }

    [Fact]
    public void Service_SaveTasks_AtomicWrite_NoTmpFileRemains()
    {
        // Verify the atomic write pattern (write to .tmp, rename) doesn't leave temp files.
        var tempFile = Path.Combine(Path.GetTempPath(), $"polypilot-sched-test-{Guid.NewGuid():N}.json");
        ScheduledTaskService.SetTasksFilePathForTesting(tempFile);

        try
        {
            var svc = CreateService();
            svc.AddTask(new ScheduledTask { Name = "AtomicTest", Prompt = "test" });

            // File should exist after save
            Assert.True(File.Exists(tempFile), "Task file should exist after AddTask");
            // .tmp file should NOT linger
            Assert.False(File.Exists(tempFile + ".tmp"), "Temp file should not remain after atomic write");

            // Verify content is valid JSON
            var json = File.ReadAllText(tempFile);
            var tasks = JsonSerializer.Deserialize<List<ScheduledTask>>(json);
            Assert.NotNull(tasks);
            Assert.Single(tasks!);
            Assert.Equal("AtomicTest", tasks[0].Name);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
            try { File.Delete(tempFile + ".tmp"); } catch { }
            ScheduledTaskService.SetTasksFilePathForTesting(
                Path.Combine(TestSetup.TestBaseDir, "scheduled-tasks.json"));
        }
    }

    // ── Cron expression parsing ─────────────────────────────────

    [Theory]
    [InlineData("0 9 * * 1-5", true)]     // weekdays at 9:00
    [InlineData("*/15 * * * *", true)]     // every 15 min
    [InlineData("0 0 1 * *", true)]        // 1st of each month at midnight
    [InlineData("30 14 * * 0,6", true)]    // weekends at 14:30
    [InlineData("0 9 * * *", true)]        // daily at 9am
    [InlineData("5 4 * * *", true)]        // daily at 4:05am
    public void CronExpression_ValidExpressions_ParseSuccessfully(string expr, bool expected)
    {
        Assert.Equal(expected, ScheduledTask.IsValidCronExpression(expr));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0 9")]                    // too few fields
    [InlineData("0 9 * * * *")]            // too many fields (6 fields)
    [InlineData("60 9 * * *")]             // minute out of range
    [InlineData("0 25 * * *")]             // hour out of range
    [InlineData("0 9 32 * *")]             // day out of range
    [InlineData("0 9 * 13 *")]             // month out of range
    [InlineData("0 9 * * 7")]              // dow out of range (0-6 only)
    [InlineData("abc * * * *")]            // non-numeric
    public void CronExpression_InvalidExpressions_ReturnFalse(string? expr)
    {
        Assert.False(ScheduledTask.IsValidCronExpression(expr));
    }

    [Fact]
    public void CronExpression_Ranges_ParseCorrectly()
    {
        Assert.True(ScheduledTask.TryParseCron("0 9 * * 1-5", out var cron));
        Assert.Equal(new HashSet<int> { 1, 2, 3, 4, 5 }, cron.DaysOfWeek);
        Assert.Equal(new HashSet<int> { 9 }, cron.Hours);
        Assert.Equal(new HashSet<int> { 0 }, cron.Minutes);
    }

    [Fact]
    public void CronExpression_StepValues_ParseCorrectly()
    {
        Assert.True(ScheduledTask.TryParseCron("*/15 * * * *", out var cron));
        Assert.Equal(new HashSet<int> { 0, 15, 30, 45 }, cron.Minutes);
    }

    [Fact]
    public void CronExpression_Lists_ParseCorrectly()
    {
        Assert.True(ScheduledTask.TryParseCron("0 9,12,18 * * *", out var cron));
        Assert.Equal(new HashSet<int> { 9, 12, 18 }, cron.Hours);
    }

    [Fact]
    public void CronSchedule_GetNextRunTimeUtc_FindsCorrectTime()
    {
        // Cron: "0 9 * * *" = daily at 9:00am local time
        var task = new ScheduledTask
        {
            Schedule = ScheduleType.Cron,
            CronExpression = "0 9 * * *",
            LastRunAt = null
        };

        var now = DateTime.UtcNow;
        var next = task.GetNextRunTimeUtc(now);
        Assert.NotNull(next);
        // Next run should be at 9:00 local time
        var nextLocal = next!.Value.ToLocalTime();
        Assert.Equal(9, nextLocal.Hour);
        Assert.Equal(0, nextLocal.Minute);
    }

    [Fact]
    public void CronSchedule_IsDue_ReturnsTrueWhenCronMatches()
    {
        var now = DateTime.UtcNow;
        var localNow = now.ToLocalTime();
        // Build a cron that matches the CURRENT minute — task has never run
        var currentMinuteCron = $"{localNow.Minute} {localNow.Hour} * * *";
        var task = new ScheduledTask
        {
            Schedule = ScheduleType.Cron,
            CronExpression = currentMinuteCron,
            IsEnabled = true,
            LastRunAt = null // never run
        };
        // GetNextCronTimeUtc now starts from the current minute (not +1), so it should
        // find the current minute as a match and return it as <= now → IsDue() == true.
        var next = task.GetNextRunTimeUtc(now);
        Assert.NotNull(next);
        Assert.True(next!.Value <= now, $"Expected next ({next.Value:O}) <= now ({now:O})");
        Assert.True(task.IsDue(now));
    }

    [Fact]
    public void CronSchedule_IsDue_ReturnsFalseWhenAlreadyRanThisMinute()
    {
        var now = DateTime.UtcNow;
        var localNow = now.ToLocalTime();
        var currentMinuteCron = $"{localNow.Minute} {localNow.Hour} * * *";
        // Ensure LastRunAt is within the current minute (after the minute boundary).
        // Use the minute boundary + 1 second so it's always in the same minute.
        var minuteStart = new DateTime(localNow.Year, localNow.Month, localNow.Day,
            localNow.Hour, localNow.Minute, 0, DateTimeKind.Local).ToUniversalTime();
        var task = new ScheduledTask
        {
            Schedule = ScheduleType.Cron,
            CronExpression = currentMinuteCron,
            IsEnabled = true,
            LastRunAt = minuteStart.AddSeconds(1) // ran 1s into this minute — same minute
        };
        // Already ran this minute — next run should be tomorrow at same time (or skipped)
        Assert.False(task.IsDue(now), "Task should not fire again in the same minute");
    }

    [Fact]
    public void CronSchedule_Description()
    {
        var task = new ScheduledTask
        {
            Schedule = ScheduleType.Cron,
            CronExpression = "0 9 * * 1-5"
        };
        Assert.Equal("Cron: 0 9 * * 1-5", task.ScheduleDescription);
    }

    [Fact]
    public void CronSchedule_NullExpression_Description()
    {
        var task = new ScheduledTask { Schedule = ScheduleType.Cron };
        Assert.Equal("Cron: (not set)", task.ScheduleDescription);
    }

    [Fact]
    public void CronSchedule_JsonRoundTrip()
    {
        var original = new ScheduledTask
        {
            Name = "Cron Task",
            Prompt = "Do cron things",
            Schedule = ScheduleType.Cron,
            CronExpression = "*/30 * * * *"
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<ScheduledTask>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(ScheduleType.Cron, deserialized!.Schedule);
        Assert.Equal("*/30 * * * *", deserialized.CronExpression);
    }

    // ── Validation tests ────────────────────────────────────────

    [Theory]
    [InlineData("09:00", true)]
    [InlineData("00:00", true)]
    [InlineData("23:59", true)]
    [InlineData("14:30", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("25:00", false)]
    [InlineData("not-a-time", false)]
    [InlineData("24:00", false)]
    public void IsValidTimeOfDay_ValidatesCorrectly(string? input, bool expected)
    {
        Assert.Equal(expected, ScheduledTask.IsValidTimeOfDay(input));
    }

    [Fact]
    public void ScheduleType_HasCronValue()
    {
        Assert.Equal(3, (int)ScheduleType.Cron);
    }

    // ── Daily schedule edge case ────────────────────────────────

    [Fact]
    public void GetNextRunTimeUtc_Daily_NeverRun_ReturnsTodaySlot()
    {
        var task = new ScheduledTask
        {
            Schedule = ScheduleType.Daily,
            TimeOfDay = "09:00",
            LastRunAt = null
        };
        var now = DateTime.UtcNow;
        var next = task.GetNextRunTimeUtc(now);
        Assert.NotNull(next);
        // Should be at 9:00 local time
        var nextLocal = next!.Value.ToLocalTime();
        Assert.Equal(9, nextLocal.Hour);
        Assert.Equal(0, nextLocal.Minute);
    }

    [Fact]
    public void GetNextRunTimeUtc_Daily_AlreadyRanToday_ReturnsNextDay()
    {
        var localNow = DateTime.Now;
        var task = new ScheduledTask
        {
            Schedule = ScheduleType.Daily,
            TimeOfDay = $"{localNow.Hour:D2}:{localNow.Minute:D2}",
            LastRunAt = DateTime.UtcNow.AddMinutes(-5)
        };
        var next = task.GetNextRunTimeUtc(DateTime.UtcNow);
        Assert.NotNull(next);
        // Next run should be tomorrow
        var nextLocal = next!.Value.ToLocalTime();
        Assert.Equal(localNow.Date.AddDays(1), nextLocal.Date);
    }

    // ── Interval snap-forward ───────────────────────────────────

    [Fact]
    public void GetNextRunTimeUtc_Interval_PastDue_ReturnsPastDueSlot()
    {
        var now = DateTime.UtcNow;
        var task = new ScheduledTask
        {
            Schedule = ScheduleType.Interval,
            IntervalMinutes = 60,
            LastRunAt = now.AddMinutes(-65)
        };
        var next = task.GetNextRunTimeUtc(now);
        Assert.NotNull(next);
        // Should return the missed slot (60 min after last run), which is 5 min ago
        Assert.True(next!.Value <= now);
        Assert.True(task.IsDue(now)); // and therefore it's due
    }

    [Fact]
    public void GetNextRunTimeUtc_Interval_VeryPastDue_ReturnsLatestMissedSlot()
    {
        var now = DateTime.UtcNow;
        var task = new ScheduledTask
        {
            Schedule = ScheduleType.Interval,
            IntervalMinutes = 60,
            LastRunAt = now.AddMinutes(-185) // missed 3+ intervals
        };
        var next = task.GetNextRunTimeUtc(now);
        Assert.NotNull(next);
        // Should return the 3rd interval boundary (180 min after last run = 5 min ago)
        Assert.True(next!.Value <= now);
    }

    // ── Clone / thread-safety tests ─────────────────────────────

    [Fact]
    public void Clone_ReturnsIndependentCopy()
    {
        var original = new ScheduledTask
        {
            Name = "Original",
            Prompt = "Do something",
            Schedule = ScheduleType.Weekly,
            DaysOfWeek = new List<int> { 1, 3 },
            RecentRuns = new List<ScheduledTaskRun>
            {
                new() { StartedAt = DateTime.UtcNow, Success = true }
            }
        };

        var clone = original.Clone();

        // Same data
        Assert.Equal(original.Id, clone.Id);
        Assert.Equal(original.Name, clone.Name);
        Assert.Equal(original.Prompt, clone.Prompt);
        Assert.Equal(original.DaysOfWeek, clone.DaysOfWeek);
        Assert.Single(clone.RecentRuns);

        // Mutating the clone must NOT affect the original
        clone.Name = "Modified";
        clone.DaysOfWeek.Add(5);
        clone.RecentRuns.Add(new ScheduledTaskRun { StartedAt = DateTime.UtcNow, Success = false });

        Assert.Equal("Original", original.Name);
        Assert.Equal(2, original.DaysOfWeek.Count);
        Assert.Single(original.RecentRuns);
    }

    [Fact]
    public void GetTasks_ReturnsClones_MutationDoesNotAffectService()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"polypilot-sched-test-{Guid.NewGuid():N}.json");
        ScheduledTaskService.SetTasksFilePathForTesting(tempFile);

        try
        {
            var svc = CreateService();
            svc.AddTask(new ScheduledTask { Name = "Test", Prompt = "p" });

            var snapshot = svc.GetTasks()[0];
            snapshot.Name = "Mutated by caller";

            // Service must still have the original name
            Assert.Equal("Test", svc.GetTasks()[0].Name);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
            ScheduledTaskService.SetTasksFilePathForTesting(
                Path.Combine(TestSetup.TestBaseDir, "scheduled-tasks.json"));
        }
    }

    [Fact]
    public void GetTask_ReturnsClone_MutationDoesNotAffectService()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"polypilot-sched-test-{Guid.NewGuid():N}.json");
        ScheduledTaskService.SetTasksFilePathForTesting(tempFile);

        try
        {
            var svc = CreateService();
            var task = new ScheduledTask { Name = "Test", Prompt = "p" };
            svc.AddTask(task);

            var clone = svc.GetTask(task.Id);
            Assert.NotNull(clone);
            clone!.Name = "Mutated by caller";

            Assert.Equal("Test", svc.GetTask(task.Id)!.Name);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
            ScheduledTaskService.SetTasksFilePathForTesting(
                Path.Combine(TestSetup.TestBaseDir, "scheduled-tasks.json"));
        }
    }

    [Fact]
    public async Task ExecuteTask_RecordsRunOnCanonicalInstance_NotStaleClone()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"polypilot-sched-test-{Guid.NewGuid():N}.json");
        ScheduledTaskService.SetTasksFilePathForTesting(tempFile);

        try
        {
            var svc = CreateService();
            var task = new ScheduledTask { Name = "test", Prompt = "p" };
            svc.AddTask(task);

            // Get a stale snapshot
            var staleClone = svc.GetTask(task.Id)!;

            // Execute using the stale clone — should still update the canonical task
            await svc.ExecuteTaskAsync(staleClone, DateTime.UtcNow);

            // Canonical task in service should have the run recorded
            var updated = svc.GetTask(task.Id);
            Assert.NotNull(updated);
            Assert.Single(updated!.RecentRuns);

            // The stale clone should NOT have been updated (it's a snapshot)
            Assert.Empty(staleClone.RecentRuns);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
            ScheduledTaskService.SetTasksFilePathForTesting(
                Path.Combine(TestSetup.TestBaseDir, "scheduled-tasks.json"));
        }
    }

    [Fact]
    public async Task CloseSessionAsync_DisablesScheduledTasksTargetingDeletedSession()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"polypilot-sched-test-{Guid.NewGuid():N}.json");
        ScheduledTaskService.SetTasksFilePathForTesting(tempFile);

        try
        {
            var copilot = CreateCopilotService();
            var svc = new ScheduledTaskService(copilot);
            await copilot.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
            await copilot.CreateSessionAsync("target-session");

            var targetedTask = new ScheduledTask
            {
                Name = "Uses Deleted Session",
                Prompt = "test",
                SessionName = "target-session",
                IsEnabled = true
            };
            var untargetedTask = new ScheduledTask
            {
                Name = "Independent Task",
                Prompt = "test",
                IsEnabled = true
            };

            svc.AddTask(targetedTask);
            svc.AddTask(untargetedTask);

            var closed = await copilot.CloseSessionAsync("target-session");

            Assert.True(closed);
            Assert.False(svc.GetTask(targetedTask.Id)!.IsEnabled);
            Assert.True(svc.GetTask(untargetedTask.Id)!.IsEnabled);
            Assert.Equal("target-session", svc.GetTask(targetedTask.Id)!.SessionName);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
            ScheduledTaskService.SetTasksFilePathForTesting(
                Path.Combine(TestSetup.TestBaseDir, "scheduled-tasks.json"));
        }
    }
}
