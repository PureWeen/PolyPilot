namespace PolyPilot.Models;

/// <summary>
/// How recently the session was active, used for display tiers in the UI.
/// </summary>
public enum ExternalSessionTier
{
    /// <summary>
    /// A live Copilot CLI process is connected (inuse.{PID}.lock with live PID), OR
    /// events.jsonl was written less than 2 minutes ago and the last event is not idle.
    /// Shown with animated status dot regardless of how long the session has been running.
    /// </summary>
    Active,
    /// <summary>
    /// No live lock file, but events.jsonl was written within the last 4 hours and the
    /// session did not send a shutdown event. The process may still be open in a terminal
    /// but idle (no recent messages).
    /// </summary>
    Idle,
    /// <summary>
    /// Process has definitively exited (session.shutdown event), or events.jsonl is
    /// older than 4 hours with no terminal event. Shown dimmed; hidden after 2 hours.
    /// </summary>
    Ended
}

/// <summary>
/// Represents a Copilot CLI session that exists in ~/.copilot/session-state/
/// but was NOT started by PolyPilot (not in active-sessions.json).
/// These are observed in read-only mode.
/// </summary>
public class ExternalSessionInfo
{
    public required string SessionId { get; init; }

    /// <summary>Display name derived from the working directory (last path segment).</summary>
    public required string DisplayName { get; init; }

    /// <summary>Working directory from workspace.yaml cwd field.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Git branch detected from the working directory (may be null if not a repo).</summary>
    public string? GitBranch { get; init; }

    /// <summary>Activity tier based on events.jsonl mtime and last event type.</summary>
    public ExternalSessionTier Tier { get; init; }

    /// <summary>
    /// True when the session appears actively running:
    /// events.jsonl was written less than 2 minutes ago AND last event is not session.idle/assistant.turn_end.
    /// Equivalent to Tier == Active.
    /// </summary>
    public bool IsActive => Tier == ExternalSessionTier.Active;

    /// <summary>Last event type seen in events.jsonl (e.g. "session.idle", "assistant.message").</summary>
    public string? LastEventType { get; init; }

    /// <summary>File modification time of events.jsonl — used as a proxy for "last activity".</summary>
    public DateTimeOffset LastEventTime { get; init; }

    /// <summary>Parsed conversation history from events.jsonl.</summary>
    public List<ChatMessage> History { get; init; } = new();

    /// <summary>
    /// True when the session appears to be asking the user a question
    /// (last assistant message ends with ? or contains question phrases).
    /// Computed from History using the same logic as AgentSessionInfo.NeedsAttention.
    /// </summary>
    public bool NeedsAttention { get; init; }

    /// <summary>
    /// PID of the live CLI process holding an inuse.{PID}.lock file, or null if no lock is active.
    /// Used to focus the terminal window running the CLI.
    /// </summary>
    public int? ActiveLockPid { get; init; }

    /// <summary>
    /// True when the session directory contains an inuse.{PID}.lock file for a still-running process.
    /// This is the definitive signal that a CLI process is actively connected to this session.
    /// </summary>
    public bool HasActiveLock => ActiveLockPid.HasValue;
}
