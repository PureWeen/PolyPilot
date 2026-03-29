using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;

namespace PolyPilot.Services;

public partial class CopilotService
{
    private bool _bridgeEventsWired;

    /// <summary>
    /// Max messages to send over bridge (initial connect, session switch, turn end).
    /// Keeps WebSocket payloads bounded for long conversations.
    /// </summary>
    internal const int HistoryLimitForBridge = 200;

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
            NotifyStateChangedCoalesced();
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
            // Ensure streaming guard is present (don't overwrite generation counter)
            _remoteStreamingSessions.TryAdd(s, 0);

            // Update local session history from remote events.
            // Only append to the LAST incomplete assistant message (active streaming).
            // If previous message is complete, content_delta signals the start of a new turn.
            var session = GetRemoteSession(s);
            if (session != null)
            {
                lock (session.HistoryLock)
                {
                    var existing = session.History.LastOrDefault(m => m.IsAssistant && !m.IsComplete);
                    if (existing != null)
                    {
                        existing.Content += c;
                    }
                    else
                    {
                        // No incomplete message — this is the start of a new text response
                        session.History.Add(new ChatMessage("assistant", c, DateTime.Now, ChatMessageType.Assistant) { IsComplete = false });
                    }
                }
            }
            InvokeOnUI(() => OnContentReceived?.Invoke(s, c));
        };
        _bridgeClient.OnToolStarted += (s, tool, id, input) =>
        {
            _remoteStreamingSessions.TryAdd(s, 0); // ensure guard present, don't reset generation
            var session = GetRemoteSession(s);
            if (session != null)
            {
                lock (session.HistoryLock)
                    session.History.Add(ChatMessage.ToolCallMessage(tool, id, input));
            }
            InvokeOnUI(() => OnToolStarted?.Invoke(s, tool, id, input));
        };
        _bridgeClient.OnToolCompleted += (s, id, result, success) =>
        {
            _remoteStreamingSessions.TryAdd(s, 0); // ensure guard present, don't reset generation
            var session = GetRemoteSession(s);
            if (session != null)
            {
                lock (session.HistoryLock)
                {
                    var toolMsg = session.History.LastOrDefault(m => m.ToolCallId == id);
                    if (toolMsg != null)
                    {
                        toolMsg.IsComplete = true;
                        toolMsg.IsSuccess = success;
                        toolMsg.Content = result;
                    }
                }
            }
            InvokeOnUI(() => OnToolCompleted?.Invoke(s, id, result, success));
        };
        _bridgeClient.OnImageReceived += (s, callId, dataUri, caption) =>
        {
            var session = GetRemoteSession(s);
            if (session != null)
            {
                lock (session.HistoryLock)
                {
                    var toolMsg = session.History.LastOrDefault(m => m.ToolCallId == callId);
                    if (toolMsg != null)
                    {
                        toolMsg.MessageType = ChatMessageType.Image;
                        toolMsg.ImageDataUri = dataUri;
                        toolMsg.Caption = caption;
                    }
                }
            }
            InvokeOnUI(() => OnStateChanged?.Invoke());
        };
        _bridgeClient.OnReasoningReceived += (s, rid, c) =>
        {
            var emittedReasoningId = rid;
            var session = GetRemoteSession(s);
            if (session != null && !string.IsNullOrEmpty(c))
            {
                lock (session.HistoryLock)
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
            }
            InvokeOnUI(() => OnReasoningReceived?.Invoke(s, emittedReasoningId, c));
        };
        _bridgeClient.OnReasoningComplete += (s, rid) =>
        {
            var session = GetRemoteSession(s);
            if (session != null)
            {
                lock (session.HistoryLock)
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
            }
            InvokeOnUI(() => OnReasoningComplete?.Invoke(s, rid));
        };
        _bridgeClient.OnIntentChanged += (s, i) => InvokeOnUI(() => OnIntentChanged?.Invoke(s, i));
        _bridgeClient.OnUsageInfoChanged += (s, u) => InvokeOnUI(() => OnUsageInfoChanged?.Invoke(s, u));
        _bridgeClient.OnTurnStart += (s) =>
        {
            // Increment generation counter — each sub-turn gets a new generation so
            // a delayed guard removal from a previous sub-turn won't kill this one.
            _remoteStreamingSessions.AddOrUpdate(s, 1, (_, prev) => prev + 1);
            // Clear the TurnEnd guard — a new turn is starting, so sessions_list should be
            // allowed to sync IsProcessing=true again.
            _recentTurnEndSessions.TryRemove(s, out _);
            // Set IsProcessing on the UI thread to avoid race with TurnEnd:
            // When TurnEnd and TurnStart arrive back-to-back, both InvokeOnUI callbacks
            // are queued. TurnEnd fires first (sets false), then TurnStart fires (sets true).
            // Previously, TurnStart set true on the background thread which was overwritten
            // by TurnEnd's UI callback.
            InvokeOnUI(() =>
            {
                var session = GetRemoteSession(s);
                if (session != null) { session.IsProcessing = true; }
                OnTurnStart?.Invoke(s);
            });
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
                    // Guard against stale sessions_list re-setting IsProcessing=true
                    _recentTurnEndSessions[s] = DateTime.UtcNow;
                    // Mark last assistant message as complete
                    var lastAssistant = session.History.LastOrDefault(m => m.IsAssistant && !m.IsComplete);
                    if (lastAssistant != null) { lastAssistant.IsComplete = true; lastAssistant.Model = session.Model; }
                }
                OnTurnEnd?.Invoke(s);
            });
            // Request fresh history (capped to avoid massive payloads for long conversations),
            // then clear the streaming guard and force a sync so SyncRemoteSessions
            // uses the up-to-date history instead of a stale cache.
            // Capture generation so the delayed removal doesn't kill a newer sub-turn's guard.
            _remoteStreamingSessions.TryGetValue(s, out var turnEndGen);
            _ = Task.Run(async () =>
            {
                try
                {
                    await _bridgeClient.RequestHistoryAsync(s, limit: HistoryLimitForBridge);
                    // Wait for the history response to arrive via the WebSocket receive loop.
                    // 2s is generous enough for LAN/tunnel round-trips.
                    await Task.Delay(2000);
                }
                catch { }
                finally
                {
                    // Only remove guard if no new sub-turn has started since this TurnEnd.
                    // Atomic removal: only succeeds if both key and value match, preventing
                    // a TOCTOU race where TurnStart increments the generation between
                    // TryGetValue and TryRemove.
                    _remoteStreamingSessions.TryRemove(new KeyValuePair<string, int>(s, turnEndGen));
                    // Force sync now that the guard is down — ensures fresh history
                    // from the server replaces incrementally-built content.
                    SyncRemoteSessions();
                    NotifyStateChangedCoalesced();
                }
            });
        };
        _bridgeClient.OnSessionComplete += (s, sum) => InvokeOnUI(() =>
        {
            // Belt-and-suspenders: also clear IsProcessing on session_complete in case
            // the turn_end message was lost or arrived out of order.
            var session = GetRemoteSession(s);
            if (session != null && session.IsProcessing)
            {
                Debug($"[BRIDGE-SESSION-COMPLETE] '{session.Name}' clearing stale IsProcessing");
                session.IsProcessing = false;
                session.IsResumed = false;
                session.ProcessingStartedAt = null;
                session.ToolCallCount = 0;
                session.ProcessingPhase = 0;
                _recentTurnEndSessions[s] = DateTime.UtcNow;
            }
            OnSessionComplete?.Invoke(s, sum);
        });
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
                    if (currentSettings.MuteWorkerNotifications && IsWorkerInMultiAgentGroup(payload.SessionName))
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

        // Set IsRemoteMode before SyncRemoteSessions to prevent ReconcileOrganization from running.
        // Wrap in try/catch to ensure consistent state: if SyncRemoteSessions fails,
        // reset IsRemoteMode so the service doesn't get stuck in a half-initialized limbo.
        IsRemoteMode = true;
        try
        {
            // Sync all received history into local sessions before returning
            SyncRemoteSessions();

            IsInitialized = true;
            NeedsConfiguration = false;
            Debug($"Connected to remote server via WebSocket bridge ({_bridgeClient.Sessions.Count} sessions, {_bridgeClient.SessionHistories.Count} histories)");
            OnStateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Debug($"Failed to complete remote initialization: {ex.Message}");
            IsRemoteMode = false;
            IsInitialized = false;
            NeedsConfiguration = true;
            _bridgeClient.Stop();
            OnStateChanged?.Invoke();
            throw;
        }

        // Request repos/worktrees so the worktree picker works on mobile
        _ = Task.Run(async () => { try { await _bridgeClient.RequestReposAsync(ct); } catch { } });

        // Start monitoring network changes for smart URL switching
        StartConnectivityMonitoring(settings);
    }

    private Timer? _connectivityDebounce;
    private ConnectionSettings? _remoteSettings;

    private void StartConnectivityMonitoring(ConnectionSettings settings)
    {
        StopConnectivityMonitoring(); // Unsubscribe any prior handler to prevent double-registration
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
                Debug("[SmartURL] Lost WiFi while on LAN — aborting connection to re-resolve via tunnel");
                _bridgeClient.AbortForReconnect();
            }
            else if (!usingLan && onWiFi && !string.IsNullOrEmpty(_remoteSettings?.LanUrl))
            {
                Debug("[SmartURL] Gained WiFi while on tunnel — aborting connection to re-resolve via LAN");
                _bridgeClient.AbortForReconnect();
            }
        }, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        var oldTimer = Interlocked.Exchange(ref _connectivityDebounce, newTimer);
        oldTimer?.Dispose();
    }
#endif

    /// <summary>
    /// Sync remote session list from WsBridgeClient into our local _sessions dictionary.
    /// </summary>
    internal void SyncRemoteSessions()
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
                    // Don't let a stale sessions_list snapshot re-set IsProcessing=true after
                    // TurnEnd already cleared it. The debounced sessions_list may have been
                    // captured before CompleteResponse ran on the server.
                    bool turnEndGuardActive = rs.IsProcessing &&
                        _recentTurnEndSessions.TryGetValue(rs.Name, out var turnEndTime) &&
                        (DateTime.UtcNow - turnEndTime).TotalSeconds < 5;

                    if (!turnEndGuardActive)
                    {
                        if (state.Info.IsProcessing != rs.IsProcessing)
                            Debug($"SyncRemoteSessions: '{rs.Name}' IsProcessing {state.Info.IsProcessing} -> {rs.IsProcessing}");
                        state.Info.IsProcessing = rs.IsProcessing;
                        state.Info.ProcessingStartedAt = rs.ProcessingStartedAt;
                        state.Info.ToolCallCount = rs.ToolCallCount;
                        state.Info.ProcessingPhase = rs.ProcessingPhase;
                    }
                    else
                    {
                        Debug($"SyncRemoteSessions: '{rs.Name}' TurnEnd guard blocked IsProcessing=true");
                    }
                    state.Info.MessageCount = rs.MessageCount;
                }
                else
                {
                    Debug($"SyncRemoteSessions: '{rs.Name}' skipped — streaming guard active");
                }
                if (!string.IsNullOrEmpty(rs.Model))
                    state.Info.Model = rs.Model;
            }
        }

        // Remove sessions that no longer exist on server (but keep pending optimistic adds)
        var remoteNames = remoteSessions.Select(s => s.Name).ToHashSet();
        foreach (var name in _sessions.Keys.ToList())
        {
            if (!remoteNames.Contains(name) && !_pendingRemoteSessions.ContainsKey(name) &&
                _sessions.TryRemove(name, out var removedState))
            {
                DisposePrematureIdleSignal(removedState);
            }
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
        // Exception: allow sync when local history is very small (initial load) — the guard is up
        // because TurnStart fired but the full history hasn't been loaded yet.
        var sessionsNeedingHistory = new List<string>();
        foreach (var (name, messages) in _bridgeClient.SessionHistories)
        {
            if (_sessions.TryGetValue(name, out var s))
            {
                // Skip history sync for sessions currently receiving streaming content —
                // the incremental content_delta/tool events are more up-to-date than the cached history.
                // BUT: Always sync if server has newer messages (messages.Count > local.Count), even with guard active.
                // This ensures text responses that arrive between tool calls are not lost.
                if (_remoteStreamingSessions.ContainsKey(name) && messages.Count <= s.Info.History.Count)
                    continue;

                if (messages.Count >= s.Info.History.Count)
                {
                    Debug($"SyncRemoteSessions: Syncing {messages.Count} messages for '{name}'");
                    lock (s.Info.HistoryLock)
                    {
                        s.Info.History.Clear();
                        s.Info.History.AddRange(messages);
                    }
                    s.Info.MessageCount = s.Info.History.Count;
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

    /// <summary>
    /// Force a full sync of sessions and history from the remote server.
    /// Returns diagnostic info about what changed (for the sync button on mobile).
    /// </summary>
    public async Task<SyncResult> ForceRefreshRemoteAsync(string? activeSessionName = null)
    {
        if (!IsRemoteMode || !_bridgeClient.IsConnected)
            return new SyncResult { Success = false, Message = "Not connected" };

        var result = new SyncResult();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Snapshot pre-sync state
            var preSyncSessionCount = _sessions.Count;
            var preSyncMessageCount = 0;
            AgentSessionInfo? activeInfo = null;
            if (activeSessionName != null && _sessions.TryGetValue(activeSessionName, out var activeState))
            {
                activeInfo = activeState.Info;
                lock (activeInfo.HistoryLock)
                    preSyncMessageCount = activeInfo.History.Count;
            }

            // Request fresh sessions + full history for active session
            await _bridgeClient.RequestSessionsAsync();

            // Snapshot the existing history cache entry so we can detect when the server responds
            List<ChatMessage>? preSyncCachedHistory = null;
            if (activeSessionName != null)
            {
                _bridgeClient.SessionHistories.TryGetValue(activeSessionName, out preSyncCachedHistory);
                await _bridgeClient.RequestHistoryAsync(activeSessionName, limit: null);
            }

            // Wait for the server response to populate SessionHistories, with timeout.
            // The old Task.Delay(500) was racy — if the response took >500ms the sync silently
            // reported "Already up to date". Poll for the cache reference to change instead.
            if (activeSessionName != null)
            {
                var deadline = sw.ElapsedMilliseconds + 3000; // 3s timeout
                while (sw.ElapsedMilliseconds < deadline)
                {
                    if (_bridgeClient.SessionHistories.TryGetValue(activeSessionName, out var current)
                        && !ReferenceEquals(current, preSyncCachedHistory))
                        break;
                    await Task.Delay(50);
                }
            }
            else
            {
                // No active session — just wait briefly for sessions list
                await Task.Delay(500);
            }

            // Force-apply server history for the active session, conditionally bypassing the streaming guard.
            // SyncRemoteSessions skips sessions in _remoteStreamingSessions to avoid overwriting
            // incrementally-built content. But a user-initiated force sync should replace local history
            // when the server has more messages (missed during disconnect) or the session isn't streaming.
            if (activeSessionName != null
                && _bridgeClient.SessionHistories.TryGetValue(activeSessionName, out var serverMessages)
                && _sessions.TryGetValue(activeSessionName, out var forceState))
            {
                var isActivelyStreaming = _remoteStreamingSessions.ContainsKey(activeSessionName);
                lock (forceState.Info.HistoryLock)
                {
                    var localCount = forceState.Info.History.Count;
                    if (!isActivelyStreaming || serverMessages.Count > localCount)
                    {
                        forceState.Info.History.Clear();
                        forceState.Info.History.AddRange(serverMessages);
                        forceState.Info.MessageCount = forceState.Info.History.Count;
                        Debug($"[SYNC] Force-applied {serverMessages.Count} messages for '{activeSessionName}' (streaming={isActivelyStreaming}, local={localCount})");
                    }
                }
            }

            // Force-sync processing state for ALL sessions from the server snapshot.
            // SyncRemoteSessions skips sessions in _remoteStreamingSessions, but a user-initiated
            // force sync should always apply the server's authoritative IsProcessing state.
            // Also clear stuck streaming guards — if the server says a session is idle,
            // any lingering guard from a dropped connection should be cleared.
            foreach (var rs in _bridgeClient.Sessions)
            {
                if (_sessions.TryGetValue(rs.Name, out var syncState))
                {
                    if (syncState.Info.IsProcessing != rs.IsProcessing)
                        Debug($"[SYNC] '{rs.Name}' IsProcessing {syncState.Info.IsProcessing} -> {rs.IsProcessing}");
                    syncState.Info.IsProcessing = rs.IsProcessing;
                    syncState.Info.ProcessingStartedAt = rs.ProcessingStartedAt;
                    syncState.Info.ToolCallCount = rs.ToolCallCount;
                    syncState.Info.ProcessingPhase = rs.ProcessingPhase;
                    // Clear stuck streaming guard if server says session is idle
                    if (!rs.IsProcessing)
                        _remoteStreamingSessions.TryRemove(rs.Name, out _);
                }
            }

            // Snapshot post-sync state
            var postSyncSessionCount = _sessions.Count;
            var postSyncMessageCount = 0;
            if (activeInfo != null)
            {
                lock (activeInfo.HistoryLock)
                    postSyncMessageCount = activeInfo.History.Count;
            }

            var sessionDelta = postSyncSessionCount - preSyncSessionCount;
            var messageDelta = postSyncMessageCount - preSyncMessageCount;

            result.Success = true;
            result.SessionCountBefore = preSyncSessionCount;
            result.SessionCountAfter = postSyncSessionCount;
            result.MessageCountBefore = preSyncMessageCount;
            result.MessageCountAfter = postSyncMessageCount;
            result.ElapsedMs = sw.ElapsedMilliseconds;

            // Build user-facing message
            var parts = new List<string>();
            if (messageDelta > 0)
                parts.Add($"{messageDelta} new message{(messageDelta != 1 ? "s" : "")}");
            if (sessionDelta > 0)
                parts.Add($"{sessionDelta} new session{(sessionDelta != 1 ? "s" : "")}");
            result.Message = parts.Count > 0
                ? $"Synced: {string.Join(", ", parts)}"
                : "Already up to date";

            // Diagnostic logging: detect missed messages
            if (messageDelta > 0)
            {
                Debug($"[SYNC] Force refresh for '{activeSessionName}': " +
                      $"{preSyncMessageCount}→{postSyncMessageCount} messages " +
                      $"(+{messageDelta}), {sw.ElapsedMilliseconds}ms. " +
                      $"⚠️ {messageDelta} messages were missed during streaming.");
            }
            else
            {
                Debug($"[SYNC] Force refresh for '{activeSessionName}': " +
                      $"{postSyncMessageCount} messages, up to date, {sw.ElapsedMilliseconds}ms");
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Sync failed: {ex.Message}";
            result.ElapsedMs = sw.ElapsedMilliseconds;
            Debug($"[SYNC] Force refresh failed: {ex.Message}");
        }

        OnStateChanged?.Invoke();
        return result;
    }

    /// <summary>
    /// Result of a forced remote sync operation.
    /// </summary>
    public class SyncResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public int SessionCountBefore { get; set; }
        public int SessionCountAfter { get; set; }
        public int MessageCountBefore { get; set; }
        public int MessageCountAfter { get; set; }
        public long ElapsedMs { get; set; }
    }

    // --- Remote repo operations ---

    public async Task<(string RepoId, string RepoName)?> AddRepoRemoteAsync(string url, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        if (!IsRemoteMode)
        {
            var repo = await _repoManager.AddRepositoryAsync(url, onProgress, ct);
            GetOrCreateRepoGroup(repo.Id, repo.Name, explicitly: true);
            return (repo.Id, repo.Name);
        }

        var result = await _bridgeClient.AddRepoAsync(url, onProgress, ct);
        // Server already created the group — request updated organization
        try { await _bridgeClient.RequestSessionsAsync(ct); } catch { }
        return (result.RepoId, result.RepoName);
    }

    /// <summary>
    /// Add an already-cloned local folder as a managed repository. The folder's 'origin'
    /// remote URL is used to create a bare clone in the PolyPilot repos directory, giving
    /// the repo all the same features as if it were cloned through the app.
    /// Desktop (local) mode only — not supported when connected to a remote server.
    /// </summary>
    /// <summary>
    /// Result of adding a local folder as a repository.
    /// </summary>
    public record AddLocalFolderResult(
        string RepoId,
        string RepoName,
        /// <summary>
        /// When non-null, the folder was registered as an external worktree under an existing
        /// repo group rather than creating a new 📁 group. Points to the existing group.
        /// </summary>
        string? ExistingGroupId,
        string? ExistingGroupName);

    public async Task<AddLocalFolderResult> AddRepoFromLocalFolderAsync(
        string localPath,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (IsRemoteMode)
            throw new InvalidOperationException(
                "Adding an existing folder is only supported in local mode. " +
                "In remote mode the server cannot access local paths on this device.");

        // Expand ~ and normalize path before any validation
        if (localPath.StartsWith("~", StringComparison.Ordinal))
            localPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                localPath.TrimStart('~').TrimStart('/', '\\'));
        localPath = Path.GetFullPath(localPath);

        if (!Directory.Exists(localPath))
            throw new InvalidOperationException($"Folder not found: '{localPath}'");

        // Register/update the bare clone and external worktree for this local path
        var repo = await _repoManager.AddRepositoryFromLocalAsync(localPath, onProgress, ct);

        // Ensure a dedicated 📁 local folder group exists for this path.
        // Use PromoteOrCreateLocalFolderGroup so that if there's an existing URL-based group
        // for this repo (created by an older code version that lacked LocalPath support),
        // we update that group in-place rather than creating a redundant duplicate.
        PromoteOrCreateLocalFolderGroup(localPath, repo.Id);
        return new AddLocalFolderResult(repo.Id, repo.Name, null, null);
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
