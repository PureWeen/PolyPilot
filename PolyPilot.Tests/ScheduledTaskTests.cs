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

            task.Name = "Updated";
            task.Prompt = "updated";
            svc.UpdateTask(task);

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

            var updated = svc.GetTask(task.Id);
            Assert.NotNull(updated);
            Assert.Single(updated!.RecentRuns);
            Assert.True(updated.RecentRuns[0].Success);
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
}
