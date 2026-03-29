using System.Text.Json;
using GitHub.Copilot.SDK;
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
                    LastUpdatedAt = s.Info.LastUpdatedAt,
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
                    LastUpdatedAt = s.Info.LastUpdatedAt,
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
                            sessionId =>
                            {
                                var dir = Path.Combine(SessionStatePath, sessionId);
                                if (!Directory.Exists(dir)) return false;
                                // Accept if events.jsonl exists (session has been used)
                                if (File.Exists(Path.Combine(dir, "events.jsonl"))) return true;
                                // Also accept recently-created directories (new sessions
                                // that haven't received their first event yet). Ghost
                                // directories from old reconnects will be stale.
                                try { return (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir)).TotalMinutes < 5; }
                                catch { return false; }
                            });
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
        // Track active display names only.
        // This stops persisted entries from shadowing active sessions after reconnect.
        // Persisted entries may still share names with each other.
        var activeNames = new HashSet<string>(active.Select(e => e.DisplayName).Where(n => n != null), StringComparer.OrdinalIgnoreCase);

        foreach (var existing in persisted)
        {
            if (activeIds.Contains(existing.SessionId)) continue;
            if (activeNames.Contains(existing.DisplayName)) continue;
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
        // Restore real LastUpdatedAt so focus detection uses actual activity time, not restore time
        if (entry.LastUpdatedAt.HasValue)
            state.Info.LastUpdatedAt = entry.LastUpdatedAt.Value;

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
    /// Check if a session belongs to a codespace group.
    /// </summary>
    private bool IsCodespaceSession(string sessionName)
    {
        // Use snapshots for thread safety — may be called from background threads
        var metas = SnapshotSessionMetas();
        var meta = metas.FirstOrDefault(m => m.SessionName == sessionName);
        if (meta?.GroupId == null) return false;
        var groups = SnapshotGroups();
        var group = groups.FirstOrDefault(g => g.Id == meta.GroupId);
        return group?.IsCodespace == true;
    }

    /// <summary>
    /// Lazily connect a placeholder session to the SDK. Called on first interaction
    /// (send message) with a session that was loaded at startup without an SDK connection.
    /// </summary>
    private async Task EnsureSessionConnectedAsync(string sessionName, SessionState state, CancellationToken cancellationToken)
    {
        var connectLock = _sessionConnectLocks.GetOrAdd(sessionName, _ => new SemaphoreSlim(1, 1));
        await connectLock.WaitAsync(cancellationToken);
        try
        {
            if (state.Session != null) return; // already connected

            var sessionId = state.Info.SessionId;
            if (string.IsNullOrEmpty(sessionId) || !Guid.TryParse(sessionId, out _))
                throw new InvalidOperationException($"Session '{sessionName}' has no valid session ID for resume.");

            if (!IsInitialized || _client == null)
                throw new InvalidOperationException("Copilot is not connected yet. Go to Settings to configure.");

            Debug($"Lazy-resuming session '{sessionName}' (id={sessionId})...");

            // Use snapshot for thread safety — may be called from ThreadPool via SendPromptAsync
            var groupId = SnapshotSessionMetas().FirstOrDefault(m => m.SessionName == sessionName)?.GroupId;
            var resumeModel = state.Info.Model ?? DefaultModel;
            var resumeWorkDir = state.Info.WorkingDirectory;

            var resumeConfig = new ResumeSessionConfig
            {
                Model = resumeModel,
                WorkingDirectory = resumeWorkDir,
                Tools = new List<Microsoft.Extensions.AI.AIFunction> { ShowImageTool.CreateFunction() },
                OnPermissionRequest = AutoApprovePermissions
            };

            CopilotSession copilotSession;
            bool wasResumed = false;
            try
            {
                copilotSession = await GetClientForGroup(groupId).ResumeSessionAsync(sessionId, resumeConfig, cancellationToken);
                wasResumed = true;
            }
            catch (Exception ex) when (
                ex.Message.Contains("Session not found", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("corrupt", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("session file", StringComparison.OrdinalIgnoreCase) ||
                IsProcessError(ex))
            {
                Debug($"Lazy-resume failed for '{sessionName}': {ex.Message} — creating fresh session");
                copilotSession = await GetClientForGroup(groupId).CreateSessionAsync(new SessionConfig
                {
                    Model = resumeModel,
                    WorkingDirectory = resumeWorkDir,
                    Tools = new List<Microsoft.Extensions.AI.AIFunction> { ShowImageTool.CreateFunction() },
                    OnPermissionRequest = AutoApprovePermissions
                }, cancellationToken);
                state.Info.SessionId = copilotSession.SessionId;
                FlushSaveActiveSessionsToDisk();
            }
            catch (Exception ex) when (IsAuthError(ex))
            {
                // Auth failure: the persistent server is running but can't authenticate.
                // Attempt server recovery (restart forces re-authentication with GitHub).
                Debug($"Lazy-resume auth failure for '{sessionName}': {ex.Message} — attempting server recovery");
                var recovered = await TryRecoverPersistentServerAsync();
                if (!recovered)
                {
                    // Recovery returned false: either it failed outright, or another session is
                    // already recovering concurrently. In the concurrent case, wait up to 30s for
                    // the in-flight recovery to complete before giving up with a permanent error.
                    if (await _recoveryLock.WaitAsync(30_000))
                    {
                        _recoveryLock.Release(); // Don't need to hold it — just waiting for in-flight recovery to finish
                        Debug($"[SERVER-RECOVERY] Lazy-resume waited for in-flight recovery — retrying session '{sessionName}'");
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            "Session authentication failed and server recovery was unsuccessful. Go to Settings → Save & Reconnect.", ex);
                    }
                }

                // Retry with the new client after recovery
                copilotSession = await GetClientForGroup(groupId).ResumeSessionAsync(sessionId, resumeConfig, cancellationToken);
            }

            // INV-16: Register event handler BEFORE publishing to state —
            // no window where events arrive with no handler. Matches the pattern
            // in sibling reconnect (CopilotService.cs:2766) and worker revival
            // (Organization.cs:1547).
            copilotSession.On(evt => HandleSessionEvent(state, evt));
            state.Session = copilotSession;
            state.IsMultiAgentSession = IsSessionInMultiAgentGroup(sessionName);

            // If we resumed a session that crashed mid-tool-execution, the SDK is stuck
            // waiting for tool results that will never arrive. It silently queues/ignores
            // new SendAsync calls until the pending tools are resolved. An explicit abort
            // clears this state and allows new messages to flow.
            //
            // IMPORTANT: Only abort if the CLI has actually stopped working. In persistent
            // mode, the headless server keeps running tools even while PolyPilot is down.
            // If IsSessionStillProcessing() says the CLI is active, the tool results WILL
            // arrive — aborting would kill legitimate in-progress work.
            if (wasResumed && HasInterruptedToolExecution(sessionId) && !IsSessionStillProcessing(sessionId))
            {
                Debug($"[RESUME-ABORT] '{sessionName}' has interrupted tool execution — sending abort to clear pending state");
                try
                {
                    await copilotSession.AbortAsync(cancellationToken);
                    Debug($"[RESUME-ABORT] '{sessionName}' abort sent successfully");
                }
                catch (Exception abortEx)
                {
                    Debug($"[RESUME-ABORT] '{sessionName}' abort failed (non-fatal): {abortEx.Message}");
                }
            }
            else if (wasResumed && HasInterruptedToolExecution(sessionId))
            {
                Debug($"[RESUME-SKIP-ABORT] '{sessionName}' has unmatched tool starts but CLI is still active — NOT aborting");
                // The CLI is still running tools — mark the session as processing so the UI
                // shows it as busy. Set watchdog flags so it gets the longer tool timeout.
                // INV-2: marshal to UI thread — EnsureSessionConnectedAsync runs from Task.Run.
                InvokeOnUI(() =>
                {
                    state.Info.IsProcessing = true;
                    state.Info.IsResumed = true;
                    state.HasUsedToolsThisTurn = true;
                    state.Info.ProcessingPhase = 3; // Working
                    state.Info.ProcessingStartedAt = DateTime.UtcNow;
                    StartProcessingWatchdog(state, sessionName);
                    NotifyStateChanged();
                });
            }

            Debug($"Lazy-resume complete: '{sessionName}'");
        }
        finally
        {
            connectLock.Release();
        }
    }

    /// <summary>
    /// Background wrapper for session restore + post-restore tasks.
    /// Runs off the UI thread so the app renders immediately on launch.
    /// </summary>
    private async Task RestoreSessionsInBackgroundAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RestorePreviousSessionsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Debug($"Background restore failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsRestoring = false;

            // Flush session list so recreated session IDs are persisted
            FlushSaveActiveSessionsToDisk();

            if (CodespacesEnabled)
                StartCodespaceHealthCheck();

            // Start external session scanner AFTER restore is complete — scanning 3K+
            // session directories would compete with session restoration if started earlier.
            StartExternalSessionScannerIfNeeded();

            // ReconcileOrganization reads/writes Organization.Sessions (a plain List<T>)
            // which is not thread-safe. Now that restore runs on ThreadPool via Task.Run,
            // we must marshal this to the UI thread to avoid concurrent enumeration crashes.
            InvokeOnUI(() =>
            {
                ReconcileOrganization();
                OnStateChanged?.Invoke();
            });

            // Resume any pending orchestration dispatch interrupted by relaunch
            _ = ResumeOrchestrationIfPendingAsync(cancellationToken);
        }
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
                var json = await File.ReadAllTextAsync(ActiveSessionsFile, cancellationToken).ConfigureAwait(false);
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

                    var eagerResumeCandidates = new List<(string SessionName, SessionState State)>();

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
                                var (history, _) = await LoadBestHistoryAsync(entry.SessionId);
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

                            // Create lightweight placeholder — actual SDK resume happens lazily
                            // when user sends a message (EnsureSessionConnectedAsync).
                            // This avoids 41 sequential SDK connections blocking app startup.
                            var (lazyHistory, _) = await LoadBestHistoryAsync(entry.SessionId);
                            var lazyModel = Models.ModelHelper.NormalizeToSlug(entry.Model ?? DefaultModel);
                            if (string.IsNullOrEmpty(lazyModel)) lazyModel = DefaultModel;
                            var lazyWorkDir = entry.WorkingDirectory ?? GetSessionWorkingDirectory(entry.SessionId);

                            var lazyInfo = new AgentSessionInfo
                            {
                                Name = entry.DisplayName,
                                Model = lazyModel,
                                CreatedAt = DateTime.Now,
                                SessionId = entry.SessionId,
                                WorkingDirectory = lazyWorkDir
                            };
                            lazyInfo.GitBranch = GetGitBranch(lazyInfo.WorkingDirectory);
                            foreach (var msg in lazyHistory) lazyInfo.History.Add(msg);
                            lazyInfo.MessageCount = lazyInfo.History.Count;
                            lazyInfo.LastReadMessageCount = lazyInfo.History.Count;
                            // Mark stale incomplete tool calls / reasoning as complete
                            foreach (var msg in lazyInfo.History.Where(m => (m.MessageType == ChatMessageType.ToolCall || m.MessageType == ChatMessageType.Reasoning) && !m.IsComplete))
                                msg.IsComplete = true;

                            var lazyState = new SessionState { Session = null!, Info = lazyInfo };
                            _sessions[entry.DisplayName] = lazyState;
                            _activeSessionName ??= entry.DisplayName;
                            RestoreUsageStats(entry);
                            // Eagerly resume sessions that are still actively processing on the
                            // headless server. Check events.jsonl (authoritative) first, then fall
                            // back to LastPrompt (saved when IsProcessing=true at debounce time).
                            // Without this, actively-running sessions appear idle after app restart
                            // because they're only loaded as lazy placeholders with no SDK connection.
                            var isStillActive = IsSessionStillProcessing(entry.SessionId);
                            if (isStillActive || !string.IsNullOrWhiteSpace(entry.LastPrompt))
                            {
                                eagerResumeCandidates.Add((entry.DisplayName, lazyState));
                                Debug($"Queued eager resume for interrupted session: {entry.DisplayName} (active={isStillActive}, hasLastPrompt={!string.IsNullOrWhiteSpace(entry.LastPrompt)})");
                            }
                            Debug($"Loaded session placeholder: {entry.DisplayName} ({lazyHistory.Count} messages)");
                        }
                        catch (Exception ex)
                        {
                            Debug($"Failed to restore '{entry.DisplayName}': {ex.GetType().Name}: {ex.Message}");

                            // "Session not found" means the CLI server doesn't know this session
                            // (e.g., worker sessions that were created but never received a message).
                            // "corrupted" / "session file" errors mean the events.jsonl is locked or
                            // unreadable (e.g., another copilot process owns the session).
                            // "No process is associated" means the CLI server process died and the
                            // SDK's StartCliServerAsync hit a stale Process handle (HasExited throws).
                            // Fall back to creating a fresh session so multi-agent workers don't vanish.
                            // Load history from the old session's events.jsonl so messages aren't lost.
                            if (ex.Message.Contains("Session not found", StringComparison.OrdinalIgnoreCase) ||
                                ex.Message.Contains("corrupt", StringComparison.OrdinalIgnoreCase) ||
                                ex.Message.Contains("session file", StringComparison.OrdinalIgnoreCase) ||
                                IsProcessError(ex))
                            {
                                try
                                {
                                    // Recover history from the best available source.
                                    // First check events.jsonl variants (FindBestEventsSource scans session dirs),
                                    // then compare against chat_history.db which survives dead event streams.
                                    var bestSourceId = FindBestEventsSource(entry.SessionId, entry.WorkingDirectory);
                                    var (oldHistory, oldFromDb) = await LoadBestHistoryAsync(bestSourceId);
                                    // Also check the original session ID in case DB has messages under that ID
                                    if (bestSourceId != entry.SessionId)
                                    {
                                        var (origHistory, origFromDb) = await LoadBestHistoryAsync(entry.SessionId);
                                        if (origHistory.Count > oldHistory.Count)
                                        {
                                            oldHistory = origHistory;
                                            oldFromDb = origFromDb;
                                            bestSourceId = entry.SessionId;
                                        }
                                        Debug($"Using better source '{bestSourceId}' instead of stale '{entry.SessionId}' for '{entry.DisplayName}' ({oldHistory.Count} messages{(oldFromDb ? " from DB" : "")})");
                                    }
                                    else
                                        Debug($"Falling back to CreateSessionAsync for '{entry.DisplayName}' (recovered {oldHistory.Count} messages{(oldFromDb ? " from DB" : "")})");

                                    await CreateSessionAsync(entry.DisplayName, entry.Model, entry.WorkingDirectory, cancellationToken, entry.GroupId);

                                    // Inject recovered history into the newly created session
                                    if (_sessions.TryGetValue(entry.DisplayName, out var recreatedState) && oldHistory.Count > 0)
                                    {
                                        // Copy events.jsonl from the best source to the new session directory
                                        // so history survives future restarts (LoadHistoryFromDisk reads events.jsonl).
                                        if (recreatedState.Info.SessionId != null && recreatedState.Info.SessionId != bestSourceId)
                                        {
                                            CopyEventsToNewSession(bestSourceId, recreatedState.Info.SessionId);
                                        }

                                        foreach (var msg in oldHistory)
                                            recreatedState.Info.History.Add(msg);

                                        // Normalize stale incomplete entries (same as ResumeSessionAsync)
                                        foreach (var msg in recreatedState.Info.History.Where(m =>
                                            (m.MessageType == ChatMessageType.ToolCall || m.MessageType == ChatMessageType.Reasoning) && !m.IsComplete))
                                        {
                                            msg.IsComplete = true;
                                        }

                                        // Sync recovered history to DB under the new session ID
                                        // (skip if history came from DB — no need to overwrite)
                                        if (recreatedState.Info.SessionId != null && !oldFromDb)
                                            SafeFireAndForget(_chatDb.BulkInsertAsync(recreatedState.Info.SessionId, oldHistory));

                                        recreatedState.Info.History.Add(ChatMessage.SystemMessage("🔄 Session recreated — conversation history recovered from previous session."));
                                        recreatedState.Info.MessageCount = recreatedState.Info.History.Count;
                                        recreatedState.Info.LastReadMessageCount = recreatedState.Info.History.Count;
                                    }

                                    // Restore usage stats (token counts, CreatedAt, etc.)
                                    RestoreUsageStats(entry);

                                    Debug($"Recreated session with {oldHistory.Count} recovered messages: {entry.DisplayName}");
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

                    if (eagerResumeCandidates.Count > 0)
                    {
                        var pendingResumes = eagerResumeCandidates.ToArray();
                        _ = Task.Run(async () =>
                        {
                            foreach (var pendingResume in pendingResumes)
                            {
                                try
                                {
                                    await EnsureSessionConnectedAsync(pendingResume.SessionName, pendingResume.State, cancellationToken).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                                {
                                    break;
                                }
                                catch (Exception resumeEx)
                                {
                                    Debug($"Eager resume failed for '{pendingResume.SessionName}': {resumeEx.Message}");
                                }
                            }
                        }, cancellationToken);
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

    public void SaveUiState(string currentPage, string? activeSession = null, int? fontSize = null, string? selectedModel = null, bool? expandedGrid = null, string? expandedSession = "<<unspecified>>", Dictionary<string, string>? inputModes = null, int? gridColumns = null, int? cardMinHeight = null)
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
                CompletedTutorials = existing?.CompletedTutorials ?? new HashSet<string>(),
                GridColumns = gridColumns ?? existing?.GridColumns ?? 3,
                CardMinHeight = cardMinHeight ?? existing?.CardMinHeight ?? 250,
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

    /// <summary>
    /// Copy events.jsonl from an old session directory to a new one.
    /// Sanitizes lines (only valid JSON) and prepends if the destination already exists.
    /// Used when the SDK returns a different session ID than requested.
    /// </summary>
    private void CopyEventsToNewSession(string oldSessionId, string newSessionId)
    {
        string? tmpFile = null;
        try
        {
            var oldEvents = Path.Combine(SessionStatePath, oldSessionId, "events.jsonl");
            var newEventsDir = Path.Combine(SessionStatePath, newSessionId);
            var newEvents = Path.Combine(newEventsDir, "events.jsonl");
            if (!File.Exists(oldEvents)) return;

            Directory.CreateDirectory(newEventsDir);

            var oldLines = new List<string>();
            int skippedLines = 0;
            foreach (var line in File.ReadLines(oldEvents))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    oldLines.Add(line);
                }
                catch (JsonException) { skippedLines++; }
            }

            if (oldLines.Count == 0) return;

            var existed = File.Exists(newEvents);
            if (!existed)
            {
                File.WriteAllLines(newEvents, oldLines);
            }
            else
            {
                var newLines = File.ReadAllLines(newEvents);
                tmpFile = newEvents + ".tmp";
                using (var writer = new StreamWriter(tmpFile, append: false))
                {
                    foreach (var line in oldLines) writer.WriteLine(line);
                    foreach (var line in newLines) writer.WriteLine(line);
                }
                File.Move(tmpFile, newEvents, overwrite: true);
                tmpFile = null; // Move succeeded, no cleanup needed
            }

            Debug($"CopyEventsToNewSession: {oldSessionId} → {newSessionId}: {oldLines.Count} lines (prepended={existed}, skipped={skippedLines})");
        }
        catch (Exception ex)
        {
            Debug($"CopyEventsToNewSession failed: {ex.Message}");
            // Clean up temp file if File.Move failed
            if (tmpFile != null)
            {
                try { File.Delete(tmpFile); } catch { }
            }
        }
    }

    /// <summary>
    /// Search session-state directories for the best events.jsonl source matching a working directory.
    /// Returns the session ID with the most event lines whose workspace.yaml cwd matches exactly.
    /// Falls back to the original session ID if no better source is found.
    /// This handles the case where active-sessions.json has a stale session ID but
    /// intermediate fallback-recreated sessions accumulated more history.
    /// Scans at most MaxSessionDirsToScan directories sorted by last-write-time (newest first).
    /// </summary>
    internal const int MaxSessionDirsToScan = 200;

    internal string FindBestEventsSource(string originalSessionId, string? workingDirectory, string? statePath = null)
    {
        if (string.IsNullOrEmpty(workingDirectory))
            return originalSessionId;

        try
        {
            var resolvedStatePath = statePath ?? SessionStatePath;
            var stateDir = new DirectoryInfo(resolvedStatePath);
            if (!stateDir.Exists) return originalSessionId;

            var originalEvents = Path.Combine(resolvedStatePath, originalSessionId, "events.jsonl");
            long originalLines = 0;
            if (File.Exists(originalEvents))
            {
                try { originalLines = File.ReadLines(originalEvents).LongCount(); }
                catch { /* ignore read errors */ }
            }

            string bestId = originalSessionId;
            long bestLines = originalLines;

            // Sort by last-write-time descending so the most recent sessions are checked first.
            // Limit to MaxSessionDirsToScan to bound scan time on large state directories.
            var dirs = stateDir.EnumerateDirectories()
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .Take(MaxSessionDirsToScan);

            foreach (var sessionDir in dirs)
            {
                if (sessionDir.Name == originalSessionId) continue;

                var eventsFile = Path.Combine(sessionDir.FullName, "events.jsonl");
                if (!File.Exists(eventsFile)) continue;

                var workspaceFile = Path.Combine(sessionDir.FullName, "workspace.yaml");
                if (!File.Exists(workspaceFile)) continue;

                try
                {
                    // Extract the exact cwd value from workspace.yaml line-by-line
                    // to avoid false positives (e.g., "/project" matching "/project-2").
                    if (!WorkspaceYamlMatchesCwd(workspaceFile, workingDirectory))
                        continue;

                    long lineCount = 0;
                    try { lineCount = File.ReadLines(eventsFile).LongCount(); }
                    catch { continue; }

                    if (lineCount > bestLines)
                    {
                        bestId = sessionDir.Name;
                        bestLines = lineCount;
                    }
                }
                catch { /* skip unreadable sessions */ }
            }

            if (bestId != originalSessionId)
                Debug($"[BEST-EVENTS] Found better events source for cwd '{workingDirectory}': {bestId} ({bestLines} lines) vs original {originalSessionId} ({originalLines} lines)");

            return bestId;
        }
        catch (Exception ex)
        {
            Debug($"FindBestEventsSource failed: {ex.Message}");
            return originalSessionId;
        }
    }

    /// <summary>
    /// Check whether a workspace.yaml file's cwd value matches the given working directory exactly.
    /// Parses "cwd: /path/to/dir" lines and compares the extracted path, avoiding substring matches.
    /// </summary>
    internal static bool WorkspaceYamlMatchesCwd(string workspaceFile, string workingDirectory)
    {
        foreach (var line in File.ReadLines(workspaceFile))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("cwd:", StringComparison.OrdinalIgnoreCase))
                continue;
            // Extract the value after "cwd:" and trim whitespace/quotes
            var value = trimmed.Substring(4).Trim().Trim('"', '\'');
            return string.Equals(value, workingDirectory, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }
}
