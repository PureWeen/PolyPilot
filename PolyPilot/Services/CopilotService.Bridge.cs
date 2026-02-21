using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;

namespace PolyPilot.Services;

public partial class CopilotService
{
    private bool _bridgeEventsWired;

    /// <summary>
    /// Initialize in Remote mode: connect WsBridgeClient for state-sync with server.
    /// </summary>
    private async Task InitializeRemoteAsync(ConnectionSettings settings, CancellationToken ct)
    {
        var wsUrl = settings.RemoteUrl!.TrimEnd('/');
        if (wsUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            wsUrl = "wss://" + wsUrl[8..];
        else if (wsUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            wsUrl = "ws://" + wsUrl[7..];
        else if (wsUrl.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)
              || wsUrl.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            { /* already a WebSocket URL */ }
        else
            wsUrl = "wss://" + wsUrl;

        Debug($"Remote mode: connecting to {wsUrl}");

        // Wire WsBridgeClient events only once (survives reconnects)
        if (!_bridgeEventsWired)
        {
            _bridgeEventsWired = true;

        // Wire WsBridgeClient events to our events
        _bridgeClient.OnStateChanged += () =>
        {
            SyncRemoteSessions();
            InvokeOnUI(() => OnStateChanged?.Invoke());
        };
        _bridgeClient.OnContentReceived += (s, c) =>
        {
            // Track that this session is actively streaming
            _remoteStreamingSessions[s] = 0;

            // Update local session history from remote events
            var session = GetRemoteSession(s);
            if (session != null)
            {
                var existing = session.History.LastOrDefault(m => m.IsAssistant && !m.IsComplete);
                if (existing != null)
                    existing.Content += c;
                else
                    session.History.Add(new ChatMessage("assistant", c, DateTime.Now, ChatMessageType.Assistant) { IsComplete = false });
            }
            InvokeOnUI(() => OnContentReceived?.Invoke(s, c));
        };
        _bridgeClient.OnToolStarted += (s, tool, id, input) =>
        {
            var session = GetRemoteSession(s);
            session?.History.Add(ChatMessage.ToolCallMessage(tool, id, input));
            InvokeOnUI(() => OnToolStarted?.Invoke(s, tool, id, input));
        };
        _bridgeClient.OnToolCompleted += (s, id, result, success) =>
        {
            var session = GetRemoteSession(s);
            var toolMsg = session?.History.LastOrDefault(m => m.ToolCallId == id);
            if (toolMsg != null)
            {
                toolMsg.IsComplete = true;
                toolMsg.IsSuccess = success;
                toolMsg.Content = result;
            }
            InvokeOnUI(() => OnToolCompleted?.Invoke(s, id, result, success));
        };
        _bridgeClient.OnImageReceived += (s, callId, dataUri, caption) =>
        {
            var session = GetRemoteSession(s);
            var toolMsg = session?.History.LastOrDefault(m => m.ToolCallId == callId);
            if (toolMsg != null)
            {
                // Convert tool call message into an Image message
                toolMsg.MessageType = ChatMessageType.Image;
                toolMsg.ImageDataUri = dataUri;
                toolMsg.Caption = caption;
            }
            InvokeOnUI(() => OnStateChanged?.Invoke());
        };
        _bridgeClient.OnReasoningReceived += (s, rid, c) =>
        {
            var emittedReasoningId = rid;
            var session = GetRemoteSession(s);
            if (session != null && !string.IsNullOrEmpty(c))
            {
                var normalizedReasoningId = ResolveReasoningId(session, rid);
                emittedReasoningId = normalizedReasoningId;
                var reasoningMsg = FindReasoningMessage(session, normalizedReasoningId);
                if (reasoningMsg == null)
                {
                    reasoningMsg = ChatMessage.ReasoningMessage(normalizedReasoningId);
                    session.History.Add(reasoningMsg);
                    session.MessageCount = session.History.Count;
                }
                reasoningMsg.ReasoningId = normalizedReasoningId;
                reasoningMsg.IsComplete = false;
                reasoningMsg.IsCollapsed = false;
                reasoningMsg.Timestamp = DateTime.Now;
                MergeReasoningContent(reasoningMsg, c, isDelta: true);
                session.LastUpdatedAt = DateTime.Now;
            }
            InvokeOnUI(() => OnReasoningReceived?.Invoke(s, emittedReasoningId, c));
        };
        _bridgeClient.OnReasoningComplete += (s, rid) =>
        {
            var session = GetRemoteSession(s);
            if (session != null)
            {
                var targets = session.History
                    .Where(m => m.MessageType == ChatMessageType.Reasoning &&
                        !m.IsComplete &&
                        (string.IsNullOrEmpty(rid) || string.Equals(m.ReasoningId, rid, StringComparison.Ordinal)))
                    .ToList();
                foreach (var msg in targets)
                {
                    msg.IsComplete = true;
                    msg.IsCollapsed = true;
                    msg.Timestamp = DateTime.Now;
                }
                if (targets.Count > 0)
                    session.LastUpdatedAt = DateTime.Now;
            }
            InvokeOnUI(() => OnReasoningComplete?.Invoke(s, rid));
        };
        _bridgeClient.OnIntentChanged += (s, i) => InvokeOnUI(() => OnIntentChanged?.Invoke(s, i));
        _bridgeClient.OnUsageInfoChanged += (s, u) => InvokeOnUI(() => OnUsageInfoChanged?.Invoke(s, u));
        _bridgeClient.OnTurnStart += (s) =>
        {
            var session = GetRemoteSession(s);
            if (session != null) { session.IsProcessing = true; }
            InvokeOnUI(() => OnTurnStart?.Invoke(s));
        };
        _bridgeClient.OnTurnEnd += (s) =>
        {
            _remoteStreamingSessions.TryRemove(s, out _);
            InvokeOnUI(() =>
            {
                var session = GetRemoteSession(s);
                if (session != null)
                {
                    Debug($"[BRIDGE-COMPLETE] '{session.Name}' OnTurnEnd cleared IsProcessing");
                    session.IsProcessing = false;
                    session.IsResumed = false;
                    session.ProcessingStartedAt = null;
                    session.ToolCallCount = 0;
                    session.ProcessingPhase = 0;
                    // Mark last assistant message as complete
                    var lastAssistant = session.History.LastOrDefault(m => m.IsAssistant && !m.IsComplete);
                    if (lastAssistant != null) { lastAssistant.IsComplete = true; lastAssistant.Model = session.Model; }
                }
                OnTurnEnd?.Invoke(s);
            });
        };
        _bridgeClient.OnSessionComplete += (s, sum) => InvokeOnUI(() => OnSessionComplete?.Invoke(s, sum));
        _bridgeClient.OnError += (s, e) => InvokeOnUI(() => OnError?.Invoke(s, e));
        _bridgeClient.OnOrganizationStateReceived += (org) =>
        {
            Organization = org;
            InvokeOnUI(() => OnStateChanged?.Invoke());
        };
        _bridgeClient.OnAttentionNeeded += (payload) =>
        {
            // Fire and forget - don't await to avoid blocking the event handler
            _ = Task.Run(async () =>
            {
                try
                {
                    // Check if notifications are enabled in settings (load fresh each time)
                    var currentSettings = ConnectionSettings.Load();
                    if (!currentSettings.EnableSessionNotifications)
                        return;
                    
                    var notificationService = _serviceProvider?.GetService<INotificationManagerService>();
                    if (notificationService != null)
                    {
                        var (title, body) = NotificationMessageBuilder.BuildMessage(payload);
                        await notificationService.SendNotificationAsync(title, body, payload.SessionId);
                        Debug($"Sent notification for session '{payload.SessionName}': {payload.Reason}");
                    }
                }
                catch (Exception ex)
                {
                    Debug($"Failed to send notification: {ex.Message}");
                    Console.WriteLine($"[Notification] Error: {ex}");
                }
            });
        };

        } // end if (!_bridgeEventsWired)

        await _bridgeClient.ConnectAsync(wsUrl, settings.RemoteToken, ct);

        // Wait for initial session list from server (arrives immediately after connect)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!_bridgeClient.HasReceivedSessionsList && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            await Task.Delay(50, ct);

        // Allow time for SessionHistory messages to follow the SessionsList
        if (_bridgeClient.HasReceivedSessionsList && _bridgeClient.Sessions.Any())
        {
            var histDeadline = DateTime.UtcNow.AddSeconds(3);
            while (_bridgeClient.SessionHistories.Count < _bridgeClient.Sessions.Count(s => s.MessageCount > 0)
                   && DateTime.UtcNow < histDeadline && !ct.IsCancellationRequested)
                await Task.Delay(50, ct);
        }

        // Set IsRemoteMode before SyncRemoteSessions to prevent ReconcileOrganization from running
        IsRemoteMode = true;

        // Sync all received history into local sessions before returning
        SyncRemoteSessions();

        IsInitialized = true;
        NeedsConfiguration = false;
        Debug($"Connected to remote server via WebSocket bridge ({_bridgeClient.Sessions.Count} sessions, {_bridgeClient.SessionHistories.Count} histories)");
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Sync remote session list from WsBridgeClient into our local _sessions dictionary.
    /// </summary>
    private void SyncRemoteSessions()
    {
        var remoteSessions = _bridgeClient.Sessions;
        var remoteActive = _bridgeClient.ActiveSessionName;

        // Sync GitHub user info from remote
        if (!string.IsNullOrEmpty(_bridgeClient.GitHubAvatarUrl))
            GitHubAvatarUrl = _bridgeClient.GitHubAvatarUrl;
        if (!string.IsNullOrEmpty(_bridgeClient.GitHubLogin))
            GitHubLogin = _bridgeClient.GitHubLogin;

        Debug($"SyncRemoteSessions: {remoteSessions.Count} remote sessions, active={remoteActive}");

        // Add/update sessions from remote
        foreach (var rs in remoteSessions)
        {
            if (!_sessions.ContainsKey(rs.Name) && !_pendingRemoteRenames.ContainsKey(rs.Name))
            {
                Debug($"SyncRemoteSessions: Adding session '{rs.Name}'");
                var info = new AgentSessionInfo
                {
                    Name = rs.Name,
                    Model = rs.Model,
                    CreatedAt = rs.CreatedAt,
                    SessionId = rs.SessionId,
                    WorkingDirectory = rs.WorkingDirectory,
                    GitBranch = GetGitBranch(rs.WorkingDirectory),
                };
                _sessions[rs.Name] = new SessionState
                {
                    Session = null!,  // No local CopilotSession in remote mode
                    Info = info
                };
            }
            // Update processing state and model from server
            if (_sessions.TryGetValue(rs.Name, out var state))
            {
                state.Info.IsProcessing = rs.IsProcessing;
                state.Info.ProcessingStartedAt = rs.ProcessingStartedAt;
                state.Info.ToolCallCount = rs.ToolCallCount;
                state.Info.ProcessingPhase = rs.ProcessingPhase;
                state.Info.MessageCount = rs.MessageCount;
                if (!string.IsNullOrEmpty(rs.Model))
                    state.Info.Model = rs.Model;
            }
        }

        // Remove sessions that no longer exist on server (but keep pending optimistic adds)
        var remoteNames = remoteSessions.Select(s => s.Name).ToHashSet();
        foreach (var name in _sessions.Keys.ToList())
        {
            if (!remoteNames.Contains(name) && !_pendingRemoteSessions.ContainsKey(name))
                _sessions.TryRemove(name, out _);
        }

        // Clear pending flag for sessions confirmed by server
        foreach (var rs in remoteSessions)
            _pendingRemoteSessions.TryRemove(rs.Name, out _);
        // Clear pending renames when old name disappears from server (rename confirmed).
        // If rename fails, old name stays on server and the 30s TTL cleanup handles it.
        foreach (var oldName in _pendingRemoteRenames.Keys.ToList())
        {
            if (!remoteNames.Contains(oldName))
                _pendingRemoteRenames.TryRemove(oldName, out _);
        }

        // Sync history from WsBridgeClient cache
        // Don't overwrite if local history has messages not yet reflected by server
        // Skip sessions that are actively streaming — content_delta handlers update history
        // incrementally; replacing it with the (stale) SessionHistories cache would cause duplicates.
        var sessionsNeedingHistory = new List<string>();
        foreach (var (name, messages) in _bridgeClient.SessionHistories)
        {
            if (_sessions.TryGetValue(name, out var s))
            {
                // Skip history sync for sessions currently receiving streaming content —
                // the incremental content_delta/tool events are more up-to-date than the cached history
                if (_remoteStreamingSessions.ContainsKey(name))
                    continue;

                if (messages.Count >= s.Info.History.Count)
                {
                    Debug($"SyncRemoteSessions: Syncing {messages.Count} messages for '{name}'");
                    s.Info.History.Clear();
                    s.Info.History.AddRange(messages);
                }
            }
        }

        // Request history for sessions that have messages but no local history yet
        foreach (var rs in remoteSessions)
        {
            if (rs.MessageCount > 0 && _sessions.TryGetValue(rs.Name, out var s) && s.Info.History.Count == 0
                && !_bridgeClient.SessionHistories.ContainsKey(rs.Name)
                && !_requestedHistorySessions.ContainsKey(rs.Name))
            {
                sessionsNeedingHistory.Add(rs.Name);
                _requestedHistorySessions[rs.Name] = 0;
            }
        }

        if (sessionsNeedingHistory.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                foreach (var name in sessionsNeedingHistory)
                {
                    try { await _bridgeClient.RequestHistoryAsync(name); }
                    catch { }
                }
            });
        }

        // Sync active session — only on first load, not on every update
        // (user may have selected a different session locally on mobile)
        if (_activeSessionName == null && remoteActive != null && _sessions.ContainsKey(remoteActive))
            _activeSessionName = remoteActive;

        Debug($"SyncRemoteSessions: Done. _sessions has {_sessions.Count} entries, active={_activeSessionName}");
        // In Remote mode, organization state comes from the server via OnOrganizationStateReceived — skip local reconcile
        if (!IsRemoteMode)
            ReconcileOrganization();
    }

    private AgentSessionInfo? GetRemoteSession(string name) =>
        _sessions.TryGetValue(name, out var state) ? state.Info : null;

    // --- Remote repo operations ---

    public async Task<(string RepoId, string RepoName)?> AddRepoRemoteAsync(string url, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        if (!IsRemoteMode)
        {
            var repo = await _repoManager.AddRepositoryAsync(url, onProgress, ct);
            GetOrCreateRepoGroup(repo.Id, repo.Name);
            return (repo.Id, repo.Name);
        }

        var result = await _bridgeClient.AddRepoAsync(url, onProgress, ct);
        // Server already created the group — request updated organization
        try { await _bridgeClient.RequestSessionsAsync(ct); } catch { }
        return (result.RepoId, result.RepoName);
    }

    public async Task RemoveRepoRemoteAsync(string repoId, string groupId, bool deleteFromDisk, CancellationToken ct = default)
    {
        if (!IsRemoteMode)
        {
            await _repoManager.RemoveRepositoryAsync(repoId, deleteFromDisk, ct);
            DeleteGroup(groupId);
            return;
        }

        await _bridgeClient.RemoveRepoAsync(repoId, deleteFromDisk, groupId, ct);
        try { await _bridgeClient.RequestSessionsAsync(ct); } catch { }
    }

    public bool RepoExistsById(string repoId)
    {
        return _repoManager.Repositories.Any(r => r.Id == repoId);
    }
}
