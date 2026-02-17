using System.Text.Json;
using System.Text.RegularExpressions;
using PolyPilot.Models;

namespace PolyPilot.Services;

public enum OrchestratorPhase { Planning, Dispatching, WaitingForWorkers, Synthesizing, Complete }

public partial class CopilotService
{
    public event Action<string, OrchestratorPhase, string?>? OnOrchestratorPhaseChanged; // groupId, phase, detail

    #region Session Organization (groups, pinning, sorting)

    public void LoadOrganization()
    {
        try
        {
            if (File.Exists(OrganizationFile))
            {
                var json = File.ReadAllText(OrganizationFile);
                Organization = JsonSerializer.Deserialize<OrganizationState>(json) ?? new OrganizationState();
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

        ReconcileOrganization();
    }

    public void SaveOrganization()
    {
        try
        {
            // Ensure directory exists (required on iOS where it may not exist by default)
            Directory.CreateDirectory(PolyPilotBaseDir);
            var json = JsonSerializer.Serialize(Organization, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(OrganizationFile, json);
        }
        catch (Exception ex)
        {
            Debug($"Failed to save organization: {ex.Message}");
        }
    }

    /// <summary>
    /// Ensure every active session has a SessionMeta entry and clean up orphans.
    /// Only prunes metadata for sessions whose on-disk session directory no longer exists.
    /// </summary>
    private void ReconcileOrganization()
    {
        var activeNames = _sessions.Keys.ToHashSet();
        bool changed = false;

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

            // Ensure sessions with worktrees are in the correct repo group
            if (meta.WorktreeId != null && meta.GroupId == SessionGroup.DefaultId)
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
                meta.GroupId = SessionGroup.DefaultId;
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

        // Remove metadata only for sessions that are truly gone (not in any known set)
        Organization.Sessions.RemoveAll(m => !knownNames.Contains(m.SessionName));

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
            OnStateChanged?.Invoke();
        }
    }

    public void DeleteGroup(string groupId)
    {
        if (groupId == SessionGroup.DefaultId) return;

        // Move all sessions in this group to default
        foreach (var meta in Organization.Sessions.Where(m => m.GroupId == groupId))
        {
            meta.GroupId = SessionGroup.DefaultId;
        }

        Organization.Groups.RemoveAll(g => g.Id == groupId);
        SaveOrganization();
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
    /// </summary>
    public IEnumerable<(SessionGroup Group, List<AgentSessionInfo> Sessions)> GetOrganizedSessions()
    {
        var metas = Organization.Sessions.ToDictionary(m => m.SessionName);
        var allSessions = GetAllSessions().ToList();

        foreach (var group in Organization.Groups.OrderBy(g => g.SortOrder))
        {
            var groupSessions = allSessions
                .Where(s => metas.TryGetValue(s.Name, out var m) && m.GroupId == group.Id)
                .ToList();

            // Pinned first, then apply sort mode within each partition
            var sorted = groupSessions
                .OrderByDescending(s => metas.TryGetValue(s.Name, out var m) && m.IsPinned)
                .ThenBy(s => ApplySort(s, metas))
                .ToList();

            yield return (group, sorted);
        }
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
    /// Get or create a SessionGroup that auto-tracks a repository.
    /// </summary>
    public SessionGroup GetOrCreateRepoGroup(string repoId, string repoName)
    {
        var existing = Organization.Groups.FirstOrDefault(g => g.RepoId == repoId);
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
    public SessionGroup CreateMultiAgentGroup(string name, MultiAgentMode mode = MultiAgentMode.Broadcast, string? orchestratorPrompt = null, List<string>? sessionNames = null)
    {
        var group = new SessionGroup
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            IsMultiAgent = true,
            OrchestratorMode = mode,
            OrchestratorPrompt = orchestratorPrompt,
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
                }
            }
        }

        SaveOrganization();
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
        }
    }

    /// <summary>
    /// Build a multi-agent context prefix for a session in a group.
    /// </summary>
    private string BuildMultiAgentPrefix(string sessionName, SessionGroup group, List<string> allMembers)
    {
        var meta = Organization.Sessions.FirstOrDefault(m => m.SessionName == sessionName);
        var role = meta?.Role ?? MultiAgentRole.Worker;
        var roleName = role == MultiAgentRole.Orchestrator ? "orchestrator" : "worker";
        var others = allMembers.Where(m => m != sessionName).ToList();
        var othersList = others.Count > 0 ? string.Join(", ", others) : "none";
        return $"[Multi-agent context: You are '{sessionName}' ({roleName}) in group '{group.Name}'. Other members: {othersList}.]\n\n";
    }

    private async Task SendBroadcastAsync(SessionGroup group, List<string> sessionNames, string prompt, CancellationToken cancellationToken)
    {
        var tasks = sessionNames.Select(name =>
        {
            var session = GetSession(name);
            if (session == null) return Task.CompletedTask;

            var prefixedPrompt = BuildMultiAgentPrefix(name, group, sessionNames) + prompt;

            if (session.IsProcessing)
            {
                EnqueueMessage(name, prefixedPrompt);
                return Task.CompletedTask;
            }

            return SendPromptAsync(name, prefixedPrompt, cancellationToken: cancellationToken)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        Debug($"Broadcast send failed for '{name}': {t.Exception?.InnerException?.Message}");
                }, TaskScheduler.Default);
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

        var planningPrompt = BuildOrchestratorPlanningPrompt(prompt, workerNames, group?.OrchestratorPrompt);
        var planResponse = await SendPromptAndWaitAsync(orchestratorName, planningPrompt, cancellationToken);

        // Phase 2: Parse task assignments from orchestrator response
        var assignments = ParseTaskAssignments(planResponse, workerNames);
        if (assignments.Count == 0)
        {
            // Orchestrator handled it without delegation — add a system note
            AddOrchestratorSystemMessage(orchestratorName, "ℹ️ Orchestrator handled the request directly (no tasks delegated to workers).");
            InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(groupId, OrchestratorPhase.Complete, null));
            return;
        }

        // Phase 3: Dispatch tasks to workers in parallel
        InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(groupId, OrchestratorPhase.Dispatching,
            $"Sending tasks to {assignments.Count} worker(s)"));

        var workerTasks = assignments.Select(a =>
            ExecuteWorkerAsync(a.WorkerName, a.Task, prompt, cancellationToken));
        var results = await Task.WhenAll(workerTasks);

        InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(groupId, OrchestratorPhase.WaitingForWorkers, null));

        // Phase 4: Synthesize — send worker results back to orchestrator
        InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(groupId, OrchestratorPhase.Synthesizing, null));

        var synthesisPrompt = BuildSynthesisPrompt(prompt, results.ToList());
        await SendPromptAsync(orchestratorName, synthesisPrompt, cancellationToken: cancellationToken);

        InvokeOnUI(() => OnOrchestratorPhaseChanged?.Invoke(groupId, OrchestratorPhase.Complete, null));
    }

    private string BuildOrchestratorPlanningPrompt(string userPrompt, List<string> workerNames, string? additionalInstructions)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"You are the orchestrator of a multi-agent group. You have {workerNames.Count} worker agent(s) available:");
        foreach (var w in workerNames)
            sb.AppendLine($"  - '{w}'");
        sb.AppendLine();
        sb.AppendLine("## User Request");
        sb.AppendLine(userPrompt);
        if (!string.IsNullOrEmpty(additionalInstructions))
        {
            sb.AppendLine();
            sb.AppendLine("## Additional Orchestration Instructions");
            sb.AppendLine(additionalInstructions);
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
        var pattern = @"@worker:(\S+)\s*([\s\S]*?)(?:@end|(?=@worker:)|$)";

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
        var workerPrompt = $"You are a worker agent. Complete the following task thoroughly. Your response will be collected and synthesized with other workers' responses.\n\n## Original User Request (context)\n{originalPrompt}\n\n## Your Assigned Task\n{task}";

        try
        {
            var response = await SendPromptAndWaitAsync(workerName, workerPrompt, cancellationToken);
            return new WorkerResult(workerName, response, true, null, sw.Elapsed);
        }
        catch (Exception ex)
        {
            return new WorkerResult(workerName, null, false, ex.Message, sw.Elapsed);
        }
    }

    private async Task<string> SendPromptAndWaitAsync(string sessionName, string prompt, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionName, out var state))
            throw new InvalidOperationException($"Session '{sessionName}' not found.");

        await SendPromptAsync(sessionName, prompt, cancellationToken: cancellationToken);

        // Wait for the response to complete via the existing ResponseCompletion TCS
        if (state.ResponseCompletion != null)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(10));
            return await state.ResponseCompletion.Task.WaitAsync(cts.Token);
        }
        return "";
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
}
