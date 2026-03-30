using System.Text.Json;
using PolyPilot.Models;

namespace PolyPilot.Services;

/// <summary>
/// Manages scheduled (recurring) tasks — persistence, background evaluation, and execution.
/// Tasks are stored in ~/.polypilot/scheduled-tasks.json and evaluated every 30 seconds.
/// When a task is due, it sends the configured prompt to the target session (or creates a new one).
/// </summary>
public class ScheduledTaskService : IDisposable
{
    private static string? _tasksFilePath;
    private static string TasksFilePath => _tasksFilePath ??= Path.Combine(GetPolyPilotDir(), "scheduled-tasks.json");

    /// <summary>Override file path for tests to prevent writing to real ~/.polypilot/.</summary>
    internal static void SetTasksFilePathForTesting(string path) => _tasksFilePath = path;

    private readonly CopilotService _copilotService;
    private readonly List<ScheduledTask> _tasks = new();
    private readonly object _lock = new();
    private Timer? _evaluationTimer;
    private int _evaluating; // Guard against overlapping evaluations
    private bool _disposed;

    /// <summary>Raised when any task list or state change occurs (for UI refresh).</summary>
    public event Action? OnTasksChanged;

    /// <summary>Interval between schedule evaluations.</summary>
    internal const int EvaluationIntervalSeconds = 30;

    public ScheduledTaskService(CopilotService copilotService)
    {
        _copilotService = copilotService;
        LoadTasks();
        Start(); // Auto-start the evaluation timer
    }

    /// <summary>Start the background evaluation timer.</summary>
    public void Start()
    {
        if (_disposed) return;
        _evaluationTimer?.Dispose();
        _evaluationTimer = new Timer(
            _ => _ = EvaluateTasksAsync(),
            null,
            TimeSpan.FromSeconds(EvaluationIntervalSeconds),
            TimeSpan.FromSeconds(EvaluationIntervalSeconds));
    }

    /// <summary>Stop the background evaluation timer.</summary>
    public void Stop()
    {
        _evaluationTimer?.Dispose();
        _evaluationTimer = null;
    }

    // ── CRUD ──────────────────────────────────────────────────────────

    public IReadOnlyList<ScheduledTask> GetTasks()
    {
        lock (_lock) return _tasks.Select(t => t.Clone()).ToList();
    }

    public ScheduledTask? GetTask(string id)
    {
        lock (_lock) return _tasks.FirstOrDefault(t => t.Id == id)?.Clone();
    }

    public void AddTask(ScheduledTask task)
    {
        lock (_lock) _tasks.Add(task);
        SaveTasks();
        OnTasksChanged?.Invoke();
    }

    public void UpdateTask(ScheduledTask updated)
    {
        lock (_lock)
        {
            var idx = _tasks.FindIndex(t => t.Id == updated.Id);
            if (idx >= 0)
            {
                var canonical = _tasks[idx];
                // Merge only user-editable fields. Never overwrite LastRunAt, RecentRuns, or
                // CreatedAt — those are owned by the service and may have been updated by the
                // background timer while the edit form was open.
                canonical.Name = updated.Name;
                canonical.Prompt = updated.Prompt;
                canonical.SessionName = updated.SessionName;
                canonical.Model = updated.Model;
                canonical.WorkingDirectory = updated.WorkingDirectory;
                canonical.Schedule = updated.Schedule;
                canonical.IntervalMinutes = updated.IntervalMinutes;
                canonical.TimeOfDay = updated.TimeOfDay;
                canonical.DaysOfWeek = updated.DaysOfWeek.ToList();
                canonical.CronExpression = updated.CronExpression;
                canonical.IsEnabled = updated.IsEnabled;
            }
        }
        SaveTasks();
        OnTasksChanged?.Invoke();
    }

    public bool DeleteTask(string id)
    {
        bool removed;
        lock (_lock) removed = _tasks.RemoveAll(t => t.Id == id) > 0;
        if (removed)
        {
            SaveTasks();
            OnTasksChanged?.Invoke();
        }
        return removed;
    }

    public void SetEnabled(string id, bool enabled)
    {
        lock (_lock)
        {
            var task = _tasks.FirstOrDefault(t => t.Id == id);
            if (task != null) task.IsEnabled = enabled;
        }
        SaveTasks();
        OnTasksChanged?.Invoke();
    }

    // ── Evaluation ───────────────────────────────────────────────────

    /// <summary>
    /// Evaluate all tasks and execute any that are due.
    /// Called by the background timer every 30 seconds.
    /// Uses an interlocked guard to prevent overlapping evaluations.
    /// </summary>
    internal async Task EvaluateTasksAsync()
    {
        // Prevent overlapping evaluations if a previous run is still executing
        if (Interlocked.CompareExchange(ref _evaluating, 1, 0) != 0)
        {
            Console.WriteLine("[ScheduledTask] Evaluation skipped — previous cycle still running");
            return;
        }

        try
        {
            List<string> dueTaskIds;
            var now = DateTime.UtcNow;

            // Collect IDs only — do not hold task references across the lock boundary.
            // ExecuteTaskAsync will re-fetch a fresh snapshot of each task under its own lock.
            lock (_lock)
            {
                dueTaskIds = _tasks.Where(t => t.IsDue(now)).Select(t => t.Id).ToList();
            }

            if (dueTaskIds.Count > 0)
                Console.WriteLine($"[ScheduledTask] Evaluation: {dueTaskIds.Count} task(s) due");

            foreach (var taskId in dueTaskIds)
            {
                try
                {
                    await ExecuteTaskAsync(taskId, now);
                }
                catch (Exception ex)
                {
                    // Isolate failures so one bad task doesn't prevent remaining tasks from running
                    Console.WriteLine($"[ScheduledTask] Unhandled error executing task {taskId}: {ex.Message}");
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _evaluating, 0);
        }
    }

    /// <summary>
    /// Execute a scheduled task by ID. Takes a snapshot of task data under lock so
    /// async execution does not race with UI mutations or timer evaluations.
    /// </summary>
    internal async Task ExecuteTaskAsync(string taskId, DateTime utcNow)
    {
        // Snapshot the task data under lock so we don't race with UpdateTask/SetEnabled
        ScheduledTask snapshot;
        lock (_lock)
        {
            var canonical = _tasks.FirstOrDefault(t => t.Id == taskId);
            if (canonical == null) return; // task was deleted between evaluation and execution
            snapshot = canonical.Clone();
        }

        Console.WriteLine($"[ScheduledTask] Executing: {snapshot.Name}");
        var run = new ScheduledTaskRun { StartedAt = utcNow };

        try
        {
            if (!_copilotService.IsInitialized)
            {
                run.Error = "CopilotService not initialized";
                run.Success = false;
                RecordRunAndSave(taskId, run);
                return;
            }

            string sessionName;

            if (!string.IsNullOrEmpty(snapshot.SessionName))
            {
                // Use existing session
                sessionName = snapshot.SessionName;
                var sessions = _copilotService.GetAllSessions();
                if (!sessions.Any(s => s.Name == sessionName))
                {
                    run.Error = $"Session '{sessionName}' not found";
                    run.Success = false;
                    RecordRunAndSave(taskId, run);
                    return;
                }
            }
            else
            {
                // Create a new session for this run
                var timestamp = utcNow.ToLocalTime().ToString("MMM dd HH:mm");
                sessionName = $"⏰ {snapshot.Name} ({timestamp})";
                try
                {
                    await _copilotService.CreateSessionAsync(sessionName, snapshot.Model, snapshot.WorkingDirectory);
                }
                catch (Exception ex)
                {
                    run.Error = $"Failed to create session: {ex.Message}";
                    run.Success = false;
                    RecordRunAndSave(taskId, run);
                    return;
                }
            }

            run.SessionName = sessionName;
            await _copilotService.SendPromptAsync(sessionName, snapshot.Prompt);

            run.CompletedAt = DateTime.UtcNow;
            run.Success = true;
        }
        catch (Exception ex)
        {
            run.CompletedAt = DateTime.UtcNow;
            run.Error = ex.Message;
            run.Success = false;
            Console.WriteLine($"[ScheduledTask] Execution failed for '{snapshot.Name}': {ex.Message}");
        }

        RecordRunAndSave(taskId, run);
    }

    /// <summary>
    /// Convenience overload that accepts a task object (e.g., from "Run Now" in the UI).
    /// Delegates to the ID-based overload so the canonical internal instance is always updated.
    /// </summary>
    internal Task ExecuteTaskAsync(ScheduledTask task, DateTime utcNow)
        => ExecuteTaskAsync(task.Id, utcNow);

    /// <summary>
    /// Records a run on the canonical task instance (looked up by ID under lock) and persists.
    /// Always operates on the internal task object so UI snapshots cannot corrupt state.
    /// </summary>
    private void RecordRunAndSave(string taskId, ScheduledTaskRun run)
    {
        lock (_lock)
        {
            var canonical = _tasks.FirstOrDefault(t => t.Id == taskId);
            canonical?.RecordRun(run);
        }
        SaveTasks(); // I/O outside lock
        OnTasksChanged?.Invoke();
    }

    // ── Persistence ──────────────────────────────────────────────────

    internal void LoadTasks()
    {
        try
        {
            if (File.Exists(TasksFilePath))
            {
                var json = File.ReadAllText(TasksFilePath);
                var loaded = JsonSerializer.Deserialize<List<ScheduledTask>>(json);
                if (loaded != null)
                {
                    lock (_lock)
                    {
                        _tasks.Clear();
                        _tasks.AddRange(loaded);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ScheduledTask] Failed to load tasks: {ex.Message}");
        }
    }

    /// <summary>
    /// Saves tasks to disk atomically (snapshot under lock, write outside lock).
    /// Uses write-to-temp + rename to prevent data loss on crash.
    /// </summary>
    internal void SaveTasks()
    {
        List<ScheduledTask> snapshot;
        lock (_lock)
        {
            snapshot = _tasks.Select(t => t.Clone()).ToList();
        }

        try
        {
            var dir = Path.GetDirectoryName(TasksFilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            var tempPath = TasksFilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, TasksFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ScheduledTask] Failed to save tasks: {ex.Message}");
        }
    }

    private static string GetPolyPilotDir()
    {
#if IOS || ANDROID
        try
        {
            return Path.Combine(FileSystem.AppDataDirectory, ".polypilot");
        }
        catch
        {
            var fallback = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(fallback))
                fallback = Path.GetTempPath();
            return Path.Combine(fallback, ".polypilot");
        }
#else
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(home, ".polypilot");
#endif
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _evaluationTimer?.Dispose();
        _evaluationTimer = null;
    }
}
