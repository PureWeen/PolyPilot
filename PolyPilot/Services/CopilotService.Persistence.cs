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
            var entries = _sessions.Values
                .Where(s => s.Info.SessionId != null)
                .Select(s => new ActiveSessionEntry
                {
                    SessionId = s.Info.SessionId!,
                    DisplayName = s.Info.Name,
                    Model = s.Info.Model
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

                    foreach (var entry in entries)
                    {
                        try
                        {
                            // Skip if already active
                            if (_sessions.ContainsKey(entry.DisplayName)) continue;
                            
                            // Check the session still exists on disk
                            var sessionDir = Path.Combine(SessionStatePath, entry.SessionId);
                            if (!Directory.Exists(sessionDir)) continue;

                            await ResumeSessionAsync(entry.SessionId, entry.DisplayName, cancellationToken);
                            Debug($"Restored session: {entry.DisplayName}");
                        }
                        catch (Exception ex)
                        {
                            Debug($"Failed to restore '{entry.DisplayName}': {ex.Message}");

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
                                    Debug($"Failed to recreate client: {clientEx.Message}");
                                    break; // Stop trying to restore sessions
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug($"Failed to load active sessions file: {ex.Message}");
            }
        }

    }

    public void SaveUiState(string currentPage, string? activeSession = null, int? fontSize = null, string? selectedModel = null, bool? expandedGrid = null, string? expandedSession = null)
    {
        try
        {
            var existing = LoadUiState();
            var state = new UiState
            {
                CurrentPage = currentPage,
                ActiveSession = activeSession ?? _activeSessionName,
                FontSize = fontSize ?? existing?.FontSize ?? 20,
                SelectedModel = selectedModel ?? existing?.SelectedModel,
                ExpandedGrid = expandedGrid ?? existing?.ExpandedGrid ?? false,
                ExpandedSession = expandedSession ?? existing?.ExpandedSession
            };
            var json = JsonSerializer.Serialize(state);
            File.WriteAllText(UiStateFile, json);
        }
        catch { }
    }

    public UiState? LoadUiState()
    {
        try
        {
            if (!File.Exists(UiStateFile)) return null;
            var json = File.ReadAllText(UiStateFile);
            return JsonSerializer.Deserialize<UiState>(json);
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
            .Select(di => CreatePersistedSessionInfo(di))
            .OrderByDescending(s => s.LastModified);
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
