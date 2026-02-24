using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using PolyPilot.Models;

namespace PolyPilot.Services;

public enum OrchestratorPhase { Planning, Dispatching, WaitingForWorkers, Synthesizing, Complete }

public partial class CopilotService
{
    public event Action<string, OrchestratorPhase, string?>? OnOrchestratorPhaseChanged; // groupId, phase, detail

    // Per-session semaphores to prevent concurrent model switches during rapid dispatch
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _modelSwitchLocks = new();

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
        Organization.Groups.Add(group);

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
            Organization.Sessions.Add(meta);
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
            Organization.Groups.Insert(0, new SessionGroup
            {
                Id = SessionGroup.DefaultId,
                Name = SessionGroup.DefaultName,
                SortOrder = 0
            });
        }

        // NOTE: Do NOT call ReconcileOrganization() here — _sessions is empty at load time,
        // so reconciliation would prune all session metadata. Reconcile is called explicitly
        // after RestorePreviousSessionsAsync populates _sessions (line 403 and 533).
    }

    public void SaveOrganization()
    {
        InvalidateOrganizedSessionsCache();
        // Snapshot JSON on caller's thread to avoid concurrent mutation during serialization
        string json;
        try { json = JsonSerializer.Serialize(Organization, new JsonSerializerOptions { WriteIndented = true }); }
        catch { return; }
        _saveOrgDebounce?.Dispose();
        _saveOrgDebounce = new Timer(_ => WriteOrgFile(json), null, 2000, Timeout.Infinite);
    }

    private void FlushSaveOrganization()
    {
        _saveOrgDebounce?.Dispose();
        _saveOrgDebounce = null;
        SaveOrganizationCore();
    }

    private void SaveOrganizationCore()
    {
        try
        {
            var json = JsonSerializer.Serialize(Organization, new JsonSerializerOptions { WriteIndented = true });
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
    internal void ReconcileOrganization()
    {
        var activeNames = _sessions.Where(kv => !kv.Value.Info.IsHidden).Select(kv => kv.Key).ToHashSet();

        // Safety: skip reconciliation during startup when sessions haven't been restored yet.
        // LoadOrganization loads the org before RestorePreviousSessionsAsync populates _sessions,
        // so reconciling then would prune all sessions. Use IsRestoring as the precise scope guard.
        if (IsRestoring)
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
        _lastReconcileSessionHash = currentHash;
        bool changed = false;

        // Build lookup of multi-agent group IDs so we can protect their sessions
        var multiAgentGroupIds = Organization.Groups.Where(g => g.IsMultiAgent).Select(g => g.Id).ToHashSet();

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
                Organization.Sessions.Add(meta);
                changed = true;
            }

            // Don't auto-reassign sessions that belong to a multi-agent group
            if (multiAgentGroupIds.Contains(meta.GroupId))
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
                        meta.GroupId = repoGroup.Id;
                        changed = true;
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

        // Ensure every tracked repo has a sidebar group (even if no sessions exist yet)
        foreach (var repo in _repoManager.Repositories)
        {
            if (!Organization.Groups.Any(g => g.RepoId == repo.Id && !g.IsMultiAgent))
            {
                GetOrCreateRepoGroup(repo.Id, repo.Name);
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
        var toRemove = Organization.Sessions.Where(m => !knownNames.Contains(m.SessionName) && !protectedNames.Contains(m.SessionName)).ToList();
        if (toRemove.Count > 0)
        {
            Debug($"ReconcileOrganization: pruning {toRemove.Count} sessions: {string.Join(", ", toRemove.Select(m => m.SessionName))}");
            changed = true;
        }
        Organization.Sessions.RemoveAll(m => !knownNames.Contains(m.SessionName) && !protectedNames.Contains(m.SessionName));

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
            Organization.Sessions.Add(meta);
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
        Organization.Groups.Add(group);
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

        var group = Organization.Groups.FirstOrDefault(g => g.Id == groupId);
        var isMultiAgent = group?.IsMultiAgent ?? false;

        if (isMultiAgent)
        {
            // Multi-agent sessions are meaningless without their group — close them
            var sessionNames = Organization.Sessions
                .Where(m => m.GroupId == groupId)
                .Select(m => m.SessionName)
                .ToList();
            // Remove org metadata first so UI updates immediately
            Organization.Sessions.RemoveAll(m => sessionNames.Contains(m.SessionName));
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
            // Fire-and-forget: close sessions asynchronously
            _ = Task.Run(async () =>
            {
                foreach (var name in sessionNames)
                    await CloseSessionAsync(name);
            });
        }
        else
        {
            // Non-multi-agent: move sessions to default group
            foreach (var meta in Organization.Sessions.Where(m => m.GroupId == groupId))
            {
                meta.GroupId = SessionGroup.DefaultId;
            }
        }

        Organization.Groups.RemoveAll(g => g.Id == groupId);
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
    /// Get or create a SessionGroup that auto-tracks a repository.
    /// </summary>
    public SessionGroup GetOrCreateRepoGroup(string repoId, string repoName)
    {
        // Skip multi-agent groups — they have a RepoId for worktree context but are
        // not the "repo group" that regular sessions should auto-join.
        var existing = Organization.Groups.FirstOrDefault(g => g.RepoId == repoId && !g.IsMultiAgent);
        if (existing != null) return existing;

        var group = new SessionGroup
        {
            Id = Guid.NewGuid().ToString(),
            Name = repoName,
            RepoId = repoId,
            SortOrder = Organization.Groups.Max(g => g.SortOrder) + 1
        };
        Organization.Groups.Add(group);
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
        Organization.Groups.Add(group);

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

    /// <summary>
    /// Send a prompt to all sessions in a multi-agent group based on its orchestration mode.
    /// </summary>
    public async Task SendToMultiAgentGroupAsync(string groupId, string prompt, CancellationToken cancellationToken = default)
    {
        var group = Organization.Groups.FirstOrDefault(g => g.Id == groupId && g.IsMultiAgent);
        if (group == null) return;

        var members = GetMultiAgentGroupMembers(groupId);
        if (members.Count == 0) return;

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
                await SendPromptAsync(name, prefixedPrompt, cancellationToken: cancellationToken);
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
                await SendPromptAsync(name, prefixedPrompt, cancellationToken: cancellationToken);
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
        var planResponse = await SendPromptAndWaitAsync(orchestratorName, planningPrompt, cancellationToken);

        // Phase 2: Parse task assignments from orchestrator response
        var rawAssignments = ParseTaskAssignments(planResponse, workerNames);
        Debug($"[DISPATCH] '{orchestratorName}' plan parsed: {rawAssignments.Count} raw assignments from {workerNames.Count} workers. Response length={planResponse.Length}");
        // Deduplicate: merge multiple tasks for the same worker into one prompt
        var assignments = rawAssignments
            .GroupBy(a => a.WorkerName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TaskAssignment(g.Key, string.Join("\n\n---\n\n", g.Select(a => a.Task))))
            .ToList();
        if (assignments.Count == 0)
        {
            // Orchestrator handled it without delegation — add a system note
            Debug($"[DISPATCH] No assignments parsed from response (length={planResponse.Length}). Workers: {string.Join(", ", workerNames)}");
            AddOrchestratorSystemMessage(orchestratorName, "ℹ️ Orchestrator handled the request directly (no tasks delegated to workers).");
            InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(groupId, OrchestratorPhase.Complete, null));
            return;
        }

        // Phase 3: Dispatch tasks to workers in parallel
        Debug($"[DISPATCH] Dispatching {assignments.Count} tasks: {string.Join(", ", assignments.Select(a => a.WorkerName))}");
        InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(groupId, OrchestratorPhase.Dispatching,
            $"Sending tasks to {assignments.Count} worker(s)"));

        InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(groupId, OrchestratorPhase.WaitingForWorkers, null));

        var workerTasks = assignments.Select(a =>
            ExecuteWorkerAsync(a.WorkerName, a.Task, prompt, cancellationToken));
        var results = await Task.WhenAll(workerTasks);

        // Phase 4: Synthesize — send worker results back to orchestrator
        InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(groupId, OrchestratorPhase.Synthesizing, null));

        var synthesisPrompt = BuildSynthesisPrompt(prompt, results.ToList());
        await SendPromptAsync(orchestratorName, synthesisPrompt, cancellationToken: cancellationToken);

        InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(groupId, OrchestratorPhase.Complete, null));
    }

    private string BuildOrchestratorPlanningPrompt(string userPrompt, List<string> workerNames, string? additionalInstructions, string? routingContext = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"You are the orchestrator of a multi-agent group. You have {workerNames.Count} worker agent(s) available:");
        foreach (var w in workerNames)
        {
            var meta = GetSessionMeta(w);
            var model = GetEffectiveModel(w);
            if (!string.IsNullOrEmpty(meta?.SystemPrompt))
                sb.AppendLine($"  - '{w}' (model: {model}) — {meta.SystemPrompt}");
            else
                sb.AppendLine($"  - '{w}' (model: {model})");
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
        sb.AppendLine("Analyze the request and assign specific tasks to your workers. Use this exact format for each assignment:");
        sb.AppendLine();
        sb.AppendLine("@worker:worker-name");
        sb.AppendLine("Detailed task description for this worker.");
        sb.AppendLine("@end");
        sb.AppendLine();
        sb.AppendLine("You may include your analysis and reasoning as normal text. Only the @worker/@end blocks will be dispatched.");
        sb.AppendLine("If you can handle the request entirely yourself, just respond normally without any @worker blocks.");
        return sb.ToString();
    }

    internal record TaskAssignment(string WorkerName, string Task);

    internal static List<TaskAssignment> ParseTaskAssignments(string orchestratorResponse, List<string> availableWorkers)
    {
        var assignments = new List<TaskAssignment>();
        var pattern = @"@worker:([^\n]+?)\s*\n([\s\S]*?)(?:@end|(?=@worker:)|$)";

        foreach (Match match in Regex.Matches(orchestratorResponse, pattern, RegexOptions.IgnoreCase))
        {
            var workerName = match.Groups[1].Value.Trim();
            var task = match.Groups[2].Value.Trim();
            if (string.IsNullOrEmpty(task)) continue;

            // Resolve worker name: exact match, then fuzzy
            var resolved = availableWorkers.FirstOrDefault(w =>
                w.Equals(workerName, StringComparison.OrdinalIgnoreCase));
            if (resolved == null)
            {
                resolved = availableWorkers.FirstOrDefault(w =>
                    w.Contains(workerName, StringComparison.OrdinalIgnoreCase) ||
                    workerName.Contains(w, StringComparison.OrdinalIgnoreCase));
            }
            if (resolved != null)
                assignments.Add(new TaskAssignment(resolved, task));
        }
        return assignments;
    }

    private record WorkerResult(string WorkerName, string? Response, bool Success, string? Error, TimeSpan Duration);

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

        var workerPrompt = $"{identity}\n\nYour response will be collected and synthesized with other workers' responses.\n\n{sharedPrefix}## Original User Request (context)\n{originalPrompt}\n\n## Your Assigned Task\n{task}";

        try
        {
            Debug($"[DISPATCH] Worker '{workerName}' starting (prompt len={workerPrompt.Length})");
            var response = await SendPromptAndWaitAsync(workerName, workerPrompt, cancellationToken);
            Debug($"[DISPATCH] Worker '{workerName}' completed (response len={response.Length}, elapsed={sw.Elapsed.TotalSeconds:F1}s)");
            return new WorkerResult(workerName, response, true, null, sw.Elapsed);
        }
        catch (Exception ex)
        {
            Debug($"[DISPATCH] Worker '{workerName}' FAILED: {ex.GetType().Name}: {ex.Message} (elapsed={sw.Elapsed.TotalSeconds:F1}s)");
            return new WorkerResult(workerName, null, false, ex.Message, sw.Elapsed);
        }
    }

    private async Task<string> SendPromptAndWaitAsync(string sessionName, string prompt, CancellationToken cancellationToken)
    {
        // Use SendPromptAsync directly — it already awaits ResponseCompletion internally.
        // Do NOT capture state and await its TCS separately: reconnection replaces the state
        // object, orphaning the old TCS and causing a 10-minute hang.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(10));
        return await SendPromptAsync(sessionName, prompt, cancellationToken: cts.Token);
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
                sb.AppendLine(result.Response);
            else
                sb.AppendLine($"*Error: {result.Error}*");
            sb.AppendLine();
        }
        sb.AppendLine("## Instructions");
        sb.AppendLine($"Original request: {originalPrompt}");
        sb.AppendLine();
        sb.AppendLine("Synthesize these worker responses into a coherent final answer. Note any tasks that failed. Provide a unified response addressing the original request.");
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
    public async Task<SessionGroup?> CreateGroupFromPresetAsync(Models.GroupPreset preset, string? workingDirectory = null, string? worktreeId = null, string? repoId = null, string? nameOverride = null, CancellationToken ct = default)
    {
        var teamName = nameOverride ?? preset.Name;
        var group = CreateMultiAgentGroup(teamName, preset.Mode, worktreeId: worktreeId, repoId: repoId);
        if (group == null) return null;

        // Store Squad context (routing, decisions) on the group for use during orchestration
        group.SharedContext = preset.SharedContext;
        group.RoutingContext = preset.RoutingContext;

        // Create orchestrator session (with uniqueness check matching CreateMultiAgentGroupAsync)
        var orchName = $"{teamName}-orchestrator";
        { int suffix = 1;
          while (_sessions.ContainsKey(orchName) || Organization.Sessions.Any(s => s.SessionName == orchName))
              orchName = $"{teamName}-orchestrator-{suffix++}";
        }
        try
        {
            await CreateSessionAsync(orchName, preset.OrchestratorModel, workingDirectory, ct);
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
        if (worktreeId != null && orchMeta != null)
            orchMeta.WorktreeId = worktreeId;

        // Create worker sessions
        for (int i = 0; i < preset.WorkerModels.Length; i++)
        {
            var workerName = $"{teamName}-worker-{i + 1}";
            { int suffix = 1;
              while (_sessions.ContainsKey(workerName) || Organization.Sessions.Any(s => s.SessionName == workerName))
                  workerName = $"{teamName}-worker-{i + 1}-{suffix++}";
            }
            var workerModel = preset.WorkerModels[i];
            try
            {
                await CreateSessionAsync(workerName, workerModel, workingDirectory, ct);
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
                if (worktreeId != null) meta.WorktreeId = worktreeId;
                if (systemPrompt != null) meta.SystemPrompt = systemPrompt;
            }
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

        var workerNames = members.Where(m => m != orchestratorName).ToList();

        while (reflectState.IsActive && !reflectState.IsPaused
               && reflectState.CurrentIteration < reflectState.MaxIterations)
        {
            ct.ThrowIfCancellationRequested();
            reflectState.CurrentIteration++;

            try
            {
            Debug($"Reflection loop: starting iteration {reflectState.CurrentIteration}/{reflectState.MaxIterations} " +
                  $"(IsActive={reflectState.IsActive}, IsPaused={reflectState.IsPaused})");
            // Phase 1: Plan (first iteration) or Re-plan (subsequent)
            var iterDetail = $"Iteration {reflectState.CurrentIteration}/{reflectState.MaxIterations}";
            InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(groupId, OrchestratorPhase.Planning, iterDetail));

            string planPrompt;
            if (reflectState.CurrentIteration == 1)
            {
                planPrompt = BuildOrchestratorPlanningPrompt(prompt, workerNames, group.OrchestratorPrompt, group.RoutingContext);
            }
            else
            {
                planPrompt = BuildReplanPrompt(reflectState.LastEvaluation ?? "Continue iterating.", workerNames, prompt);
            }

            var planResponse = await SendPromptAndWaitAsync(orchestratorName, planPrompt, ct);
            var rawAssignments = ParseTaskAssignments(planResponse, workerNames);
            // Deduplicate: merge multiple tasks for the same worker into one prompt
            var assignments = rawAssignments
                .GroupBy(a => a.WorkerName, StringComparer.OrdinalIgnoreCase)
                .Select(g => new TaskAssignment(g.Key, string.Join("\n\n---\n\n", g.Select(a => a.Task))))
                .ToList();

            if (assignments.Count == 0)
            {
                if (reflectState.CurrentIteration == 1)
                {
                    // First iteration with no assignments = orchestrator failed to delegate.
                    // Treat as error, not goal met, so we can retry.
                    AddOrchestratorSystemMessage(orchestratorName,
                        "⚠️ No @worker assignments parsed from orchestrator response. Retrying...");
                    reflectState.ConsecutiveErrors++;
                    if (reflectState.ConsecutiveErrors >= 3)
                    {
                        reflectState.IsStalled = true;
                        reflectState.IsCancelled = true;
                        break;
                    }
                    continue;
                }
                // Later iterations: orchestrator decided no more work needed
                reflectState.GoalMet = true;
                AddOrchestratorSystemMessage(orchestratorName, $"✅ Orchestrator completed without delegation (iteration {reflectState.CurrentIteration}).");
                break;
            }

            // Phase 2-3: Dispatch + Collect
            InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(groupId, OrchestratorPhase.Dispatching,
                $"Sending tasks to {assignments.Count} worker(s) — {iterDetail}"));

            InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(groupId, OrchestratorPhase.WaitingForWorkers, iterDetail));

            var workerTasks = assignments.Select(a => ExecuteWorkerAsync(a.WorkerName, a.Task, prompt, ct));
            var results = await Task.WhenAll(workerTasks);

            // Phase 4: Synthesize + Evaluate
            InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(groupId, OrchestratorPhase.Synthesizing, iterDetail));

            var synthEvalPrompt = BuildSynthesisWithEvalPrompt(prompt, results.ToList(), reflectState);

            // Use dedicated evaluator session if configured, otherwise orchestrator self-evaluates
            string evaluatorName = reflectState.EvaluatorSessionName ?? orchestratorName;
            string synthesisResponse;
            if (reflectState.EvaluatorSessionName != null && reflectState.EvaluatorSessionName != orchestratorName)
            {
                // Send results to orchestrator for synthesis
                var synthOnlyPrompt = BuildSynthesisOnlyPrompt(prompt, results.ToList());
                synthesisResponse = await SendPromptAndWaitAsync(orchestratorName, synthOnlyPrompt, ct);

                // Send to evaluator for independent scoring
                var evalOnlyPrompt = BuildEvaluatorPrompt(prompt, synthesisResponse, reflectState);
                var evalResponse = await SendPromptAndWaitAsync(evaluatorName, evalOnlyPrompt, ct);

                // Parse score from evaluator
                var (score, rationale) = ParseEvaluationScore(evalResponse);
                var evaluatorModel = GetEffectiveModel(evaluatorName);
                var trend = reflectState.RecordEvaluation(reflectState.CurrentIteration, score, rationale, evaluatorModel);

                // Check if evaluator says complete
                if (evalResponse.Contains("[[GROUP_REFLECT_COMPLETE]]", StringComparison.OrdinalIgnoreCase) || score >= 0.9)
                {
                    reflectState.GoalMet = true;
                    reflectState.IsActive = false;
                    AddOrchestratorSystemMessage(orchestratorName, $"✅ {reflectState.BuildCompletionSummary()} (score: {score:F1})");
                    break;
                }

                reflectState.LastEvaluation = rationale;
                if (trend == Models.QualityTrend.Degrading)
                    reflectState.PendingAdjustments.Add("📉 Quality degrading — consider changing worker models or refining the goal.");
            }
            else
            {
                synthesisResponse = await SendPromptAndWaitAsync(orchestratorName, synthEvalPrompt, ct);

                // Check completion sentinel
                if (synthesisResponse.Contains("[[GROUP_REFLECT_COMPLETE]]", StringComparison.OrdinalIgnoreCase))
                {
                    reflectState.GoalMet = true;
                    reflectState.IsActive = false;
                    AddOrchestratorSystemMessage(orchestratorName, $"✅ {reflectState.BuildCompletionSummary()}");
                    break;
                }

                // Extract evaluation for next iteration
                reflectState.LastEvaluation = ExtractIterationEvaluation(synthesisResponse);

                // Record a self-eval score (estimated from sentinel presence)
                var selfScore = synthesisResponse.Contains("[[NEEDS_ITERATION]]", StringComparison.OrdinalIgnoreCase) ? 0.4 : 0.7;
                reflectState.RecordEvaluation(reflectState.CurrentIteration, selfScore,
                    reflectState.LastEvaluation ?? "", GetEffectiveModel(orchestratorName));
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

        reflectState.IsActive = false;
        reflectState.CompletedAt = DateTime.Now;
        SaveOrganization();
        InvokeOnUI(() =>
        {
            OnOrchestratorPhaseChanged?.Invoke(groupId, OrchestratorPhase.Complete, reflectState.BuildCompletionSummary());
            OnStateChanged?.Invoke();
        });
    }

    private string BuildSynthesisWithEvalPrompt(string originalPrompt, List<WorkerResult> results, ReflectionCycle state)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(BuildSynthesisPrompt(originalPrompt, results));
        sb.AppendLine();
        sb.AppendLine($"## Evaluation Check (Iteration {state.CurrentIteration}/{state.MaxIterations})");
        sb.AppendLine($"**Goal:** {state.Goal}");
        sb.AppendLine();
        sb.AppendLine("### Quality Assessment");
        sb.AppendLine("Before deciding, evaluate each worker's output:");
        sb.AppendLine("1. **Completeness** — Did they fully address their assigned task?");
        sb.AppendLine("2. **Correctness** — Is the output accurate and well-reasoned?");
        sb.AppendLine("3. **Relevance** — Does it contribute meaningfully toward the goal?");
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

    private string BuildReplanPrompt(string lastEvaluation, List<string> workerNames, string originalPrompt)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Previous Iteration Evaluation");
        sb.AppendLine(lastEvaluation);
        sb.AppendLine();
        sb.AppendLine("## Original Request (context)");
        sb.AppendLine(originalPrompt);
        sb.AppendLine();
        sb.AppendLine($"Available workers ({workerNames.Count}):");
        foreach (var w in workerNames)
            sb.AppendLine($"  - '{w}' (model: {GetEffectiveModel(w)})");
        sb.AppendLine();
        sb.AppendLine("Assign refined tasks using `@worker:name` / `@end` blocks to address the gaps identified above.");
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
        sb.AppendLine();
        if (state.EvaluationHistory.Count > 0)
        {
            var last = state.EvaluationHistory.Last();
            sb.AppendLine($"Previous iteration scored: {last.Score:F1} — {last.Rationale}");
            sb.AppendLine("Indicate whether quality improved, degraded, or stayed flat.");
            sb.AppendLine();
        }
        sb.AppendLine("### Response Format");
        sb.AppendLine("SCORE: <average of 4 dimensions as decimal, e.g. 0.75>");
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

    #endregion
}
