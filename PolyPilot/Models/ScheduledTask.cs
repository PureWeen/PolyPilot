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
    Weekly,
    /// <summary>Run on a cron schedule (5-field: min hour dom month dow).</summary>
    Cron
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

    /// <summary>Time of day (local) — used when Schedule is Daily or Weekly. Must be "HH:mm" format.</summary>
    public string TimeOfDay { get; set; } = "09:00";

    /// <summary>Days of week — used when Schedule == Weekly. 0=Sunday..6=Saturday.</summary>
    public List<int> DaysOfWeek { get; set; } = new() { 1, 2, 3, 4, 5 }; // weekdays

    /// <summary>
    /// Optional cron expression (5-field: min hour dom month dow).
    /// Used when Schedule == Cron. Examples: "0 9 * * 1-5" = weekdays at 9am.
    /// </summary>
    public string? CronExpression { get; set; }

    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>
    /// UTC timestamp of the most recent execution attempt (successful or failed).
    /// Daily/Weekly scheduling uses the most recent successful run from RecentRuns
    /// so a failure does not suppress the rest of today's schedule.
    /// </summary>
    public DateTime? LastRunAt { get; set; }

    /// <summary>Recent execution history (kept to last 10 runs).</summary>
    public List<ScheduledTaskRun> RecentRuns { get; set; } = new();

    // ── Validation ──────────────────────────────────────────────

    /// <summary>Returns true if TimeOfDay is a valid "HH:mm" string.</summary>
    public static bool IsValidTimeOfDay(string? time)
        => !string.IsNullOrEmpty(time) && TimeSpan.TryParse(time, out var ts) && ts.TotalHours >= 0 && ts.TotalHours < 24;

    // ── Schedule calculation ──────────────────────────────────────────

    /// <summary>
    /// Parses the TimeOfDay string ("HH:mm") into hours and minutes.
    /// Returns (9, 0) as default if parsing fails.
    /// </summary>
    internal (int hours, int minutes) ParseTimeOfDay()
    {
        if (TimeSpan.TryParse(TimeOfDay, out var ts) && ts.TotalHours >= 0 && ts.TotalHours < 24)
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
                if (next <= now)
                {
                    // Task was missed. Return the next interval boundary from LastRunAt.
                    // This will be <= now, so IsDue() returns true and the task fires once.
                    // After RecordRun updates LastRunAt, the next call computes a future time.
                    var elapsed = (now - LastRunAt.Value).TotalMinutes;
                    var periods = (int)Math.Floor(elapsed / IntervalMinutes);
                    next = LastRunAt.Value.AddMinutes(periods * IntervalMinutes);
                    if (next <= LastRunAt.Value) next = next.AddMinutes(IntervalMinutes);
                }
                return next;

            case ScheduleType.Daily:
            {
                var (h, m) = ParseTimeOfDay();
                var localNow = now.ToLocalTime();
                var todaySlot = localNow.Date.AddHours(h).AddMinutes(m);
                var todaySlotUtc = todaySlot.ToUniversalTime();
                var lastSuccessfulLocal = GetLastSuccessfulRunAt()?.ToLocalTime();

                if (lastSuccessfulLocal == null)
                    return todaySlotUtc; // never run — schedule for today's slot

                // Compare dates in local time consistently
                if (lastSuccessfulLocal.Value.Date < localNow.Date && todaySlotUtc > now)
                    return todaySlotUtc; // haven't run today and slot is still ahead
                if (lastSuccessfulLocal.Value.Date < localNow.Date && todaySlotUtc <= now)
                    return todaySlotUtc; // haven't run today, slot passed — fire now

                // Already completed successfully today — next day
                return todaySlot.AddDays(1).ToUniversalTime();
            }

            case ScheduleType.Weekly:
            {
                if (DaysOfWeek.Count == 0) return null;
                var (h, m) = ParseTimeOfDay();
                var localNow = now.ToLocalTime();
                var lastSuccessfulRunAt = GetLastSuccessfulRunAt();
                // Look up to 8 days ahead to find the next matching day
                for (int i = 0; i <= 7; i++)
                {
                    var candidate = localNow.Date.AddDays(i).AddHours(h).AddMinutes(m);
                    var candidateUtc = candidate.ToUniversalTime();
                    if (candidateUtc <= now && i == 0)
                    {
                        // Today's slot time has passed. Only skip if we already ran today
                        // (mirrors Daily logic). If LastRunAt is before today, the task missed
                        // its slot today and should still be treated as due.
                        if (lastSuccessfulRunAt.HasValue && lastSuccessfulRunAt.Value.ToLocalTime().Date >= localNow.Date)
                            continue;
                        // Slot passed but not yet run today — fall through to check the day
                    }
                    var dow = (int)candidate.DayOfWeek;
                    if (DaysOfWeek.Contains(dow))
                    {
                        // Ensure we haven't already completed successfully at this slot.
                        if (lastSuccessfulRunAt != null && lastSuccessfulRunAt.Value >= candidateUtc) continue;
                        return candidateUtc;
                    }
                }
                return null;
            }

            case ScheduleType.Cron:
                return GetNextCronTimeUtc(now);

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

    private DateTime? GetLastSuccessfulRunAt()
    {
        for (int i = RecentRuns.Count - 1; i >= 0; i--)
        {
            if (RecentRuns[i].Success)
                return RecentRuns[i].StartedAt;
        }

        // Backward compatibility for older persisted tasks that predate RecentRuns history.
        return RecentRuns.Count == 0 ? LastRunAt : null;
    }

    /// <summary>
    /// Returns a deep copy of this task, including all schedule fields and run history.
    /// Used so callers (UI, tests) get an independent snapshot that cannot race with
    /// the background timer mutating <see cref="RecentRuns"/> or <see cref="LastRunAt"/>.
    /// </summary>
    public ScheduledTask Clone() => new ScheduledTask
    {
        Id = Id,
        Name = Name,
        Prompt = Prompt,
        SessionName = SessionName,
        Model = Model,
        WorkingDirectory = WorkingDirectory,
        Schedule = Schedule,
        IntervalMinutes = IntervalMinutes,
        TimeOfDay = TimeOfDay,
        DaysOfWeek = DaysOfWeek.ToList(),
        CronExpression = CronExpression,
        IsEnabled = IsEnabled,
        CreatedAt = CreatedAt,
        LastRunAt = LastRunAt,
        RecentRuns = RecentRuns.Select(r => new ScheduledTaskRun
        {
            StartedAt = r.StartedAt,
            CompletedAt = r.CompletedAt,
            SessionName = r.SessionName,
            Success = r.Success,
            Error = r.Error
        }).ToList()
    };

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
                ScheduleType.Cron => $"Cron: {CronExpression ?? "(not set)"}",
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

    // ── Cron expression support ──────────────────────────────────────

    /// <summary>
    /// Simple 5-field cron parser: minute hour day-of-month month day-of-week.
    /// Supports: numbers, ranges (1-5), lists (1,3,5), step values (*/5), and wildcards (*).
    /// Follows POSIX cron semantics: when both day-of-month and day-of-week are non-wildcard,
    /// they are OR'd (fire if either matches). When only one is specified, the other is ignored.
    /// </summary>
    internal static bool TryParseCron(string? expression, out CronSchedule schedule)
    {
        schedule = default;
        if (string.IsNullOrWhiteSpace(expression)) return false;

        var parts = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return false;

        if (!TryParseField(parts[0], 0, 59, out var minutes)) return false;
        if (!TryParseField(parts[1], 0, 23, out var hours)) return false;
        if (!TryParseField(parts[2], 1, 31, out var doms)) return false;
        if (!TryParseField(parts[3], 1, 12, out var months)) return false;
        if (!TryParseField(parts[4], 0, 6, out var dows)) return false;

        // Track whether DOM/DOW were wildcards for POSIX OR semantics
        var domIsWildcard = parts[2].Trim() == "*";
        var dowIsWildcard = parts[4].Trim() == "*";

        schedule = new CronSchedule(minutes, hours, doms, months, dows, domIsWildcard, dowIsWildcard);
        return true;
    }

    /// <summary>Validates whether a cron expression is syntactically valid.</summary>
    public static bool IsValidCronExpression(string? expression)
        => TryParseCron(expression, out _);

    private static bool TryParseField(string field, int min, int max, out HashSet<int> values)
    {
        values = new HashSet<int>();
        foreach (var part in field.Split(','))
        {
            var item = part.Trim();
            if (item == "*")
            {
                for (int i = min; i <= max; i++) values.Add(i);
            }
            else if (item.Contains('/'))
            {
                var stepParts = item.Split('/');
                if (stepParts.Length != 2 || !int.TryParse(stepParts[1], out var step) || step <= 0) return false;
                int start = min;
                if (stepParts[0] != "*")
                {
                    if (!int.TryParse(stepParts[0], out start) || start < min || start > max) return false;
                }
                for (int i = start; i <= max; i += step) values.Add(i);
            }
            else if (item.Contains('-'))
            {
                var rangeParts = item.Split('-');
                if (rangeParts.Length != 2) return false;
                if (!int.TryParse(rangeParts[0], out var lo) || !int.TryParse(rangeParts[1], out var hi)) return false;
                if (lo < min || hi > max || lo > hi) return false;
                for (int i = lo; i <= hi; i++) values.Add(i);
            }
            else
            {
                if (!int.TryParse(item, out var val) || val < min || val > max) return false;
                values.Add(val);
            }
        }
        return values.Count > 0;
    }

    /// <summary>Calculate the next cron fire time after 'now' in UTC.</summary>
    private DateTime? GetNextCronTimeUtc(DateTime now)
    {
        if (!TryParseCron(CronExpression, out var cron)) return null;

        var local = now.ToLocalTime();
        // Start from the current minute. LastRunAt prevents re-firing within the same minute.
        var candidate = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Local);

        // Search up to 366 days ahead
        for (int i = 0; i < 366 * 24 * 60; i++)
        {
            if (cron.Months.Contains(candidate.Month) &&
                cron.MatchesDay(candidate.Day, (int)candidate.DayOfWeek) &&
                cron.Hours.Contains(candidate.Hour) &&
                cron.Minutes.Contains(candidate.Minute))
            {
                var utc = candidate.ToUniversalTime();
                // Skip if we already ran at this exact slot
                if (LastRunAt != null && LastRunAt.Value >= utc)
                {
                    candidate = candidate.AddMinutes(1);
                    continue;
                }
                return utc;
            }
            candidate = candidate.AddMinutes(1);
        }
        return null; // no match within a year
    }

    internal readonly record struct CronSchedule(
        HashSet<int> Minutes, HashSet<int> Hours,
        HashSet<int> DaysOfMonth, HashSet<int> Months,
        HashSet<int> DaysOfWeek,
        bool DomIsWildcard, bool DowIsWildcard)
    {
        /// <summary>
        /// POSIX cron day-matching: when both DOM and DOW are explicit (non-wildcard),
        /// they are OR'd — fire if EITHER matches. When only one is explicit, match that one only.
        /// When both are wildcards, any day matches.
        /// </summary>
        public bool MatchesDay(int dayOfMonth, int dayOfWeek)
        {
            bool domMatch = DaysOfMonth.Contains(dayOfMonth);
            bool dowMatch = DaysOfWeek.Contains(dayOfWeek);

            if (!DomIsWildcard && !DowIsWildcard)
                return domMatch || dowMatch; // POSIX OR semantics
            return domMatch && dowMatch; // one or both are wildcards → AND (wildcard always matches)
        }
    }
}
