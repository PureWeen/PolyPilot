using System.Text.Json;
using PolyPilot.Models;
using GitHub.Copilot.SDK;

namespace PolyPilot.Services;

public partial class CopilotService
{
    /// <summary>
    /// Save active session list to disk so we can restore on relaunch
    /// </summary>
    private void SaveActiveSessionsToDisk()
    {
        try
        {
            // Ensure directory exists (required on iOS where it may not exist by default)
            Directory.CreateDirectory(PolyPilotBaseDir);
            
            var entries = _sessions.Values
                .Where(s => s.Info.SessionId != null)
                .Select(s => new ActiveSessionEntry
                {
                    SessionId = s.Info.SessionId!,
                    DisplayName = s.Info.Name,
                    Model = s.Info.Model,
                    WorkingDirectory = s.Info.WorkingDirectory
                })
                .ToList();
            
            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ActiveSessionsFile, json);
        }
        catch (Exception ex)
        {
            Debug($"Failed to save active sessions: {ex.Message}");
        }
    }

    /// <summary>
    /// Load session metadata without resuming SDK sessions (lazy loading for fast startup)
    /// </summary>
    public async Task RestorePreviousSessionsAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ActiveSessionsFile)) return;

        try
        {
            var json = await File.ReadAllTextAsync(ActiveSessionsFile, cancellationToken);
            var entries = JsonSerializer.Deserialize<List<ActiveSessionEntry>>(json);
            if (entries == null || entries.Count == 0) return;

            // Filter to restorable entries upfront (avoids per-iteration checks)
            var toRestore = entries
                .Where(e => !_sessions.ContainsKey(e.DisplayName))
                .Where(e => Directory.Exists(Path.Combine(SessionStatePath, e.SessionId)))
                .ToList();

            if (toRestore.Count == 0) return;
            Debug($"Lazy-loading {toRestore.Count} previous sessions (metadata only)...");

            foreach (var entry in toRestore)
            {
                try
                {
                    // Create lightweight placeholder with metadata only
                    var info = new AgentSessionInfo
                    {
                        Name = entry.DisplayName,
                        SessionId = entry.SessionId,
                        Model = entry.Model ?? DefaultModel,
                        WorkingDirectory = entry.WorkingDirectory,
                        CreatedAt = DateTime.Now,
                        IsResumed = true
                    };
                    info.GitBranch = GetGitBranch(info.WorkingDirectory);

                    var state = new SessionState
                    {
                        Session = null!,  // Will be lazy-loaded on first access
                        Info = info,
                        IsLazyLoaded = true
                    };

                    _sessions[entry.DisplayName] = state;
                    Debug($"Lazy-loaded session metadata: {entry.DisplayName}");
                }
                catch (Exception ex)
                {
                    Debug($"Failed to lazy-load '{entry.DisplayName}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug($"Failed to load active sessions file: {ex.Message}");
        }
    }

    /// <summary>
    /// Ensure a lazy-loaded session is fully resumed before use
    /// </summary>
    private async Task<SessionState> EnsureSessionResumedAsync(string sessionName, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionName, out var state))
            throw new InvalidOperationException($"Session '{sessionName}' not found");

        if (!state.IsLazyLoaded || state.Session != null)
            return state;  // Already fully loaded

        Debug($"Hydrating lazy-loaded session: {sessionName}");

        try
        {
            // Load history from disk
            var history = LoadHistoryFromDisk(state.Info.SessionId!);
            if (history.Count > 0)
                await _chatDb.BulkInsertAsync(state.Info.SessionId!, history);

            // Resume the SDK session
            var resumeConfig = new ResumeSessionConfig
            {
                Model = state.Info.Model,
                WorkingDirectory = state.Info.WorkingDirectory
            };
            var copilotSession = await _client!.ResumeSessionAsync(state.Info.SessionId!, resumeConfig, cancellationToken);

            // Update state with full session
            state.Session = copilotSession;
            state.IsLazyLoaded = false;

            // Add history to info
            foreach (var msg in history)
                state.Info.History.Add(msg);
            state.Info.MessageCount = state.Info.History.Count;
            state.Info.LastReadMessageCount = state.Info.History.Count;

            // Mark stale incomplete tool calls/reasoning as complete
            foreach (var msg in state.Info.History.Where(m => m.MessageType == ChatMessageType.ToolCall && !m.IsComplete))
                msg.IsComplete = true;
            foreach (var msg in state.Info.History.Where(m => m.MessageType == ChatMessageType.Reasoning && !m.IsComplete))
                msg.IsComplete = true;

            // Subscribe to events
            state.Session!.On(evt => HandleSessionEvent(state, evt));

            Debug($"Session hydrated: {sessionName}");
            OnStateChanged?.Invoke();
            return state;
        }
        catch (Exception ex)
        {
            Debug($"Failed to hydrate session '{sessionName}': {ex.Message}");
            throw;
        }
    }

    public void SaveUiState(string currentPage, string? activeSession = null, int? fontSize = null, string? selectedModel = null, bool? expandedGrid = null, string? expandedSession = "<<unspecified>>")
    {
        try
        {
            // Ensure directory exists (critical for iOS where it doesn't exist by default)
            Directory.CreateDirectory(PolyPilotBaseDir);
            
            var existing = LoadUiState();
            var state = new UiState
            {
                CurrentPage = currentPage,
                ActiveSession = activeSession ?? _activeSessionName,
                FontSize = fontSize ?? existing?.FontSize ?? 20,
                SelectedModel = selectedModel ?? existing?.SelectedModel,
                ExpandedGrid = expandedGrid ?? existing?.ExpandedGrid ?? false,
                ExpandedSession = expandedSession == "<<unspecified>>" ? existing?.ExpandedSession : expandedSession
            };
            var json = JsonSerializer.Serialize(state);
            File.WriteAllText(UiStateFile, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save UI state: {ex.Message}");
        }
    }

    public UiState? LoadUiState()
    {
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
                    File.WriteAllText(ActiveSessionsFile, updatedJson);
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
