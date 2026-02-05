using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AutoPilot.App.Models;
using GitHub.Copilot.SDK;

namespace AutoPilot.App.Services;

public class CopilotService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new();
    private CopilotClient? _client;
    private string? _activeSessionName;
    private SynchronizationContext? _syncContext;
    
    private static readonly string SessionStatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".copilot", "session-state");

    private static readonly string ActiveSessionsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".copilot", "autopilot-active-sessions.json");

    private static readonly string UiStateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".copilot", "autopilot-ui-state.json");

    private static readonly string ProjectDir = "/Users/shneuvil/Projects/AutoPilot.App";

    public string DefaultModel { get; set; } = "claude-opus-4.6";
    public string? SystemInstructions { get; set; }
    public bool IsInitialized { get; private set; }
    public string? ActiveSessionName => _activeSessionName;

    // Debug info
    public string LastDebugMessage { get; private set; } = "";

    public event Action? OnStateChanged;
    public event Action<string, string>? OnContentReceived; // sessionName, content
    public event Action<string, string>? OnError; // sessionName, error
    public event Action<string, string>? OnSessionComplete; // sessionName, summary
    public event Action<string, string>? OnActivity; // sessionName, activity description
    public event Action<string>? OnDebug; // debug messages

    private class SessionState
    {
        public required CopilotSession Session { get; init; }
        public required AgentSessionInfo Info { get; init; }
        public TaskCompletionSource<string>? ResponseCompletion { get; set; }
        public StringBuilder CurrentResponse { get; } = new();
    }

    private void Debug(string message)
    {
        LastDebugMessage = message;
        Console.WriteLine($"[DEBUG] {message}");
        OnDebug?.Invoke(message);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsInitialized) return;

        // Capture the sync context for marshaling events back to UI thread
        _syncContext = SynchronizationContext.Current;
        Debug($"SyncContext captured: {_syncContext?.GetType().Name ?? "null"}");

        _client = new CopilotClient();
        await _client.StartAsync(cancellationToken);
        IsInitialized = true;
        Debug("Copilot client started");

        // Load default system instructions from the project's copilot-instructions.md
        var instructionsPath = Path.Combine(ProjectDir, ".github", "copilot-instructions.md");
        if (File.Exists(instructionsPath) && string.IsNullOrEmpty(SystemInstructions))
        {
            SystemInstructions = await File.ReadAllTextAsync(instructionsPath, cancellationToken);
            Debug("Loaded system instructions from copilot-instructions.md");
        }

        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Gets a list of persisted session GUIDs from ~/.copilot/session-state
    /// </summary>
    public IEnumerable<PersistedSessionInfo> GetPersistedSessions()
    {
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
                        if (root.TryGetProperty("data", out var data) &&
                            data.TryGetProperty("workingDirectory", out var wd))
                        {
                            workingDir = wd.GetString();
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

        return new PersistedSessionInfo
        {
            SessionId = di.Name,
            LastModified = lastUsed,
            Path = di.FullName,
            Title = title ?? "Untitled session",
            Preview = preview ?? "No preview available",
            WorkingDirectory = workingDir
        };
    }

    /// <summary>
    /// Load conversation history from events.jsonl
    /// </summary>
    private List<ChatMessage> LoadHistoryFromDisk(string sessionId)
    {
        var history = new List<ChatMessage>();
        var eventsFile = Path.Combine(SessionStatePath, sessionId, "events.jsonl");
        
        if (!File.Exists(eventsFile))
            return history;

        try
        {
            string? lastUserMessage = null;
            DateTime lastUserTimestamp = DateTime.Now;
            var assistantResponses = new List<string>();
            DateTime lastAssistantTimestamp = DateTime.Now;

            foreach (var line in File.ReadLines(eventsFile))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                
                if (!root.TryGetProperty("type", out var typeEl)) continue;
                var type = typeEl.GetString();
                
                if (!root.TryGetProperty("data", out var data)) continue;
                var timestamp = DateTime.Now;
                if (root.TryGetProperty("timestamp", out var tsEl))
                    DateTime.TryParse(tsEl.GetString(), out timestamp);

                if (type == "user.message")
                {
                    // Before processing new user message, flush any pending assistant responses
                    if (lastUserMessage != null)
                    {
                        history.Add(new ChatMessage("user", lastUserMessage, lastUserTimestamp));
                        if (assistantResponses.Count > 0)
                        {
                            // Join all assistant responses for this turn
                            var fullResponse = string.Join("\n\n", assistantResponses.Where(r => r.Length > 50));
                            if (!string.IsNullOrEmpty(fullResponse))
                                history.Add(new ChatMessage("assistant", fullResponse, lastAssistantTimestamp));
                            assistantResponses.Clear();
                        }
                    }
                    
                    // Start tracking new user message
                    if (data.TryGetProperty("content", out var userContent))
                    {
                        lastUserMessage = userContent.GetString();
                        lastUserTimestamp = timestamp;
                    }
                }
                else if (type == "assistant.message")
                {
                    // Check if this message has tool requests (skip those, they're just "thinking")
                    bool hasToolRequests = data.TryGetProperty("toolRequests", out var tools) && 
                                           tools.ValueKind == JsonValueKind.Array && 
                                           tools.GetArrayLength() > 0;
                    
                    if (data.TryGetProperty("content", out var assistantContent))
                    {
                        var content = assistantContent.GetString();
                        // Only capture substantial content without tool requests
                        if (!string.IsNullOrEmpty(content) && content.Length > 20 && !hasToolRequests)
                        {
                            assistantResponses.Add(content);
                            lastAssistantTimestamp = timestamp;
                        }
                    }
                }
            }
            
            // Flush final turn
            if (lastUserMessage != null)
            {
                history.Add(new ChatMessage("user", lastUserMessage, lastUserTimestamp));
                if (assistantResponses.Count > 0)
                {
                    var fullResponse = string.Join("\n\n", assistantResponses.Where(r => r.Length > 50));
                    if (!string.IsNullOrEmpty(fullResponse))
                        history.Add(new ChatMessage("assistant", fullResponse, lastAssistantTimestamp));
                }
            }
        }
        catch
        {
            // Ignore parse errors, return what we have
        }

        return history;
    }

    /// <summary>
    /// Resume an existing session by its GUID
    /// </summary>
    public async Task<AgentSessionInfo> ResumeSessionAsync(string sessionId, string displayName, CancellationToken cancellationToken = default)
    {
        if (!IsInitialized || _client == null)
            throw new InvalidOperationException("Service not initialized. Call InitializeAsync first.");

        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));

        if (_sessions.ContainsKey(displayName))
            throw new InvalidOperationException($"Session '{displayName}' already exists.");

        // Load history from disk before resuming
        var history = LoadHistoryFromDisk(sessionId);

        // Resume the session using the SDK
        var copilotSession = await _client.ResumeSessionAsync(sessionId, cancellationToken: cancellationToken);

        var info = new AgentSessionInfo
        {
            Name = displayName,
            Model = "resumed", // Model info may not be immediately available
            CreatedAt = DateTime.Now,
            SessionId = sessionId,
            IsResumed = true
        };

        // Add loaded history to the session info
        foreach (var msg in history)
        {
            info.History.Add(msg);
        }
        info.MessageCount = info.History.Count;

        var state = new SessionState
        {
            Session = copilotSession,
            Info = info
        };

        copilotSession.On(evt => HandleSessionEvent(displayName, state, evt));

        if (!_sessions.TryAdd(displayName, state))
        {
            await copilotSession.DisposeAsync();
            throw new InvalidOperationException($"Failed to add session '{displayName}'.");
        }

        _activeSessionName ??= displayName;
        OnStateChanged?.Invoke();
        SaveActiveSessionsToDisk();
        return info;
    }

    public async Task<AgentSessionInfo> CreateSessionAsync(string name, string? model = null, CancellationToken cancellationToken = default)
    {
        if (!IsInitialized || _client == null)
            throw new InvalidOperationException("Service not initialized. Call InitializeAsync first.");

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Session name cannot be empty.", nameof(name));

        if (_sessions.ContainsKey(name))
            throw new InvalidOperationException($"Session '{name}' already exists.");

        var sessionModel = model ?? DefaultModel;
        var config = new SessionConfig
        {
            Model = sessionModel,
            WorkingDirectory = ProjectDir,
        };

        // Inject system instructions if set
        if (!string.IsNullOrEmpty(SystemInstructions))
        {
            config.SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = SystemInstructions
            };
        }

        var copilotSession = await _client.CreateSessionAsync(config, cancellationToken);

        var info = new AgentSessionInfo
        {
            Name = name,
            Model = sessionModel,
            CreatedAt = DateTime.Now
        };

        var state = new SessionState
        {
            Session = copilotSession,
            Info = info
        };

        copilotSession.On(evt => HandleSessionEvent(name, state, evt));

        if (!_sessions.TryAdd(name, state))
        {
            await copilotSession.DisposeAsync();
            throw new InvalidOperationException($"Failed to add session '{name}'.");
        }

        _activeSessionName ??= name;
        OnStateChanged?.Invoke();
        return info;
    }

    private void HandleSessionEvent(string sessionName, SessionState state, SessionEvent evt)
    {
        // Marshal to UI thread if we have a sync context
        void Invoke(Action action)
        {
            if (_syncContext != null)
                _syncContext.Post(_ => action(), null);
            else
                action();
        }
        
        switch (evt)
        {
            case AssistantMessageDeltaEvent delta:
                var deltaContent = delta.Data.DeltaContent;
                state.CurrentResponse.Append(deltaContent);
                Invoke(() => OnContentReceived?.Invoke(sessionName, deltaContent ?? ""));
                break;

            case AssistantMessageEvent msg:
                var msgContent = msg.Data.Content;
                if (!string.IsNullOrEmpty(msgContent) && state.CurrentResponse.Length == 0)
                {
                    state.CurrentResponse.Append(msgContent);
                    Invoke(() => OnContentReceived?.Invoke(sessionName, msgContent));
                }
                // Show tool requests as activity
                var toolReqs = msg.Data.ToolRequests;
                if (toolReqs != null && toolReqs.Any())
                {
                    foreach (var tool in toolReqs!)
                    {
                        Invoke(() => OnActivity?.Invoke(sessionName, $"🔧 Calling {tool.Name}..."));
                    }
                }
                break;

            case ToolExecutionStartEvent toolStart:
                Invoke(() => OnActivity?.Invoke(sessionName, $"🔧 Running {toolStart.Data.ToolName}..."));
                break;

            case ToolExecutionCompleteEvent toolDone:
                Invoke(() => OnActivity?.Invoke(sessionName, $"✅ Tool completed"));
                break;

            case ToolExecutionProgressEvent toolProgress:
                Invoke(() => OnActivity?.Invoke(sessionName, "⚙️ Tool executing..."));
                break;

            case AssistantIntentEvent intent:
                Invoke(() => OnActivity?.Invoke(sessionName, $"💭 {intent.Data.Intent}"));
                break;

            case AssistantTurnStartEvent:
                Invoke(() => OnActivity?.Invoke(sessionName, "🤔 Thinking..."));
                break;

            case AssistantTurnEndEvent:
                Invoke(() => OnActivity?.Invoke(sessionName, ""));
                break;

            case SessionIdleEvent:
                CompleteResponse(state);
                break;

            case SessionStartEvent start:
                state.Info.SessionId = start.Data.SessionId;
                Debug($"Session ID assigned: {start.Data.SessionId}");
                SaveActiveSessionsToDisk();
                break;

            case SessionErrorEvent err:
                Invoke(() => OnError?.Invoke(sessionName, err.Data.Message));
                state.ResponseCompletion?.TrySetException(new Exception(err.Data.Message));
                state.Info.IsProcessing = false;
                Invoke(() => OnStateChanged?.Invoke());
                break;
                
            default:
                break;
        }
    }

    private void CompleteResponse(SessionState state)
    {
        var response = state.CurrentResponse.ToString();
        if (!string.IsNullOrEmpty(response))
        {
            state.Info.History.Add(new ChatMessage("assistant", response, DateTime.Now));
            state.Info.MessageCount = state.Info.History.Count;
        }
        state.ResponseCompletion?.TrySetResult(response);
        state.CurrentResponse.Clear();
        state.Info.IsProcessing = false;
        OnStateChanged?.Invoke();
        
        // Fire completion notification
        var summary = response.Length > 100 ? response[..100] + "..." : response;
        OnSessionComplete?.Invoke(state.Info.Name, summary);
    }

    public async Task<string> SendPromptAsync(string sessionName, string prompt, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionName, out var state))
            throw new InvalidOperationException($"Session '{sessionName}' not found.");

        if (state.Info.IsProcessing)
            throw new InvalidOperationException("Session is already processing a request.");

        state.Info.IsProcessing = true;
        state.ResponseCompletion = new TaskCompletionSource<string>();
        state.CurrentResponse.Clear();

        state.Info.History.Add(new ChatMessage("user", prompt, DateTime.Now));
        state.Info.MessageCount = state.Info.History.Count;
        OnStateChanged?.Invoke();

        Console.WriteLine($"[DEBUG] Sending prompt to session '{sessionName}': {prompt.Substring(0, Math.Min(50, prompt.Length))}...");
        
        try 
        {
            await state.Session.SendAsync(new MessageOptions
            {
                Prompt = prompt
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] SendAsync threw: {ex.Message}");
            OnError?.Invoke(sessionName, $"SendAsync failed: {ex.Message}");
            state.Info.IsProcessing = false;
            OnStateChanged?.Invoke();
            throw;
        }

        Console.WriteLine($"[DEBUG] SendAsync completed, waiting for response...");

        // Add timeout - if no response in 120 seconds, something is wrong
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(120));
        cts.Token.Register(() => 
        {
            if (!state.ResponseCompletion!.Task.IsCompleted)
            {
                OnError?.Invoke(sessionName, "Response timeout after 120 seconds");
                state.ResponseCompletion.TrySetCanceled();
            }
        });

        return await state.ResponseCompletion.Task;
    }

    public AgentSessionInfo? GetSession(string name)
    {
        return _sessions.TryGetValue(name, out var state) ? state.Info : null;
    }

    public AgentSessionInfo? GetActiveSession()
    {
        return _activeSessionName != null ? GetSession(_activeSessionName) : null;
    }

    public bool SwitchSession(string name)
    {
        if (!_sessions.ContainsKey(name))
            return false;

        _activeSessionName = name;
        OnStateChanged?.Invoke();
        return true;
    }

    public void SetActiveSession(string? name)
    {
        if (name != null && _sessions.ContainsKey(name))
            _activeSessionName = name;
    }

    public async Task<bool> CloseSessionAsync(string name)
    {
        if (!_sessions.TryRemove(name, out var state))
            return false;

        await state.Session.DisposeAsync();

        if (_activeSessionName == name)
        {
            _activeSessionName = _sessions.Keys.FirstOrDefault();
        }

        OnStateChanged?.Invoke();
        SaveActiveSessionsToDisk();
        return true;
    }

    public void ClearHistory(string name)
    {
        if (_sessions.TryGetValue(name, out var state))
        {
            state.Info.History.Clear();
            state.Info.MessageCount = 0;
            OnStateChanged?.Invoke();
        }
    }

    public IEnumerable<AgentSessionInfo> GetAllSessions() => _sessions.Values.Select(s => s.Info);

    public int SessionCount => _sessions.Count;

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
        if (!File.Exists(ActiveSessionsFile)) return;

        try
        {
            var json = await File.ReadAllTextAsync(ActiveSessionsFile, cancellationToken);
            var entries = JsonSerializer.Deserialize<List<ActiveSessionEntry>>(json);
            if (entries == null || entries.Count == 0) return;

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
                }
            }
        }
        catch (Exception ex)
        {
            Debug($"Failed to load active sessions file: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        SaveActiveSessionsToDisk();
        
        foreach (var state in _sessions.Values)
        {
            await state.Session.DisposeAsync();
        }
        _sessions.Clear();

        if (_client != null)
        {
            await _client.DisposeAsync();
        }
    }

    public void SaveUiState(string currentPage, string? activeSession = null)
    {
        try
        {
            var state = new UiState
            {
                CurrentPage = currentPage,
                ActiveSession = activeSession ?? _activeSessionName
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
}

public class UiState
{
    public string CurrentPage { get; set; } = "/";
    public string? ActiveSession { get; set; }
}

public class ActiveSessionEntry
{
    public string SessionId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Model { get; set; } = "";
}

public class PersistedSessionInfo
{
    public required string SessionId { get; init; }
    public DateTime LastModified { get; init; }
    public string? Path { get; init; }
    public string? Title { get; init; }  // First user message truncated
    public string? Preview { get; init; } // Full first user message for tooltip
    public string? WorkingDirectory { get; init; }
}
