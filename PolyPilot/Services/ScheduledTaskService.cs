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
        lock (_lock) return _tasks.ToList();
    }

    public ScheduledTask? GetTask(string id)
    {
        lock (_lock) return _tasks.FirstOrDefault(t => t.Id == id);
    }

    public void AddTask(ScheduledTask task)
    {
        lock (_lock) _tasks.Add(task);
        SaveTasks();
        OnTasksChanged?.Invoke();
    }

    public void UpdateTask(ScheduledTask task)
    {
        lock (_lock)
        {
            var idx = _tasks.FindIndex(t => t.Id == task.Id);
            if (idx >= 0) _tasks[idx] = task;
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
        if (Interlocked.CompareExchange(ref _evaluating, 1, 0) != 0) return;

        try
        {
            List<ScheduledTask> dueTasks;
            var now = DateTime.UtcNow;

            lock (_lock)
            {
                dueTasks = _tasks.Where(t => t.IsDue(now)).ToList();
            }

            foreach (var task in dueTasks)
            {
                await ExecuteTaskAsync(task, now);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _evaluating, 0);
        }
    }

    /// <summary>Execute a single scheduled task.</summary>
    internal async Task ExecuteTaskAsync(ScheduledTask task, DateTime utcNow)
    {
        var run = new ScheduledTaskRun { StartedAt = utcNow };

        try
        {
            if (!_copilotService.IsInitialized)
            {
                run.Error = "CopilotService not initialized";
                run.Success = false;
                RecordRunAndSave(task, run);
                return;
            }

            string sessionName;

            if (!string.IsNullOrEmpty(task.SessionName))
            {
                // Use existing session
                sessionName = task.SessionName;
                var sessions = _copilotService.GetAllSessions();
                if (!sessions.Any(s => s.Name == sessionName))
                {
                    run.Error = $"Session '{sessionName}' not found";
                    run.Success = false;
                    RecordRunAndSave(task, run);
                    return;
                }
            }
            else
            {
                // Create a new session for this run
                var timestamp = utcNow.ToLocalTime().ToString("MMM dd HH:mm");
                sessionName = $"⏰ {task.Name} ({timestamp})";
                try
                {
                    await _copilotService.CreateSessionAsync(sessionName, task.Model, task.WorkingDirectory);
                }
                catch (Exception ex)
                {
                    run.Error = $"Failed to create session: {ex.Message}";
                    run.Success = false;
                    RecordRunAndSave(task, run);
                    return;
                }
            }

            run.SessionName = sessionName;
            await _copilotService.SendPromptAsync(sessionName, task.Prompt);

            run.CompletedAt = DateTime.UtcNow;
            run.Success = true;
        }
        catch (Exception ex)
        {
            run.CompletedAt = DateTime.UtcNow;
            run.Error = ex.Message;
            run.Success = false;
            System.Diagnostics.Debug.WriteLine($"[ScheduledTask] Execution failed for '{task.Name}': {ex.Message}");
        }

        RecordRunAndSave(task, run);
    }

    private void RecordRunAndSave(ScheduledTask task, ScheduledTaskRun run)
    {
        lock (_lock) task.RecordRun(run);
        SaveTasks();
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
            System.Diagnostics.Debug.WriteLine($"[ScheduledTask] Failed to load tasks: {ex.Message}");
        }
    }

    internal void SaveTasks()
    {
        try
        {
            List<ScheduledTask> snapshot;
            lock (_lock) snapshot = _tasks.ToList();

            var dir = Path.GetDirectoryName(TasksFilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(TasksFilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ScheduledTask] Failed to save tasks: {ex.Message}");
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
