using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;

namespace PolyPilot.Services;

public partial class CopilotService
{
    private bool _bridgeEventsWired;

    /// <summary>
    /// Max messages to request after a turn ends. Keeps WebSocket payloads bounded
    /// for long conversations. Users can still load full history via "Load rest of conversation".
    /// </summary>
    private const int TurnEndHistoryLimit = 200;

    /// <summary>
    /// Convert an HTTP(S) URL to its WebSocket equivalent. Returns null for null/empty input.
    /// </summary>
    private static string? ToWebSocketUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var trimmed = url.TrimEnd('/');
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "wss://" + trimmed[8..];
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return "ws://" + trimmed[7..];
        if (trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        return "wss://" + trimmed;
    }

    /// <summary>
    /// Initialize in Remote mode: connect WsBridgeClient for state-sync with server.
    /// </summary>
    private async Task InitializeRemoteAsync(ConnectionSettings settings, CancellationToken ct)
    {
        var tunnelWsUrl = ToWebSocketUrl(settings.RemoteUrl);
        var lanWsUrl = ToWebSocketUrl(settings.LanUrl);

        Debug($"Remote mode: tunnel={tunnelWsUrl ?? "(none)"}, lan={lanWsUrl ?? "(none)"}");

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
        _bridgeClient.OnReposListReceived += payload =>
        {
            // Must run on UI thread — RepoManager lists are iterated by Blazor components
            InvokeOnUI(() =>
            {
                // Reconcile local RepoManager with server state — add new, remove stale
                var serverRepoIds = new HashSet<string>(payload.Repos.Select(r => r.Id));
                var serverWorktreeIds = new HashSet<string>(payload.Worktrees.Select(w => w.Id));

                // Remove worktrees/repos that no longer exist on the server
                foreach (var wt in _repoManager.Worktrees.ToList())
                {
                    if (!serverWorktreeIds.Contains(wt.Id))
                        _repoManager.RemoveRemoteWorktree(wt.Id);
                }
                foreach (var r in _repoManager.Repositories.ToList())
                {
                    if (!serverRepoIds.Contains(r.Id))
                        _repoManager.RemoveRemoteRepo(r.Id);
                }

                // Add new entries from server
                foreach (var r in payload.Repos)
                {
                    if (!_repoManager.Repositories.Any(existing => existing.Id == r.Id))
                        _repoManager.AddRemoteRepo(new RepositoryInfo { Id = r.Id, Name = r.Name, Url = r.Url });
                }
                foreach (var w in payload.Worktrees)
                {
                    _repoManager.AddRemoteWorktree(new WorktreeInfo { Id = w.Id, RepoId = w.RepoId, Branch = w.Branch, Path = w.Path, PrNumber = w.PrNumber, Remote = w.Remote });
                }
            });
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
            // Don't remove from _remoteStreamingSessions yet — SyncRemoteSessions could
            // overwrite our incrementally-built history with a stale SessionHistories cache.
            // Instead, request fresh history from the server first, then clear the guard.
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
            // Request fresh history (capped to avoid massive payloads for long conversations),
            // then clear the streaming guard so SyncRemoteSessions
            // uses the up-to-date history instead of a stale cache.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _bridgeClient.RequestHistoryAsync(s, limit: TurnEndHistoryLimit);
                    // Small delay to let the history response arrive before unguarding
                    await Task.Delay(500);
                }
                catch { }
                finally
                {
                    _remoteStreamingSessions.TryRemove(s, out _);
                }
            });
        };
        _bridgeClient.OnSessionComplete += (s, sum) => InvokeOnUI(() => OnSessionComplete?.Invoke(s, sum));
        _bridgeClient.OnError += (s, e) => InvokeOnUI(() =>
        {
            // Ignore errors for sessions already deleted locally (e.g., SDK error during dispose)
            if (!string.IsNullOrEmpty(s) && !_sessions.ContainsKey(s)) return;
            OnError?.Invoke(s, e);
        });
        _bridgeClient.OnOrganizationStateReceived += (org) =>
        {
            InvokeOnUI(() =>
            {
                Organization = org;
                OnStateChanged?.Invoke();
            });
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

        // Use smart connect when both URLs are available, single connect otherwise
        if (!string.IsNullOrEmpty(tunnelWsUrl) && !string.IsNullOrEmpty(lanWsUrl))
        {
            await _bridgeClient.ConnectSmartAsync(tunnelWsUrl, settings.RemoteToken, lanWsUrl, settings.LanToken, ct);
        }
        else
        {
            var wsUrl = tunnelWsUrl ?? lanWsUrl ?? throw new InvalidOperationException("No remote URL configured");
            var token = !string.IsNullOrEmpty(tunnelWsUrl) ? settings.RemoteToken : settings.LanToken;
            await _bridgeClient.ConnectAsync(wsUrl, token, ct);
        }

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

        // Request repos/worktrees so the worktree picker works on mobile
        _ = Task.Run(async () => { try { await _bridgeClient.RequestReposAsync(ct); } catch { } });

        // Start monitoring network changes for smart URL switching
        StartConnectivityMonitoring(settings);
    }

    private Timer? _connectivityDebounce;
    private ConnectionSettings? _remoteSettings;

    private void StartConnectivityMonitoring(ConnectionSettings settings)
    {
        _remoteSettings = settings;
        // Only monitor if both URLs are available (otherwise nothing to switch)
        if (string.IsNullOrWhiteSpace(settings.RemoteUrl) || string.IsNullOrWhiteSpace(settings.LanUrl))
            return;

#if IOS || ANDROID
        try
        {
            Microsoft.Maui.Networking.Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
        }
        catch (Exception ex)
        {
            Debug($"Connectivity monitoring unavailable: {ex.Message}");
        }
#endif
    }

    private void StopConnectivityMonitoring()
    {
#if IOS || ANDROID
        try { Microsoft.Maui.Networking.Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged; } catch { }
#endif
        Interlocked.Exchange(ref _connectivityDebounce, null)?.Dispose();
    }

#if IOS || ANDROID
    private void OnConnectivityChanged(object? sender, Microsoft.Maui.Networking.ConnectivityChangedEventArgs e)
    {
        // Debounce: iOS fires multiple events per transition. Use Interlocked to avoid race.
        var newTimer = new Timer(_ =>
        {
            Debug($"[SmartURL] Network changed: {string.Join(", ", e.ConnectionProfiles)}");
            // The reconnect loop re-resolves URLs automatically.
            // If we're currently connected via LAN and lost WiFi, force a reconnect attempt.
            if (!_bridgeClient.IsConnected) return;

            var onWiFi = e.ConnectionProfiles.Contains(Microsoft.Maui.Networking.ConnectionProfile.WiFi);
            var lanWs = ToWebSocketUrl(_remoteSettings?.LanUrl);
            var usingLan = _bridgeClient.ActiveUrl != null
                && lanWs != null
                && _bridgeClient.ActiveUrl == lanWs;

            if (usingLan && !onWiFi)
            {
                Debug("[SmartURL] Lost WiFi while on LAN — forcing reconnect to re-resolve via tunnel");
                _bridgeClient.Stop();
            }
        }, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        var oldTimer = Interlocked.Exchange(ref _connectivityDebounce, newTimer);
        oldTimer?.Dispose();
    }
#endif

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
            // Don't re-add sessions that were just closed locally — the server broadcast
            // may still include them because the close hasn't propagated yet
            if (_recentlyClosedRemoteSessions.ContainsKey(rs.Name))
                continue;

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
                // Don't overwrite IsProcessing for sessions that are actively streaming —
                // event-driven state (TurnStart/TurnEnd) is more accurate than the periodic
                // sessions list, which may be stale by the time it arrives.
                if (!_remoteStreamingSessions.ContainsKey(rs.Name))
                {
                    state.Info.IsProcessing = rs.IsProcessing;
                    state.Info.ProcessingStartedAt = rs.ProcessingStartedAt;
                    state.Info.ToolCallCount = rs.ToolCallCount;
                    state.Info.ProcessingPhase = rs.ProcessingPhase;
                }
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
        // Clear recently-closed guard when the server confirms the session is gone
        foreach (var closedName in _recentlyClosedRemoteSessions.Keys.ToList())
        {
            if (!remoteNames.Contains(closedName))
                _recentlyClosedRemoteSessions.TryRemove(closedName, out _);
        }
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
                    try { await _bridgeClient.RequestHistoryAsync(name, limit: 10); }
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

    /// <summary>
    /// Whether the server has more history for this session than what's been loaded.
    /// </summary>
    public bool HasMoreRemoteHistory(string sessionName) =>
        IsRemoteMode && _bridgeClient.SessionHistoryHasMore.TryGetValue(sessionName, out var hasMore) && hasMore;

    /// <summary>
    /// Request the full (unlimited) history for a session from the remote server.
    /// </summary>
    public async Task LoadFullRemoteHistoryAsync(string sessionName)
    {
        if (!IsRemoteMode) return;
        await _bridgeClient.RequestHistoryAsync(sessionName, limit: null);
    }

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

    public async Task<WorktreeCreatedPayload> CreateWorktreeViaBridgeAsync(string repoId, string? branchName, int? prNumber, CancellationToken ct = default)
    {
        if (!IsRemoteMode)
            throw new InvalidOperationException("CreateWorktreeViaBridgeAsync is only for remote mode");
        return await _bridgeClient.CreateWorktreeAsync(repoId, branchName, prNumber, ct);
    }

    public async Task RemoveWorktreeViaBridgeAsync(string worktreeId, bool deleteBranch = false, CancellationToken ct = default)
    {
        if (!IsRemoteMode)
            throw new InvalidOperationException("RemoveWorktreeViaBridgeAsync is only for remote mode");
        await _bridgeClient.RemoveWorktreeAsync(worktreeId, deleteBranch, ct);
    }
}
