using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;

namespace PolyPilot.Services;

public partial class CopilotService
{
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
        else
            wsUrl = "wss://" + wsUrl;

        Debug($"Remote mode: connecting to {wsUrl}");

        // Wire WsBridgeClient events to our events
        _bridgeClient.OnStateChanged += () =>
        {
            SyncRemoteSessions();
            InvokeOnUI(() => OnStateChanged?.Invoke());
        };
        _bridgeClient.OnContentReceived += (s, c) =>
        {
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
        _bridgeClient.OnToolStarted += (s, tool, id) =>
        {
            var session = GetRemoteSession(s);
            session?.History.Add(ChatMessage.ToolCallMessage(tool, id));
            InvokeOnUI(() => OnToolStarted?.Invoke(s, tool, id, null));
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
            if (session != null) session.IsProcessing = true;
            InvokeOnUI(() => OnTurnStart?.Invoke(s));
        };
        _bridgeClient.OnTurnEnd += (s) =>
        {
            var session = GetRemoteSession(s);
            if (session != null)
            {
                session.IsProcessing = false;
                // Mark last assistant message as complete
                var lastAssistant = session.History.LastOrDefault(m => m.IsAssistant && !m.IsComplete);
                if (lastAssistant != null) lastAssistant.IsComplete = true;
            }
            InvokeOnUI(() => OnTurnEnd?.Invoke(s));
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

        await _bridgeClient.ConnectAsync(wsUrl, settings.RemoteToken, ct);

        IsInitialized = true;
        IsRemoteMode = true;
        NeedsConfiguration = false;
        Debug("Connected to remote server via WebSocket bridge");
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
            if (!_sessions.ContainsKey(rs.Name))
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
            // Update processing state from server
            if (_sessions.TryGetValue(rs.Name, out var state))
            {
                state.Info.IsProcessing = rs.IsProcessing;
                state.Info.MessageCount = rs.MessageCount;
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

        // Sync history from WsBridgeClient cache
        // Don't overwrite if local history has messages not yet reflected by server
        // Skip sessions that are actively streaming — content_delta handlers update history
        // incrementally; replacing it with the (stale) SessionHistories cache would cause duplicates.
        var sessionsNeedingHistory = new List<string>();
        foreach (var (name, messages) in _bridgeClient.SessionHistories)
        {
            if (_sessions.TryGetValue(name, out var s))
            {
                // Skip history sync for sessions currently processing — the incremental
                // content_delta/tool events are more up-to-date than the cached history
                if (s.Info.IsProcessing)
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
                && !_bridgeClient.SessionHistories.ContainsKey(rs.Name))
            {
                sessionsNeedingHistory.Add(rs.Name);
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
}
