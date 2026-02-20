using System.Text.Json;
using PolyPilot.Models;

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
                .Where(s => s.Info.SessionId != null && !s.Info.IsHidden)
                .Select(s => new ActiveSessionEntry
                {
                    SessionId = s.Info.SessionId!,
                    DisplayName = s.Info.Name,
                    Model = s.Info.Model,
                    WorkingDirectory = s.Info.WorkingDirectory
                })
                .ToList();
            
            // Merge: preserve entries from the existing file that aren't currently in memory
            // but whose session directory still exists on disk. This prevents data loss when
            // sessions fail to restore (e.g. during mode switches) or if the app is killed
            // mid-restore.
            try
            {
                if (File.Exists(ActiveSessionsFile))
                {
                    var existingJson = File.ReadAllText(ActiveSessionsFile);
                    var existingEntries = JsonSerializer.Deserialize<List<ActiveSessionEntry>>(existingJson);
                    if (existingEntries != null)
                    {
                        var closedIds = new HashSet<string>(_closedSessionIds.Keys, StringComparer.OrdinalIgnoreCase);
                        entries = MergeSessionEntries(entries, existingEntries, closedIds,
                            sessionId => Directory.Exists(Path.Combine(SessionStatePath, sessionId)));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug($"Failed to merge existing sessions: {ex.Message}");
            }
            
            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ActiveSessionsFile, json);
        }
        catch (Exception ex)
        {
            Debug($"Failed to save active sessions: {ex.Message}");
        }
    }

    /// <summary>
    /// Merge active (in-memory) session entries with persisted (on-disk) entries.
    /// Persisted entries are kept if they aren't already active, weren't explicitly
    /// closed, and their session directory still exists.
    /// </summary>
    internal static List<ActiveSessionEntry> MergeSessionEntries(
        List<ActiveSessionEntry> active,
        List<ActiveSessionEntry> persisted,
        ISet<string> closedIds,
        Func<string, bool> sessionDirExists)
    {
        var merged = new List<ActiveSessionEntry>(active);
        var activeIds = new HashSet<string>(active.Select(e => e.SessionId), StringComparer.OrdinalIgnoreCase);

        foreach (var existing in persisted)
        {
            if (activeIds.Contains(existing.SessionId)) continue;
            if (closedIds.Contains(existing.SessionId)) continue;
            if (!sessionDirExists(existing.SessionId)) continue;

            merged.Add(existing);
            activeIds.Add(existing.SessionId);
        }

        return merged;
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

                    // Collect evaluator session names referenced by active reflection cycles
                    var activeEvaluators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var g in Organization.Groups)
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

                            await ResumeSessionAsync(entry.SessionId, entry.DisplayName, entry.WorkingDirectory, entry.Model, cancellationToken);
                            Debug($"Restored session: {entry.DisplayName}");
                        }
                        catch (Exception ex)
                        {
                            Debug($"Failed to restore '{entry.DisplayName}': {ex.GetType().Name}: {ex.Message}");

                            // If the connection broke, recreate the client
                            if (ex is System.IO.IOException or System.Net.Sockets.SocketException
                                or ObjectDisposedException
                                || ex.InnerException is System.IO.IOException or System.Net.Sockets.SocketException
                                || ex.Message.Contains("Connection", StringComparison.OrdinalIgnoreCase)
                                || ex.Message.Contains("transport", StringComparison.OrdinalIgnoreCase))
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
                ExpandedSession = expandedSession == "<<unspecified>>" ? existing?.ExpandedSession : expandedSession,
                InputModes = inputModes != null
                    ? new Dictionary<string, string>(inputModes)
                    : existing?.InputModes ?? new Dictionary<string, string>()
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
