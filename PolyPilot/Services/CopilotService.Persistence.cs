using System.Text.Json;
using PolyPilot.Models;

namespace PolyPilot.Services;

public partial class CopilotService
{
    /// <summary>
    /// Save active session list to disk so we can restore on relaunch.
    /// Uses a 2-second debounce — multiple calls within the window coalesce into a single write.
    /// Snapshots session data on the caller's thread for thread safety.
    /// </summary>
    private void SaveActiveSessionsToDisk()
    {
        if (IsDemoMode || IsRestoring) return;
        // Snapshot session metas for thread-safe read (this may run on timer callback thread)
        var sessionMetas = SnapshotSessionMetas();
        List<ActiveSessionEntry> entries;
        try
        {
            entries = _sessions.Values
                .Where(s => s.Info.SessionId != null && !s.Info.IsHidden)
                .Select(s => new ActiveSessionEntry
                {
                    SessionId = s.Info.SessionId!,
                    DisplayName = s.Info.Name,
                    Model = s.Info.Model,
                    WorkingDirectory = s.Info.WorkingDirectory,
                    GroupId = sessionMetas.FirstOrDefault(m => m.SessionName == s.Info.Name)?.GroupId,
                    LastPrompt = s.Info.IsProcessing
                        ? s.Info.History.LastOrDefault(m => m.IsUser)?.Content
                        : null,
                    TotalInputTokens = s.Info.TotalInputTokens,
                    TotalOutputTokens = s.Info.TotalOutputTokens,
                    ContextCurrentTokens = s.Info.ContextCurrentTokens,
                    ContextTokenLimit = s.Info.ContextTokenLimit,
                    PremiumRequestsUsed = s.Info.PremiumRequestsUsed,
                    TotalApiTimeSeconds = s.Info.TotalApiTimeSeconds,
                    CreatedAt = s.Info.CreatedAt,
                })
                .ToList();
        }
        catch { return; }
        _saveSessionsDebounce?.Dispose();
        _saveSessionsDebounce = new Timer(_ => WriteActiveSessionsFile(entries), null, 2000, Timeout.Infinite);
    }

    /// <summary>
    /// Flush pending session save immediately (used during dispose/shutdown).
    /// </summary>
    private void FlushSaveActiveSessionsToDisk()
    {
        _saveSessionsDebounce?.Dispose();
        _saveSessionsDebounce = null;
        if (IsRestoring) return;
        SaveActiveSessionsToDiskCore();
    }

    private void SaveActiveSessionsToDiskCore()
    {
        if (IsDemoMode) return;
        var sessionMetas = SnapshotSessionMetas();

        try
        {
            var entries = _sessions.Values
                .Where(s => s.Info.SessionId != null && !s.Info.IsHidden)
                .Select(s => new ActiveSessionEntry
                {
                    SessionId = s.Info.SessionId!,
                    DisplayName = s.Info.Name,
                    Model = s.Info.Model,
                    WorkingDirectory = s.Info.WorkingDirectory,
                    GroupId = sessionMetas.FirstOrDefault(m => m.SessionName == s.Info.Name)?.GroupId,
                    LastPrompt = s.Info.IsProcessing
                        ? s.Info.History.LastOrDefault(m => m.IsUser)?.Content
                        : null,
                    TotalInputTokens = s.Info.TotalInputTokens,
                    TotalOutputTokens = s.Info.TotalOutputTokens,
                    ContextCurrentTokens = s.Info.ContextCurrentTokens,
                    ContextTokenLimit = s.Info.ContextTokenLimit,
                    PremiumRequestsUsed = s.Info.PremiumRequestsUsed,
                    TotalApiTimeSeconds = s.Info.TotalApiTimeSeconds,
                    CreatedAt = s.Info.CreatedAt,
                })
                .ToList();
            WriteActiveSessionsFile(entries);
        }
        catch (Exception ex)
        {
            Debug($"Failed to save active sessions: {ex.Message}");
        }
    }

    /// <summary>
    /// Write entries to disk, merging with existing file to preserve sessions not in memory.
    /// Safe to call from any thread — only does file I/O on pre-built data.
    /// </summary>
    private void WriteActiveSessionsFile(List<ActiveSessionEntry> entries)
    {
        if (IsDemoMode) return;
        try
        {
            Directory.CreateDirectory(PolyPilotBaseDir);
            try
            {
                if (File.Exists(ActiveSessionsFile))
                {
                    var existingJson = File.ReadAllText(ActiveSessionsFile);
                    var existingEntries = JsonSerializer.Deserialize<List<ActiveSessionEntry>>(existingJson);
                    if (existingEntries != null)
                    {
                        var closedIds = new HashSet<string>(_closedSessionIds.Keys, StringComparer.OrdinalIgnoreCase);
                        var closedNames = new HashSet<string>(_closedSessionNames.Keys, StringComparer.OrdinalIgnoreCase);
                        entries = MergeSessionEntries(entries, existingEntries, closedIds, closedNames,
                            sessionId => Directory.Exists(Path.Combine(SessionStatePath, sessionId)));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug($"Failed to merge existing sessions: {ex.Message}");
            }
            
            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            // Atomic write: write to temp file then rename to prevent corruption on crash
            var tempFile = ActiveSessionsFile + ".tmp";
            File.WriteAllText(tempFile, json);
            File.Move(tempFile, ActiveSessionsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug($"Failed to save active sessions: {ex.Message}");
        }
    }

    /// <summary>
    /// Merge active (in-memory) session entries with persisted (on-disk) entries.
    /// Persisted entries are kept if they aren't already active, weren't explicitly
    /// closed (by ID or display name), and their session directory still exists.
    /// </summary>
    internal static List<ActiveSessionEntry> MergeSessionEntries(
        List<ActiveSessionEntry> active,
        List<ActiveSessionEntry> persisted,
        ISet<string> closedIds,
        ISet<string> closedNames,
        Func<string, bool> sessionDirExists)
    {
        var merged = new List<ActiveSessionEntry>(active);
        var activeIds = new HashSet<string>(active.Select(e => e.SessionId), StringComparer.OrdinalIgnoreCase);

        foreach (var existing in persisted)
        {
            if (activeIds.Contains(existing.SessionId)) continue;
            if (closedIds.Contains(existing.SessionId)) continue;
            if (closedNames.Contains(existing.DisplayName)) continue;
            if (!sessionDirExists(existing.SessionId)) continue;

            merged.Add(existing);
            activeIds.Add(existing.SessionId);
        }

        return merged;
    }

    /// <summary>
    /// Restore persisted usage stats onto the in-memory session after resume.
    /// Uses Math.Max for accumulative fields to avoid overwriting values the SDK
    /// may have already set via event replay during ResumeSessionAsync.
    /// If usage fields are missing (e.g., from before the feature was added),
    /// backfill from events.jsonl on disk.
    /// </summary>
    private void RestoreUsageStats(ActiveSessionEntry entry)
    {
        if (!_sessions.TryGetValue(entry.DisplayName, out var state)) return;
        // Use Max to preserve values from SDK event replay (which may have already
        // incremented these via SessionUsageInfoEvent/AssistantUsageEvent)
        state.Info.TotalInputTokens = Math.Max(state.Info.TotalInputTokens, entry.TotalInputTokens);
        state.Info.TotalOutputTokens = Math.Max(state.Info.TotalOutputTokens, entry.TotalOutputTokens);
        if (entry.ContextCurrentTokens.HasValue)
            state.Info.ContextCurrentTokens = entry.ContextCurrentTokens;
        if (entry.ContextTokenLimit.HasValue)
            state.Info.ContextTokenLimit = entry.ContextTokenLimit;
        state.Info.PremiumRequestsUsed = Math.Max(state.Info.PremiumRequestsUsed, entry.PremiumRequestsUsed);
        state.Info.TotalApiTimeSeconds = Math.Max(state.Info.TotalApiTimeSeconds, entry.TotalApiTimeSeconds);
        if (entry.CreatedAt.HasValue)
            state.Info.CreatedAt = entry.CreatedAt.Value;

        // Backfill from events.jsonl only when ALL tracked fields are zero (indicating "never tracked")
        if (entry.PremiumRequestsUsed == 0 && entry.TotalApiTimeSeconds == 0 && !entry.CreatedAt.HasValue)
        {
            try { BackfillUsageFromEvents(state.Info, entry.SessionId); }
            catch (Exception ex) { Debug($"BackfillUsageFromEvents failed for '{entry.DisplayName}': {ex.Message}"); }
        }
    }

    /// <summary>
    /// Scan events.jsonl to backfill premium request count, session start time,
    /// and API time for sessions persisted before usage tracking was added.
    /// </summary>
    private static void BackfillUsageFromEvents(AgentSessionInfo info, string sessionId)
    {
        var eventsFile = Path.Combine(SessionStatePath, sessionId, "events.jsonl");
        if (!File.Exists(eventsFile)) return;

        int turnEndCount = 0;
        DateTime? firstTimestamp = null;
        double apiTimeSeconds = 0;
        DateTime? lastUserMessage = null;
        DateTime? lastTurnEnd = null;

        foreach (var line in File.ReadLines(eventsFile))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            // Fast string check before parsing JSON
            if (firstTimestamp == null || line.Contains("user.message") ||
                line.Contains("assistant.turn_end") || line.Contains("session.idle"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    DateTime? eventTime = null;
                    if (root.TryGetProperty("timestamp", out var tsEl) &&
                        DateTime.TryParse(tsEl.GetString(), null,
                            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                            out var ts))
                    {
                        eventTime = ts;
                        if (firstTimestamp == null) firstTimestamp = ts;
                    }

                    if (!root.TryGetProperty("type", out var typeEl)) continue;
                    var type = typeEl.GetString();

                    switch (type)
                    {
                        case "user.message":
                            // Close previous turn's API time if we hit a new user message
                            if (lastUserMessage.HasValue && lastTurnEnd.HasValue)
                                apiTimeSeconds += (lastTurnEnd.Value - lastUserMessage.Value).TotalSeconds;
                            lastUserMessage = eventTime;
                            lastTurnEnd = null;
                            break;

                        case "assistant.turn_end":
                            turnEndCount++;
                            lastTurnEnd = eventTime;
                            break;

                        case "session.idle":
                            // Prefer session.idle as the end marker when available
                            if (eventTime.HasValue)
                                lastTurnEnd = eventTime;
                            break;
                    }
                }
                catch { /* skip malformed lines */ }
            }
        }

        // Close the last turn
        if (lastUserMessage.HasValue && lastTurnEnd.HasValue)
            apiTimeSeconds += (lastTurnEnd.Value - lastUserMessage.Value).TotalSeconds;

        if (info.PremiumRequestsUsed == 0 && turnEndCount > 0)
            info.PremiumRequestsUsed = turnEndCount;

        if (info.TotalApiTimeSeconds == 0 && apiTimeSeconds > 0)
            info.TotalApiTimeSeconds = apiTimeSeconds;

        if (firstTimestamp.HasValue && info.CreatedAt == default)
            info.CreatedAt = firstTimestamp.Value;
    }

    /// <summary>
    /// Load and resume all previously active sessions
    /// </summary>
    public async Task RestorePreviousSessionsAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(ActiveSessionsFile))
        {
            try
            {
                var json = await File.ReadAllTextAsync(ActiveSessionsFile, cancellationToken);
                var entries = JsonSerializer.Deserialize<List<ActiveSessionEntry>>(json);
                if (entries != null && entries.Count > 0)
                {
                    Debug($"Restoring {entries.Count} previous sessions...");
                    IsRestoring = true;

                    // Snapshot groups once for thread safety — bridge/SDK events can
                    // call AddGroup/RemoveGroupsWhere concurrently during restore.
                    var restoreGroups = SnapshotGroups();

                    // Mark all codespace groups as Reconnecting — health check will connect them in background.
                    // We do NOT block app startup with slow SSH calls here.
                    // When CodespacesEnabled is off, groups stay in memory but are inert (UI hidden, health check off).
                    if (CodespacesEnabled)
                    {
                        foreach (var group in restoreGroups.Where(g => g.IsCodespace))
                            group.ConnectionState = CodespaceConnectionState.Reconnecting;
                    }

                    // Collect evaluator session names referenced by active reflection cycles
                    var activeEvaluators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var g in restoreGroups)
                    {
                        if (g.ReflectionState?.IsActive == true && !string.IsNullOrEmpty(g.ReflectionState.EvaluatorSessionName))
                            activeEvaluators.Add(g.ReflectionState.EvaluatorSessionName);
                    }

                    foreach (var entry in entries)
                    {
                        try
                        {
                            // Prune ghost evaluator sessions from crashed cycles
                            if (entry.DisplayName.StartsWith("__evaluator_") && !activeEvaluators.Contains(entry.DisplayName))
                            {
                                Debug($"Pruning ghost evaluator session '{entry.DisplayName}' — not referenced by active cycle");
                                _closedSessionIds[entry.SessionId] = 0; // prevent merge from re-adding
                                // Clean up persisted session directory
                                var ghostDir = Path.Combine(SessionStatePath, entry.SessionId);
                                if (Directory.Exists(ghostDir))
                                {
                                    try { Directory.Delete(ghostDir, recursive: true); }
                                    catch (Exception delEx) { Debug($"Failed to delete ghost session dir: {delEx.Message}"); }
                                }
                                continue;
                            }
                            // Skip if already active
                            if (_sessions.ContainsKey(entry.DisplayName))
                            {
                                Debug($"Skipping '{entry.DisplayName}' — already active");
                                continue;
                            }
                            
                            // Check the session still exists on disk
                            var sessionDir = Path.Combine(SessionStatePath, entry.SessionId);
                            if (!Directory.Exists(sessionDir))
                            {
                                Debug($"Skipping '{entry.DisplayName}' — session dir not found: {sessionDir}");
                                continue;
                            }

                            // Codespace sessions: create placeholder state (client not yet connected).
                            // Health check will resume them after the codespace tunnel is established.
                            // When toggle is off, skip entirely — don't create null-session placeholders.
                            var isCodespaceSession = !string.IsNullOrEmpty(entry.GroupId) &&
                                restoreGroups.Any(g => g.Id == entry.GroupId && g.IsCodespace);
                            if (isCodespaceSession)
                            {
                                if (!CodespacesEnabled)
                                {
                                    Debug($"Skipping codespace session '{entry.DisplayName}' — CodespacesEnabled is off");
                                    continue;
                                }
                                Debug($"Deferring codespace session '{entry.DisplayName}' — client not connected yet");
                                var history = LoadHistoryFromDisk(entry.SessionId);
                                var resumeModel = Models.ModelHelper.NormalizeToSlug(entry.Model ?? DefaultModel);

                                // Use the codespace working directory (/workspaces/{repo}) instead of the
                                // persisted local Mac path which doesn't exist inside the codespace.
                                var csGroup = restoreGroups.FirstOrDefault(g => g.Id == entry.GroupId);
                                var csWorkDir = csGroup?.CodespaceWorkingDirectory ?? entry.WorkingDirectory;

                                var info = new AgentSessionInfo
                                {
                                    Name = entry.DisplayName,
                                    Model = resumeModel ?? DefaultModel,
                                    CreatedAt = DateTime.Now,
                                    SessionId = entry.SessionId,
                                    WorkingDirectory = csWorkDir
                                };
                                foreach (var msg in history) info.History.Add(msg);
                                info.History.Add(ChatMessage.SystemMessage("🔄 Waiting for codespace connection..."));
                                var placeholderState = new SessionState { Session = null!, Info = info };
                                _sessions[entry.DisplayName] = placeholderState;
                                _activeSessionName ??= entry.DisplayName;
                                Debug($"Created placeholder for codespace session: {entry.DisplayName}");
                                continue;
                            }

                            await ResumeSessionAsync(entry.SessionId, entry.DisplayName, entry.WorkingDirectory, entry.Model, cancellationToken, entry.LastPrompt, entry.GroupId);
                            RestoreUsageStats(entry);
                            Debug($"Restored session: {entry.DisplayName}");
                        }
                        catch (Exception ex)
                        {
                            Debug($"Failed to restore '{entry.DisplayName}': {ex.GetType().Name}: {ex.Message}");

                            // "Session not found" means the CLI server doesn't know this session
                            // (e.g., worker sessions that were created but never received a message).
                            // "corrupted" / "session file" errors mean the events.jsonl is locked or
                            // unreadable (e.g., another copilot process owns the session).
                            // Fall back to creating a fresh session so multi-agent workers don't vanish.
                            if (ex.Message.Contains("Session not found", StringComparison.OrdinalIgnoreCase) ||
                                ex.Message.Contains("corrupt", StringComparison.OrdinalIgnoreCase) ||
                                ex.Message.Contains("session file", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    Debug($"Falling back to CreateSessionAsync for '{entry.DisplayName}'");
                                    await CreateSessionAsync(entry.DisplayName, entry.Model, entry.WorkingDirectory, cancellationToken, entry.GroupId);
                                    Debug($"Recreated session: {entry.DisplayName}");
                                    continue;
                                }
                                catch (Exception createEx)
                                {
                                    Debug($"Fallback CreateSessionAsync also failed for '{entry.DisplayName}': {createEx.Message}");
                                }
                            }

                            // If the connection broke, recreate the client
                            if (IsConnectionError(ex))
                            {
                                Debug("Connection lost during restore, recreating client...");
                                try
                                {
                                    if (_client != null) await _client.DisposeAsync();
                                    var settings = ConnectionSettings.Load();
                                    _client = CreateClient(settings);
                                    await _client.StartAsync(cancellationToken);
                                    Debug("Client recreated successfully");
                                }
                                catch (Exception clientEx)
                                {
                                    Debug($"Failed to recreate client: {clientEx.GetType().Name}: {clientEx.Message}");
                                    break; // Stop trying to restore sessions
                                }
                            }
                        }
                    }
                    
                    IsRestoring = false;
                }
            }
            catch (Exception ex)
            {
                Debug($"Failed to load active sessions file: {ex.Message}");
            }
        }

    }

    public void SaveUiState(string currentPage, string? activeSession = null, int? fontSize = null, string? selectedModel = null, bool? expandedGrid = null, string? expandedSession = "<<unspecified>>", Dictionary<string, string>? inputModes = null)
    {
        try
        {
            Directory.CreateDirectory(PolyPilotBaseDir);
            
            var existing = LoadUiState();
            var state = new UiState
            {
                CurrentPage = currentPage,
                ActiveSession = activeSession ?? _activeSessionName,
                FontSize = fontSize ?? existing?.FontSize ?? 20,
                SelectedModel = selectedModel ?? existing?.SelectedModel,
                ExpandedGrid = expandedGrid ?? existing?.ExpandedGrid ?? false,
                ExpandedSession = expandedSession == "<<unspecified>>" ? existing?.ExpandedSession : expandedSession,
                InputModes = inputModes != null
                    ? new Dictionary<string, string>(inputModes)
                    : existing?.InputModes ?? new Dictionary<string, string>(),
                CompletedTutorials = existing?.CompletedTutorials ?? new HashSet<string>()
            };

            lock (_uiStateLock)
            {
                _pendingUiState = state;
            }
            _saveUiStateDebounce?.Dispose();
            _saveUiStateDebounce = new Timer(_ => FlushUiState(), null, 1000, Timeout.Infinite);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to prepare UI state: {ex.Message}");
        }
    }

    private void FlushUiState()
    {
        UiState? state;
        lock (_uiStateLock)
        {
            state = _pendingUiState;
            _pendingUiState = null;
        }
        if (state == null) return;
        try
        {
            var json = JsonSerializer.Serialize(state);
            File.WriteAllText(UiStateFile, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save UI state: {ex.Message}");
        }
    }

    public void SaveTutorialProgress(HashSet<string> completedChapters)
    {
        try
        {
            var existing = LoadUiState() ?? new UiState();
            existing.CompletedTutorials = completedChapters;
            lock (_uiStateLock)
            {
                _pendingUiState = existing;
            }
            _saveUiStateDebounce?.Dispose();
            _saveUiStateDebounce = new Timer(_ => FlushUiState(), null, 1000, Timeout.Infinite);
        }
        catch { }
    }

    public UiState? LoadUiState()
    {
        // Return pending (debounced) state if available — avoids stale disk reads
        lock (_uiStateLock)
        {
            if (_pendingUiState != null) return _pendingUiState;
        }
        try
        {
            if (!File.Exists(UiStateFile)) return null;
            var json = File.ReadAllText(UiStateFile);
            var state = JsonSerializer.Deserialize<UiState>(json);
            // Normalize model slug — UI state may have display names from CLI sessions
            if (state != null && Models.ModelHelper.IsDisplayName(state.SelectedModel))
                state.SelectedModel = Models.ModelHelper.NormalizeToSlug(state.SelectedModel);
            return state;
        }
        catch { return null; }
    }

    // --- Session Aliases ---

    private Dictionary<string, string>? _aliasCache;

    private Dictionary<string, string> LoadAliases()
    {
        if (_aliasCache != null) return _aliasCache;
        try
        {
            if (File.Exists(SessionAliasesFile))
            {
                var json = File.ReadAllText(SessionAliasesFile);
                _aliasCache = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
                return _aliasCache;
            }
        }
        catch { }
        _aliasCache = new();
        return _aliasCache;
    }

    public string? GetSessionAlias(string sessionId)
    {
        var aliases = LoadAliases();
        return aliases.TryGetValue(sessionId, out var alias) ? alias : null;
    }

    public void SetSessionAlias(string sessionId, string alias)
    {
        var aliases = LoadAliases();
        if (string.IsNullOrWhiteSpace(alias))
            aliases.Remove(sessionId);
        else
            aliases[sessionId] = alias.Trim();
        _aliasCache = aliases;
        try
        {
            // Ensure directory exists (required on iOS where it may not exist by default)
            Directory.CreateDirectory(PolyPilotBaseDir);
            var json = JsonSerializer.Serialize(aliases, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SessionAliasesFile, json);
        }
        catch { }
    }

    /// <summary>
    /// Gets a list of persisted session GUIDs from ~/.copilot/session-state
    /// </summary>
    public IEnumerable<PersistedSessionInfo> GetPersistedSessions()
    {
        // In remote mode, return persisted sessions from the bridge
        if (IsRemoteMode)
        {
            return _bridgeClient.PersistedSessions
                .Select(p => new PersistedSessionInfo
                {
                    SessionId = p.SessionId,
                    Title = p.Title,
                    Preview = p.Preview,
                    WorkingDirectory = p.WorkingDirectory,
                    LastModified = p.LastModified,
                });
        }

        if (!Directory.Exists(SessionStatePath))
            return Enumerable.Empty<PersistedSessionInfo>();

        return Directory.GetDirectories(SessionStatePath)
            .Select(dir => new DirectoryInfo(dir))
            .Where(di => Guid.TryParse(di.Name, out _))
            .Where(IsResumableSessionDirectory)
            .Select(di => CreatePersistedSessionInfo(di))
            .OrderByDescending(s => s.LastModified);
    }

    private static bool IsResumableSessionDirectory(DirectoryInfo di)
    {
        var eventsFile = Path.Combine(di.FullName, "events.jsonl");
        var workspaceFile = Path.Combine(di.FullName, "workspace.yaml");

        if (!File.Exists(eventsFile) || !File.Exists(workspaceFile))
            return false;

        try
        {
            var headerLines = File.ReadLines(workspaceFile).Take(20).ToList();
            var idLine = headerLines.FirstOrDefault(l => l.StartsWith("id:", StringComparison.OrdinalIgnoreCase));
            var cwdLine = headerLines.FirstOrDefault(l => l.StartsWith("cwd:", StringComparison.OrdinalIgnoreCase));
            if (idLine == null || cwdLine == null)
                return false;

            var parsedId = idLine["id:".Length..].Trim().Trim('"', '\'');
            return string.Equals(parsedId, di.Name, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public bool DeletePersistedSession(string sessionId)
    {
        // In demo mode, don't delete real session data from disk
        if (IsDemoMode) return false;

        if (string.IsNullOrWhiteSpace(sessionId) || !Guid.TryParse(sessionId, out _))
            return false;

        var deleted = false;

        try
        {
            var sessionDir = Path.Combine(SessionStatePath, sessionId);
            if (Directory.Exists(sessionDir))
            {
                Directory.Delete(sessionDir, recursive: true);
                deleted = true;
            }
        }
        catch (Exception ex)
        {
            Debug($"Failed to delete persisted session directory '{sessionId}': {ex.Message}");
        }

        try
        {
            if (File.Exists(ActiveSessionsFile))
            {
                var json = File.ReadAllText(ActiveSessionsFile);
                var entries = JsonSerializer.Deserialize<List<ActiveSessionEntry>>(json) ?? new();
                var kept = entries
                    .Where(e => !string.Equals(e.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (kept.Count != entries.Count)
                {
                    var updatedJson = JsonSerializer.Serialize(kept, new JsonSerializerOptions { WriteIndented = true });
                    var tempFile = ActiveSessionsFile + ".tmp";
                    File.WriteAllText(tempFile, updatedJson);
                    File.Move(tempFile, ActiveSessionsFile, overwrite: true);
                    deleted = true;
                }
            }
        }
        catch (Exception ex)
        {
            Debug($"Failed to prune active session entry '{sessionId}': {ex.Message}");
        }

        return deleted;
    }

    private PersistedSessionInfo CreatePersistedSessionInfo(DirectoryInfo di)
    {
        string? title = null;
        string? preview = null;
        string? workingDir = null;

        var eventsFile = Path.Combine(di.FullName, "events.jsonl");
        if (File.Exists(eventsFile))
        {
            try
            {
                // Read first few lines to find first user message and working directory
                foreach (var line in File.ReadLines(eventsFile).Take(50))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    
                    if (!root.TryGetProperty("type", out var typeEl)) continue;
                    var type = typeEl.GetString();

                    // Get working directory from session.start
                    if (type == "session.start" && workingDir == null)
                    {
                        if (root.TryGetProperty("data", out var data))
                        {
                            // Try data.context.cwd first (newer format), then data.workingDirectory
                            if (data.TryGetProperty("context", out var ctx) &&
                                ctx.TryGetProperty("cwd", out var cwd))
                            {
                                workingDir = cwd.GetString();
                            }
                            else if (data.TryGetProperty("workingDirectory", out var wd))
                            {
                                workingDir = wd.GetString();
                            }
                        }
                    }
                    
                    // Get first user message
                    if (type == "user.message" && title == null)
                    {
                        if (root.TryGetProperty("data", out var data) &&
                            data.TryGetProperty("content", out var content))
                        {
                            preview = content.GetString();
                            if (!string.IsNullOrEmpty(preview))
                            {
                                // Create truncated title (max 60 chars)
                                title = preview.Length > 60 
                                    ? preview[..57] + "..." 
                                    : preview;
                                // Clean up newlines for title
                                title = title.Replace("\n", " ").Replace("\r", "");
                            }
                        }
                        break; // Got what we need
                    }
                }
            }
            catch { /* Ignore parse errors */ }
        }

        // Use events.jsonl modification time for accurate "last used" sorting
        var eventsFileInfo = new FileInfo(eventsFile);
        var lastUsed = eventsFileInfo.Exists ? eventsFileInfo.LastWriteTime : di.LastWriteTime;

        // Priority: alias > active session name > first message > "Untitled session"
        var alias = GetSessionAlias(di.Name);
        string resolvedTitle;
        if (!string.IsNullOrEmpty(alias))
            resolvedTitle = alias;
        else if (title != null)
            resolvedTitle = title;
        else
        {
            var activeMatch = _sessions.Values.FirstOrDefault(s => s.Info.SessionId == di.Name);
            resolvedTitle = activeMatch?.Info.Name ?? "Untitled session";
        }

        return new PersistedSessionInfo
        {
            SessionId = di.Name,
            LastModified = lastUsed,
            Path = di.FullName,
            Title = resolvedTitle,
            Preview = preview ?? "No preview available",
            WorkingDirectory = workingDir
        };
    }
}
