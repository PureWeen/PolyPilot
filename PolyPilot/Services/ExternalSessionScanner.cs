using System.Text.Json;
using PolyPilot.Models;

namespace PolyPilot.Services;

/// <summary>
/// Scans ~/.copilot/session-state/ for Copilot CLI sessions NOT owned by PolyPilot
/// (i.e. not in active-sessions.json). Polls every 15 seconds and fires a callback
/// when the external session list changes. Desktop-only (skips scan on mobile/remote).
/// </summary>
public class ExternalSessionScanner : IDisposable
{
    private readonly string _sessionStatePath;
    private readonly Func<IReadOnlySet<string>> _getOwnedSessionIds;
    // Optional: extra CWD-based exclusion (e.g., filter sessions inside PolyPilot's own directories)
    private readonly Func<string?, bool>? _isExcludedCwd;
    // Optional: PIDs to exclude from lock file detection (e.g., PolyPilot's own persistent server)
    private readonly Func<IReadOnlySet<int>>? _getExcludedPids;

    private Timer? _pollTimer;
    private IReadOnlyList<ExternalSessionInfo> _sessions = Array.Empty<ExternalSessionInfo>();
    private bool _disposed;

    // Cache: sessionId -> (eventsFileMtime, parsedInfo)
    private readonly Dictionary<string, (DateTimeOffset mtime, ExternalSessionInfo info)> _cache = new();

    private static readonly TimeSpan ActiveThreshold = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromHours(4);
    // MaxAge for Active/Idle sessions — show up to 4 hours of quiet time (same as IdleThreshold).
    // Ended sessions (session.shutdown, etc.) use a shorter 2-hour window so stale closed
    // sessions from hours ago don't clutter the panel.
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(4);
    private static readonly TimeSpan EndedMaxAge = TimeSpan.FromHours(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private static readonly string[] _questionPhrases = AgentSessionInfo.QuestionPhrases;

    // Event types that indicate the session is paused but the process may still be alive.
    private static readonly string[] _inactiveEventTypes =
    [
        "session.idle", "assistant.turn_end"
    ];

    // Event types that indicate the process has definitively exited — always Ended tier.
    private static readonly HashSet<string> _terminalEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "session.shutdown", "session.exited", "session.exit"
    };

    public event Action? OnChanged;

    public IReadOnlyList<ExternalSessionInfo> Sessions => _sessions;

    public ExternalSessionScanner(string sessionStatePath, Func<IReadOnlySet<string>> getOwnedSessionIds,
        Func<string?, bool>? isExcludedCwd = null, Func<IReadOnlySet<int>>? getExcludedPids = null)
    {
        _sessionStatePath = sessionStatePath;
        _getOwnedSessionIds = getOwnedSessionIds;
        _isExcludedCwd = isExcludedCwd;
        _getExcludedPids = getExcludedPids;
    }

    public void Start()
    {
        if (_disposed) return;
        // Create timer paused, assign to field, THEN arm it.
        // This avoids a race where the callback fires before _pollTimer is assigned,
        // which would skip the re-arm and kill the poll loop forever.
        _pollTimer = new Timer(_ => { SafeScan(); RearmTimer(); },
            null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _pollTimer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private void RearmTimer()
    {
        try { _pollTimer?.Change(PollInterval, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* Timer disposed during callback — expected on shutdown */ }
    }

    public void Stop()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private void SafeScan()
    {
        try { Scan(); }
        catch { /* Never crash the poll thread */ }
    }

    internal void Scan()
    {
        if (!Directory.Exists(_sessionStatePath))
        {
            if (_sessions.Count > 0)
            {
                _sessions = Array.Empty<ExternalSessionInfo>();
                OnChanged?.Invoke();
            }
            return;
        }

        var ownedIds = _getOwnedSessionIds();
        var now = DateTimeOffset.UtcNow;
        var newSessions = new List<ExternalSessionInfo>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var dirs = Directory.GetDirectories(_sessionStatePath);
        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (!Guid.TryParse(name, out _)) continue;
            if (ownedIds.Contains(name)) continue;

            var eventsFile = Path.Combine(dir, "events.jsonl");
            var workspaceFile = Path.Combine(dir, "workspace.yaml");
            if (!File.Exists(eventsFile) || !File.Exists(workspaceFile)) continue;

            DateTimeOffset eventsMtime;
            try { eventsMtime = new DateTimeOffset(File.GetLastWriteTimeUtc(eventsFile), TimeSpan.Zero); }
            catch { continue; }

            // Check if a live CLI process is connected via inuse.{PID}.lock files.
            // This is the most reliable signal — it means someone has this session open RIGHT NOW.
            int? activeLockPid = FindActiveLockPid(dir);
            bool hasActiveLock = activeLockPid.HasValue;

            // Age filter: skip sessions older than MaxAge UNLESS an active lock file is present
            // (the CLI may be idle — no new events — but still connected)
            if (!hasActiveLock && now - eventsMtime > MaxAge) continue;

            seenIds.Add(name);

            // Use cache if mtime hasn't changed AND lock status hasn't changed
            if (_cache.TryGetValue(name, out var cached) && cached.mtime == eventsMtime
                && cached.info.HasActiveLock == hasActiveLock)
            {
                newSessions.Add(cached.info);
                continue;
            }

            // Parse workspace.yaml for cwd
            string? cwd = null;
            try
            {
                foreach (var line in File.ReadLines(workspaceFile).Take(10))
                {
                    if (line.StartsWith("cwd:", StringComparison.OrdinalIgnoreCase))
                    {
                        cwd = line["cwd:".Length..].Trim().Trim('"', '\'');
                        break;
                    }
                }
            }
            catch { }

            // CWD-based exclusion: sessions inside ~/.polypilot/ are PolyPilot worker sessions.
            // HOWEVER, if a live CLI process has the session open (active lock file), always show it —
            // the user may be running their CLI from a worktree directory.
            if (!hasActiveLock && _isExcludedCwd != null && _isExcludedCwd(cwd)) continue;

            // Parse events.jsonl for history + last event type
            var (history, lastEventType) = ParseEventsFile(eventsFile);

            var age = now - eventsMtime;
            ExternalSessionTier tier;
            if (hasActiveLock)
            {
                // A live process is connected — session is Active regardless of events.jsonl age/events.
                // The CLI may have resumed an old session and be idle waiting for user input.
                tier = ExternalSessionTier.Active;
            }
            else if (!string.IsNullOrEmpty(lastEventType) && _terminalEventTypes.Contains(lastEventType))
                tier = ExternalSessionTier.Ended; // process definitively exited
            else if (age < ActiveThreshold && !_inactiveEventTypes.Contains(lastEventType))
                tier = ExternalSessionTier.Active;
            else if (age < IdleThreshold)
                tier = ExternalSessionTier.Idle;
            else
                tier = ExternalSessionTier.Ended;

            // Ended sessions older than EndedMaxAge are not worth showing — they're stale history,
            // not recently-closed sessions worth resuming.
            if (tier == ExternalSessionTier.Ended && age > EndedMaxAge) continue;

            var displayName= string.IsNullOrEmpty(cwd)
                ? name[..8] // short UUID fallback
                : Path.GetFileName(cwd.TrimEnd('/', '\\'));

            var needsAttention = ComputeNeedsAttention(history);

            string? gitBranch = null;
            if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd))
                gitBranch = TryGetGitBranch(cwd);

            var info = new ExternalSessionInfo
            {
                SessionId = name,
                DisplayName = displayName,
                WorkingDirectory = cwd,
                GitBranch = gitBranch,
                Tier = tier,
                LastEventType = lastEventType,
                LastEventTime = eventsMtime,
                History = history,
                NeedsAttention = needsAttention,
                ActiveLockPid = activeLockPid
            };

            _cache[name] = (eventsMtime, info);
            newSessions.Add(info);
        }

        // Evict cache entries for directories that no longer exist
        var staleKeys = _cache.Keys.Where(k => !seenIds.Contains(k)).ToList();
        foreach (var k in staleKeys) _cache.Remove(k);

        // Sort: active first, then idle, then ended; within tier sort by recency
        newSessions.Sort((a, b) =>
        {
            var tierCmp = a.Tier.CompareTo(b.Tier);
            if (tierCmp != 0) return tierCmp;
            return b.LastEventTime.CompareTo(a.LastEventTime);
        });

        var changed = !ExternalSessionListEquals(_sessions, newSessions);
        _sessions = newSessions;
        if (changed) OnChanged?.Invoke();
    }

    /// <summary>
    /// Parse events.jsonl, returning conversation history and the last event type seen.
    /// Opens with FileShare.ReadWrite to avoid IOException when the CLI is actively writing.
    /// </summary>
    internal static (List<ChatMessage> history, string? lastEventType) ParseEventsFile(string eventsFile)
    {
        var history = new List<ChatMessage>();
        string? lastEventType = null;

        try
        {
            using var fs = new FileStream(eventsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("type", out var typeEl)) continue;
                    var type = typeEl.GetString();
                    lastEventType = type;

                    if (!root.TryGetProperty("data", out var data)) continue;

                    var timestamp = DateTime.Now;
                    if (root.TryGetProperty("timestamp", out var tsEl))
                        DateTime.TryParse(tsEl.GetString(), out timestamp);

                    switch (type)
                    {
                        case "user.message":
                            if (data.TryGetProperty("content", out var uc))
                            {
                                var content = uc.GetString();
                                if (!string.IsNullOrEmpty(content))
                                {
                                    var msg = ChatMessage.UserMessage(content);
                                    msg.Timestamp = timestamp;
                                    history.Add(msg);
                                }
                            }
                            break;

                        case "assistant.message":
                            if (data.TryGetProperty("content", out var ac))
                            {
                                var content = ac.GetString()?.Trim();
                                if (!string.IsNullOrEmpty(content))
                                {
                                    var msg = ChatMessage.AssistantMessage(content);
                                    msg.Timestamp = timestamp;
                                    history.Add(msg);
                                }
                            }
                            break;
                    }
                }
                catch { /* skip malformed lines */ }
            }
        }
        catch { /* file not readable */ }

        return (history, lastEventType);
    }

    private static bool ComputeNeedsAttention(List<ChatMessage> history)
    {
        // Active sessions CAN be waiting for user input — e.g., when the last event
        // was assistant.message (with a question) or session.idle. The last-message
        // heuristic works the same for both active and idle sessions.
        var last = history.LastOrDefault(m =>
            (m.IsUser || m.IsAssistant) &&
            m.MessageType is ChatMessageType.User or ChatMessageType.Assistant);
        if (last == null || !last.IsAssistant || string.IsNullOrEmpty(last.Content)) return false;
        if (last.Content.TrimEnd().EndsWith('?')) return true;
        var lower = last.Content.ToLowerInvariant();
        foreach (var phrase in _questionPhrases)
            if (lower.Contains(phrase)) return true;
        return false;
    }

    private static string? TryGetGitBranch(string dir)
    {
        try
        {
            var gitPath = Path.Combine(dir, ".git");

            string headFile;
            if (File.Exists(gitPath))
            {
                // Git worktree: .git is a file containing "gitdir: /absolute/path/to/worktree"
                var gitFileContent = File.ReadAllText(gitPath).Trim();
                const string gitdirPrefix = "gitdir:";
                if (!gitFileContent.StartsWith(gitdirPrefix, StringComparison.OrdinalIgnoreCase))
                    return null;
                var worktreeGitDir = gitFileContent[gitdirPrefix.Length..].Trim();
                if (!Path.IsPathRooted(worktreeGitDir))
                    worktreeGitDir = Path.Combine(dir, worktreeGitDir);
                headFile = Path.Combine(worktreeGitDir, "HEAD");
            }
            else if (Directory.Exists(gitPath))
            {
                // Normal repo: .git is a directory
                headFile = Path.Combine(gitPath, "HEAD");
            }
            else
            {
                return null;
            }

            if (!File.Exists(headFile)) return null;
            var head = File.ReadAllText(headFile).Trim();
            const string refPrefix = "ref: refs/heads/";
            return head.StartsWith(refPrefix) ? head[refPrefix.Length..] : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Find an active inuse.{PID}.lock file in a session directory and return the PID
    /// of the live CLI process, or null if no active lock exists.
    /// The Copilot CLI creates these files when it connects to a session.
    /// </summary>
    internal int? FindActiveLockPid(string sessionDir)
    {
        var excludedPids = _getExcludedPids?.Invoke();
        try
        {
            foreach (var file in Directory.GetFiles(sessionDir, "inuse.*.lock"))
            {
                var fileName = Path.GetFileNameWithoutExtension(file); // "inuse.12345"
                var parts = fileName.Split('.');
                if (parts.Length >= 2 && int.TryParse(parts[1], out var pid))
                {
                    if (excludedPids != null && excludedPids.Contains(pid)) continue;
                    try
                    {
                        using var proc = System.Diagnostics.Process.GetProcessById(pid);
                        if (!proc.HasExited) return pid;
                    }
                    catch { /* Process doesn't exist — stale lock */ }
                }
            }
        }
        catch { /* Directory not accessible */ }
        return null;
    }

    private static bool ExternalSessionListEquals(
        IReadOnlyList<ExternalSessionInfo> a, IReadOnlyList<ExternalSessionInfo> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var ai = a[i];
            var bi = b[i];
            if (ai.SessionId != bi.SessionId ||
                ai.Tier != bi.Tier ||
                ai.NeedsAttention != bi.NeedsAttention ||
                ai.HasActiveLock != bi.HasActiveLock ||
                ai.LastEventTime != bi.LastEventTime ||
                ai.History.Count != bi.History.Count)
                return false;
        }
        return true;
    }
}
