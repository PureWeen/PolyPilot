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
    private readonly SynchronizationContext? _uiContext;
    private readonly List<ScheduledTask> _tasks = new();
    private readonly object _lock = new();
    private readonly object _saveLock = new();
    private readonly HashSet<string> _inFlight = new(); // Per-task in-flight guard
    private readonly CancellationTokenSource _disposeCts = new();
    private Timer? _evaluationTimer;
    private int _evaluating; // Guard against overlapping evaluations
    private bool _disposed;
    private long _saveVersion;
    private long _lastSavedVersion;

    /// <summary>Raised when any task list or state change occurs (for UI refresh).</summary>
    public event Action? OnTasksChanged;

    /// <summary>Interval between schedule evaluations.</summary>
    internal const int EvaluationIntervalSeconds = 30;
    private static readonly TimeSpan SessionCompletionTimeout = TimeSpan.FromMinutes(11);
    private const string SessionClosedSummary = "[Error] session closed during execution";

    public ScheduledTaskService(CopilotService copilotService)
    {
        _copilotService = copilotService;
        _uiContext = SynchronizationContext.Current;
        _copilotService.OnSessionClosed += HandleSessionClosed;
        LoadTasks();
        Start(); // Auto-start the evaluation timer
    }

    /// <summary>Start the background evaluation timer.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _evaluationTimer?.Dispose();
            _evaluationTimer = new Timer(
                _ => _ = EvaluateTasksAsync(),
                null,
                TimeSpan.FromSeconds(EvaluationIntervalSeconds),
                TimeSpan.FromSeconds(EvaluationIntervalSeconds));
        }
    }

    /// <summary>Stop the background evaluation timer.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _evaluationTimer?.Dispose();
            _evaluationTimer = null;
        }
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
        lock (_lock)
        {
            _tasks.Add(task);
            _saveVersion++;
        }
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
                // IsEnabled is toggled separately via SetEnabled(). Do not overwrite it
                // from a potentially stale edit-form snapshot.
                _saveVersion++;
            }
        }
        SaveTasks();
        OnTasksChanged?.Invoke();
    }

    public bool DeleteTask(string id)
    {
        bool removed;
        lock (_lock)
        {
            removed = _tasks.RemoveAll(t => t.Id == id) > 0;
            if (removed)
                _saveVersion++;
        }
        if (removed)
        {
            SaveTasks();
            OnTasksChanged?.Invoke();
        }
        return removed;
    }

    public void SetEnabled(string id, bool enabled)
    {
        bool found;
        lock (_lock)
        {
            var task = _tasks.FirstOrDefault(t => t.Id == id);
            found = task != null;
            if (found)
            {
                task!.IsEnabled = enabled;
                _saveVersion++;
            }
        }
        if (found)
        {
            SaveTasks();
            OnTasksChanged?.Invoke();
        }
    }

    private void HandleSessionClosed(string sessionName)
    {
        bool changed = false;

        lock (_lock)
        {
            foreach (var task in _tasks)
            {
                if (task.IsEnabled &&
                    !string.IsNullOrEmpty(task.SessionName) &&
                    string.Equals(task.SessionName, sessionName, StringComparison.Ordinal))
                {
                    task.IsEnabled = false;
                    changed = true;
                }
            }

            if (changed)
                _saveVersion++;
        }

        if (changed)
        {
            SaveTasks();
            OnTasksChanged?.Invoke();
        }
    }

    // ── Evaluation ───────────────────────────────────────────────────

    /// <summary>
    /// Evaluate all tasks and dispatch any that are due.
    /// Called by the background timer every 30 seconds.
    /// Uses an interlocked guard to prevent overlapping scans while due tasks continue running.
    /// </summary>
    internal async Task EvaluateTasksAsync()
    {
        // Prevent overlapping scans if a previous timer tick is still collecting/dispatching work.
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
                DispatchDueTask(taskId, now);
        }
        finally
        {
            Interlocked.Exchange(ref _evaluating, 0);
        }
    }

    /// <summary>
    /// Execute a scheduled task by ID. Takes a snapshot of task data under lock so
    /// async execution does not race with UI mutations or timer evaluations.
    /// Uses a per-task in-flight guard to prevent RunNow + timer double-execution.
    /// </summary>
    internal async Task ExecuteTaskAsync(string taskId, DateTime utcNow)
    {
        // Per-task in-flight guard: prevent RunNow and timer from double-executing the same task
        lock (_lock)
        {
            if (!_inFlight.Add(taskId))
            {
                Console.WriteLine($"[ScheduledTask] Skipped — task {taskId} is already in-flight");
                return;
            }
        }

        try
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
                    // Create a new session for this run. Run-now can be triggered multiple times in
                    // the same minute, so retry with a suffixed name if the timestamp-based default
                    // already exists.
                    try
                    {
                        sessionName = await CreateScheduledSessionAsync(snapshot, utcNow);
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
                var completionSummary = await WaitForSessionCompletionAsync(
                    sessionName,
                    () => RunOnUiThreadAsync(() => _copilotService.SendPromptAsync(sessionName, snapshot.Prompt)),
                    _disposeCts.Token);

                run.CompletedAt = DateTime.UtcNow;
                run.Success = IsSuccessfulCompletionSummary(completionSummary);
                run.Error = run.Success ? null : completionSummary;
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
        finally
        {
            lock (_lock) { _inFlight.Remove(taskId); }
        }
    }

    /// <summary>
    /// Convenience overload that accepts a task object (e.g., from "Run Now" in the UI).
    /// Delegates to the ID-based overload so the canonical internal instance is always updated.
    /// </summary>
    internal Task ExecuteTaskAsync(ScheduledTask task, DateTime utcNow)
        => ExecuteTaskAsync(task.Id, utcNow);

    private void DispatchDueTask(string taskId, DateTime utcNow)
    {
        _ = ExecuteTaskAsync(taskId, utcNow).ContinueWith(
            t =>
            {
                var message = t.Exception?.GetBaseException().Message ?? "unknown error";
                Console.WriteLine($"[ScheduledTask] Unhandled error executing task {taskId}: {message}");
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Records a run on the canonical task instance (looked up by ID under lock) and persists.
    /// Always operates on the internal task object so UI snapshots cannot corrupt state.
    /// </summary>
    private void RecordRunAndSave(string taskId, ScheduledTaskRun run)
    {
        lock (_lock)
        {
            var canonical = _tasks.FirstOrDefault(t => t.Id == taskId);
            if (canonical != null)
            {
                canonical.RecordRun(run);
                _saveVersion++;
            }
        }
        SaveTasks(); // I/O outside lock
        OnTasksChanged?.Invoke();
    }

    private async Task<string> CreateScheduledSessionAsync(ScheduledTask snapshot, DateTime utcNow)
    {
        const int maxAttempts = 10;
        Exception? lastDuplicate = null;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var sessionName = BuildScheduledSessionName(snapshot.Name, utcNow, attempt);

            if (_copilotService.GetAllSessions().Any(s => s.Name == sessionName))
            {
                lastDuplicate = new InvalidOperationException($"Session '{sessionName}' already exists.");
                continue;
            }

            try
            {
                await RunOnUiThreadAsync(() =>
                    _copilotService.CreateSessionAsync(sessionName, snapshot.Model, snapshot.WorkingDirectory));
                return sessionName;
            }
            catch (Exception ex) when (attempt < maxAttempts - 1 &&
                                       ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                lastDuplicate = ex;
            }
        }

        throw lastDuplicate ?? new InvalidOperationException(
            $"Unable to generate a unique session name for scheduled task '{snapshot.Name}'.");
    }

    private static string BuildScheduledSessionName(string taskName, DateTime utcNow, int attempt)
    {
        var timestamp = utcNow.ToLocalTime().ToString("MMM dd HH:mm");
        var baseName = $"⏰ {taskName} ({timestamp})";
        return attempt == 0 ? baseName : $"{baseName} #{attempt + 1}";
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
        long saveVersion;
        lock (_lock)
        {
            snapshot = _tasks.Select(t => t.Clone()).ToList();
            saveVersion = _saveVersion;
        }

        lock (_saveLock)
        {
            // If a newer snapshot was already persisted while we waited, skip this stale write.
            if (saveVersion < Interlocked.Read(ref _lastSavedVersion))
                return;

            try
            {
                var dir = Path.GetDirectoryName(TasksFilePath)!;
                Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                var tempPath = TasksFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, TasksFilePath, overwrite: true);
                Interlocked.Exchange(ref _lastSavedVersion, saveVersion);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScheduledTask] Failed to save tasks: {ex.Message}");
            }
        }
    }

    // SDK-gap: SendPromptAsync/CreateSessionAsync mutate IsProcessing and companion state
    // synchronously on the caller's thread. Scheduled task evaluation runs on a Timer
    // ThreadPool thread, so these calls must be marshaled to the UI thread.
    private Task RunOnUiThreadAsync(Func<Task> action)
    {
        if (_uiContext == null || SynchronizationContext.Current == _uiContext)
            return action();

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiContext.Post(async _ =>
        {
            try
            {
                await action();
                tcs.TrySetResult(null);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, null);

        return tcs.Task;
    }

    private async Task<string> WaitForSessionCompletionAsync(string sessionName, Func<Task> sendPromptAsync, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawProcessing = false;
        DateTime? observedIdleAt = null;
        var initialHistoryCount = 0;

        if (_copilotService.GetSession(sessionName) is { } initialSession)
        {
            lock (initialSession.HistoryLock)
            {
                initialHistoryCount = initialSession.History.Count;
            }
        }

        void Handler(string completedSessionName, string summary)
        {
            if (string.Equals(completedSessionName, sessionName, StringComparison.Ordinal))
                tcs.TrySetResult(summary);
        }

        void ClosedHandler(string closedSessionName)
        {
            if (string.Equals(closedSessionName, sessionName, StringComparison.Ordinal))
                tcs.TrySetResult(SessionClosedSummary);
        }

        _copilotService.OnSessionComplete += Handler;
        _copilotService.OnSessionClosed += ClosedHandler;
        try
        {
            var sendTask = sendPromptAsync();

            // CopilotService.SendPromptAsync sets IsProcessing=true synchronously before its first
            // await. Mark the turn as "seen processing" once dispatch begins so fast demo/local
            // responses can't slip from false→true→false entirely between polling intervals.
            sawProcessing = true;

            var deadline = DateTime.UtcNow + SessionCompletionTimeout;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (sendTask.IsFaulted || sendTask.IsCanceled)
                    await sendTask;

                if (tcs.Task.IsCompleted)
                {
                    await sendTask;
                    return await tcs.Task;
                }

                var session = _copilotService.GetSession(sessionName);
                if (session != null)
                {
                    if (session.IsProcessing)
                    {
                        sawProcessing = true;
                        observedIdleAt = null;
                    }
                    else if (sawProcessing)
                    {
                        if (HasAssistantMessageSince(session, initialHistoryCount))
                        {
                            await sendTask;
                            return tcs.Task.IsCompleted ? await tcs.Task : string.Empty;
                        }

                        observedIdleAt ??= DateTime.UtcNow;
                        if (DateTime.UtcNow - observedIdleAt.Value >= TimeSpan.FromSeconds(1))
                        {
                            await sendTask;
                            return tcs.Task.IsCompleted ? await tcs.Task : string.Empty;
                        }
                    }
                }
                else if (sawProcessing || sendTask.IsCompleted)
                {
                    await sendTask;
                    return tcs.Task.IsCompleted ? await tcs.Task : SessionClosedSummary;
                }

                await Task.Delay(250, cancellationToken);
            }

            throw new TimeoutException($"Timed out waiting for session '{sessionName}' to complete");
        }
        finally
        {
            _copilotService.OnSessionComplete -= Handler;
            _copilotService.OnSessionClosed -= ClosedHandler;
        }
    }

    private static bool HasAssistantMessageSince(AgentSessionInfo session, int initialHistoryCount)
    {
        lock (session.HistoryLock)
        {
            return session.History
                .Skip(Math.Min(initialHistoryCount, session.History.Count))
                .Any(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static bool IsSuccessfulCompletionSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return true;

        return !summary.StartsWith("[Error]", StringComparison.OrdinalIgnoreCase)
            && !summary.StartsWith("[Abort]", StringComparison.OrdinalIgnoreCase)
            && !summary.StartsWith("[Watchdog]", StringComparison.OrdinalIgnoreCase)
            && !summary.StartsWith("[Recovery] failed", StringComparison.OrdinalIgnoreCase)
            && !summary.StartsWith("[SteerError]", StringComparison.OrdinalIgnoreCase);
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
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _evaluationTimer?.Dispose();
            _evaluationTimer = null;
        }
        _disposeCts.Cancel();
        _disposeCts.Dispose();
        _copilotService.OnSessionClosed -= HandleSessionClosed;
    }
}
