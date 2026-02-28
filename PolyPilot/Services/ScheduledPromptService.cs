using System.Text.Json;
using PolyPilot.Models;

namespace PolyPilot.Services;

/// <summary>
/// Manages scheduled prompts: persists them, runs a background timer that fires
/// due prompts into their target Copilot sessions, and notifies when results arrive.
/// </summary>
public class ScheduledPromptService : IDisposable
{
    private static readonly object _pathLock = new();
    private static string? _storePath;
    private static string StorePath
    {
        get
        {
            lock (_pathLock)
            {
                if (_storePath == null)
                    _storePath = Path.Combine(GetPolyPilotDir(), "scheduled-prompts.json");
                return _storePath;
            }
        }
    }

    /// <summary>Override the store path for tests.</summary>
    internal static void SetStorePathForTesting(string path) { lock (_pathLock) _storePath = path; }

    private static string GetPolyPilotDir()
    {
#if IOS || ANDROID
        try { return Path.Combine(FileSystem.AppDataDirectory, ".polypilot"); }
        catch
        {
            var fallback = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(fallback)) fallback = Path.GetTempPath();
            return Path.Combine(fallback, ".polypilot");
        }
#else
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(home, ".polypilot");
#endif
    }

    private readonly CopilotService _copilot;
    private readonly INotificationManagerService? _notifications;
    private readonly List<ScheduledPrompt> _prompts = new();
    private readonly object _lock = new();
    private Timer? _timer;
    private int _firing; // Interlocked flag to prevent concurrent firings

    public event Action? OnStateChanged;

    public ScheduledPromptService(CopilotService copilot, INotificationManagerService? notifications = null)
    {
        _copilot = copilot;
        _notifications = notifications;
        Load();
    }

    /// <summary>Snapshot of all scheduled prompts (safe to iterate on the UI thread).</summary>
    public IReadOnlyList<ScheduledPrompt> Prompts
    {
        get { lock (_lock) return _prompts.ToList(); }
    }

    /// <summary>Starts the background timer that checks for due prompts every minute.</summary>
    public void Start()
    {
        _timer?.Dispose();
        _timer = new Timer(CheckDuePrompts, null, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Adds a new scheduled prompt and saves.
    /// If <paramref name="runAt"/> is null, schedules for immediate execution.
    /// </summary>
    public ScheduledPrompt Add(string sessionName, string prompt, string label = "",
        DateTime? runAt = null, int repeatIntervalMinutes = 0)
    {
        var sp = new ScheduledPrompt
        {
            SessionName = sessionName,
            Prompt = prompt,
            Label = label,
            NextRunAt = runAt ?? DateTime.UtcNow,
            RepeatIntervalMinutes = repeatIntervalMinutes,
            IsEnabled = true
        };
        lock (_lock) _prompts.Add(sp);
        Save();
        NotifyChanged();
        return sp;
    }

    /// <summary>Removes a scheduled prompt by ID and saves.</summary>
    public bool Remove(string id)
    {
        bool removed;
        lock (_lock) removed = _prompts.RemoveAll(p => p.Id == id) > 0;
        if (!removed) return false;
        Save();
        NotifyChanged();
        return true;
    }

    /// <summary>Enables or disables a scheduled prompt by ID and saves.</summary>
    public bool SetEnabled(string id, bool enabled)
    {
        ScheduledPrompt? target;
        lock (_lock) target = _prompts.FirstOrDefault(p => p.Id == id);
        if (target == null) return false;
        target.IsEnabled = enabled;
        Save();
        NotifyChanged();
        return true;
    }

    // ---- Background execution ----

    private void CheckDuePrompts(object? state)
    {
        if (Interlocked.CompareExchange(ref _firing, 1, 0) != 0) return;
        _ = CheckDuePromptsAsync();
    }

    private async Task CheckDuePromptsAsync()
    {
        try
        {
            List<ScheduledPrompt> due;
            lock (_lock)
                due = _prompts
                    .Where(p => p.IsEnabled && p.NextRunAt.HasValue && p.NextRunAt.Value <= DateTime.UtcNow)
                    .ToList();

            foreach (var sp in due)
                await FirePromptAsync(sp);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ScheduledPrompt] Background check error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _firing, 0);
        }
    }

    internal async Task FirePromptAsync(ScheduledPrompt sp)
    {
        try
        {
            sp.LastRunAt = DateTime.UtcNow;

            // Send the prompt to the target session
            var session = _copilot.GetSession(sp.SessionName);
            if (session == null)
            {
                // Session gone — keep enabled so it retries on next check
                Console.WriteLine($"[ScheduledPrompt] Session '{sp.SessionName}' not found; will retry.");
                return;
            }

            // Advance or disable now that we've confirmed the session exists
            if (sp.RepeatIntervalMinutes > 0)
                sp.NextRunAt = DateTime.UtcNow.AddMinutes(sp.RepeatIntervalMinutes);
            else
            {
                sp.NextRunAt = null;
                sp.IsEnabled = false;
            }

            Save();
            NotifyChanged();

            if (session.IsProcessing)
            {
                // Session busy — queue for when it's ready
                _copilot.EnqueueMessage(sp.SessionName, sp.Prompt);
                return;
            }

            var result = await _copilot.SendPromptAsync(sp.SessionName, sp.Prompt);

            // Notify user the result is ready
            if (_notifications != null && _notifications.HasPermission)
            {
                var bodyPreview = result.Length > 80 ? result[..77] + "…" : result;
                var cleanBody = bodyPreview.Replace("\n", " ").Replace("\r", "").Trim();
                await _notifications.SendNotificationAsync(
                    $"📅 {sp.DisplayName}",
                    string.IsNullOrWhiteSpace(cleanBody) ? "Scheduled prompt complete" : cleanBody,
                    session.SessionId);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ScheduledPrompt] Error firing '{sp.DisplayName}': {ex.Message}");
        }
    }

    // ---- Persistence ----

    private void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var json = File.ReadAllText(StorePath);
            var loaded = JsonSerializer.Deserialize<List<ScheduledPrompt>>(json);
            if (loaded == null) return;
            lock (_lock)
            {
                _prompts.Clear();
                _prompts.AddRange(loaded);
            }
        }
        catch { }
    }

    internal void Save()
    {
        try
        {
            List<ScheduledPrompt> snapshot;
            lock (_lock) snapshot = _prompts.ToList();
            var dir = Path.GetDirectoryName(StorePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StorePath, json);
        }
        catch { }
    }

    private void NotifyChanged() => OnStateChanged?.Invoke();

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        GC.SuppressFinalize(this);
    }
}
