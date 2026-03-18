using System.Text.Json;
using PolyPilot.Models;

namespace PolyPilot.Services;

/// <summary>
/// Scans ~/.copilot/session-state/ for Copilot CLI sessions NOT owned by PolyPilot
/// (i.e. not in active-sessions.json). Polls every 2 minutes and fires a callback
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
    private volatile IReadOnlyList<ExternalSessionInfo> _sessions = Array.Empty<ExternalSessionInfo>();
    private volatile bool _disposed;

    // Cache: sessionId -> (eventsFileMtime, parsedInfo)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset mtime, ExternalSessionInfo info)> _cache = new();

    private static readonly TimeSpan ActiveThreshold = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromHours(4);
    // MaxAge for Active/Idle sessions — show up to 4 hours of quiet time (same as IdleThreshold).
    // Ended sessions (session.shutdown, etc.) use a shorter 2-hour window so stale closed
    // sessions from hours ago don't clutter the panel.
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(4);
    private static readonly TimeSpan EndedMaxAge = TimeSpan.FromHours(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);

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
        var excludedPids = _getExcludedPids?.Invoke();
        var now = DateTimeOffset.UtcNow;
        var newSessions = new List<ExternalSessionInfo>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Process-first approach: find running copilot processes and map them to sessions.
        // This avoids scanning 3K+ directories — we only look at sessions with live processes.
        var liveSessionPids = DiscoverLiveSessionPids(ownedIds, excludedPids);

        foreach (var (sessionId, pid) in liveSessionPids)
        {
            var dir = Path.Combine(_sessionStatePath, sessionId);
            if (!Directory.Exists(dir)) continue;

            var eventsFile = Path.Combine(dir, "events.jsonl");
            var workspaceFile = Path.Combine(dir, "workspace.yaml");
            if (!File.Exists(eventsFile) || !File.Exists(workspaceFile)) continue;

            DateTimeOffset eventsMtime;
            try { eventsMtime = new DateTimeOffset(File.GetLastWriteTimeUtc(eventsFile), TimeSpan.Zero); }
            catch { continue; }

            seenIds.Add(sessionId);

            // Use cache if mtime hasn't changed
            if (_cache.TryGetValue(sessionId, out var cached) && cached.mtime == eventsMtime
                && cached.info.HasActiveLock)
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

            if (_isExcludedCwd != null && _isExcludedCwd(cwd)) continue;

            // Parse events.jsonl for history + last event type
            var (history, lastEventType) = ParseEventsFile(eventsFile);

            var displayName = string.IsNullOrEmpty(cwd)
                ? sessionId[..8]
                : Path.GetFileName(cwd.TrimEnd('/', '\\'));

            var needsAttention = ComputeNeedsAttention(history);

            string? gitBranch = null;
            if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd))
                gitBranch = TryGetGitBranch(cwd);

            var info = new ExternalSessionInfo
            {
                SessionId = sessionId,
                DisplayName = displayName,
                WorkingDirectory = cwd,
                GitBranch = gitBranch,
                Tier = ExternalSessionTier.Active, // Live process = always active
                LastEventType = lastEventType,
                LastEventTime = eventsMtime,
                History = history,
                NeedsAttention = needsAttention,
                ActiveLockPid = pid
            };

            _cache[sessionId] = (eventsMtime, info);
            newSessions.Add(info);
        }

        // Second pass: discover sessions via lock files for directories not found
        // by the process scan. Only checks cached dirs or dirs with recent mtime.
        // This handles copilot processes not discoverable via `ps -eo pid,args`
        // (e.g., different process naming conventions or test scenarios).
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_sessionStatePath))
            {
                var dirName = Path.GetFileName(dir);
                if (!Guid.TryParse(dirName, out _)) continue;
                if (seenIds.Contains(dirName)) continue;
                if (ownedIds.Contains(dirName)) continue;

                bool inCache = _cache.ContainsKey(dirName);
                if (!inCache)
                {
                    try
                    {
                        var dirMtime = new DateTimeOffset(Directory.GetLastWriteTimeUtc(dir), TimeSpan.Zero);
                        if (now - dirMtime > MaxAge) continue;
                    }
                    catch { continue; }
                }

                var lockPid = FindAnyLiveLockPid(dir, excludedPids);
                if (lockPid == null) continue;

                var evFile = Path.Combine(dir, "events.jsonl");
                var wsFile = Path.Combine(dir, "workspace.yaml");
                if (!File.Exists(evFile) || !File.Exists(wsFile)) continue;

                DateTimeOffset evMtime;
                try { evMtime = new DateTimeOffset(File.GetLastWriteTimeUtc(evFile), TimeSpan.Zero); }
                catch { continue; }

                seenIds.Add(dirName);

                if (_cache.TryGetValue(dirName, out var c2) && c2.mtime == evMtime
                    && c2.info.HasActiveLock)
                {
                    newSessions.Add(c2.info);
                    continue;
                }

                string? lockCwd = null;
                try
                {
                    foreach (var wline in File.ReadLines(wsFile).Take(10))
                    {
                        if (wline.StartsWith("cwd:", StringComparison.OrdinalIgnoreCase))
                        {
                            lockCwd = wline["cwd:".Length..].Trim().Trim('"', '\'');
                            break;
                        }
                    }
                }
                catch { }

                if (_isExcludedCwd != null && _isExcludedCwd(lockCwd)) continue;

                var (hist, lastEvt) = ParseEventsFile(evFile);

                var dName = string.IsNullOrEmpty(lockCwd)
                    ? dirName[..8]
                    : Path.GetFileName(lockCwd.TrimEnd('/', '\\'));

                var attn = ComputeNeedsAttention(hist);

                string? branch = null;
                if (!string.IsNullOrEmpty(lockCwd) && Directory.Exists(lockCwd))
                    branch = TryGetGitBranch(lockCwd);

                var lockInfo = new ExternalSessionInfo
                {
                    SessionId = dirName,
                    DisplayName = dName,
                    WorkingDirectory = lockCwd,
                    GitBranch = branch,
                    Tier = ExternalSessionTier.Active,
                    LastEventType = lastEvt,
                    LastEventTime = evMtime,
                    History = hist,
                    NeedsAttention = attn,
                    ActiveLockPid = lockPid
                };

                _cache[dirName] = (evMtime, lockInfo);
                newSessions.Add(lockInfo);
            }
        }
        catch { }

        // Evict cache entries for sessions no longer live
        var staleKeys = _cache.Keys.Where(k => !seenIds.Contains(k)).ToList();
        foreach (var k in staleKeys) _cache.TryRemove(k, out _);

        // Sort by recency
        newSessions.Sort((a, b) => b.LastEventTime.CompareTo(a.LastEventTime));

        var changed = !ExternalSessionListEquals(_sessions, newSessions);
        _sessions = newSessions;
        if (changed) OnChanged?.Invoke();
    }

    /// <summary>
    /// Find live copilot processes and extract their session IDs from command-line args
    /// (--resume=&lt;guid&gt;). Uses a single `ps` call instead of per-process spawning.
    /// This is O(1) shell command not O(directories) — runs in ~200ms instead of ~50s.
    /// </summary>
    private List<(string sessionId, int pid)> DiscoverLiveSessionPids(
        IReadOnlySet<string> ownedIds, IReadOnlySet<int>? excludedPids)
    {
        var results = new List<(string sessionId, int pid)>();
        var seenSessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Single ps call to get all process PIDs + args
            var psi = new System.Diagnostics.ProcessStartInfo("ps", "-eo pid,args")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return results;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            var guidPattern = new System.Text.RegularExpressions.Regex(
                @"--resume[=\s]+([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains("copilot", StringComparison.OrdinalIgnoreCase)) continue;

                var trimmed = line.TrimStart();
                var spaceIdx = trimmed.IndexOf(' ');
                if (spaceIdx <= 0) continue;
                if (!int.TryParse(trimmed[..spaceIdx], out var pid)) continue;
                if (excludedPids != null && excludedPids.Contains(pid)) continue;

                var match = guidPattern.Match(trimmed);
                if (!match.Success) continue;

                var sessionId = match.Groups[1].Value;
                if (!ownedIds.Contains(sessionId) && seenSessions.Add(sessionId))
                    results.Add((sessionId, pid));
            }
        }
        catch { }

        return results;
    }

    /// <summary>
    /// Parse events.jsonl, returning conversation history and the last event type seen.
    /// Opens with FileShare.ReadWrite to avoid IOException when the CLI is actively writing.
    /// Only reads the last ~32KB of the file for performance — external sessions with
    /// multi-MB event files would freeze the UI if parsed fully.
    /// </summary>
    internal static (List<ChatMessage> history, string? lastEventType) ParseEventsFile(string eventsFile)
    {
        var history = new List<ChatMessage>();
        string? lastEventType = null;
        const int TailBytes = 32 * 1024; // 32KB tail — enough for ~20-30 messages

        try
        {
            using var fs = new FileStream(eventsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // For large files, seek to the tail to avoid parsing multi-MB of events
            if (fs.Length > TailBytes)
                fs.Seek(-TailBytes, SeekOrigin.End);

            using var reader = new StreamReader(fs);

            // Skip the first partial line if we seeked into the middle
            if (fs.Position > 0)
                reader.ReadLine();

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
                        if (proc.HasExited) continue;
                        // Guard against PID recycling: only accept processes whose name
                        // plausibly belongs to a Copilot CLI or its host runtime.
                        var name = proc.ProcessName?.ToLowerInvariant() ?? "";
                        if (!name.Contains("copilot") && !name.Contains("node") &&
                            !name.Contains("dotnet") && !name.Contains("github"))
                            continue;
                        return pid;
                    }
                    catch { /* Process doesn't exist — stale lock */ }
                }
            }
        }
        catch { /* Directory not accessible */ }
        return null;
    }

    /// <summary>
    /// Check for any lock file with a live PID, without verifying the process name.
    /// Used in Scan() where the lock file's existence in copilot's session-state
    /// directory is sufficient evidence of a copilot-related process.
    /// </summary>
    private static int? FindAnyLiveLockPid(string sessionDir, IReadOnlySet<int>? excludedPids)
    {
        try
        {
            foreach (var file in Directory.GetFiles(sessionDir, "inuse.*.lock"))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                var parts = fileName.Split('.');
                if (parts.Length >= 2 && int.TryParse(parts[1], out var pid))
                {
                    if (excludedPids != null && excludedPids.Contains(pid)) continue;
                    try
                    {
                        using var proc = System.Diagnostics.Process.GetProcessById(pid);
                        if (proc.HasExited) continue;
                        // Guard against PID recycling — only accept processes that look like copilot
                        var name = proc.ProcessName?.ToLowerInvariant() ?? "";
                        if (!name.Contains("copilot") && !name.Contains("node") &&
                            !name.Contains("dotnet") && !name.Contains("github"))
                            continue;
                        return pid;
                    }
                    catch { /* PID doesn't exist — stale lock */ }
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
