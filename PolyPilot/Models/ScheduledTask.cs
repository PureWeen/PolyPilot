using System.Text.Json;
using System.Text.Json.Serialization;

namespace PolyPilot.Models;

/// <summary>
/// Defines the recurrence type for a scheduled task.
/// </summary>
public enum ScheduleType
{
    /// <summary>Run every N minutes.</summary>
    Interval,
    /// <summary>Run once daily at a specific time.</summary>
    Daily,
    /// <summary>Run on specific days of the week at a specific time.</summary>
    Weekly
}

/// <summary>
/// A single execution log entry for a scheduled task.
/// </summary>
public class ScheduledTaskRun
{
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? SessionName { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// A recurring task definition — prompt, schedule, and execution state.
/// Persisted to ~/.polypilot/scheduled-tasks.json.
/// </summary>
public class ScheduledTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Prompt { get; set; } = "";

    /// <summary>
    /// Target an existing session by name. If null, a new session is created for each run.
    /// </summary>
    public string? SessionName { get; set; }

    /// <summary>Model to use when creating a new session. Ignored when SessionName is set.</summary>
    public string? Model { get; set; }

    /// <summary>Working directory for newly created sessions.</summary>
    public string? WorkingDirectory { get; set; }

    public ScheduleType Schedule { get; set; } = ScheduleType.Daily;

    /// <summary>Interval in minutes — used when Schedule == Interval.</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>Time of day (local) — used when Schedule is Daily or Weekly.</summary>
    public string TimeOfDay { get; set; } = "09:00";

    /// <summary>Days of week — used when Schedule == Weekly. 0=Sunday..6=Saturday.</summary>
    public List<int> DaysOfWeek { get; set; } = new() { 1, 2, 3, 4, 5 }; // weekdays

    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastRunAt { get; set; }

    /// <summary>Recent execution history (kept to last 10 runs).</summary>
    public List<ScheduledTaskRun> RecentRuns { get; set; } = new();

    // ── Schedule calculation ──────────────────────────────────────────

    /// <summary>
    /// Parses the TimeOfDay string ("HH:mm") into hours and minutes.
    /// Returns (9, 0) as default if parsing fails.
    /// </summary>
    internal (int hours, int minutes) ParseTimeOfDay()
    {
        if (TimeSpan.TryParse(TimeOfDay, out var ts))
            return (ts.Hours, ts.Minutes);
        return (9, 0);
    }

    /// <summary>
    /// Calculates the next run time based on the schedule and the last run time.
    /// Returns null if the task cannot be scheduled (e.g., Weekly with no days selected).
    /// </summary>
    public DateTime? GetNextRunTimeUtc(DateTime now)
    {
        switch (Schedule)
        {
            case ScheduleType.Interval:
                if (IntervalMinutes <= 0) return null;
                if (LastRunAt == null) return now; // run immediately
                var next = LastRunAt.Value.AddMinutes(IntervalMinutes);
                return next <= now ? now : next;

            case ScheduleType.Daily:
            {
                var (h, m) = ParseTimeOfDay();
                var todayLocal = now.ToLocalTime().Date.AddHours(h).AddMinutes(m);
                var todayUtc = todayLocal.ToUniversalTime();
                if (LastRunAt == null)
                    return todayUtc; // never run — schedule for today's slot (may be in the past, that's fine — IsDue will fire)
                if (todayUtc > now && LastRunAt.Value.Date < now.ToLocalTime().Date)
                    return todayUtc;
                // Next day
                return todayLocal.AddDays(1).ToUniversalTime();
            }

            case ScheduleType.Weekly:
            {
                if (DaysOfWeek.Count == 0) return null;
                var (h, m) = ParseTimeOfDay();
                var localNow = now.ToLocalTime();
                // Look up to 8 days ahead to find the next matching day
                for (int i = 0; i <= 7; i++)
                {
                    var candidate = localNow.Date.AddDays(i).AddHours(h).AddMinutes(m);
                    var candidateUtc = candidate.ToUniversalTime();
                    if (candidateUtc <= now && i == 0) continue; // today's slot already passed
                    var dow = (int)candidate.DayOfWeek;
                    if (DaysOfWeek.Contains(dow))
                    {
                        // Ensure we haven't already run at this slot
                        if (LastRunAt != null && LastRunAt.Value >= candidateUtc) continue;
                        return candidateUtc;
                    }
                }
                return null;
            }

            default:
                return null;
        }
    }

    /// <summary>Returns true if the task is due to run now.</summary>
    public bool IsDue(DateTime utcNow)
    {
        if (!IsEnabled) return false;
        var next = GetNextRunTimeUtc(utcNow);
        return next != null && next.Value <= utcNow;
    }

    /// <summary>Adds a run entry and trims history to 10 entries.</summary>
    public void RecordRun(ScheduledTaskRun run)
    {
        RecentRuns.Add(run);
        if (RecentRuns.Count > 10)
            RecentRuns.RemoveRange(0, RecentRuns.Count - 10);
        LastRunAt = run.StartedAt;
    }

    /// <summary>Human-readable schedule description for the UI.</summary>
    [JsonIgnore]
    public string ScheduleDescription
    {
        get
        {
            return Schedule switch
            {
                ScheduleType.Interval => $"Every {IntervalMinutes} minute{(IntervalMinutes != 1 ? "s" : "")}",
                ScheduleType.Daily => $"Daily at {TimeOfDay}",
                ScheduleType.Weekly => $"Weekly ({FormatDays()}) at {TimeOfDay}",
                _ => "Unknown"
            };
        }
    }

    private string FormatDays()
    {
        var dayNames = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        var sorted = DaysOfWeek.Where(d => d >= 0 && d <= 6).OrderBy(d => d);
        return string.Join(", ", sorted.Select(d => dayNames[d]));
    }
}
