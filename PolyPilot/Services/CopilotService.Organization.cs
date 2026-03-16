using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using PolyPilot.Models;

namespace PolyPilot.Services;

public enum OrchestratorPhase { Planning, Dispatching, WaitingForWorkers, Synthesizing, Complete, Resuming }

/// <summary>
/// Persisted state for an in-progress orchestration dispatch.
/// Saved to disk before workers are dispatched so the orchestration can be
/// resumed after an app relaunch.
/// </summary>
internal class PendingOrchestration
{
    public string GroupId { get; set; } = "";
    public string OrchestratorName { get; set; } = "";
    public List<string> WorkerNames { get; set; } = new();
    public string OriginalPrompt { get; set; } = "";
    public DateTime StartedAt { get; set; }
    /// <summary>True if this is an OrchestratorReflect dispatch (has reflection loop).</summary>
    public bool IsReflect { get; set; }
    /// <summary>Current reflection iteration (only meaningful for reflect mode).</summary>
    public int ReflectIteration { get; set; }
}

public partial class CopilotService
{
    public event Action<string, OrchestratorPhase, string?>? OnOrchestratorPhaseChanged; // groupId, phase, detail

    /// <summary>Maximum time a single worker is allowed to run before being cancelled.
    /// Set high (60 min) because the smart watchdog (events.jsonl freshness) handles dead
    /// session detection in ~90s. This is only an absolute backstop.</summary>
    private static readonly TimeSpan WorkerExecutionTimeout = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan WorkerExecutionTimeoutRemote = TimeSpan.FromMinutes(10);

    /// <summary>How long to poll for premature idle indicators after the initial TCS completes.
    /// Checks both WasPrematurelyIdled flag (set by EVT-REARM) and events.jsonl freshness
    /// (CLI still writing events). The events.jsonl check catches cases where EVT-REARM
    /// takes 30-60s to fire.</summary>
    internal const int PrematureIdleDetectionWindowMs = 10_000;

    /// <summary>If events.jsonl was modified within this many seconds of TCS completion,
    /// the worker is likely still active despite the premature session.idle.</summary>
    internal const int PrematureIdleEventsFileFreshnessSeconds = 15;

    /// <summary>Maximum time to wait for the worker's real completion after detecting a
    /// premature session.idle re-arm. Workers with long tool runs can take minutes.</summary>
    internal const int PrematureIdleRecoveryTimeoutMs = 300_000;

    // Per-session semaphores to prevent concurrent model switches during rapid dispatch
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _modelSwitchLocks = new();

    // Per-group semaphore to prevent concurrent reflect loop invocations.
    // Without this, a second user message while the loop is awaiting workers
    // starts a competing loop that races over shared ReflectionCycle state,
    // causing worker results to be silently lost.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _reflectLoopLocks = new();

    // Per-group semaphore to serialize orchestrator dispatches.
    // The bridge's send_message handler and event queue drain can both call
    // SendToMultiAgentGroupAsync for the same group; this ensures they run sequentially.
    // Concurrent callers wait in line rather than running simultaneously, which prevents
    // "Session already processing" errors from overlapping SendPromptAndWaitAsync calls.
    // New user messages sent while a dispatch is in progress execute after the current one completes.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _groupDispatchLocks = new();

    // Queued user prompts received while a reflect loop is running.
    // Drained at the start of each loop iteration and sent to the orchestrator
    // so the model sees them in its conversation context.
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _reflectQueuedPrompts = new();

    #region Session Organization (groups, pinning, sorting)

    public async Task<string> CreateMultiAgentGroupAsync(string groupName, string orchestratorModel, string workerModel, int workerCount, MultiAgentMode mode, string? systemPrompt = null)
    {
        // 1. Create the group
        var group = new SessionGroup
        {
            Id = Guid.NewGuid().ToString(),
            Name = groupName,
            IsMultiAgent = true,
            OrchestratorMode = mode,
            OrchestratorPrompt = systemPrompt,
            DefaultOrchestratorModel = orchestratorModel,
            DefaultWorkerModel = workerModel,
            SortOrder = Organization.Groups.Max(g => g.SortOrder) + 1
        };
        AddGroup(group);

        // 2. Create Orchestrator Session
        var orchName = $"{groupName}-Orchestrator";
        // Ensure name uniqueness
        int suffix = 1;
        while (_sessions.ContainsKey(orchName) || Organization.Sessions.Any(s => s.SessionName == orchName))
            orchName = $"{groupName}-Orchestrator-{suffix++}";

        var orchSession = await CreateSessionAsync(orchName, orchestratorModel, null); // Use default dir
        var orchMeta = GetOrCreateSessionMeta(orchSession.Name);
        orchMeta.GroupId = group.Id;
        orchMeta.Role = MultiAgentRole.Orchestrator;
        orchMeta.PreferredModel = orchestratorModel;

        // 3. Create Worker Sessions
        for (int i = 1; i <= workerCount; i++)
        {
            var workerName = $"{groupName}-Worker-{i}";
            suffix = 1;
            while (_sessions.ContainsKey(workerName) || Organization.Sessions.Any(s => s.SessionName == workerName))
                workerName = $"{groupName}-Worker-{i}-{suffix++}";

            var workerSession = await CreateSessionAsync(workerName, workerModel, null);
            var workerMeta = GetOrCreateSessionMeta(workerSession.Name);
            workerMeta.GroupId = group.Id;
            workerMeta.Role = MultiAgentRole.Worker;
            workerMeta.PreferredModel = workerModel;
        }

        SaveOrganization();
        FlushSaveOrganization();
        FlushSaveActiveSessionsToDisk();
        OnStateChanged?.Invoke();
        return group.Id;
    }

    private SessionMeta GetOrCreateSessionMeta(string sessionName)
    {
        var meta = Organization.Sessions.FirstOrDefault(m => m.SessionName == sessionName);
        if (meta == null)
        {
            meta = new SessionMeta { SessionName = sessionName, GroupId = SessionGroup.DefaultId };
            AddSessionMeta(meta);
        }
        return meta;
    }

    public void LoadOrganization()
    {
        try
        {
            if (File.Exists(OrganizationFile))
            {
                var json = File.ReadAllText(OrganizationFile);
                Organization = JsonSerializer.Deserialize<OrganizationState>(json) ?? new OrganizationState();
                Organization.DeletedRepoGroupRepoIds ??= new();
                Debug($"LoadOrganization: loaded {Organization.Groups.Count} groups, {Organization.Sessions.Count} sessions");
            }
            else
            {
                Organization = new OrganizationState();
            }
        }
        catch (Exception ex)
        {
            Debug($"Failed to load organization: {ex.Message}");
            Organization = new OrganizationState();
        }

        // Ensure default group always exists
        if (!Organization.Groups.Any(g => g.Id == SessionGroup.DefaultId))
        {
            InsertGroup(0, new SessionGroup
            {
                Id = SessionGroup.DefaultId,
                Name = SessionGroup.DefaultName,
                SortOrder = 0
            });
        }

        // NOTE: Do NOT call ReconcileOrganization() here — _sessions is empty at load time,
        // so reconciliation would prune all session metadata. Reconcile is called explicitly
        // after RestorePreviousSessionsAsync populates _sessions (line 403 and 533).

        // Self-healing: if any group has orchestrator/worker sessions but IsMultiAgent is false,
        // restore IsMultiAgent. This protects against stale debounce writes or serialization issues.
        foreach (var group in Organization.Groups)
        {
            if (!group.IsMultiAgent && Organization.Sessions.Any(m => m.GroupId == group.Id && m.Role == MultiAgentRole.Orchestrator))
            {
                Debug($"LoadOrganization: healing group '{group.Name}' (Id={group.Id}) — has orchestrator session but IsMultiAgent=false");
                group.IsMultiAgent = true;
            }
        }
    }

    public void SaveOrganization()
    {
        InvalidateOrganizedSessionsCache();
        // Debounce: restart the timer. The callback marshals to the UI thread for
        // serialization since Organization contains non-thread-safe List<T> collections.
        _saveOrgDebounce?.Dispose();
        _saveOrgDebounce = new Timer(_ => InvokeOnUI(() => SaveOrganizationCore()), null, 2000, Timeout.Infinite);
    }

    internal void FlushSaveOrganization()
    {
        _saveOrgDebounce?.Dispose();
        _saveOrgDebounce = null;
        SaveOrganizationCore();
    }

    private void SaveOrganizationCore()
    {
        try
        {
            // Snapshot under lock, serialize outside — keeps lock hold time minimal
            OrganizationState snapshot;
            lock (_organizationLock)
            {
                snapshot = new OrganizationState
                {
                    Groups = Organization.Groups.ToList(),
                    Sessions = Organization.Sessions.ToList(),
                    SortMode = Organization.SortMode,
                    DeletedRepoGroupRepoIds = new HashSet<string>(Organization.DeletedRepoGroupRepoIds)
                };
            }
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            WriteOrgFile(json);
        }
        catch (Exception ex)
        {
            Debug($"Failed to save organization: {ex.Message}");
        }
    }

    private void WriteOrgFile(string json)
    {
        try
        {
            Directory.CreateDirectory(PolyPilotBaseDir);
            // Atomic write: write to temp file then rename to prevent corruption on crash
            var tempFile = OrganizationFile + ".tmp";
            File.WriteAllText(tempFile, json);
            File.Move(tempFile, OrganizationFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug($"Failed to write organization file: {ex.Message}");
        }
    }

    /// <summary>
    /// Ensure every active session has a SessionMeta entry and clean up orphans.
    /// Only prunes metadata for sessions whose on-disk session directory no longer exists.
    /// Skips work if the active session set hasn't changed since last reconciliation.
    /// </summary>
    private int _lastReconcileSessionHash;
    internal void ReconcileOrganization(bool allowPruning = true)
    {
        var activeNames = _sessions.Where(kv => !kv.Value.Info.IsHidden).Select(kv => kv.Key).ToHashSet();

        // Safety: skip reconciliation during startup when sessions haven't been restored yet.
        // LoadOrganization loads the org before RestorePreviousSessionsAsync populates _sessions,
        // so reconciling then would prune all sessions. Use IsRestoring as the precise scope guard.
        if (IsRestoring && allowPruning)
        {
            Debug("ReconcileOrganization: skipping — session restore in progress");
            return;
        }
        // Pre-initialization guard: before RestorePreviousSessionsAsync runs, _sessions is empty
        // but Organization.Sessions still has metadata from disk. Don't prune in this window.
        // After initialization completes, zero active sessions means the user closed everything — allow cleanup.
        if (!IsInitialized && activeNames.Count == 0 && Organization.Sessions.Count > 0)
        {
            Debug("ReconcileOrganization: skipping — not yet initialized and no active sessions");
            return;
        }
        
        // Quick check: skip if active session set hasn't changed (order-independent additive hash)
        var currentHash = activeNames.Count;
        unchecked { foreach (var name in activeNames) currentHash += name.GetHashCode() * 31; }
        if (currentHash == _lastReconcileSessionHash && currentHash != 0) return;
        // Only update the hash when doing a full reconciliation (with pruning).
        // Additive-only calls (allowPruning=false) during restore must not poison the cache,
        // or the post-restore full reconciliation will be skipped via hash match. (PR #284 review)
        if (allowPruning) _lastReconcileSessionHash = currentHash;
        bool changed = false;

        // Build lookup of group IDs that must not be auto-reassigned by reconciliation.
        // Multi-agent and codespace group sessions must stay where they were placed.
        var multiAgentGroupIds = Organization.Groups.Where(g => g.IsMultiAgent).Select(g => g.Id).ToHashSet();
        var codespaceGroupIds = Organization.Groups.Where(g => g.IsCodespace).Select(g => g.Id).ToHashSet();
        var protectedGroupIds = multiAgentGroupIds.Union(codespaceGroupIds).ToHashSet();

        // Add missing sessions to default group and link to worktrees
        foreach (var name in activeNames)
        {
            var meta = Organization.Sessions.FirstOrDefault(m => m.SessionName == name);
            if (meta == null)
            {
                meta = new SessionMeta
                {
                    SessionName = name,
                    GroupId = SessionGroup.DefaultId
                };
                AddSessionMeta(meta);
                changed = true;
            }

            // Don't auto-reassign sessions that belong to a multi-agent or codespace group
            if (protectedGroupIds.Contains(meta.GroupId))
                continue;
            
            // Auto-link session to worktree if working directory matches
            if (meta.WorktreeId == null && _sessions.TryGetValue(name, out var sessionState))
            {
                var workingDir = sessionState.Info.WorkingDirectory;
                if (!string.IsNullOrEmpty(workingDir))
                {
                    var worktree = _repoManager.Worktrees.FirstOrDefault(w => 
                        workingDir.StartsWith(w.Path, StringComparison.OrdinalIgnoreCase));
                    if (worktree != null)
                    {
                        meta.WorktreeId = worktree.Id;
                        _repoManager.LinkSessionToWorktree(worktree.Id, name);
                        
                        // Move session to repo's group
                        var repo = _repoManager.Repositories.FirstOrDefault(r => r.Id == worktree.RepoId);
                        if (repo != null)
                        {
                            var repoGroup = GetOrCreateRepoGroup(repo.Id, repo.Name);
                            if (repoGroup != null)
                                meta.GroupId = repoGroup.Id;
                        }
                        changed = true;
                    }
                }
            }

            // Ensure sessions with worktrees are in the correct repo group.
            // Skip sessions that were part of a multi-agent team (identifiable by having
            // an Orchestrator role or a PreferredModel set — regular sessions never have these).
            bool wasMultiAgent = meta.Role == MultiAgentRole.Orchestrator || meta.PreferredModel != null;
            if (meta.WorktreeId != null && meta.GroupId == SessionGroup.DefaultId && !wasMultiAgent)
            {
                var worktree = _repoManager.Worktrees.FirstOrDefault(w => w.Id == meta.WorktreeId);
                if (worktree != null)
                {
                    var repo = _repoManager.Repositories.FirstOrDefault(r => r.Id == worktree.RepoId);
                    if (repo != null)
                    {
                        var repoGroup = GetOrCreateRepoGroup(repo.Id, repo.Name);
                        if (repoGroup != null)
                        {
                            meta.GroupId = repoGroup.Id;
                            changed = true;
                        }
                    }
                }
            }
        }

        // Fix sessions pointing to deleted groups
        var groupIds = Organization.Groups.Select(g => g.Id).ToHashSet();
        foreach (var meta in Organization.Sessions)
        {
            if (!groupIds.Contains(meta.GroupId))
            {
                Debug($"ReconcileOrganization: orphaned session '{meta.SessionName}' (GroupId={meta.GroupId}) → _default");
                meta.GroupId = SessionGroup.DefaultId;
                changed = true;
            }
        }

        // Ensure every tracked repo has a sidebar group (unless user deleted it)
        foreach (var repo in _repoManager.Repositories)
        {
            if (!Organization.Groups.Any(g => g.RepoId == repo.Id && !g.IsMultiAgent))
            {
                if (GetOrCreateRepoGroup(repo.Id, repo.Name) != null)
                    changed = true;
            }
        }

        // Build the full set of known session names: active sessions + aliases (persisted names)
        var knownNames = new HashSet<string>(activeNames);
        try
        {
            var aliases = LoadAliases();
            foreach (var alias in aliases.Values)
                knownNames.Add(alias);

            // Also include display names from the active-sessions file (covers sessions not yet resumed)
            if (File.Exists(ActiveSessionsFile))
            {
                var json = File.ReadAllText(ActiveSessionsFile);
                var entries = JsonSerializer.Deserialize<List<ActiveSessionEntry>>(json);
                if (entries != null)
                {
                    foreach (var e in entries)
                        knownNames.Add(e.DisplayName);
                }
            }
        }
        catch (Exception ex)
        {
            Debug($"ReconcileOrganization: error loading known names, skipping prune: {ex.Message}");
            // If we can't determine known names, don't prune anything
            if (changed) SaveOrganization();
            return;
        }

        // Protect multi-agent group sessions from pruning — they may not yet be in
        // active-sessions.json if the app was killed before the debounce timer fired.
        // The authoritative source for these sessions is organization.json itself.
        var protectedNames = new HashSet<string>(
            Organization.Sessions
                .Where(m => multiAgentGroupIds.Contains(m.GroupId))
                .Select(m => m.SessionName));

        // Remove metadata only for sessions that are truly gone (not in any known set)
        if (allowPruning)
        {
            var toRemove = Organization.Sessions.Where(m => !knownNames.Contains(m.SessionName) && !protectedNames.Contains(m.SessionName)).ToList();
            if (toRemove.Count > 0)
            {
                Debug($"ReconcileOrganization: pruning {toRemove.Count} sessions: {string.Join(", ", toRemove.Select(m => m.SessionName))}");
                changed = true;
            }
            RemoveSessionMetasWhere(m => !knownNames.Contains(m.SessionName) && !protectedNames.Contains(m.SessionName));
        }

        if (changed) SaveOrganization();
    }

    public void PinSession(string sessionName, bool pinned)
    {
        var meta = Organization.Sessions.FirstOrDefault(m => m.SessionName == sessionName);
        if (meta != null)
        {
            meta.IsPinned = pinned;
            SaveOrganization();
            OnStateChanged?.Invoke();
        }
    }

    public void MoveSession(string sessionName, string groupId)
    {
        if (!Organization.Groups.Any(g => g.Id == groupId))
            return;

        var meta = Organization.Sessions.FirstOrDefault(m => m.SessionName == sessionName);
        if (meta == null)
        {
            // Session exists but wasn't reconciled yet — create meta on the fly
            meta = new SessionMeta { SessionName = sessionName, GroupId = groupId };
            AddSessionMeta(meta);
        }
        else
        {
            meta.GroupId = groupId;
        }

        SaveOrganization();
        OnStateChanged?.Invoke();
    }

    public SessionGroup CreateGroup(string name)
    {
        var group = new SessionGroup
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            SortOrder = Organization.Groups.Max(g => g.SortOrder) + 1
        };
        AddGroup(group);
        SaveOrganization();
        OnStateChanged?.Invoke();
        return group;
    }

    public void RenameGroup(string groupId, string name)
    {
        var group = Organization.Groups.FirstOrDefault(g => g.Id == groupId);
        if (group != null)
        {
            group.Name = name;
            SaveOrganization();
            FlushSaveOrganization();
            OnStateChanged?.Invoke();
        }
    }

    public void DeleteGroup(string groupId)
    {
        if (groupId == SessionGroup.DefaultId) return;

        // Provider groups are managed by their plugin — can't be deleted while plugin is loaded
        if (GetProviderForGroup(groupId) != null) return;

        var group = Organization.Groups.FirstOrDefault(g => g.Id == groupId);
        var isMultiAgent = group?.IsMultiAgent ?? false;

        // Collect all worktree IDs for cleanup before removing metadata
        var worktreeIds = new HashSet<string>();
        if (group?.WorktreeId != null) worktreeIds.Add(group.WorktreeId);
        // CreatedWorktreeIds is the authoritative list (covers cases where session creation failed)
        if (group?.CreatedWorktreeIds != null)
            foreach (var id in group.CreatedWorktreeIds) worktreeIds.Add(id);
        foreach (var m in Organization.Sessions.Where(m => m.GroupId == groupId))
            if (m.WorktreeId != null) worktreeIds.Add(m.WorktreeId);

        if (isMultiAgent || group?.IsCodespace == true)
        {
            // Multi-agent and codespace sessions are bound to their group — close them
            var sessionNames = Organization.Sessions
                .Where(m => m.GroupId == groupId)
                .Select(m => m.SessionName)
                .ToList();
            // Remove org metadata first so UI updates immediately
            RemoveSessionMetasWhere(m => sessionNames.Contains(m.SessionName));
            // Mark sessions as hidden so ReconcileOrganization won't re-add them
            // to the default group while CloseSessionAsync is still running
            foreach (var name in sessionNames)
            {
                if (_sessions.TryGetValue(name, out var s))
                {
                    s.Info.IsHidden = true;
                    // Track as closed so merge won't re-add from active-sessions.json on restart
                    if (s.Info.SessionId != null)
                        _closedSessionIds[s.Info.SessionId] = 0;
                }
            }
            // Persist immediately so hidden sessions are excluded if app restarts
            // before the fire-and-forget CloseSessionAsync completes
            SaveActiveSessionsToDisk();
            FlushSaveActiveSessionsToDisk();
            // Fire-and-forget: close sessions then remove worktrees
            // Snapshot worktree IDs — removal must be sequential (not parallel)
            // because RepoManager._state.Worktrees is a plain List<T>.
            var wtIdsSnapshot = worktreeIds.ToList();
            var isRemote = IsRemoteMode;
            _ = Task.Run(async () =>
            {
                foreach (var name in sessionNames)
                    try { await CloseSessionCoreAsync(name, notifyUi: false); } catch (Exception ex) { Debug($"DeleteGroup: failed to close '{name}': {ex.Message}"); }
                // Clean up worktrees sequentially after all sessions are closed
                foreach (var wtId in wtIdsSnapshot)
                {
                    try
                    {
                        if (isRemote)
                            await _bridgeClient.RemoveWorktreeAsync(wtId, deleteBranch: true);
                        else
                            await _repoManager.RemoveWorktreeAsync(wtId, deleteBranch: true);
                    }
                    catch (Exception ex) { Debug($"DeleteGroup: failed to remove worktree '{wtId}': {ex.Message}"); }
                }
            });
        }
        else
        {
            // Non-multi-agent: move sessions to default group
            foreach (var meta in Organization.Sessions.Where(m => m.GroupId == groupId))
            {
                meta.GroupId = SessionGroup.DefaultId;
                // Clear worktree link for repo groups so ReconcileOrganization
                // won't reassign these sessions back to a recreated repo group
                if (group?.RepoId != null)
                    meta.WorktreeId = null;
            }
        }

        // Track deleted repo groups so ReconcileOrganization won't resurrect them.
        // Only tombstone for regular repo groups — multi-agent groups share the same RepoId key
        // but are tracked separately, so deleting a squad must not suppress the regular sidebar group.
        if (group?.RepoId != null && !isMultiAgent)
            Organization.DeletedRepoGroupRepoIds.Add(group.RepoId);

        RemoveGroupsWhere(g => g.Id == groupId);


        // Clean up codespace tunnel and client if applicable
        if (_codespaceClients.TryRemove(groupId, out var csClient))
            _ = Task.Run(async () => { try { await csClient.DisposeAsync(); } catch { } });
        if (_tunnelHandles.TryRemove(groupId, out var tunnel))
            _ = Task.Run(async () => { try { await tunnel.DisposeAsync(); } catch { } });

        // Clean up per-group caches to prevent memory leaks
        _reflectLoopLocks.TryRemove(groupId, out _);
        _reflectQueuedPrompts.TryRemove(groupId, out _);
        SaveOrganization();
        FlushSaveOrganization();
        OnStateChanged?.Invoke();
    }

    public void ToggleGroupCollapsed(string groupId)
    {
        var group = Organization.Groups.FirstOrDefault(g => g.Id == groupId);
        if (group != null)
        {
            group.IsCollapsed = !group.IsCollapsed;
            SaveOrganization();
            OnStateChanged?.Invoke();
        }
    }

    public void ToggleUnpinnedCollapsed(string groupId)
    {
        var group = Organization.Groups.FirstOrDefault(g => g.Id == groupId);
        if (group != null)
        {
            group.UnpinnedCollapsed = !group.UnpinnedCollapsed;
            SaveOrganization();
            OnStateChanged?.Invoke();
        }
    }

    public void SetSortMode(SessionSortMode mode)
    {
        Organization.SortMode = mode;
        SaveOrganization();
        OnStateChanged?.Invoke();
    }

    public void SetSessionManualOrder(string sessionName, int order)
    {
        var meta = Organization.Sessions.FirstOrDefault(m => m.SessionName == sessionName);
        if (meta != null)
        {
            meta.ManualOrder = order;
            SaveOrganization();
        }
    }

    public void SetGroupOrder(string groupId, int order)
    {
        var group = Organization.Groups.FirstOrDefault(g => g.Id == groupId);
        if (group != null)
        {
            group.SortOrder = order;
            SaveOrganization();
            OnStateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Returns sessions organized by group, with pinned sessions first and sorted by the current sort mode.
    /// Results are cached and invalidated when sessions or organization change.
    /// </summary>
    private List<(SessionGroup Group, List<AgentSessionInfo> Sessions)>? _organizedSessionsCache;
    private int _organizedSessionsCacheKey;

    public void InvalidateOrganizedSessionsCache() => _organizedSessionsCache = null;

    public IReadOnlyList<(SessionGroup Group, List<AgentSessionInfo> Sessions)> GetOrganizedSessions()
    {
        // Compute a lightweight cache key from session count + group count + sort mode
        var key = HashCode.Combine(_sessions.Count, Organization.Groups.Count, Organization.SortMode);
        foreach (var s in _sessions) key = HashCode.Combine(key, s.Key.GetHashCode(), s.Value.Info.IsProcessing ? 1 : 0);

        if (_organizedSessionsCache != null && key == _organizedSessionsCacheKey)
            return _organizedSessionsCache;

        var metas = Organization.Sessions.ToDictionary(m => m.SessionName);
        var allSessions = GetAllSessions().ToList();
        var result = new List<(SessionGroup Group, List<AgentSessionInfo> Sessions)>();

        foreach (var group in Organization.Groups.OrderBy(g => g.SortOrder))
        {
            var groupSessions = allSessions
                .Where(s => metas.TryGetValue(s.Name, out var m) && m.GroupId == group.Id)
                .ToList();

            var sorted = groupSessions
                .OrderByDescending(s => metas.TryGetValue(s.Name, out var m) && m.IsPinned)
                .ThenBy(s => ApplySort(s, metas))
                .ToList();

            result.Add((group, sorted));
        }

        _organizedSessionsCache = result;
        _organizedSessionsCacheKey = key;
        return result;
    }

    private object ApplySort(AgentSessionInfo session, Dictionary<string, SessionMeta> metas)
    {
        return Organization.SortMode switch
        {
            SessionSortMode.LastActive => DateTime.MaxValue - session.LastUpdatedAt,
            SessionSortMode.CreatedAt => DateTime.MaxValue - session.CreatedAt,
            SessionSortMode.Alphabetical => session.Name,
            SessionSortMode.Manual => (object)(metas.TryGetValue(session.Name, out var m) ? m.ManualOrder : int.MaxValue),
            _ => DateTime.MaxValue - session.LastUpdatedAt
        };
    }

    public bool HasMultipleGroups => Organization.Groups.Count > 1;

    public SessionMeta? GetSessionMeta(string sessionName) =>
        Organization.Sessions.FirstOrDefault(m => m.SessionName == sessionName);

    /// <summary>
    /// Resolves the GitHub repo URL for a session (via WorktreeId or group RepoId).
    /// Returns null if the session has no associated repo.
    /// </summary>
    public string? GetRepoUrlForSession(string sessionName)
    {
        var meta = GetSessionMeta(sessionName);
        if (meta == null) return null;

        // Try via WorktreeId first
        if (meta.WorktreeId != null)
        {
            var wt = _repoManager.Worktrees.FirstOrDefault(w => w.Id == meta.WorktreeId);
            if (wt != null)
            {
                var repo = _repoManager.Repositories.FirstOrDefault(r => r.Id == wt.RepoId);
                if (repo != null) return repo.Url;
            }
        }

        // Fall back to group's RepoId
        var group = Organization.Groups.FirstOrDefault(g => g.Id == meta.GroupId);
        if (group?.RepoId != null)
        {
            var repo = _repoManager.Repositories.FirstOrDefault(r => r.Id == group.RepoId);
            if (repo != null) return repo.Url;
        }

        return null;
    }

    /// <summary>
    /// Check whether a session belongs to a multi-agent group.
    /// Used by the watchdog to apply the longer timeout for orchestrated workers.
    /// </summary>
    internal bool IsSessionInMultiAgentGroup(string sessionName)
    {
        var meta = Organization.Sessions.FirstOrDefault(m => m.SessionName == sessionName);
        if (meta == null) return false;
        var group = Organization.Groups.FirstOrDefault(g => g.Id == meta.GroupId);
        return group?.IsMultiAgent == true;
    }

    /// <summary>
    /// Check whether a session is a worker in a multi-agent group.
    /// Used by notification filtering to suppress noisy worker notifications.
    /// Best-effort read — returns false on any error (safe to call from background threads).
    /// </summary>
    internal bool IsWorkerInMultiAgentGroup(string sessionName)
    {
        try
        {
            var org = Organization;
            var sessions = org.Sessions.ToArray();
            var groups = org.Groups.ToArray();
            var meta = sessions.FirstOrDefault(m => m.SessionName == sessionName);
            if (meta == null) return false;
            var group = groups.FirstOrDefault(g => g.Id == meta.GroupId);
            return group?.IsMultiAgent == true && meta.Role == MultiAgentRole.Worker;
        }
        catch { return false; }
    }

    /// <summary>
    /// Get or create a SessionGroup that auto-tracks a repository.
    /// When <paramref name="explicitly"/> is false (called from ReconcileOrganization),
    /// returns null for repos whose groups were previously deleted by the user.
    /// When true (called from explicit repo-add operations), clears the deleted flag.
    /// </summary>
    public SessionGroup? GetOrCreateRepoGroup(string repoId, string repoName, bool explicitly = false)
    {
        // Skip multi-agent groups — they have a RepoId for worktree context but are
        // not the "repo group" that regular sessions should auto-join.
        // Also skip groups that have orchestrator/worker sessions (defensive: protects against
        // IsMultiAgent being lost due to stale writes or serialization issues).
        var existing = Organization.Groups.FirstOrDefault(g => g.RepoId == repoId && !g.IsMultiAgent
            && !Organization.Sessions.Any(m => m.GroupId == g.Id && m.Role == MultiAgentRole.Orchestrator));
        if (existing != null) return existing;

        // Don't recreate groups the user explicitly deleted (unless re-adding)
        if (!explicitly && Organization.DeletedRepoGroupRepoIds.Contains(repoId))
            return null;

        // Clear the deleted flag when explicitly re-adding
        Organization.DeletedRepoGroupRepoIds.Remove(repoId);

        var group = new SessionGroup
        {
            Id = Guid.NewGuid().ToString(),
            Name = repoName,
            RepoId = repoId,
            SortOrder = Organization.Groups.Max(g => g.SortOrder) + 1
        };
        AddGroup(group);
        SaveOrganization();
        OnStateChanged?.Invoke();
        return group;
    }

    #endregion

    #region Multi-Agent Orchestration

    /// <summary>
    /// Create a multi-agent group and optionally move existing sessions into it.
    /// </summary>
    public SessionGroup CreateMultiAgentGroup(string name, MultiAgentMode mode = MultiAgentMode.Broadcast, string? orchestratorPrompt = null, List<string>? sessionNames = null, string? worktreeId = null, string? repoId = null)
    {
        var group = new SessionGroup
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            IsMultiAgent = true,
            OrchestratorMode = mode,
            OrchestratorPrompt = orchestratorPrompt,
            WorktreeId = worktreeId,
            RepoId = repoId,
            SortOrder = Organization.Groups.Any() ? Organization.Groups.Max(g => g.SortOrder) + 1 : 0
        };
        AddGroup(group);

        if (sessionNames != null)
        {
            foreach (var sessionName in sessionNames)
            {
                var meta = Organization.Sessions.FirstOrDefault(m => m.SessionName == sessionName);
                if (meta != null)
                {
                    meta.GroupId = group.Id;
                    if (worktreeId != null)
                        meta.WorktreeId = worktreeId;
                }
            }
        }

        // Multi-agent group creation is a critical structural change — flush immediately
        // instead of relying on the 2s debounce. If the process is killed (e.g., relaunch),
        // the debounce timer never fires and the group is lost on restart.
        SaveOrganization();
        FlushSaveOrganization();
        OnStateChanged?.Invoke();
        return group;
    }

    /// <summary>
    /// Convert an existing regular group into a multi-agent group.
    /// </summary>
    public void ConvertToMultiAgent(string groupId)
    {
        var group = Organization.Groups.FirstOrDefault(g => g.Id == groupId);
        if (group == null || group.IsMultiAgent) return;
        group.IsMultiAgent = true;
        group.OrchestratorMode = MultiAgentMode.Broadcast;
        SaveOrganization();
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Set the orchestration mode for a multi-agent group.
    /// </summary>
    public void SetMultiAgentMode(string groupId, MultiAgentMode mode)
    {
        var group = Organization.Groups.FirstOrDefault(g => g.Id == groupId);
        if (group != null && group.IsMultiAgent)
        {
            group.OrchestratorMode = mode;
            SaveOrganization();
            OnStateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Set the role of a session within a multi-agent group.
    /// When promoting to Orchestrator, any existing orchestrator in the same group is demoted to Worker.
    /// </summary>
    public void SetSessionRole(string sessionName, MultiAgentRole role)
    {
        var meta = Organization.Sessions.FirstOrDefault(m => m.SessionName == sessionName);
        if (meta == null) return;

        var oldRole = meta.Role;

        // Enforce single orchestrator per group
        if (role == MultiAgentRole.Orchestrator)
        {
            var group = Organization.Groups.FirstOrDefault(g => g.Id == meta.GroupId);
            if (group is { IsMultiAgent: true })
            {
                foreach (var other in Organization.Sessions
                    .Where(m => m.GroupId == meta.GroupId && m.SessionName != sessionName && m.Role == MultiAgentRole.Orchestrator))
                {
                    other.Role = MultiAgentRole.Worker;
                }
            }
        }

        meta.Role = role;
        SaveOrganization();
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Get all session names in a multi-agent group.
    /// </summary>
    public List<string> GetMultiAgentGroupMembers(string groupId)
    {
        return Organization.Sessions
            .Where(m => m.GroupId == groupId)
            .Select(m => m.SessionName)
            .ToList();
    }

    /// <summary>
    /// Get the orchestrator session name for an orchestrator-mode group, if any.
    /// </summary>
    public string? GetOrchestratorSession(string groupId)
    {
        return Organization.Sessions
            .FirstOrDefault(m => m.GroupId == groupId && m.Role == MultiAgentRole.Orchestrator)
            ?.SessionName;
    }

    /// <summary>Log routing decision for dispatch debugging (goes to event-diagnostics.log).</summary>
    public void LogDispatchRoute(string sessionName, bool hasMeta, string? groupName, bool? isMulti, MultiAgentMode? mode, string? orchSession, bool isOrch)
    {
        Debug($"[DISPATCH-ROUTE] session='{sessionName}' hasMeta={hasMeta} group='{groupName}' isMulti={isMulti} mode={mode} orchSession='{orchSession}' isOrch={isOrch}");
    }

    /// <summary>
    /// Returns the group ID if the given session is an orchestrator in an active multi-agent group.
    /// Used by the message queue drain to route dequeued messages through the dispatch pipeline.
    /// </summary>
    public string? GetOrchestratorGroupId(string sessionName)
    {
        var meta = GetSessionMeta(sessionName);
        if (meta?.GroupId == null) return null;
        var group = Organization.Groups.FirstOrDefault(g => g.Id == meta.GroupId);
        if (group is not { IsMultiAgent: true }) return null;
        if (group.OrchestratorMode != MultiAgentMode.Orchestrator && group.OrchestratorMode != MultiAgentMode.OrchestratorReflect) return null;
        var orchSession = GetOrchestratorSession(group.Id);
        return orchSession == sessionName ? group.Id : null;
    }

    /// <summary>
    /// Send a prompt to all sessions in a multi-agent group based on its orchestration mode.
    /// </summary>
    public async Task SendToMultiAgentGroupAsync(string groupId, string prompt, CancellationToken cancellationToken = default)
    {
        var group = Organization.Groups.FirstOrDefault(g => g.Id == groupId && g.IsMultiAgent);
        if (group == null) { Debug($"[DISPATCH] SendToMultiAgentGroupAsync: group '{groupId}' not found or not multi-agent"); return; }

        var members = GetMultiAgentGroupMembers(groupId);
        if (members.Count == 0) { Debug($"[DISPATCH] SendToMultiAgentGroupAsync: no members for group '{group.Name}'"); return; }

        // Serialize dispatches to the same group (bridge + event queue drain race).
        // Callers wait their turn rather than being dropped.
        var dispatchLock = _groupDispatchLocks.GetOrAdd(groupId, _ => new SemaphoreSlim(1, 1));
        await dispatchLock.WaitAsync(cancellationToken);

        try
        {
            Debug($"[DISPATCH] SendToMultiAgentGroupAsync: group='{group.Name}', mode={group.OrchestratorMode}, members={members.Count}");

            switch (group.OrchestratorMode)
            {
                case MultiAgentMode.Broadcast:
                    await SendBroadcastAsync(group, members, prompt, cancellationToken);
                    break;

                case MultiAgentMode.Sequential:
                    await SendSequentialAsync(group, members, prompt, cancellationToken);
                    break;

                case MultiAgentMode.Orchestrator:
                    await SendViaOrchestratorAsync(groupId, members, prompt, cancellationToken);
                    break;

                case MultiAgentMode.OrchestratorReflect:
                    await SendViaOrchestratorReflectAsync(groupId, members, prompt, cancellationToken);
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug($"[DISPATCH] SendToMultiAgentGroupAsync FAILED: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            dispatchLock.Release();
        }
    }

    /// <summary>
    /// Build a multi-agent context prefix for a session in a group.
    /// Includes model info for each member so agents know each other's capabilities.
    /// </summary>
    private string BuildMultiAgentPrefix(string sessionName, SessionGroup group, List<string> allMembers)
    {
        var meta = Organization.Sessions.FirstOrDefault(m => m.SessionName == sessionName);
        var role = meta?.Role ?? MultiAgentRole.Worker;
        var roleName = role == MultiAgentRole.Orchestrator ? "orchestrator" : "worker";
        var memberDetails = allMembers.Where(m => m != sessionName)
            .Select(m => $"'{m}' ({GetEffectiveModel(m)})")
            .ToList();
        var othersList = memberDetails.Count > 0 ? string.Join(", ", memberDetails) : "none";
        return $"[Multi-agent context: You are '{sessionName}' ({roleName}, {GetEffectiveModel(sessionName)}) in group '{group.Name}'. Other members: {othersList}.]\n\n";
    }

    private async Task SendBroadcastAsync(SessionGroup group, List<string> sessionNames, string prompt, CancellationToken cancellationToken)
    {
        var tasks = sessionNames.Select(async name =>
        {
            var session = GetSession(name);
            if (session == null) return;

            await EnsureSessionModelAsync(name, cancellationToken);
            var prefixedPrompt = BuildMultiAgentPrefix(name, group, sessionNames) + prompt;

            if (session.IsProcessing)
            {
                EnqueueMessage(name, prefixedPrompt);
                return;
            }

            try
            {
                await SendPromptAsync(name, prefixedPrompt, cancellationToken: cancellationToken, originalPrompt: prompt);
            }
            catch (Exception ex)
            {
                Debug($"Broadcast send failed for '{name}': {ex.Message}");
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task SendSequentialAsync(SessionGroup group, List<string> sessionNames, string prompt, CancellationToken cancellationToken)
    {
        foreach (var name in sessionNames)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var session = GetSession(name);
            if (session == null) continue;

            await EnsureSessionModelAsync(name, cancellationToken);
            var prefixedPrompt = BuildMultiAgentPrefix(name, group, sessionNames) + prompt;

            if (session.IsProcessing)
            {
                EnqueueMessage(name, prefixedPrompt);
                continue;
            }

            try
            {
                await SendPromptAsync(name, prefixedPrompt, cancellationToken: cancellationToken, originalPrompt: prompt);
            }
            catch (Exception ex)
            {
                Debug($"Sequential send failed for '{name}': {ex.Message}");
            }
        }
    }

    private async Task SendViaOrchestratorAsync(string groupId, List<string> members, string prompt, CancellationToken cancellationToken)
    {
        var group = Organization.Groups.FirstOrDefault(g => g.Id == groupId);
        var orchestratorName = GetOrchestratorSession(groupId);
        if (orchestratorName == null)
        {
            // Fall back to broadcast if no orchestrator is designated
            if (group != null)
                await SendBroadcastAsync(group, members, prompt, cancellationToken);
            return;
        }

        var workerNames = members.Where(m => m != orchestratorName).ToList();

        // Phase 1: Planning — ask orchestrator to analyze and assign tasks
        InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(groupId, OrchestratorPhase.Planning, null));

        var planningPrompt = BuildOrchestratorPlanningPrompt(prompt, workerNames, group?.OrchestratorPrompt, group?.RoutingContext);
        // Enable early dispatch for the non-reflect orchestrator flow too
        if (_sessions.TryGetValue(orchestratorName, out var orchPlanning))
            orchPlanning.EarlyDispatchOnWorkerBlocks = true;
        var planResponse = await SendPromptAndWaitAsync(orchestratorName, planningPrompt, cancellationToken, originalPrompt: prompt);

        // Phase 2: Parse task assignments from orchestrator response.
        // If the orchestrator assigns fewer workers than available, respect that decision —
        // not every request needs all workers (e.g., "post a comment on PR #341" only needs
        // the worker that reviewed that PR). Only nudge when ZERO assignments (format failure).
        var allAssignments = new List<TaskAssignment>();
        var dispatchedWorkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var rawAssignments = ParseTaskAssignments(planResponse, workerNames);
        Debug($"[DISPATCH] '{orchestratorName}' iteration 0: {rawAssignments.Count} raw assignments. Response length={planResponse.Length}");

        var iterAssignments = DeduplicateAssignments(rawAssignments, dispatchedWorkers);

        if (iterAssignments.Count == 0)
        {
            // First pass produced nothing — send a single nudge (format failure recovery)
            Debug($"[DISPATCH] No assignments parsed. Sending delegation nudge.");
            var nudgePrompt = BuildDelegationNudgePrompt(workerNames);
            var nudgeResponse = await SendPromptAndWaitAsync(orchestratorName, nudgePrompt, cancellationToken, originalPrompt: prompt);
            iterAssignments = DeduplicateAssignments(ParseTaskAssignments(nudgeResponse, workerNames), dispatchedWorkers);
            Debug($"[DISPATCH] Nudge parsed: {iterAssignments.Count} assignments.");

            if (iterAssignments.Count == 0)
            {
                // Still nothing after nudge — force a second nudge reminding it MUST delegate
                Debug($"[DISPATCH] No assignments after nudge. Sending final delegation reminder.");
                var finalNudge = "You MUST delegate this task to at least one worker using @worker:name...@end blocks. You cannot do it yourself. Pick the most relevant worker and assign the task now.";
                var finalResponse = await SendPromptAndWaitAsync(orchestratorName, finalNudge, cancellationToken, originalPrompt: prompt);
                iterAssignments = DeduplicateAssignments(ParseTaskAssignments(finalResponse, workerNames), dispatchedWorkers);
                Debug($"[DISPATCH] Final nudge parsed: {iterAssignments.Count} assignments.");

                if (iterAssignments.Count == 0)
                {
                    Debug($"[DISPATCH] No assignments after all nudges. Cannot proceed without delegation.");
                    AddOrchestratorSystemMessage(orchestratorName, "⚠️ Failed to delegate — orchestrator did not produce any @worker assignments after multiple attempts.");
                    InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(groupId, OrchestratorPhase.Complete, null));
                    return;
                }
            }
        }

        allAssignments.AddRange(iterAssignments);
        foreach (var a in iterAssignments)
            dispatchedWorkers.Add(a.WorkerName);

        Debug($"[DISPATCH] Orchestrator assigned {dispatchedWorkers.Count}/{workerNames.Count} workers. Respecting partial assignment.");

        var assignments = allAssignments;

        // Phase 3: Dispatch tasks to workers in parallel
        Debug($"[DISPATCH] Dispatching {assignments.Count} tasks: {string.Join(", ", assignments.Select(a => a.WorkerName))}");
        InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(groupId, OrchestratorPhase.Dispatching,
            $"Sending tasks to {assignments.Count} worker(s)"));

        // Persist dispatch state BEFORE dispatching — if the app is relaunched while
        // workers are processing, we can resume and collect their results.
        SavePendingOrchestration(new PendingOrchestration
        {
            GroupId = groupId,
            OrchestratorName = orchestratorName,
            WorkerNames = assignments.Select(a => a.WorkerName).ToList(),
            OriginalPrompt = prompt,
            StartedAt = DateTime.UtcNow
        });

        try
        {
            InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(groupId, OrchestratorPhase.WaitingForWorkers, null));

            Debug($"[DISPATCH] Staggering {assignments.Count} workers with 1s delay");
            var workerTasks = new List<Task<WorkerResult>>();
            foreach (var a in assignments)
            {
                workerTasks.Add(ExecuteWorkerAsync(a.WorkerName, a.Task, prompt, cancellationToken));
                if (workerTasks.Count < assignments.Count)
                    await Task.Delay(1000, cancellationToken);
            }
            var results = await Task.WhenAll(workerTasks);

            // After early dispatch, the orchestrator may still be doing tool work.
            await WaitForSessionIdleAsync(orchestratorName, cancellationToken);

            // Phase 4: Synthesize — send worker results back to orchestrator
            InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(groupId, OrchestratorPhase.Synthesizing, null));

            var synthesisPrompt = BuildSynthesisPrompt(prompt, results.ToList());
            await SendPromptAsync(orchestratorName, synthesisPrompt, cancellationToken: cancellationToken, originalPrompt: prompt);
        }
        finally
        {
            ClearPendingOrchestration();
            InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(groupId, OrchestratorPhase.Complete, null));
        }
    }

    private string BuildOrchestratorPlanningPrompt(string userPrompt, List<string> workerNames, string? additionalInstructions, string? routingContext = null, bool dispatcherOnly = false)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"You are the orchestrator of a multi-agent group. You have {workerNames.Count} worker agent(s) available:");
        foreach (var w in workerNames)
        {
            var meta = GetSessionMeta(w);
            var model = GetEffectiveModel(w);
            var wtInfo = meta?.WorktreeId != null
                ? _repoManager.Worktrees.FirstOrDefault(wt => wt.Id == meta.WorktreeId) : null;
            var desc = $"  - '{w}' (model: {model})";
            if (!string.IsNullOrEmpty(meta?.SystemPrompt))
                desc += $" — {meta.SystemPrompt}";
            if (wtInfo != null)
                desc += $" [isolated worktree: {wtInfo.Path}, branch: {wtInfo.Branch}]";
            sb.AppendLine(desc);
        }
        sb.AppendLine();
        sb.AppendLine("Route tasks to workers based on their specialization. If a worker has a described role, assign tasks that match their expertise.");
        sb.AppendLine();
        sb.AppendLine("## User Request");
        sb.AppendLine(userPrompt);
        if (!string.IsNullOrEmpty(additionalInstructions))
        {
            sb.AppendLine();
            sb.AppendLine("## Additional Orchestration Instructions");
            sb.AppendLine(additionalInstructions);
        }
        if (!string.IsNullOrEmpty(routingContext))
        {
            sb.AppendLine();
            sb.AppendLine("## Work Routing (from team definition)");
            sb.AppendLine(routingContext);
        }
        sb.AppendLine();
        sb.AppendLine("## Your Task");
        if (dispatcherOnly)
        {
            sb.AppendLine("You are a DISPATCHER ONLY. You do NOT have tools. You CANNOT write code, read files, or run commands yourself.");
            sb.AppendLine("Your ONLY job is to write @worker/@end blocks that assign work to your workers.");
            sb.AppendLine("⚠️ This is a NEW request. Even if your conversation history shows previous work on a similar topic, you MUST delegate FRESH tasks to your workers. Previous results may be stale.");
        }
        else
        {
            sb.AppendLine("You are a DISPATCHER. Break the request into tasks and assign them to workers via @worker blocks.");
            sb.AppendLine("You MUST always delegate work to workers. Do NOT attempt to do the work yourself.");
        }
        sb.AppendLine();
        sb.AppendLine("IMPORTANT: Each worker MUST receive a DIFFERENT sub-task. Do NOT assign the same work to two workers.");
        sb.AppendLine("If the request has fewer sub-tasks than workers, only assign to the workers you need.");
        sb.AppendLine();
        sb.AppendLine("Use this exact format for each assignment:");
        sb.AppendLine();
        sb.AppendLine("@worker:worker-name");
        sb.AppendLine("Detailed task description for this worker.");
        sb.AppendLine("@end");
        sb.AppendLine();
        sb.AppendLine($"IMPORTANT: Assign ALL workers that have relevant work IN THIS SINGLE RESPONSE — produce multiple @worker blocks now, not one at a time.");
        sb.AppendLine("However, only assign workers that have RELEVANT work to do. If the task only needs one worker (e.g., a follow-up on a specific PR), assign just that one worker — the one with the right context.");
        sb.AppendLine("Each worker retains conversation history from previous turns, so prefer the worker who already worked on the relevant topic.");
        sb.AppendLine("You may include brief analysis before the @worker blocks, but every response MUST contain @worker blocks for all workers you intend to use.");
        sb.AppendLine("NEVER attempt to do the work yourself. ALWAYS delegate via @worker blocks.");
        return sb.ToString();
    }

    internal record TaskAssignment(string WorkerName, string Task);

    /// <summary>Deduplicate raw assignments by merging tasks for the same worker.</summary>
    private static List<TaskAssignment> DeduplicateAssignments(
        List<TaskAssignment> raw,
        HashSet<string>? excludeWorkers = null)
    {
        var query = raw
            .GroupBy(a => a.WorkerName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TaskAssignment(g.Key, string.Join("\n\n---\n\n", g.Select(a => a.Task))));
        if (excludeWorkers != null)
            query = query.Where(a => !excludeWorkers.Contains(a.WorkerName));
        return query.ToList();
    }

    /// <summary>
    /// Builds a delegation nudge prompt with explicit format example.
    /// The multiline format is required because ParseTaskAssignments' regex
    /// needs a newline after @worker:name to capture the task body.
    /// </summary>
    internal static string BuildDelegationNudgePrompt(List<string> workerNames)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("⚠️ CRITICAL: Your previous response did not contain any @worker delegation blocks.");
        sb.AppendLine();
        sb.AppendLine("You are a DISPATCHER. You CANNOT do the work yourself. Even if you believe the work is already done from a previous conversation, you MUST delegate FRESH work to your workers NOW.");
        sb.AppendLine("Any previous results are STALE — the user is requesting a NEW evaluation. Ignore prior results and dispatch fresh tasks.");
        sb.AppendLine();
        sb.AppendLine($"Available workers ({workerNames.Count}): {string.Join(", ", workerNames)}");
        sb.AppendLine();
        sb.AppendLine("Use this EXACT format (task must be on its own line AFTER the @worker line):");
        sb.AppendLine();
        sb.AppendLine($"@worker:{workerNames.FirstOrDefault() ?? "worker-name"}");
        sb.AppendLine("Describe the task here on a separate line.");
        sb.AppendLine("@end");
        sb.AppendLine();
        sb.AppendLine("Produce @worker blocks for ALL workers now. Do NOT explain, do NOT summarize previous work, ONLY output @worker blocks.");
        return sb.ToString();
    }

    internal static List<TaskAssignment> ParseTaskAssignments(string orchestratorResponse, List<string> availableWorkers)
    {
        // Try JSON parsing first — more reliable than regex
        var jsonAssignments = TryParseJsonAssignments(orchestratorResponse, availableWorkers);
        if (jsonAssignments.Count > 0)
            return jsonAssignments;

        // Fall back to @worker:name...@end regex parsing
        var assignments = new List<TaskAssignment>();
        var pattern = @"@worker:([^\n]+?)\s*\n([\s\S]*?)(?:@end|(?=@worker:)|$)";

        foreach (Match match in Regex.Matches(orchestratorResponse, pattern, RegexOptions.IgnoreCase))
        {
            var workerName = match.Groups[1].Value.Trim().Trim('`', '\'', '"');
            var task = match.Groups[2].Value.Trim();
            if (string.IsNullOrEmpty(task)) continue;

            // Exact match only — no fuzzy bidirectional Contains (caused misroutes)
            var resolved = availableWorkers.FirstOrDefault(w =>
                w.Equals(workerName, StringComparison.OrdinalIgnoreCase));
            if (resolved != null)
                assignments.Add(new TaskAssignment(resolved, task));
        }
        return assignments;
    }

    /// <summary>
    /// Try to parse orchestrator response as JSON array of worker assignments.
    /// Accepts: [{"worker":"name","task":"..."},...] with optional markdown code fences.
    /// </summary>
    internal static List<TaskAssignment> TryParseJsonAssignments(string response, List<string> availableWorkers)
    {
        var assignments = new List<TaskAssignment>();
        try
        {
            // Strip markdown code fences if present
            var json = response.Trim();
            var fenceMatch = Regex.Match(json, @"```(?:json)?\s*\n?([\s\S]*?)\n?```", RegexOptions.IgnoreCase);
            if (fenceMatch.Success)
                json = fenceMatch.Groups[1].Value.Trim();

            // Must start with [ to be a JSON array
            if (!json.StartsWith("[", StringComparison.Ordinal)) return assignments;

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var workerName = element.TryGetProperty("worker", out var w) ? w.GetString() : null;
                var task = element.TryGetProperty("task", out var t) ? t.GetString() : null;
                if (string.IsNullOrEmpty(workerName) || string.IsNullOrEmpty(task)) continue;

                var resolved = availableWorkers.FirstOrDefault(wk =>
                    wk.Equals(workerName, StringComparison.OrdinalIgnoreCase));
                if (resolved != null)
                    assignments.Add(new TaskAssignment(resolved, task));
            }
        }
        catch (System.Text.Json.JsonException) { /* Not valid JSON — fall through to regex */ }
        return assignments;
    }

    private record WorkerResult(string WorkerName, string? Response, bool Success, string? Error, TimeSpan Duration);

    /// <summary>
    /// Full INV-1-compliant force-completion of a session's processing state.
    /// Clears all 9+ companion fields, resolves the ResponseCompletion TCS,
    /// fires OnSessionComplete, and cancels background timers.
    /// Must be awaited — runs state mutation on UI thread via TCS synchronization.
    /// </summary>
    private async Task ForceCompleteProcessingAsync(string sessionName, SessionState state, string reason)
    {
        // Cancel timers first (thread-safe — use Interlocked internally)
        CancelProcessingWatchdog(state);
        CancelTurnEndFallback(state);
        CancelToolHealthCheck(state);

        var tcs = new TaskCompletionSource<bool>();
        InvokeOnUI(() =>
        {
            try
            {
                if (!state.Info.IsProcessing) { tcs.TrySetResult(true); return; }

                // Full cleanup mirroring CompleteResponse / unstartedWorkers recovery
                FlushCurrentResponse(state);
                Interlocked.Exchange(ref state.ActiveToolCallCount, 0);
                Interlocked.Exchange(ref state.SendingFlag, 0);
                Interlocked.Exchange(ref state.SuccessfulToolCountThisTurn, 0);
                Interlocked.Exchange(ref state.WatchdogCaseAResets, 0);
                Interlocked.Exchange(ref state.WatchdogCaseBResets, 0);
                state.HasUsedToolsThisTurn = false;
                state.FallbackCanceledByTurnStart = false;
                state.Info.IsResumed = false;
                state.Info.ProcessingStartedAt = null;
                state.Info.ToolCallCount = 0;
                state.Info.ProcessingPhase = 0;
                state.Info.ClearPermissionDenials();

                var response = state.CurrentResponse.ToString();
                var fullResponse = state.FlushedResponse.Length > 0
                    ? (string.IsNullOrEmpty(response)
                        ? state.FlushedResponse.ToString()
                        : state.FlushedResponse + "\n\n" + response)
                    : response;

                state.CurrentResponse.Clear();
                state.FlushedResponse.Clear();
                state.PendingReasoningMessages.Clear();
                state.Info.IsProcessing = false;

                state.ResponseCompletion?.TrySetResult(fullResponse);
                var summary = fullResponse.Length > 0 ? (fullResponse.Length > 100 ? fullResponse[..100] + "..." : fullResponse) : "";
                OnSessionComplete?.Invoke(sessionName, summary);
                OnStateChanged?.Invoke();
                Debug($"[DISPATCH] ForceCompleteProcessing '{sessionName}': {reason} (responseLen={fullResponse.Length})");
                tcs.TrySetResult(true);
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        try { await tcs.Task; } catch { }
    }

    /// <summary>
    /// Wait for a session to finish processing (go idle). Used after early dispatch
    /// resolves the orchestrator's TCS while it's still doing tool work — we must
    /// wait for it to go idle before sending the next prompt (synthesis).
    /// Uses a short inactivity threshold: if no SDK events for 60s, the session is
    /// likely stuck in a "zero-idle" state and will be aborted.
    /// </summary>
    private async Task WaitForSessionIdleAsync(string sessionName, CancellationToken ct, int timeoutSeconds = 300)
    {
        if (!_sessions.TryGetValue(sessionName, out var state))
            return;
        if (!state.Info.IsProcessing)
            return;

        Debug($"[DISPATCH] Waiting for '{sessionName}' to go idle before sending next prompt...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int inactivityThresholdSeconds = 60;

        while (state.Info.IsProcessing && !ct.IsCancellationRequested && sw.Elapsed.TotalSeconds < timeoutSeconds)
        {
            // Check if the session has been quiet (no SDK events) for the inactivity threshold.
            // This catches the "zero-idle" bug where the SDK never emits SessionIdleEvent.
            var lastEventTicks = Interlocked.Read(ref state.LastEventAtTicks);
            var secondsSinceLastEvent = (DateTime.UtcNow - new DateTime(lastEventTicks)).TotalSeconds;
            if (secondsSinceLastEvent >= inactivityThresholdSeconds)
            {
                Debug($"[DISPATCH] '{sessionName}' no SDK events for {secondsSinceLastEvent:F0}s — force-completing to proceed with synthesis");
                await ForceCompleteProcessingAsync(sessionName, state, $"inactivity {secondsSinceLastEvent:F0}s");
                await Task.Delay(500, ct);
                break;
            }
            await Task.Delay(1000, ct);
        }
        if (state.Info.IsProcessing)
        {
            Debug($"[DISPATCH] '{sessionName}' still processing after {sw.Elapsed.TotalSeconds:F1}s — force-completing to allow synthesis");
            await ForceCompleteProcessingAsync(sessionName, state, $"timeout {sw.Elapsed.TotalSeconds:F1}s");
            await Task.Delay(500, ct);
        }
        Debug($"[DISPATCH] '{sessionName}' now idle after {sw.Elapsed.TotalSeconds:F1}s");
    }

    private async Task<WorkerResult> ExecuteWorkerAsync(string workerName, string task, string originalPrompt, CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await EnsureSessionModelAsync(workerName, cancellationToken);

        // Use per-worker system prompt if set, otherwise generic.
        // Note: .github/copilot-instructions.md is auto-loaded by the SDK for each session's working directory,
        // so workers already inherit repo-level copilot instructions without explicit injection here.
        var meta = GetSessionMeta(workerName);
        var identity = !string.IsNullOrEmpty(meta?.SystemPrompt)
            ? meta.SystemPrompt
            : "You are a worker agent. Complete the following task thoroughly.";

        // Inject shared context (e.g., Squad decisions.md) if the group has it
        var group = meta != null ? Organization.Groups.FirstOrDefault(g => g.Id == meta.GroupId) : null;
        var sharedPrefix = !string.IsNullOrEmpty(group?.SharedContext)
            ? $"## Team Context (shared knowledge)\n{group.SharedContext}\n\n"
            : "";

        // Inject worktree awareness if the worker has an isolated worktree
        var wtInfo = meta?.WorktreeId != null
            ? _repoManager.Worktrees.FirstOrDefault(wt => wt.Id == meta.WorktreeId) : null;
        var worktreeNote = wtInfo != null && group?.WorktreeStrategy != WorktreeStrategy.Shared
            ? $"\n\n## Your Worktree\nYou have an isolated git worktree at `{wtInfo.Path}` (branch: {wtInfo.Branch}). " +
              "You can safely run any git operations without affecting other workers. " +
              "To check out a PR: `git fetch origin pull/<N>/head:pr-<N> && git checkout pr-<N>`\n"
            : "";

        var workerPrompt = BuildWorkerPrompt(identity, worktreeNote, sharedPrefix, originalPrompt, task);

        const int maxRetries = 2;
        var dispatchTime = DateTime.Now;

        // Pre-dispatch: if worker is still processing from a previous run (e.g., restored
        // mid-processing after app relaunch), wait for it to become idle. The watchdog will
        // clear IsProcessing within 30-120s for restored sessions.
        if (_sessions.TryGetValue(workerName, out var preState) && preState.Info.IsProcessing)
        {
            Debug($"[DISPATCH] Worker '{workerName}' is still processing from previous run — waiting up to 150s");
            var waitStart = DateTime.UtcNow;
            while (preState.Info.IsProcessing && (DateTime.UtcNow - waitStart).TotalSeconds < 150)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(2000, cancellationToken);
            }
            if (preState.Info.IsProcessing)
            {
                Debug($"[DISPATCH] Worker '{workerName}' still processing after 150s wait — force-completing");
                await ForceCompleteProcessingAsync(workerName, preState, "pre-dispatch 150s timeout");
            }
            else
            {
                Debug($"[DISPATCH] Worker '{workerName}' became idle after {(DateTime.UtcNow - waitStart).TotalSeconds:F1}s — proceeding with dispatch");
            }
        }

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                Debug($"[DISPATCH] Worker '{workerName}' starting (prompt len={workerPrompt.Length}, attempt={attempt})");
                var response = await SendPromptAndWaitAsync(workerName, workerPrompt, cancellationToken, originalPrompt: originalPrompt);

                // Premature session.idle recovery (SDK bug #299):
                // The SDK sometimes sends session.idle mid-turn, completing the TCS with
                // truncated content. The EVT-REARM path detects the follow-up TurnStartEvent
                // and sets WasPrematurelyIdled=true. Poll briefly for this flag, then wait
                // for the worker's real completion and re-collect full content from History.
                if (_sessions.TryGetValue(workerName, out var postState) && postState.IsMultiAgentSession)
                {
                    response = await RecoverFromPrematureIdleIfNeededAsync(
                        workerName, postState, response, dispatchTime, cancellationToken);
                }

                // Worker revival: empty response means the session died (e.g., dead SSE stream
                // after reconnect). Create a fresh session and retry once.
                if (string.IsNullOrWhiteSpace(response))
                {
                    Debug($"[DISPATCH] Worker '{workerName}' returned empty — attempting fresh session revival");
                    if (_sessions.TryGetValue(workerName, out var deadState))
                    {
                        // Mark old state as orphaned so any lingering callbacks are no-ops
                        deadState.IsOrphaned = true;
                        try { await deadState.Session.DisposeAsync(); } catch { }

                        var workerMeta = GetSessionMeta(workerName);
                        var client = GetClientForGroup(workerMeta?.GroupId);
                        if (client != null)
                        {
                            var freshConfig = BuildFreshSessionConfig(deadState);
                            var freshSession = await client.CreateSessionAsync(freshConfig, cancellationToken);
                            var freshState = new SessionState { Session = freshSession, Info = deadState.Info };
                            freshState.IsMultiAgentSession = deadState.IsMultiAgentSession;
                            // Register event handler BEFORE sending — without this, the SDK
                            // writes events.jsonl but HandleSessionEvent never fires, creating
                            // a dead event stream where the watchdog is the only recovery path.
                            freshSession.On(evt => HandleSessionEvent(freshState, evt));
                            // Use TryUpdate for atomic swap — prevents a stale Task.Run
                            // from a concurrent reconnect from overwriting newer state (INV-15).
                            if (!_sessions.TryUpdate(workerName, freshState, deadState))
                            {
                                Debug($"[DISPATCH] Worker '{workerName}' revival state already replaced — discarding");
                                freshState.IsOrphaned = true;
                                try { await freshSession.DisposeAsync(); } catch { }
                                DisposePrematureIdleSignal(deadState);
                            }
                            else
                            {
                                // Commit SessionId only after TryUpdate succeeds — avoids
                                // mutating shared Info on a path that might discard the state.
                                deadState.Info.SessionId = freshSession.SessionId;
                                DisposePrematureIdleSignal(deadState);
                                Debug($"[DISPATCH] Worker '{workerName}' revived with fresh session '{freshSession.SessionId}'");
                                response = await SendPromptAndWaitAsync(workerName, workerPrompt, cancellationToken, originalPrompt: originalPrompt);
                            }
                        }
                    }
                }

                // Fallback: if response is still empty, try extracting from chat history.
                // The SDK may have streamed content via delta events that ended up in history
                // even though FlushedResponse/CurrentResponse were empty (e.g., watchdog completion).
                if (string.IsNullOrWhiteSpace(response) && _sessions.TryGetValue(workerName, out var histState))
                {
                    ChatMessage[] historySnapshot;
                    try { historySnapshot = histState.Info.History.ToArray(); }
                    catch (InvalidOperationException) { historySnapshot = Array.Empty<ChatMessage>(); }

                    // First try: last assistant text message after dispatch
                    var lastAssistant = historySnapshot
                        .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content)
                            && m.MessageType == ChatMessageType.Assistant
                            && m.Timestamp >= dispatchTime);
                    if (lastAssistant != null)
                    {
                        response = lastAssistant.Content;
                        Debug($"[DISPATCH] Worker '{workerName}' recovered {response!.Length} chars from chat history (assistant message)");
                    }
                    else
                    {
                        // Second try: reconstruct from tool outputs. When the agent runs a long
                        // tool (e.g., skill-validator) and the session completes before the agent
                        // writes its verdict, the tool results are in ToolCall messages but no
                        // assistant text message exists.
                        var toolOutputs = historySnapshot
                            .Where(m => m.MessageType == ChatMessageType.ToolCall
                                && m.IsComplete && !string.IsNullOrWhiteSpace(m.Content)
                                && m.Timestamp >= dispatchTime)
                            .ToList();
                        if (toolOutputs.Count > 0)
                        {
                            var sb = new System.Text.StringBuilder();
                            sb.AppendLine("## Tool Execution Results (agent did not produce a final summary)");
                            foreach (var tool in toolOutputs)
                            {
                                sb.AppendLine($"### {tool.ToolName ?? "tool"}");
                                // Truncate very large tool outputs to keep synthesis manageable
                                var content = tool.Content!.Length > 8000 ? tool.Content[..8000] + "\n... (truncated)" : tool.Content;
                                sb.AppendLine(content);
                                sb.AppendLine();
                            }
                            response = sb.ToString();
                            Debug($"[DISPATCH] Worker '{workerName}' recovered {response.Length} chars from {toolOutputs.Count} tool output(s)");
                        }
                    }

                    // Third try: dead event stream recovery. When the SDK event callback stops
                    // firing (common after session revival), History is empty but events.jsonl
                    // has the full response written by the server process. Parse it directly.
                    if (string.IsNullOrWhiteSpace(response))
                    {
                        var sessionId = histState.Info.SessionId;
                        if (!string.IsNullOrEmpty(sessionId))
                        {
                            try
                            {
                                var diskHistory = await LoadHistoryFromDiskAsync(sessionId);
                                var lastDiskAssistant = diskHistory
                                    .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content)
                                        && m.MessageType == ChatMessageType.Assistant
                                        && m.Timestamp >= dispatchTime);
                                if (lastDiskAssistant != null)
                                {
                                    response = lastDiskAssistant.Content;
                                    Debug($"[DISPATCH] Worker '{workerName}' recovered {response!.Length} chars from events.jsonl (dead event stream fallback)");

                                    // Also backfill the in-memory history so it's visible in the UI
                                    if (histState.Info.History.Count == 0)
                                    {
                                        InvokeOnUI(() =>
                                        {
                                            histState.Info.History.AddRange(diskHistory);
                                            histState.Info.MessageCount = histState.Info.History.Count;
                                            OnStateChanged?.Invoke();
                                        });
                                    }
                                }
                            }
                            catch (Exception diskEx)
                            {
                                Debug($"[DISPATCH] Worker '{workerName}' events.jsonl fallback failed: {diskEx.Message}");
                            }
                        }
                    }
                }

                Debug($"[DISPATCH] Worker '{workerName}' completed (response len={response?.Length ?? 0}, elapsed={sw.Elapsed.TotalSeconds:F1}s)");
                return new WorkerResult(workerName, response, true, null, sw.Elapsed);
            }
            catch (Exception ex) when (attempt < maxRetries && IsConnectionError(ex))
            {
                Debug($"[DISPATCH] Worker '{workerName}' attempt {attempt} failed with {ex.GetType().Name} — retrying in 2s");
                await Task.Delay(2000, cancellationToken);
                continue;
            }
            catch (Exception ex)
            {
                Debug($"[DISPATCH] Worker '{workerName}' FAILED: {ex.GetType().Name}: {ex.Message} (elapsed={sw.Elapsed.TotalSeconds:F1}s)");
                return new WorkerResult(workerName, null, false, ex.Message, sw.Elapsed);
            }
        }
        throw new UnreachableException(); // for loop always returns or continues
    }

    /// <summary>Max times to retry after permission recovery cancels the ResponseCompletion TCS.</summary>
    internal const int MaxPermissionRecoveryRetries = 3;

    private async Task<string> SendPromptAndWaitAsync(string sessionName, string prompt, CancellationToken cancellationToken, string? originalPrompt = null)
    {
        // Use SendPromptAsync directly — it already awaits ResponseCompletion internally.
        // Do NOT capture state and await its TCS separately: reconnection replaces the state
        // object, orphaning the old TCS and causing a 10-minute hang.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(IsDemoMode || IsRemoteMode ? WorkerExecutionTimeoutRemote : WorkerExecutionTimeout);
        // Wire CTS to ResponseCompletion TCS so the 10-minute timeout actually cancels the await.
        // Must look up from _sessions dict (not captured ref) since reconnect replaces state.
        await using var ctsReg = cts.Token.Register(() =>
        {
            if (_sessions.TryGetValue(sessionName, out var s))
                s.ResponseCompletion?.TrySetCanceled();
        });

        // Permission recovery (TryRecoverPermissionAsync) cancels the ResponseCompletion TCS
        // to unblock SendPromptAsync, then reconnects the session and optionally resends.
        // For multi-agent workers, we must detect this and wait for the new session's completion
        // instead of propagating the TaskCanceledException up to ExecuteWorkerAsync.
        for (int recoveryAttempt = 0; recoveryAttempt < MaxPermissionRecoveryRetries; recoveryAttempt++)
        {
            try
            {
                if (recoveryAttempt == 0)
                {
                    return await SendPromptAsync(sessionName, prompt, cancellationToken: cts.Token, originalPrompt: originalPrompt);
                }
                else
                {
                    // After permission recovery, the session has been reconnected and may have
                    // resent the prompt. Wait for the NEW state's ResponseCompletion TCS.
                    if (!_sessions.TryGetValue(sessionName, out var newState) || newState.IsOrphaned)
                        throw new InvalidOperationException("Session lost after permission recovery");

                    // If recovery skipped the resend (tools already completed), IsProcessing
                    // is false and the new TCS will never complete. Return partial content.
                    if (!newState.Info.IsProcessing)
                    {
                        Debug($"[DISPATCH] Worker '{sessionName}' recovery skipped resend — collecting partial response");
                        var lastAssistant = newState.Info.History
                            .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content));
                        return lastAssistant?.Content ?? "";
                    }

                    var tcs = newState.ResponseCompletion;
                    if (tcs == null)
                    {
                        var lastAssistant = newState.Info.History
                            .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content));
                        return lastAssistant?.Content ?? "";
                    }

                    Debug($"[DISPATCH] Worker '{sessionName}' re-awaiting after permission recovery (attempt {recoveryAttempt})");
                    return await tcs.Task.WaitAsync(cts.Token);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !cts.IsCancellationRequested)
            {
                // The TCS was cancelled but our dispatch token is still alive — this is
                // permission recovery cancelling the old TCS. Check if the session was
                // reconnected (not orphaned) and retry with the new state.
                if (!_sessions.TryGetValue(sessionName, out var recoveredState) || recoveredState.IsOrphaned)
                    throw; // Session truly gone

                Debug($"[DISPATCH] Worker '{sessionName}' detected permission recovery cancellation — retrying (attempt {recoveryAttempt + 1}/{MaxPermissionRecoveryRetries})");

                // Brief delay to let the recovery's UI-thread cleanup finish
                await Task.Delay(500, cancellationToken);
                continue;
            }
        }

        throw new OperationCanceledException("Max permission recovery retries exceeded");
    }

    /// <summary>
    /// Detects premature session.idle (SDK bug #299) and recovers the full worker response.
    /// After the initial TCS completes, polls briefly for the WasPrematurelyIdled flag set by
    /// EVT-REARM. If detected, subscribes to OnSessionComplete and waits for the worker's real
    /// completion, then re-collects full content from History or events.jsonl.
    /// </summary>
    private async Task<string?> RecoverFromPrematureIdleIfNeededAsync(
        string workerName, SessionState state, string? initialResponse,
        DateTime dispatchTime, CancellationToken cancellationToken)
    {
        // Two detection signals (either triggers recovery):
        // 1. PrematureIdleSignal (ManualResetEventSlim) — set by EVT-REARM, event-based (efficient)
        // 2. events.jsonl freshness — CLI is still writing events despite our TCS completing
        //    This catches cases where EVT-REARM takes 30-60s to fire
        
        bool IsPrematureIdleSignalSet()
        {
            try { return state.PrematureIdleSignal.IsSet; }
            catch (ObjectDisposedException) { return false; }
        }

        bool WaitForPrematureIdleSignal()
        {
            try { return state.PrematureIdleSignal.Wait(500, cancellationToken); }
            catch (ObjectDisposedException) { return false; }
        }

        // Fast path: check if PrematureIdleSignal was already set
        var detected = IsPrematureIdleSignalSet();
        
        if (!detected)
        {
            // Check events.jsonl freshness immediately — if the file was just written,
            // the CLI is still actively working despite the premature session.idle
            detected = IsEventsFileActive(state.Info.SessionId);
        }
        
        if (!detected)
        {
            // Wait for PrematureIdleSignal OR poll events.jsonl freshness
            var detectStart = DateTime.UtcNow;
            while ((DateTime.UtcNow - detectStart).TotalMilliseconds < PrematureIdleDetectionWindowMs)
            {
                // Wait up to 500ms on the signal (exits immediately if Set())
                var signaled = await Task.Run(WaitForPrematureIdleSignal, cancellationToken)
                    .ConfigureAwait(false);
                
                if (signaled || cancellationToken.IsCancellationRequested)
                {
                    detected = signaled;
                    break;
                }
                
                // Re-check events.jsonl freshness each cycle
                if (IsEventsFileActive(state.Info.SessionId))
                {
                    detected = true;
                    break;
                }
            }
        }

        if (!detected)
            return initialResponse; // Normal completion — no premature idle indicators

        var signal = IsPrematureIdleSignalSet() ? "PrematureIdleSignal" : "events.jsonl freshness";
        Debug($"[DISPATCH-RECOVER] Worker '{workerName}' premature idle detected via {signal} — " +
              $"truncated response={initialResponse?.Length ?? 0} chars, " +
              $"IsProcessing={state.Info.IsProcessing}. Waiting for real completion...");

        // The worker may hit premature idle repeatedly (observed: 4x in a row),
        // so we loop until events.jsonl goes stale (worker truly done).
        string? bestResponse = initialResponse;
        try
        {
            using var recoveryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            recoveryCts.CancelAfter(PrematureIdleRecoveryTimeoutMs);
            var rounds = 0;
            
            while (!recoveryCts.Token.IsCancellationRequested)
            {
                rounds++;
                var completionTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void LocalHandler(string name, string _)
                {
                    if (name == workerName)
                        completionTcs.TrySetResult(true);
                }
                OnSessionComplete += LocalHandler;
                
                try
                {
                    // If worker already finished while we were setting up, complete immediately
                    if (!state.Info.IsProcessing)
                        completionTcs.TrySetResult(true);

                    await using var reg = recoveryCts.Token.Register(() => completionTcs.TrySetResult(false));
                    var completed = await completionTcs.Task;
                    if (!completed)
                    {
                        Debug($"[DISPATCH-RECOVER] Worker '{workerName}' recovery timed out after {PrematureIdleRecoveryTimeoutMs / 1000}s " +
                              $"(round {rounds}) — using best response ({bestResponse?.Length ?? 0} chars)");
                        break;
                    }
                    
                    // Collect content from this completion round
                    ChatMessage[] histSnapshot;
                    try { histSnapshot = state.Info.History.ToArray(); }
                    catch (InvalidOperationException) { histSnapshot = Array.Empty<ChatMessage>(); }

                    var latestContent = histSnapshot
                        .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content)
                            && m.MessageType == ChatMessageType.Assistant
                            && m.Timestamp >= dispatchTime);

                    if (latestContent != null && latestContent.Content!.Length > (bestResponse?.Length ?? 0))
                    {
                        bestResponse = latestContent.Content;
                        Debug($"[DISPATCH-RECOVER] Worker '{workerName}' round {rounds}: collected {bestResponse.Length} chars from History");
                    }
                    
                    // Check if the worker is truly done or will hit premature idle again
                    try { await Task.Delay(2000, recoveryCts.Token); } // Brief settle time
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { break; }
                    
                    if (!IsEventsFileActive(state.Info.SessionId) && !state.Info.IsProcessing)
                    {
                        Debug($"[DISPATCH-RECOVER] Worker '{workerName}' events.jsonl stale and not processing " +
                              $"after round {rounds} — worker is truly done ({bestResponse?.Length ?? 0} chars)");
                        break;
                    }
                    
                    if (state.Info.IsProcessing)
                    {
                        Debug($"[DISPATCH-RECOVER] Worker '{workerName}' still processing after round {rounds} " +
                              $"(re-armed again) — waiting for next completion...");
                    }
                }
                finally
                {
                    OnSessionComplete -= LocalHandler;
                }
            }

            // If History didn't have better content, try disk fallback
            if ((bestResponse?.Length ?? 0) <= (initialResponse?.Length ?? 0))
            {
                var sessionId = state.Info.SessionId;
                if (!string.IsNullOrEmpty(sessionId))
                {
                    try
                    {
                        var diskHistory = await LoadHistoryFromDiskAsync(sessionId);
                        var lastDisk = diskHistory
                            .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content)
                                && m.MessageType == ChatMessageType.Assistant
                                && m.Timestamp >= dispatchTime);
                        if (lastDisk != null && lastDisk.Content!.Length > (bestResponse?.Length ?? 0))
                        {
                            Debug($"[DISPATCH-RECOVER] Worker '{workerName}' recovered {lastDisk.Content.Length} chars from events.jsonl");
                            return lastDisk.Content;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug($"[DISPATCH-RECOVER] Worker '{workerName}' events.jsonl recovery failed: {ex.Message}");
                    }
                }
            }

            if ((bestResponse?.Length ?? 0) > (initialResponse?.Length ?? 0))
            {
                Debug($"[DISPATCH-RECOVER] Worker '{workerName}' final recovery: {bestResponse!.Length} chars " +
                      $"(was {initialResponse?.Length ?? 0} chars truncated, {rounds} rounds)");
                return bestResponse;
            }

            Debug($"[DISPATCH-RECOVER] Worker '{workerName}' recovery found no additional content after {rounds} rounds — " +
                  $"using original response ({initialResponse?.Length ?? 0} chars)");
            return initialResponse;
        }
        catch (OperationCanceledException)
        {
            // Catches both: (1) outer cancellation (user abort) and (2) inner recoveryCts
            // timeout. Without this, the 120s recovery OCE escapes to ExecuteWorkerAsync's
            // generic catch, which logs FAILED and discards bestResponse.
            return bestResponse ?? initialResponse;
        }
    }

    /// <summary>Check if a session's events.jsonl was recently modified, indicating the CLI
    /// is still actively working. Used by premature idle recovery to detect truncation
    /// when EVT-REARM hasn't fired yet.</summary>
    private bool IsEventsFileActive(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;
        try
        {
            var eventsPath = Path.Combine(SessionStatePath, sessionId, "events.jsonl");
            if (!File.Exists(eventsPath)) return false;
            var lastWrite = File.GetLastWriteTimeUtc(eventsPath);
            var fileAge = (DateTime.UtcNow - lastWrite).TotalSeconds;
            return fileAge < PrematureIdleEventsFileFreshnessSeconds;
        }
        catch { return false; }
    }

    private static string BuildWorkerPrompt(string identity, string worktreeNote, string sharedPrefix, string originalPrompt, string task)
    {
        return $"{identity}{worktreeNote}\n\nYour response will be collected and synthesized with other workers' responses.\n\n" +
            "## CRITICAL: Tool Usage & Honesty Policy\n" +
            "- You MUST use your CLI tools (file reads, builds, tests, grep, etc.) to complete your task. Do NOT rely on assumptions or memory.\n" +
            "- If a tool call fails or is unavailable, REPORT THE FAILURE explicitly. Say what you tried, what failed, and why.\n" +
            "- NEVER fabricate, invent, or assume tool outputs. If you cannot run a tool, say so — do NOT generate plausible-looking results.\n" +
            "- NEVER evaluate or assess code, tests, or behavior without actually running the relevant tools first.\n" +
            "- If you cannot complete your task because tools are unavailable, respond with: " +
            "\"TOOL_FAILURE: [description of what failed and why]\"\n\n" +
            $"{sharedPrefix}## Original User Request (context)\n{originalPrompt}\n\n## Your Assigned Task\n{task}";
    }

    private string BuildSynthesisPrompt(string originalPrompt, List<WorkerResult> results)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Worker Results");
        sb.AppendLine();
        foreach (var result in results)
        {
            sb.AppendLine($"### {result.WorkerName} ({(result.Success ? "✅ completed" : "❌ failed")}, {result.Duration.TotalSeconds:F1}s)");
            if (result.Success)
            {
                // Strip completion sentinel from worker responses to prevent the orchestrator
                // from echoing it and causing false loop termination in the reflect cycle.
                var sanitized = result.Response?.Replace("[[GROUP_REFLECT_COMPLETE]]", "[WORKER_APPROVED]", StringComparison.OrdinalIgnoreCase) ?? "";
                sb.AppendLine(sanitized);
            }
            else
                sb.AppendLine($"*Error: {result.Error}*");
            sb.AppendLine();
        }
        sb.AppendLine("## Instructions");
        sb.AppendLine($"Original request: {originalPrompt}");
        sb.AppendLine();
        sb.AppendLine("Synthesize these worker responses into a coherent final answer. Follow these rules:");
        sb.AppendLine("- Note any tasks that failed and clearly report what went wrong.");
        sb.AppendLine("- Flag any worker response that appears to contain fabricated results (results not backed by actual tool execution).");
        sb.AppendLine("- If a worker reports TOOL_FAILURE, do NOT attempt to fill in or guess the missing results — report the failure as-is.");
        sb.AppendLine("- Prefer worker outputs that show evidence of actual tool usage (file contents, build output, test results) over generic assessments.");
        sb.AppendLine("Provide a unified response addressing the original request.");
        return sb.ToString();
    }

    private void AddOrchestratorSystemMessage(string sessionName, string message)
    {
        var session = GetSession(sessionName);
        if (session != null)
        {
            session.History.Add(ChatMessage.SystemMessage(message));
            InvokeOnUI(() => OnStateChanged?.Invoke());
        }
    }

    #region Orchestration Persistence (relaunch resilience)

    private static string? _pendingOrchestrationFile;
    private static string PendingOrchestrationFile { get { lock (_pathLock) return _pendingOrchestrationFile ??= Path.Combine(PolyPilotBaseDir, "pending-orchestration.json"); } }

    internal void SavePendingOrchestration(PendingOrchestration pending)
    {
        try
        {
            Directory.CreateDirectory(PolyPilotBaseDir);
            var json = JsonSerializer.Serialize(pending, new JsonSerializerOptions { WriteIndented = true });
            var tmp = PendingOrchestrationFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, PendingOrchestrationFile, overwrite: true);
            Debug($"[DISPATCH] Saved pending orchestration: group={pending.GroupId}, workers={string.Join(",", pending.WorkerNames)}");
        }
        catch (Exception ex)
        {
            Debug($"[DISPATCH] Failed to save pending orchestration: {ex.Message}");
        }
    }

    private static PendingOrchestration? LoadPendingOrchestration()
    {
        try
        {
            if (!File.Exists(PendingOrchestrationFile)) return null;
            var json = File.ReadAllText(PendingOrchestrationFile);
            return JsonSerializer.Deserialize<PendingOrchestration>(json);
        }
        catch
        {
            return null;
        }
    }

    private static void ClearPendingOrchestration()
    {
        try
        {
            if (File.Exists(PendingOrchestrationFile))
                File.Delete(PendingOrchestrationFile);
        }
        catch { }
    }

    // Test helpers
    internal static PendingOrchestration? LoadPendingOrchestrationForTest() => LoadPendingOrchestration();
    internal static void ClearPendingOrchestrationForTest() => ClearPendingOrchestration();

    /// <summary>
    /// After session restore, check for a pending orchestration dispatch that was interrupted
    /// by an app relaunch. If found, monitor workers and auto-synthesize when all complete.
    /// </summary>
    internal async Task ResumeOrchestrationIfPendingAsync(CancellationToken ct = default)
    {
        var pending = LoadPendingOrchestration();
        if (pending == null) return;

        var group = Organization.Groups.FirstOrDefault(g => g.Id == pending.GroupId && g.IsMultiAgent);
        if (group == null)
        {
            Debug($"[DISPATCH] Pending orchestration group '{pending.GroupId}' no longer exists — clearing");
            ClearPendingOrchestration();
            return;
        }

        // Verify orchestrator session exists
        if (!_sessions.ContainsKey(pending.OrchestratorName))
        {
            Debug($"[DISPATCH] Pending orchestration orchestrator '{pending.OrchestratorName}' not found — clearing");
            ClearPendingOrchestration();
            return;
        }

        Debug($"[DISPATCH] Resuming pending orchestration for group '{group.Name}' " +
              $"(orchestrator={pending.OrchestratorName}, workers={string.Join(",", pending.WorkerNames)})");

        AddOrchestratorSystemMessage(pending.OrchestratorName,
            $"🔄 App restarted — resuming orchestration. Waiting for {pending.WorkerNames.Count} worker(s) to complete...");
        InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(pending.GroupId, OrchestratorPhase.Resuming,
            $"Waiting for {pending.WorkerNames.Count} worker(s)"));

        // Monitor workers in background — poll until all are idle
        _ = Task.Run(async () =>
        {
            try
            {
                await MonitorAndSynthesizeAsync(pending, ct);
            }
            catch (Exception ex)
            {
                Debug($"[DISPATCH] Resume orchestration failed: {ex.Message}");
                AddOrchestratorSystemMessage(pending.OrchestratorName,
                    $"⚠️ Failed to resume orchestration: {ex.Message}");
                ClearPendingOrchestration();
                InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(pending.GroupId, OrchestratorPhase.Complete, null));
            }
        });
    }

    private async Task MonitorAndSynthesizeAsync(PendingOrchestration pending, CancellationToken ct)
    {
        var timeout = TimeSpan.FromMinutes(15);
        var started = DateTime.UtcNow;

        // Poll every 5 seconds until all workers are idle
        while (!ct.IsCancellationRequested && (DateTime.UtcNow - started) < timeout)
        {
            var allIdle = true;
            foreach (var workerName in pending.WorkerNames)
            {
                if (_sessions.TryGetValue(workerName, out var state) && state.Info.IsProcessing)
                {
                    allIdle = false;
                    break;
                }
            }

            if (allIdle)
            {
                Debug($"[DISPATCH] All workers idle — collecting results for synthesis");
                break;
            }

            await Task.Delay(5000, ct);
        }

        if (ct.IsCancellationRequested)
        {
            ClearPendingOrchestration();
            InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(pending.GroupId, OrchestratorPhase.Complete, null));
            return;
        }

        if ((DateTime.UtcNow - started) >= timeout)
        {
            AddOrchestratorSystemMessage(pending.OrchestratorName,
                "⚠️ Orchestration resume timed out after 15 minutes — some workers may not have completed.");
        }

        // Collect worker results from their chat history (last assistant message AFTER the dispatch)
        var dispatchTimeLocal = pending.StartedAt.Kind == DateTimeKind.Utc
            ? pending.StartedAt.ToLocalTime()
            : pending.StartedAt;
        var results = new List<WorkerResult>();
        var unstartedWorkers = new List<string>();
        foreach (var workerName in pending.WorkerNames)
        {
            var session = GetSession(workerName);
            if (session == null)
            {
                results.Add(new WorkerResult(workerName, null, false, "Session not found after restart", TimeSpan.Zero));
                continue;
            }

            // Find the last assistant message AFTER the dispatch started — avoids picking up
            // stale pre-dispatch history from prior conversations or reflection iterations
            var lastAssistant = session.History.ToArray().LastOrDefault(m => m.Role == "assistant" && m.Timestamp >= dispatchTimeLocal);
            if (lastAssistant != null && !string.IsNullOrWhiteSpace(lastAssistant.Content))
            {
                results.Add(new WorkerResult(workerName, lastAssistant.Content, true, null,
                    TimeSpan.FromSeconds((DateTime.UtcNow - pending.StartedAt).TotalSeconds)));
            }
            else
            {
                // Dead event stream fallback: in-memory history may be empty if the SDK event
                // callback stopped firing. Try reading events.jsonl directly from disk.
                string? diskResponse = null;
                if (!session.IsProcessing)
                {
                    try
                    {
                        var diskHistory = await LoadHistoryFromDiskAsync(session.SessionId);
                        var lastDiskAssistant = diskHistory
                            .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content)
                                && m.MessageType == ChatMessageType.Assistant
                                && m.Timestamp >= dispatchTimeLocal);
                        if (lastDiskAssistant != null)
                        {
                            diskResponse = lastDiskAssistant.Content;
                            Debug($"[DISPATCH] Worker '{workerName}' recovered {diskResponse!.Length} chars from events.jsonl (resume fallback)");
                        }
                    }
                    catch (Exception diskEx)
                    {
                        Debug($"[DISPATCH] Worker '{workerName}' events.jsonl resume fallback failed: {diskEx.Message}");
                    }
                }

                if (!string.IsNullOrWhiteSpace(diskResponse))
                {
                    results.Add(new WorkerResult(workerName, diskResponse, true, null,
                        TimeSpan.FromSeconds((DateTime.UtcNow - pending.StartedAt).TotalSeconds)));
                }
                else if (session.IsProcessing)
                {
                    results.Add(new WorkerResult(workerName, null, false, "Still processing (timed out)", TimeSpan.Zero));
                }
                else
                {
                    // Worker is idle with no response after dispatch — likely never started
                    // (e.g., TaskCanceledException killed the dispatch before the worker ran).
                    // Track these so we can re-dispatch them.
                    unstartedWorkers.Add(workerName);
                    results.Add(new WorkerResult(workerName, null, false, "No response found after restart", TimeSpan.Zero));
                }
            }
        }

        // Re-dispatch workers that never started (idle + no response since dispatch time).
        // This handles the case where TaskCanceledException killed SendToMultiAgentGroupAsync
        // before workers were dispatched, or the app restarted mid-dispatch.
        if (unstartedWorkers.Count > 0 && !ct.IsCancellationRequested)
        {
            Debug($"[DISPATCH] Re-dispatching {unstartedWorkers.Count} unstarted worker(s): {string.Join(", ", unstartedWorkers)}");
            AddOrchestratorSystemMessage(pending.OrchestratorName,
                $"🔄 Re-dispatching {unstartedWorkers.Count} worker(s) that never started: {string.Join(", ", unstartedWorkers)}");
            InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(pending.GroupId, OrchestratorPhase.Dispatching,
                $"Re-dispatching {unstartedWorkers.Count} worker(s)"));

            // BUG FIX: Clear stuck IsProcessing state on workers before re-dispatch.
            // After an app restart, workers may have IsProcessing=true from a previous
            // incomplete turn (e.g., tool.execution_start with no tool.execution_complete).
            // This prevents SendPromptAsync from accepting new prompts. Clear it here
            // so re-dispatch can actually send the prompt.
            foreach (var workerName in unstartedWorkers)
            {
                if (_sessions.TryGetValue(workerName, out var workerState) && workerState.Info.IsProcessing)
                {
                    Debug($"[DISPATCH] Clearing stuck IsProcessing on '{workerName}' before re-dispatch");
                    await ForceCompleteProcessingAsync(workerName, workerState, "pre-redispatch cleanup");
                }
            }

            // Build a generic task from the original prompt for re-dispatched workers.
            // Materialize as an array so we can inspect individual task results even on partial failure.
            var redispatchTaskArray = unstartedWorkers
                .Select(w => ExecuteWorkerAsync(w, $"Complete the following task:\n\n{pending.OriginalPrompt}", pending.OriginalPrompt, ct))
                .ToArray();

            try
            {
                await Task.WhenAll(redispatchTaskArray);
            }
            catch (OperationCanceledException)
            {
                // Propagate cancellation; don't continue to synthesis with stale results
                throw;
            }
            catch
            {
                // Partial failure: individual task errors are handled per-task below.
                // Do NOT abort — preserve results from workers that succeeded.
            }

            // Replace placeholders with actual results — preserves successes even on partial failure
            for (int i = 0; i < unstartedWorkers.Count; i++)
            {
                var t = redispatchTaskArray[i];
                var idx = results.FindIndex(r => r.WorkerName == unstartedWorkers[i]);
                if (idx < 0) continue;

                if (t.IsCompletedSuccessfully)
                {
                    results[idx] = t.Result;
                }
                else
                {
                    var errorMsg = t.Exception?.InnerException?.Message ?? t.Exception?.Message ?? "Task failed";
                    Debug($"[DISPATCH] Re-dispatch failed for '{unstartedWorkers[i]}': {errorMsg}");
                    AddOrchestratorSystemMessage(pending.OrchestratorName,
                        $"⚠️ Re-dispatch of '{unstartedWorkers[i]}' failed: {errorMsg}");
                    results[idx] = new WorkerResult(unstartedWorkers[i], null, false, errorMsg, TimeSpan.Zero);
                }
            }
        }

        var successCount = results.Count(r => r.Success);
        Debug($"[DISPATCH] Collected {successCount}/{results.Count} worker results for synthesis");

        if (successCount == 0)
        {
            AddOrchestratorSystemMessage(pending.OrchestratorName,
                "⚠️ No worker responses available after restart — orchestration aborted.");
            ClearPendingOrchestration();
            InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(pending.GroupId, OrchestratorPhase.Complete, null));
            return;
        }

        // Phase: Synthesize
        AddOrchestratorSystemMessage(pending.OrchestratorName,
            $"✅ Collected {successCount}/{results.Count} worker response(s) — sending synthesis to orchestrator...");
        InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(pending.GroupId, OrchestratorPhase.Synthesizing, "Resumed"));

        try
        {
            var synthesisPrompt = BuildSynthesisPrompt(pending.OriginalPrompt, results);

            // Wait for orchestrator to go idle if it's still processing (e.g., planning phase
            // response still streaming when workers complete and we try to send synthesis).
            await WaitForSessionIdleAsync(pending.OrchestratorName, ct);

            await SendPromptAsync(pending.OrchestratorName, synthesisPrompt, cancellationToken: ct, originalPrompt: pending.OriginalPrompt);
            Debug($"[DISPATCH] Resume synthesis sent to '{pending.OrchestratorName}'");
        }
        catch (Exception ex)
        {
            Debug($"[DISPATCH] Resume synthesis failed: {ex.Message}");
            AddOrchestratorSystemMessage(pending.OrchestratorName,
                $"⚠️ Failed to send synthesis: {ex.Message}");
        }

        ClearPendingOrchestration();
        InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(pending.GroupId, OrchestratorPhase.Complete, null));
    }

    #endregion

    /// <summary>
    /// Manually re-trigger orchestration for a multi-agent group.
    /// Use when a previous dispatch was interrupted (app restart, TaskCanceledException)
    /// and workers were never dispatched. Re-sends the provided prompt (or a resume
    /// instruction) through the normal dispatch path.
    /// </summary>
    public async Task RetryOrchestrationAsync(string groupId, string? prompt = null, CancellationToken ct = default)
    {
        var group = Organization.Groups.FirstOrDefault(g => g.Id == groupId && g.IsMultiAgent);
        if (group == null)
        {
            Debug($"[DISPATCH] RetryOrchestration: group '{groupId}' not found");
            return;
        }

        var members = GetMultiAgentGroupMembers(groupId);
        if (members.Count == 0)
        {
            Debug($"[DISPATCH] RetryOrchestration: no members for group '{group.Name}'");
            return;
        }

        // Use provided prompt, or construct a resume instruction from the orchestrator's last user message
        var effectivePrompt = prompt;
        if (string.IsNullOrEmpty(effectivePrompt))
        {
            var orchestratorName = GetOrchestratorSession(groupId);
            if (orchestratorName != null)
            {
                var session = GetSession(orchestratorName);
                var lastUserMsg = session?.History.ToArray().LastOrDefault(m => m.Role == "user");
                effectivePrompt = lastUserMsg?.Content;
            }
        }

        if (string.IsNullOrEmpty(effectivePrompt))
        {
            effectivePrompt = "Continue with any pending work from the previous orchestration round.";
        }

        Debug($"[DISPATCH] RetryOrchestration: group='{group.Name}', mode={group.OrchestratorMode}, prompt len={effectivePrompt.Length}");

        // Reset reflect state if needed so the loop can re-enter
        if (group.OrchestratorMode == MultiAgentMode.OrchestratorReflect && group.ReflectionState != null)
        {
            if (!group.ReflectionState.IsActive)
            {
                group.ReflectionState.IsActive = true;
                group.ReflectionState.CurrentIteration = 0;
                group.ReflectionState.GoalMet = false;
                group.ReflectionState.ConsecutiveErrors = 0;
                group.ReflectionState.CompletedAt = null;
            }
        }

        await SendToMultiAgentGroupAsync(groupId, effectivePrompt, ct);
    }

    /// <summary>
    /// Get the progress of a multi-agent group (how many sessions have completed their current turn).
    /// </summary>
    public (int Total, int Completed, int Processing, List<string> CompletedNames) GetMultiAgentProgress(string groupId)
    {
        var members = GetMultiAgentGroupMembers(groupId);
        var completed = new List<string>();
        int processing = 0;

        foreach (var name in members)
        {
            var session = GetSession(name);
            if (session == null) continue;

            if (session.IsProcessing)
                processing++;
            else
                completed.Add(name);
        }

        return (members.Count, completed.Count, processing, completed);
    }

    #endregion

    #region Per-Agent Model Assignment

    /// <summary>
    /// Set the preferred model for a session in a multi-agent group.
    /// The model is applied at dispatch time via EnsureSessionModelAsync.
    /// </summary>
    public void SetSessionPreferredModel(string sessionName, string? modelSlug)
    {
        var meta = Organization.Sessions.FirstOrDefault(m => m.SessionName == sessionName);
        if (meta == null) return;
        meta.PreferredModel = modelSlug != null ? Models.ModelHelper.NormalizeToSlug(modelSlug) : null;
        SaveOrganization();
        OnStateChanged?.Invoke();
    }

    public void SetSessionSystemPrompt(string sessionName, string? systemPrompt)
    {
        var meta = Organization.Sessions.FirstOrDefault(m => m.SessionName == sessionName);
        if (meta == null) return;
        meta.SystemPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt.Trim();
        SaveOrganization();
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Returns the model a session will use: PreferredModel if set, else live AgentSessionInfo.Model.
    /// </summary>
    public string GetEffectiveModel(string sessionName)
    {
        var meta = Organization.Sessions.FirstOrDefault(m => m.SessionName == sessionName);
        if (meta?.PreferredModel != null) return meta.PreferredModel;
        var session = GetSession(sessionName);
        return session?.Model ?? DefaultModel;
    }

    /// <summary>
    /// Create a multi-agent group from a preset template, creating sessions with assigned models.
    /// </summary>
    public async Task<SessionGroup?> CreateGroupFromPresetAsync(Models.GroupPreset preset, string? workingDirectory = null, string? worktreeId = null, string? repoId = null, string? nameOverride = null, WorktreeStrategy? strategyOverride = null, CancellationToken ct = default)
    {
        var teamName = nameOverride ?? preset.Name;
        var strategy = strategyOverride ?? preset.DefaultWorktreeStrategy ?? WorktreeStrategy.Shared;
        var group = CreateMultiAgentGroup(teamName, preset.Mode, worktreeId: worktreeId, repoId: repoId);
        if (group == null) return null;
        group.WorktreeStrategy = strategy;

        // Sanitize team name for use in git branch names (no spaces or special chars)
        var branchPrefix = System.Text.RegularExpressions.Regex.Replace(teamName, @"[^a-zA-Z0-9_-]", "-").Trim('-');
        if (string.IsNullOrEmpty(branchPrefix)) branchPrefix = "team";

        // Store Squad context (routing, decisions) on the group for use during orchestration
        group.SharedContext = preset.SharedContext;
        group.RoutingContext = preset.RoutingContext;
        if (preset.MaxReflectIterations.HasValue)
            group.MaxReflectIterations = preset.MaxReflectIterations;

        // Determine orchestrator working directory based on strategy
        var orchWorkDir = workingDirectory;
        var orchWtId = worktreeId;

        // Pre-fetch once to avoid parallel git lock contention (local mode only; server handles fetch in remote)
        if (repoId != null && strategy != WorktreeStrategy.Shared && !IsRemoteMode)
        {
            try { await _repoManager.FetchAsync(repoId, ct); }
            catch (Exception ex) { Debug($"Pre-fetch failed (continuing): {ex.Message}"); }
        }

        // For Shared strategy with a repo but no worktree, create a single shared worktree
        if (repoId != null && strategy == WorktreeStrategy.Shared && string.IsNullOrEmpty(worktreeId) && string.IsNullOrEmpty(workingDirectory))
        {
            try
            {
                if (!IsRemoteMode) await _repoManager.FetchAsync(repoId, ct);
                var sharedWt = await CreateWorktreeLocalOrRemoteAsync(repoId, $"{branchPrefix}-shared-{Guid.NewGuid().ToString()[..4]}", ct);
                orchWorkDir = sharedWt.Path;
                orchWtId = sharedWt.Id;
                group.WorktreeId = orchWtId;
                group.CreatedWorktreeIds.Add(orchWtId);
            }
            catch (Exception ex)
            {
                Debug($"Failed to create shared worktree (sessions will use temp dirs): {ex.Message}");
            }
        }

        if (repoId != null && strategy != WorktreeStrategy.Shared && string.IsNullOrEmpty(worktreeId))
        {
            try
            {
                var orchWt = await CreateWorktreeLocalOrRemoteAsync(repoId, $"{branchPrefix}-orchestrator-{Guid.NewGuid().ToString()[..4]}", ct);
                orchWorkDir = orchWt.Path;
                orchWtId = orchWt.Id;
                group.WorktreeId = orchWtId;
                group.CreatedWorktreeIds.Add(orchWtId);
            }
            catch (Exception ex)
            {
                Debug($"Failed to create orchestrator worktree (falling back to shared): {ex.Message}");
            }
        }

        // Pre-create worker worktrees sequentially (git worktree add uses locks on bare repos)
        string?[] workerWorkDirs = new string?[preset.WorkerModels.Length];
        string?[] workerWtIds = new string?[preset.WorkerModels.Length];
        if (repoId != null && strategy == WorktreeStrategy.FullyIsolated)
        {
            for (int i = 0; i < preset.WorkerModels.Length; i++)
            {
                try
                {
                    var wt = await CreateWorktreeLocalOrRemoteAsync(repoId, $"{branchPrefix}-worker-{i + 1}-{Guid.NewGuid().ToString()[..4]}", ct);
                    workerWorkDirs[i] = wt.Path;
                    workerWtIds[i] = wt.Id;
                    group.CreatedWorktreeIds.Add(wt.Id);
                }
                catch (Exception ex)
                {
                    Debug($"Failed to create worker-{i + 1} worktree (falling back to shared): {ex.Message}");
                }
            }
        }
        else if (repoId != null && strategy == WorktreeStrategy.OrchestratorIsolated)
        {
            try
            {
                var sharedWorkerWt = await CreateWorktreeLocalOrRemoteAsync(repoId, $"{branchPrefix}-workers-{Guid.NewGuid().ToString()[..4]}", ct);
                group.CreatedWorktreeIds.Add(sharedWorkerWt.Id);
                for (int i = 0; i < preset.WorkerModels.Length; i++)
                {
                    workerWorkDirs[i] = sharedWorkerWt.Path;
                    workerWtIds[i] = sharedWorkerWt.Id;
                }
            }
            catch (Exception ex)
            {
                Debug($"Failed to create shared worker worktree (falling back to shared): {ex.Message}");
            }
        }

        var createdWtCount = workerWtIds.Count(id => id != null) + (orchWtId != worktreeId ? 1 : 0);
        Debug($"[WorktreeStrategy] Strategy={strategy}, orchDir={orchWorkDir ?? "(null)"}, orchWtId={orchWtId ?? "(none)"}, workerWts created={workerWtIds.Count(id => id != null)}/{preset.WorkerModels.Length}");

        // Create orchestrator session (with uniqueness check matching CreateMultiAgentGroupAsync)
        var orchName = $"{teamName}-orchestrator";
        { int suffix = 1;
          while (_sessions.ContainsKey(orchName) || Organization.Sessions.Any(s => s.SessionName == orchName))
              orchName = $"{teamName}-orchestrator-{suffix++}";
        }
        try
        {
            await CreateSessionAsync(orchName, preset.OrchestratorModel, orchWorkDir, ct);
        }
        catch (Exception ex)
        {
            Debug($"Failed to create orchestrator session: {ex.Message}");
        }
        // Assign role/group/model even if session already existed from a previous run
        MoveSession(orchName, group.Id);
        SetSessionRole(orchName, MultiAgentRole.Orchestrator);
        SetSessionPreferredModel(orchName, preset.OrchestratorModel);
        // Pin orchestrator so it sorts to the top of the group
        var orchMeta = GetSessionMeta(orchName);
        if (orchMeta != null) orchMeta.IsPinned = true;
        if (orchWtId != null && orchMeta != null)
            orchMeta.WorktreeId = orchWtId;
        if (orchWtId != null && _sessions.TryGetValue(orchName, out var orchState))
            orchState.Info.WorktreeId = orchWtId;

        // Create worker sessions
        Debug($"[WorktreeStrategy] Creating {preset.WorkerModels.Length} workers with strategy={strategy}, repoId={repoId}");
        for (int i = 0; i < preset.WorkerModels.Length; i++)
        {
            var displayName = preset.WorkerDisplayNames != null && i < preset.WorkerDisplayNames.Length && preset.WorkerDisplayNames[i] != null
                ? preset.WorkerDisplayNames[i]!
                : $"worker-{i + 1}";
            var workerName = $"{teamName}-{displayName}";
            { int suffix = 1;
              while (_sessions.ContainsKey(workerName) || Organization.Sessions.Any(s => s.SessionName == workerName))
                  workerName = $"{teamName}-{displayName}-{suffix++}";
            }
            var workerModel = preset.WorkerModels[i];
            var workerWorkDir = workerWorkDirs[i] ?? orchWorkDir ?? workingDirectory;
            Debug($"[WorktreeStrategy] Worker '{workerName}': wtId={workerWtIds[i] ?? "(none)"}, dir={workerWorkDir ?? "(null)"}");
            try
            {
                await CreateSessionAsync(workerName, workerModel, workerWorkDir, ct);
            }
            catch (Exception ex)
            {
                Debug($"Failed to create worker session '{workerName}': {ex.Message}");
            }
            // Assign group/model/prompt even if session already existed from a previous run
            MoveSession(workerName, group.Id);
            SetSessionPreferredModel(workerName, workerModel);
            var systemPrompt = preset.WorkerSystemPrompts != null && i < preset.WorkerSystemPrompts.Length
                ? preset.WorkerSystemPrompts[i] : null;
            var meta = GetSessionMeta(workerName);
            if (meta != null)
            {
                meta.WorktreeId = workerWtIds[i] ?? worktreeId;
                if (systemPrompt != null) meta.SystemPrompt = systemPrompt;
            }
            var effectiveWtId = workerWtIds[i] ?? worktreeId;
            if (effectiveWtId != null && _sessions.TryGetValue(workerName, out var workerState))
                workerState.Info.WorktreeId = effectiveWtId;
        }

        SaveOrganization();
        // Multi-agent group creation is a critical structural change — flush immediately
        // instead of relying on the 2s debounce. If the process is killed (e.g., relaunch),
        // the debounce timer never fires and the group is lost on restart.
        FlushSaveOrganization();
        // Also flush active-sessions.json so the new sessions are known on restart.
        // Without this, ReconcileOrganization prunes the squad sessions from org
        // because they're not in active-sessions.json yet (still waiting on 2s debounce).
        FlushSaveActiveSessionsToDisk();
        OnStateChanged?.Invoke();

        // In remote mode, push the organization to the server so it knows about the
        // multi-agent group structure. Without this, the server puts all sessions in the
        // default group and overwrites the mobile's local organization on the next broadcast.
        if (IsRemoteMode)
        {
            try { await _bridgeClient.PushOrganizationAsync(Organization, ct); }
            catch (Exception ex) { Debug($"Failed to push organization to server: {ex.Message}"); }
        }

        return group;
    }

    /// <summary>
    /// Ensures a session's live model matches its PreferredModel before dispatch.
    /// Uses per-session semaphore to prevent concurrent model switches.
    /// No-op if PreferredModel is null or already matches.
    /// </summary>
    private async Task EnsureSessionModelAsync(string sessionName, CancellationToken ct)
    {
        var meta = Organization.Sessions.FirstOrDefault(m => m.SessionName == sessionName);
        if (meta?.PreferredModel == null) return;

        var session = GetSession(sessionName);
        if (session == null) return;

        var currentSlug = Models.ModelHelper.NormalizeToSlug(session.Model);
        if (currentSlug == meta.PreferredModel) return;

        var semaphore = _modelSwitchLocks.GetOrAdd(sessionName, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);
        try
        {
            // Re-check after acquiring lock — another dispatch may have already switched
            currentSlug = Models.ModelHelper.NormalizeToSlug(GetSession(sessionName)?.Model ?? "");
            if (currentSlug == meta.PreferredModel) return;

            await ChangeModelAsync(sessionName, meta.PreferredModel, ct);
            Debug($"Switched '{sessionName}' model to '{meta.PreferredModel}' for multi-agent dispatch");
        }
        catch (Exception ex)
        {
            Debug($"Failed to switch model for '{sessionName}': {ex.Message}");
        }
        finally
        {
            semaphore.Release();
        }
    }

    #endregion

    #region OrchestratorReflect Loop

    /// <summary>
    /// Start a reflection loop on a multi-agent group.
    /// </summary>
    public void StartGroupReflection(string groupId, string goal, int maxIterations = 5)
    {
        var group = Organization.Groups.FirstOrDefault(g => g.Id == groupId && g.IsMultiAgent);
        if (group == null) return;

        // Don't overwrite an active reflection — the user must stop it first.
        // This runs on the UI thread (called from Dashboard.razor event handlers),
        // so no lock is needed — all Organization mutations are UI-thread-only.
        if (group.ReflectionState is { IsActive: true, IsCancelled: false })
        {
            Debug($"StartGroupReflection: skipping — group '{group.Name}' already has active reflection (iteration {group.ReflectionState.CurrentIteration})");
            return;
        }

        group.ReflectionState = ReflectionCycle.Create(goal, maxIterations);
        group.OrchestratorMode = MultiAgentMode.OrchestratorReflect;
        SaveOrganization();
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Stop an active group reflection loop.
    /// </summary>
    public void StopGroupReflection(string groupId)
    {
        var group = Organization.Groups.FirstOrDefault(g => g.Id == groupId);
        if (group?.ReflectionState == null) return;

        group.ReflectionState.IsActive = false;
        group.ReflectionState.IsCancelled = true;
        group.ReflectionState.CompletedAt = DateTime.Now;
        SaveOrganization();
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Pause/resume a group reflection loop.
    /// </summary>
    public void PauseGroupReflection(string groupId, bool paused)
    {
        var group = Organization.Groups.FirstOrDefault(g => g.Id == groupId);
        if (group?.ReflectionState == null) return;
        group.ReflectionState.IsPaused = paused;
        SaveOrganization();
        OnStateChanged?.Invoke();
    }

    private async Task SendViaOrchestratorReflectAsync(string groupId, List<string> members, string prompt, CancellationToken ct)
    {
        var group = Organization.Groups.FirstOrDefault(g => g.Id == groupId);
        if (group == null) return;

        var reflectState = group.ReflectionState;
        if (reflectState == null || !reflectState.IsActive)
        {
            // Not in reflect mode — fall back to regular orchestrator
            await SendViaOrchestratorAsync(groupId, members, prompt, ct);
            return;
        }

        var orchestratorName = GetOrchestratorSession(groupId);
        if (orchestratorName == null)
        {
            await SendBroadcastAsync(group, members, prompt, ct);
            return;
        }

        // Prevent concurrent reflect loop invocations for the same group.
        // A second user message while workers are running would start a competing
        // loop that races over shared reflectState, causing worker results to be lost.
        var loopLock = _reflectLoopLocks.GetOrAdd(groupId, _ => new SemaphoreSlim(1, 1));
        if (!loopLock.Wait(0))
        {
            // Loop already running — queue the prompt so it gets sent to the orchestrator's
            // SDK session at the start of the next iteration (not just local UI history).
            Debug($"[DISPATCH] Reflect loop already running for group '{group.Name}' — queuing prompt for next iteration");
            var queue = _reflectQueuedPrompts.GetOrAdd(groupId, _ => new ConcurrentQueue<string>());
            queue.Enqueue(prompt);
            AddOrchestratorSystemMessage(orchestratorName,
                $"📨 New user message queued (will be sent to orchestrator at next iteration): {prompt}");
            return;
        }

        var leftoverPrompts = new List<string>();
        try
        {

        var workerNames = members.Where(m => m != orchestratorName).ToList();
        // Tracks workers that have successfully completed at least once across all iterations.
        // Access is sequential (single async flow, no concurrent modification).
        var dispatchedWorkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var attemptedWorkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
        while (reflectState.IsActive && !reflectState.IsPaused
               && reflectState.CurrentIteration < reflectState.MaxIterations)
        {
            ct.ThrowIfCancellationRequested();
            reflectState.CurrentIteration++;

            try
            {
            Debug($"Reflection loop: starting iteration {reflectState.CurrentIteration}/{reflectState.MaxIterations} " +
                  $"(IsActive={reflectState.IsActive}, IsPaused={reflectState.IsPaused})");

            // Drain any user prompts queued while the loop was busy (e.g., waiting for workers).
            // These are sent to the orchestrator's SDK session so the model sees them.
            // If the model responds with @worker blocks, merge them into this iteration's assignments.
            var queuedAssignments = new List<TaskAssignment>();
            if (_reflectQueuedPrompts.TryGetValue(groupId, out var promptQueue))
            {
                while (promptQueue.TryDequeue(out var queuedPrompt))
                {
                    Debug($"[DISPATCH] Draining queued prompt for '{orchestratorName}' (len={queuedPrompt.Length})");
                    var queuedResponse = await SendPromptAndWaitAsync(orchestratorName,
                        $"[User sent a new message while you were working]\n\n{queuedPrompt}", ct, originalPrompt: queuedPrompt);
                    var parsed = ParseTaskAssignments(queuedResponse, workerNames);
                    if (parsed.Count > 0)
                    {
                        Debug($"[DISPATCH] Queued prompt response contained {parsed.Count} @worker assignments");
                        queuedAssignments.AddRange(parsed);
                    }
                }
            }

            // Phase 1: Plan (first iteration) or Re-plan (subsequent)
            var iterDetail = $"Iteration {reflectState.CurrentIteration}/{reflectState.MaxIterations}";
            InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(groupId, OrchestratorPhase.Planning, iterDetail));

            string planPrompt;
            if (reflectState.CurrentIteration == 1)
            {
                planPrompt = BuildOrchestratorPlanningPrompt(prompt, workerNames, group.OrchestratorPrompt, group.RoutingContext, dispatcherOnly: true);
            }
            else
            {
                planPrompt = BuildReplanPrompt(reflectState.LastEvaluation ?? "Continue iterating.", workerNames, prompt, group.RoutingContext);
            }

            // Enable early dispatch: if the orchestrator writes @worker blocks in an intermediate
            // sub-turn (before finishing all tool rounds), FlushCurrentResponse will resolve the
            // TCS immediately. This prevents the orchestrator from doing all the work itself.
            if (_sessions.TryGetValue(orchestratorName, out var orchState))
                orchState.EarlyDispatchOnWorkerBlocks = true;

            var planResponse = await SendPromptAndWaitAsync(orchestratorName, planPrompt, ct, originalPrompt: prompt);
            var rawAssignments = ParseTaskAssignments(planResponse, workerNames);
            Debug($"[DISPATCH] '{orchestratorName}' reflect plan parsed: {rawAssignments.Count} raw assignments from {workerNames.Count} workers. Iteration={reflectState.CurrentIteration}, Response length={planResponse.Length}");
            var assignments = DeduplicateAssignments(rawAssignments);

            if (assignments.Count == 0)
            {
                if (reflectState.CurrentIteration == 1)
                {
                    // First iteration with no assignments = orchestrator failed to delegate.
                    // Send a stronger nudge prompt instead of repeating the same planning prompt.
                    Debug($"[DISPATCH] Reflect iteration 1: no assignments, sending delegation nudge");
                    AddOrchestratorSystemMessage(orchestratorName,
                        "⚠️ No @worker assignments parsed from orchestrator response. Retrying...");
                    var nudgePrompt = BuildDelegationNudgePrompt(workerNames);
                    // Re-enable early dispatch for the nudge attempt too
                    if (_sessions.TryGetValue(orchestratorName, out var nudgeState))
                        nudgeState.EarlyDispatchOnWorkerBlocks = true;
                    var nudgeResponse = await SendPromptAndWaitAsync(orchestratorName, nudgePrompt, ct, originalPrompt: prompt);
                    var nudgeAssignments = ParseTaskAssignments(nudgeResponse, workerNames);
                    Debug($"[DISPATCH] '{orchestratorName}' nudge parsed: {nudgeAssignments.Count} raw assignments. Response length={nudgeResponse.Length}");
                    if (nudgeAssignments.Count > 0)
                    {
                        assignments = DeduplicateAssignments(nudgeAssignments);
                        // Fall through to dispatch below
                    }
                    else
                    {
                        // Nudge also failed — the orchestrator refuses to delegate (likely due to
                        // stale history from a previous run). Force delegation by creating synthetic
                        // @worker blocks that assign the original prompt to each worker.
                        Debug($"[DISPATCH] Nudge failed, forcing delegation to all {workerNames.Count} workers");
                        AddOrchestratorSystemMessage(orchestratorName,
                            $"⚡ Orchestrator refused to delegate after nudge. Forcing dispatch to all {workerNames.Count} workers.");
                        assignments = workerNames.Select(w => new TaskAssignment(w, prompt)).ToList();
                        // Fall through to dispatch below
                    }
                }
                else
                {
                    // Later iterations: orchestrator decided no more work needed —
                    // but only declare GoalMet if all workers have been dispatched
                    var allDispatched = workerNames.All(w => dispatchedWorkers.Contains(w));
                    var allAttempted = workerNames.All(w => attemptedWorkers.Contains(w));
                    if (queuedAssignments.Count == 0 && (allDispatched || allAttempted))
                    {
                        reflectState.GoalMet = true;
                        AddOrchestratorSystemMessage(orchestratorName, $"✅ Orchestrator completed without delegation (iteration {reflectState.CurrentIteration}).");
                        break;
                    }
                    if (!allDispatched && queuedAssignments.Count == 0)
                    {
                        // Not all workers dispatched — force dispatch remaining workers
                        var remaining = workerNames.Where(w => !dispatchedWorkers.Contains(w) && !attemptedWorkers.Contains(w)).ToList();
                        Debug($"[DISPATCH] Iteration {reflectState.CurrentIteration}: 0 assignments but {remaining.Count} workers never dispatched — forcing: {string.Join(", ", remaining)}");
                        AddOrchestratorSystemMessage(orchestratorName,
                            $"⚡ Forcing dispatch to {remaining.Count} remaining worker(s): {string.Join(", ", remaining)}");
                        assignments = remaining.Select(w => new TaskAssignment(w, prompt)).ToList();
                        // Fall through to dispatch below
                    }
                    // Fall through to merge and dispatch queued work
                }
            }

            // Merge any @worker assignments from queued prompt responses
            if (queuedAssignments.Count > 0)
            {
                var extra = DeduplicateAssignments(queuedAssignments);
                foreach (var a in extra)
                {
                    var existing = assignments.FirstOrDefault(x => string.Equals(x.WorkerName, a.WorkerName, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                        assignments[assignments.IndexOf(existing)] = new TaskAssignment(existing.WorkerName, existing.Task + "\n\n---\n\n" + a.Task);
                    else
                        assignments.Add(a);
                }
            }

            if (assignments.Count == 0)
                continue;

            // Phase 2-3: Dispatch + Collect
            InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(groupId, OrchestratorPhase.Dispatching,
                $"Sending tasks to {assignments.Count} worker(s) — {iterDetail}"));

            // Persist dispatch state for relaunch resilience
            SavePendingOrchestration(new PendingOrchestration
            {
                GroupId = groupId,
                OrchestratorName = orchestratorName,
                WorkerNames = assignments.Select(a => a.WorkerName).ToList(),
                OriginalPrompt = prompt,
                StartedAt = DateTime.UtcNow,
                IsReflect = true,
                ReflectIteration = reflectState.CurrentIteration
            });

            InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(groupId, OrchestratorPhase.WaitingForWorkers, iterDetail));

            var workerTasks = assignments.Select(a => ExecuteWorkerAsync(a.WorkerName, a.Task, prompt, ct));
            var results = await Task.WhenAll(workerTasks);

            // Track both attempted and successful workers across all iterations
            foreach (var a in assignments)
                attemptedWorkers.Add(a.WorkerName);
            foreach (var r in results.Where(r => r.Success))
                dispatchedWorkers.Add(r.WorkerName);

            // After early dispatch, the orchestrator may still be doing tool work.
            // Wait for it to go idle before sending the synthesis prompt.
            await WaitForSessionIdleAsync(orchestratorName, ct);

            // Phase 4: Synthesize + Evaluate
            InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(groupId, OrchestratorPhase.Synthesizing, iterDetail));

            var synthEvalPrompt = BuildSynthesisWithEvalPrompt(prompt, results.ToList(), reflectState,
                group.RoutingContext, dispatchedWorkers, workerNames);

            // Check worker participation once for both evaluator and self-eval paths
            var allWorkersDispatched = workerNames.All(w => dispatchedWorkers.Contains(w));


            // Use dedicated evaluator session if configured, otherwise orchestrator self-evaluates
            string evaluatorName = reflectState.EvaluatorSessionName ?? orchestratorName;
            string synthesisResponse;
            if (reflectState.EvaluatorSessionName != null && reflectState.EvaluatorSessionName != orchestratorName)
            {
                // Send results to orchestrator for synthesis
                var synthOnlyPrompt = BuildSynthesisOnlyPrompt(prompt, results.ToList());
                synthesisResponse = await SendPromptAndWaitAsync(orchestratorName, synthOnlyPrompt, ct, originalPrompt: prompt);

                // Send to evaluator for independent scoring
                var evalOnlyPrompt = BuildEvaluatorPrompt(prompt, synthesisResponse, reflectState);
                var evalResponse = await SendPromptAndWaitAsync(evaluatorName, evalOnlyPrompt, ct, originalPrompt: prompt);

                // Parse score from evaluator
                var (score, rationale) = ParseEvaluationScore(evalResponse);
                var evaluatorModel = GetEffectiveModel(evaluatorName);
                var trend = reflectState.RecordEvaluation(reflectState.CurrentIteration, score, rationale, evaluatorModel);

                // Check if evaluator says complete
                var allWorkersAttempted = workerNames.All(w => attemptedWorkers.Contains(w));
                if ((evalResponse.Contains("[[GROUP_REFLECT_COMPLETE]]", StringComparison.OrdinalIgnoreCase) || score >= 0.9)
                    && (allWorkersDispatched || allWorkersAttempted))
                {
                    var partial = !allWorkersDispatched && allWorkersAttempted;
                    reflectState.GoalMet = true;
                    reflectState.IsActive = false;
                    var suffix = partial ? " (some workers failed but all were attempted)" : "";
                    AddOrchestratorSystemMessage(orchestratorName, $"✅ {reflectState.BuildCompletionSummary()} (score: {score:F1}){suffix}");
                    break;
                }

                if (!allWorkersDispatched)
                {
                    var missing = workerNames.Where(w => !dispatchedWorkers.Contains(w)).ToList();
                    var failedButAttempted = missing.Where(w => attemptedWorkers.Contains(w)).ToList();
                    var neverDispatched = missing.Where(w => !attemptedWorkers.Contains(w)).ToList();
                    var detail = neverDispatched.Count > 0
                        ? $"Not yet dispatched: {string.Join(", ", neverDispatched)}."
                        : $"Dispatched but failed: {string.Join(", ", failedButAttempted)}. Will retry next iteration.";
                    Debug($"Reflection: overriding completion — {detail}");
                    reflectState.LastEvaluation = $"{detail} Dispatch to the remaining workers before completing.";
                    AddOrchestratorSystemMessage(orchestratorName, $"🔄 Overriding completion — {detail}");
                }
                else
                {
                    reflectState.LastEvaluation = rationale;
                }

                if (trend == Models.QualityTrend.Degrading)
                    reflectState.PendingAdjustments.Add("📉 Quality degrading — consider changing worker models or refining the goal.");
            }
            else
            {
                synthesisResponse = await SendPromptAndWaitAsync(orchestratorName, synthEvalPrompt, ct, originalPrompt: prompt);

                // Check completion sentinel
                var allWorkersAttempted = workerNames.All(w => attemptedWorkers.Contains(w));
                if (synthesisResponse.Contains("[[GROUP_REFLECT_COMPLETE]]", StringComparison.OrdinalIgnoreCase)
                    && (allWorkersDispatched || allWorkersAttempted))
                {
                    var partial = !allWorkersDispatched && allWorkersAttempted;
                    reflectState.GoalMet = true;
                    reflectState.IsActive = false;
                    var suffix = partial ? " (some workers failed but all were attempted)" : "";
                    AddOrchestratorSystemMessage(orchestratorName, $"✅ {reflectState.BuildCompletionSummary()}{suffix}");
                    break;
                }

                if (synthesisResponse.Contains("[[GROUP_REFLECT_COMPLETE]]", StringComparison.OrdinalIgnoreCase)
                    && !allWorkersAttempted)
                {
                    // Override premature completion — not all workers have participated
                    var missing = workerNames.Where(w => !dispatchedWorkers.Contains(w)).ToList();
                    var failedButAttempted = missing.Where(w => attemptedWorkers.Contains(w)).ToList();
                    var neverDispatched = missing.Where(w => !attemptedWorkers.Contains(w)).ToList();
                    var detail = neverDispatched.Count > 0
                        ? $"Not yet dispatched: {string.Join(", ", neverDispatched)}."
                        : $"Dispatched but failed: {string.Join(", ", failedButAttempted)}. Retry or address errors.";
                    Debug($"Reflection: overriding [[GROUP_REFLECT_COMPLETE]] — {detail}");
                    reflectState.LastEvaluation = $"{detail} Dispatch to the remaining workers before completing.";
                    AddOrchestratorSystemMessage(orchestratorName, $"🔄 Overriding completion — {detail}");
                    reflectState.RecordEvaluation(reflectState.CurrentIteration, 0.3,
                        reflectState.LastEvaluation, GetEffectiveModel(orchestratorName));
                }
                else
                {
                    // Extract evaluation for next iteration
                    reflectState.LastEvaluation = ExtractIterationEvaluation(synthesisResponse);

                    // Record a self-eval score (estimated from sentinel presence)
                    var selfScore = synthesisResponse.Contains("[[NEEDS_ITERATION]]", StringComparison.OrdinalIgnoreCase) ? 0.4 : 0.7;
                    reflectState.RecordEvaluation(reflectState.CurrentIteration, selfScore,
                        reflectState.LastEvaluation ?? "", GetEffectiveModel(orchestratorName));
                }
            }

            // Auto-adjustment: analyze worker results and suggest/apply changes
            AutoAdjustFromFeedback(groupId, group, results.ToList(), reflectState);

            // Stall detection — use 2-consecutive tolerance like single-agent Advance()
            if (reflectState.CheckStall(synthesisResponse))
            {
                reflectState.ConsecutiveStalls++;
                if (reflectState.ConsecutiveStalls >= 2)
                {
                    reflectState.IsStalled = true;
                    reflectState.IsCancelled = true;
                    AddOrchestratorSystemMessage(orchestratorName, $"⚠️ {reflectState.BuildCompletionSummary()}");
                    break;
                }
                // First stall: warn but continue
                reflectState.PendingAdjustments.Add("⚠️ Output similarity detected — may be stalling. Will stop if it repeats.");
            }
            else
            {
                reflectState.ConsecutiveStalls = 0;
                reflectState.ConsecutiveErrors = 0;
            }

            SaveOrganization();
            InvokeOnUI(() => OnStateChanged?.Invoke());

            } // end try
            catch (OperationCanceledException)
            {
                reflectState.IsCancelled = true;
                throw;
            }
            catch (Exception ex)
            {
                Debug($"Reflection iteration {reflectState.CurrentIteration} error: {ex.GetType().Name}: {ex.Message}");
                // Decrement so we retry the same iteration, not skip ahead
                reflectState.CurrentIteration--;
                // But limit retries per iteration to 3 (uses separate error counter)
                reflectState.ConsecutiveErrors++;
                if (reflectState.ConsecutiveErrors >= 3)
                {
                    reflectState.IsStalled = true;
                    reflectState.IsCancelled = true;
                    AddOrchestratorSystemMessage(orchestratorName,
                        $"⚠️ Iteration failed after retries: {ex.Message}");
                    break;
                }
                AddOrchestratorSystemMessage(orchestratorName,
                    $"⚠️ Iteration {reflectState.CurrentIteration + 1} error: {ex.Message}. Retrying...");
                InvokeOnUI(() => OnStateChanged?.Invoke());
                await Task.Delay(2000, ct);
            }
        }

        if (!reflectState.GoalMet && !reflectState.IsStalled && !reflectState.IsPaused)
        {
            // Max-iteration exit without goal met — mark as cancelled so callers
            // can distinguish "ran out of iterations" from "succeeded".
            reflectState.IsCancelled = true;
            AddOrchestratorSystemMessage(orchestratorName, $"⏱️ {reflectState.BuildCompletionSummary()}");
        }
        }
        finally
        {
            // Always clear IsActive — even on OperationCanceledException.
            // Without this, a cancelled reflection permanently blocks future reflections.
            reflectState.IsActive = false;
            reflectState.CompletedAt = DateTime.Now;
            ClearPendingOrchestration();
            // Collect leftover queued prompts synchronously for best-effort delivery
            // AFTER releasing the semaphore. This prevents holding the semaphore forever
            // if SendPromptAndWaitAsync blocks on a broken SDK connection (e.g., StreamJsonRpc
            // serialization errors during cancellation). See PR #323.
            if (_reflectQueuedPrompts.TryGetValue(groupId, out var remainingQueue))
            {
                while (remainingQueue.TryDequeue(out var leftover))
                    leftoverPrompts.Add(leftover);
            }

            SaveOrganization();
            InvokeOnUI(() =>
            {
                OnOrchestratorPhaseChanged?.Invoke(groupId, OrchestratorPhase.Complete, reflectState.BuildCompletionSummary());
                OnStateChanged?.Invoke();
            });
        }

        } // end loopLock guard
        finally
        {
            loopLock.Release();
        }

        // Fire-and-forget: deliver leftover prompts after semaphore is released.
        // If delivery fails (broken connection), prompts are still visible in chat history.
        if (leftoverPrompts.Count > 0)
        {
            var orchName = orchestratorName;
            SafeFireAndForget(Task.Run(async () =>
            {
                foreach (var leftover in leftoverPrompts)
                {
                    try
                    {
                        Debug($"[DISPATCH] Sending leftover queued prompt after loop exit (len={leftover.Length})");
                        using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        await SendPromptAndWaitAsync(orchName,
                            $"[User sent a message — the reflection loop has completed]\n\n{leftover}",
                            cleanupCts.Token, originalPrompt: leftover);
                    }
                    catch (Exception ex)
                    {
                        Debug($"[DISPATCH] Failed to send leftover queued prompt: {ex.Message}");
                    }
                }
            }), "leftover-prompt-delivery");
        }
    }

    private string BuildSynthesisWithEvalPrompt(string originalPrompt, List<WorkerResult> results, ReflectionCycle state,
        string? routingContext = null, HashSet<string>? dispatchedWorkers = null, List<string>? allWorkers = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(BuildSynthesisPrompt(originalPrompt, results));
        sb.AppendLine();
        if (!string.IsNullOrEmpty(routingContext))
        {
            sb.AppendLine("## Work Routing (from team definition)");
            sb.AppendLine(routingContext);
            sb.AppendLine();
        }
        // Show which workers have/haven't participated
        if (dispatchedWorkers != null && allWorkers != null)
        {
            var missing = allWorkers.Where(w => !dispatchedWorkers.Contains(w)).ToList();
            if (missing.Count > 0)
            {
                sb.AppendLine("### ⚠️ Worker Participation");
                sb.AppendLine($"The following workers have NOT yet been dispatched: **{string.Join(", ", missing)}**");
                sb.AppendLine("You MUST include `[[NEEDS_ITERATION]]` and dispatch to them before marking complete.");
                sb.AppendLine();
            }
        }
        sb.AppendLine($"## Evaluation Check (Iteration {state.CurrentIteration}/{state.MaxIterations})");
        sb.AppendLine($"**Goal:** {state.Goal}");
        sb.AppendLine();
        sb.AppendLine("### Quality Assessment");
        sb.AppendLine("Before deciding, evaluate each worker's output:");
        sb.AppendLine("1. **Completeness** — Did they fully address their assigned task?");
        sb.AppendLine("2. **Correctness** — Is the output accurate and well-reasoned?");
        sb.AppendLine("3. **Relevance** — Does it contribute meaningfully toward the goal?");
        sb.AppendLine("4. **Tool Verification** — Did the worker actually run tools, or did they fabricate results? Workers reporting TOOL_FAILURE are honest failures; workers presenting results without evidence of tool execution may have fabricated their output.");
        sb.AppendLine();
        if (state.CurrentIteration > 1 && state.LastEvaluation != null)
        {
            sb.AppendLine("### Previous Iteration Feedback");
            sb.AppendLine(state.LastEvaluation);
            sb.AppendLine();
            sb.AppendLine("Check whether the identified gaps have been addressed in this iteration.");
            sb.AppendLine();
        }
        sb.AppendLine("### Decision");
        sb.AppendLine("- If the combined output **fully satisfies** the goal: Include `[[GROUP_REFLECT_COMPLETE]]` with a summary.");
        sb.AppendLine("- If **not yet complete**: Include `[[NEEDS_ITERATION]]` followed by:");
        sb.AppendLine("  1. What specific gaps remain (be precise)");
        sb.AppendLine("  2. Whether quality improved, degraded, or stalled vs. previous iteration");
        sb.AppendLine("  3. Revised `@worker:name` / `@end` blocks for the next iteration");
        if (state.CurrentIteration >= state.MaxIterations - 1)
        {
            sb.AppendLine();
            sb.AppendLine($"⚠️ This is iteration {state.CurrentIteration} of {state.MaxIterations}. If close to the goal, consider completing with what you have rather than requesting another iteration.");
        }
        return sb.ToString();
    }

    private string BuildReplanPrompt(string lastEvaluation, List<string> workerNames, string originalPrompt,
        string? routingContext = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are a DISPATCHER ONLY. You do NOT have tools. You CANNOT write code, read files, or run commands yourself.");
        sb.AppendLine("Your ONLY job is to write @worker/@end blocks that assign work to your workers.");
        sb.AppendLine("Even if previous conversation history shows completed work, you MUST dispatch FRESH tasks now.");
        sb.AppendLine();
        sb.AppendLine("## Previous Iteration Evaluation");
        sb.AppendLine(lastEvaluation);
        sb.AppendLine();
        sb.AppendLine("## Original Request (context)");
        sb.AppendLine(originalPrompt);
        sb.AppendLine();
        if (!string.IsNullOrEmpty(routingContext))
        {
            sb.AppendLine("## Work Routing (from team definition)");
            sb.AppendLine(routingContext);
            sb.AppendLine();
        }
        sb.AppendLine($"Available workers ({workerNames.Count}):");
        foreach (var w in workerNames)
            sb.AppendLine($"  - '{w}' (model: {GetEffectiveModel(w)})");
        sb.AppendLine();
        sb.AppendLine("Assign refined tasks using `@worker:name` / `@end` blocks to address the gaps identified above.");
        sb.AppendLine("You MUST produce at least one @worker block. NEVER attempt to do the work yourself.");
        return sb.ToString();
    }

    private static string ExtractIterationEvaluation(string response)
    {
        // Extract text after [[NEEDS_ITERATION]] marker, or use full response as evaluation
        var idx = response.IndexOf("[[NEEDS_ITERATION]]", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var afterMarker = response[(idx + "[[NEEDS_ITERATION]]".Length)..].Trim();
            // Take text up to first @worker block as the evaluation
            var workerIdx = afterMarker.IndexOf("@worker:", StringComparison.OrdinalIgnoreCase);
            return workerIdx >= 0 ? afterMarker[..workerIdx].Trim() : afterMarker;
        }
        // No marker — use last paragraph as evaluation
        var lines = response.Split('\n');
        return string.Join('\n', lines.TakeLast(5)).Trim();
    }

    /// <summary>Build a synthesis-only prompt (no evaluation decision) for use with separate evaluator.</summary>
    private string BuildSynthesisOnlyPrompt(string originalPrompt, List<WorkerResult> results)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(BuildSynthesisPrompt(originalPrompt, results));
        sb.AppendLine();
        sb.AppendLine("Synthesize the worker outputs into a unified, coherent response. Do NOT make a completion decision — an independent evaluator will assess quality separately.");
        return sb.ToString();
    }

    /// <summary>Build a prompt for an independent evaluator session to score synthesis quality.</summary>
    private static string BuildEvaluatorPrompt(string originalGoal, string synthesisResponse, ReflectionCycle state)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Independent Quality Evaluation");
        sb.AppendLine($"**Goal:** {state.Goal}");
        sb.AppendLine($"**Iteration:** {state.CurrentIteration}/{state.MaxIterations}");
        sb.AppendLine();
        sb.AppendLine("### Synthesized Output to Evaluate");
        sb.AppendLine(synthesisResponse);
        sb.AppendLine();
        sb.AppendLine("### Scoring Rubric");
        sb.AppendLine("Rate the output on a 0.0–1.0 scale across these dimensions:");
        sb.AppendLine("1. **Completeness** (0-1): Does it fully address the goal?");
        sb.AppendLine("2. **Correctness** (0-1): Is it accurate and well-reasoned?");
        sb.AppendLine("3. **Coherence** (0-1): Is the synthesis well-organized?");
        sb.AppendLine("4. **Actionability** (0-1): Can the user act on this output?");
        sb.AppendLine("5. **Tool Verification** (0-1): Are results backed by actual tool execution? Score 0 if workers appear to have fabricated results without running tools. Score 1 only if outputs clearly reference real tool outputs (file contents, build logs, test results, command output).");
        sb.AppendLine();
        if (state.EvaluationHistory.Count > 0)
        {
            var last = state.EvaluationHistory.Last();
            sb.AppendLine($"Previous iteration scored: {last.Score:F1} — {last.Rationale}");
            sb.AppendLine("Indicate whether quality improved, degraded, or stayed flat.");
            sb.AppendLine();
        }
        sb.AppendLine("### Response Format");
        sb.AppendLine("SCORE: <average of 5 dimensions as decimal, e.g. 0.75>");
        sb.AppendLine("RATIONALE: <2-3 sentences explaining the score and gaps>");
        sb.AppendLine();
        sb.AppendLine("If score >= 0.9, include `[[GROUP_REFLECT_COMPLETE]]`.");
        sb.AppendLine("If score < 0.9, include `[[NEEDS_ITERATION]]` and list specific improvements needed.");
        return sb.ToString();
    }

    /// <summary>Parse a score and rationale from evaluator response.</summary>
    internal static (double Score, string Rationale) ParseEvaluationScore(string evalResponse)
    {
        double score = 0.5; // default if parsing fails
        string rationale = evalResponse;

        // Try to find "SCORE: X.X" pattern
        var scoreMatch = System.Text.RegularExpressions.Regex.Match(evalResponse, @"SCORE:\s*(-?[\d.]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (scoreMatch.Success && double.TryParse(scoreMatch.Groups[1].Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            score = Math.Clamp(parsed, 0.0, 1.0);
        }

        // Extract rationale
        var rationaleMatch = System.Text.RegularExpressions.Regex.Match(evalResponse, @"RATIONALE:\s*(.+?)(?:\[\[|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        if (rationaleMatch.Success)
            rationale = rationaleMatch.Groups[1].Value.Trim();

        return (score, rationale);
    }

    /// <summary>
    /// Auto-adjust agent configuration based on iteration feedback.
    /// Called after each reflect iteration to detect quality issues and apply fixes.
    /// Surfaces adjustments both as orchestrator system messages and as PendingAdjustments on state (for UI banners).
    /// </summary>
    private void AutoAdjustFromFeedback(string groupId, SessionGroup group, List<WorkerResult> results, ReflectionCycle state)
    {
        var failedWorkers = results.Where(r => !r.Success).ToList();
        var adjustments = new List<string>();

        // Auto-reassign tasks from failed workers to successful ones
        if (failedWorkers.Count > 0 && results.Any(r => r.Success))
        {
            foreach (var failed in failedWorkers)
            {
                adjustments.Add($"🔄 Worker '{failed.WorkerName}' failed ({failed.Error}). Its tasks will be reassigned in the next iteration.");
            }
        }

        // Detect workers with suspiciously short responses (quality issue)
        foreach (var result in results.Where(r => r.Success))
        {
            if (result.Response != null && result.Response.Length < 100 && state.CurrentIteration > 1)
            {
                var caps = Models.ModelCapabilities.GetCapabilities(GetEffectiveModel(result.WorkerName));
                if (caps.HasFlag(Models.ModelCapability.CostEfficient) && !caps.HasFlag(Models.ModelCapability.ReasoningExpert))
                {
                    adjustments.Add($"📈 Worker '{result.WorkerName}' produced a brief response. Consider upgrading from a cost-efficient model to improve quality.");
                }
            }
        }

        // Detect quality degradation from evaluation history
        if (state.EvaluationHistory.Count >= 2)
        {
            var lastTwo = state.EvaluationHistory.TakeLast(2).ToList();
            if (lastTwo[1].Score < lastTwo[0].Score - 0.15)
                adjustments.Add("📉 Quality degraded significantly vs. previous iteration. Review worker models or task clarity.");
        }

        // Detect quality degradation: if consecutive stalls detected, suggest model changes
        if (state.ConsecutiveStalls == 1)
        {
            adjustments.Add("⚠️ Output repetition detected. The orchestrator may benefit from a different model or clearer instructions.");
        }

        // Surface adjustments for UI banners (non-blocking)
        state.PendingAdjustments.Clear();
        state.PendingAdjustments.AddRange(adjustments);

        // Surface adjustments as system messages to orchestrator
        if (adjustments.Count > 0)
        {
            var orchestratorName = GetOrchestratorSession(groupId);
            if (orchestratorName != null)
            {
                AddOrchestratorSystemMessage(orchestratorName,
                    $"🔧 Auto-analysis (iteration {state.CurrentIteration}):\n" + string.Join("\n", adjustments));
            }
        }
    }

    /// <summary>
    /// Get diagnostics for a multi-agent group (model conflicts, capability gaps).
    /// </summary>
    public List<Models.GroupModelAnalyzer.GroupDiagnostic> GetGroupDiagnostics(string groupId)
    {
        var group = Organization.Groups.FirstOrDefault(g => g.Id == groupId);
        if (group == null || !group.IsMultiAgent) return new();

        var members = GetMultiAgentGroupMembers(groupId)
            .Select(name =>
            {
                var meta = Organization.Sessions.FirstOrDefault(m => m.SessionName == name);
                return (name, GetEffectiveModel(name), meta?.Role ?? MultiAgentRole.Worker);
            })
            .ToList();

        return Models.GroupModelAnalyzer.Analyze(group, members);
    }

    /// <summary>
    /// Save the current multi-agent group configuration as a reusable user preset.
    /// </summary>
    public Models.GroupPreset? SaveGroupAsPreset(string groupId, string name, string description, string emoji)
    {
        var group = Organization.Groups.FirstOrDefault(g => g.Id == groupId && g.IsMultiAgent);
        if (group == null) return null;

        var members = GetMultiAgentGroupMembers(groupId)
            .Select(n => Organization.Sessions.FirstOrDefault(m => m.SessionName == n))
            .Where(m => m != null)
            .ToList();

        // Resolve worktree path for .squad/ write-back
        string? worktreeRoot = null;
        if (!string.IsNullOrEmpty(group.WorktreeId))
        {
            var wt = _repoManager.Worktrees.FirstOrDefault(w => w.Id == group.WorktreeId);
            if (wt != null) worktreeRoot = wt.Path;
        }

        return Models.UserPresets.SaveGroupAsPreset(PolyPilotBaseDir, name, description, emoji,
            group, members!, GetEffectiveModel, worktreeRoot);
    }

    /// <summary>
    /// Creates a worktree via the bridge (remote mode) or locally, returning a uniform (Id, Path) result.
    /// </summary>
    private async Task<(string Id, string Path)> CreateWorktreeLocalOrRemoteAsync(string repoId, string branchName, CancellationToken ct)
    {
        if (IsRemoteMode)
        {
            var result = await _bridgeClient.CreateWorktreeAsync(repoId, branchName, null, ct);
            return (result.WorktreeId, result.Path);
        }
        else
        {
            var wt = await _repoManager.CreateWorktreeAsync(repoId, branchName, skipFetch: true, ct: ct);
            return (wt.Id, wt.Path);
        }
    }

    #endregion
}
