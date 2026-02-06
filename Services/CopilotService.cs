using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AutoPilot.App.Models;
using GitHub.Copilot.SDK;

namespace AutoPilot.App.Services;

public class CopilotService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new();
    private readonly ChatDatabase _chatDb;
    private readonly ServerManager _serverManager;
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

    private static readonly string ProjectDir = FindProjectDir();

    private static string FindProjectDir()
    {
        // Walk up from the base directory to find the .csproj (works from bin/Debug/... at runtime)
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            if (Directory.GetFiles(dir, "*.csproj").Length > 0)
                return dir;
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }
        // Fallback to user home
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public string DefaultModel { get; set; } = "claude-opus-4.6";
    public string? SystemInstructions { get; set; }
    public bool IsInitialized { get; private set; }
    public string? ActiveSessionName => _activeSessionName;
    public ChatDatabase ChatDb => _chatDb;
    public ConnectionMode CurrentMode { get; private set; } = ConnectionMode.Embedded;

    public CopilotService(ChatDatabase chatDb, ServerManager serverManager)
    {
        _chatDb = chatDb;
        _serverManager = serverManager;
    }

    // Debug info
    public string LastDebugMessage { get; private set; } = "";

    public event Action? OnStateChanged;
    public event Action<string, string>? OnContentReceived; // sessionName, content
    public event Action<string, string>? OnError; // sessionName, error
    public event Action<string, string>? OnSessionComplete; // sessionName, summary
    public event Action<string, string>? OnActivity; // sessionName, activity description
    public event Action<string>? OnDebug; // debug messages

    // Rich event types
    public event Action<string, string, string>? OnToolStarted; // sessionName, toolName, callId
    public event Action<string, string, string, bool>? OnToolCompleted; // sessionName, callId, result, success
    public event Action<string, string, string>? OnReasoningReceived; // sessionName, reasoningId, deltaContent
    public event Action<string, string>? OnReasoningComplete; // sessionName, reasoningId
    public event Action<string, string>? OnIntentChanged; // sessionName, intent
    public event Action<string, SessionUsageInfo>? OnUsageInfoChanged; // sessionName, usageInfo
    public event Action<string>? OnTurnStart; // sessionName
    public event Action<string>? OnTurnEnd; // sessionName

    private class SessionState
    {
        public required CopilotSession Session { get; init; }
        public required AgentSessionInfo Info { get; init; }
        public TaskCompletionSource<string>? ResponseCompletion { get; set; }
        public StringBuilder CurrentResponse { get; } = new();
        public bool HasReceivedDeltasThisTurn { get; set; }
        public string? LastMessageId { get; set; }
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

        var settings = ConnectionSettings.Load();
        CurrentMode = settings.Mode;

        // In Persistent mode, auto-start the server if not already running
        if (settings.Mode == ConnectionMode.Persistent)
        {
            if (!_serverManager.CheckServerRunning("localhost", settings.Port))
            {
                Debug($"Persistent server not running, auto-starting on port {settings.Port}...");
                var started = await _serverManager.StartServerAsync(settings.Port);
                if (!started)
                {
                    Debug("Failed to auto-start server, falling back to Embedded mode");
                    settings.Mode = ConnectionMode.Embedded;
                    CurrentMode = ConnectionMode.Embedded;
                }
            }
            else
            {
                Debug($"Persistent server already running on port {settings.Port}");
            }
        }

        _client = CreateClient(settings);

        await _client.StartAsync(cancellationToken);
        IsInitialized = true;
        Debug($"Copilot client started in {settings.Mode} mode");

        // Load default system instructions from the project's copilot-instructions.md
        var instructionsPath = Path.Combine(ProjectDir, ".github", "copilot-instructions.md");
        if (File.Exists(instructionsPath) && string.IsNullOrEmpty(SystemInstructions))
        {
            SystemInstructions = await File.ReadAllTextAsync(instructionsPath, cancellationToken);
            Debug("Loaded system instructions from copilot-instructions.md");
        }

        OnStateChanged?.Invoke();

        // Restore previous sessions (includes subscribing to untracked server sessions in Persistent mode)
        await RestorePreviousSessionsAsync(cancellationToken);
    }

    /// <summary>
    /// Disconnect from current client and reconnect with new settings
    /// </summary>
    public async Task ReconnectAsync(ConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        Debug($"Reconnecting with mode: {settings.Mode}...");

        // Dispose existing sessions and client
        foreach (var state in _sessions.Values)
        {
            try { await state.Session.DisposeAsync(); } catch { }
        }
        _sessions.Clear();
        _activeSessionName = null;

        if (_client != null)
        {
            try { await _client.DisposeAsync(); } catch { }
            _client = null;
        }

        IsInitialized = false;
        CurrentMode = settings.Mode;
        OnStateChanged?.Invoke();

        _client = CreateClient(settings);

        await _client.StartAsync(cancellationToken);
        IsInitialized = true;
        Debug($"Reconnected in {settings.Mode} mode");
        OnStateChanged?.Invoke();

        // Restore previous sessions
        await RestorePreviousSessionsAsync(cancellationToken);
    }

    private static CopilotClient CreateClient(ConnectionSettings settings)
    {
        return settings.Mode switch
        {
            ConnectionMode.Persistent => new CopilotClient(new CopilotClientOptions
            {
                CliUrl = settings.CliUrl,
                UseStdio = false
            }),
            _ => new CopilotClient()
        };
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
            // Track tool calls by ID so we can update them when complete
            var toolCallMessages = new Dictionary<string, ChatMessage>();

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

                switch (type)
                {
                    case "user.message":
                    {
                        if (data.TryGetProperty("content", out var userContent))
                        {
                            var content = userContent.GetString();
                            if (!string.IsNullOrEmpty(content))
                            {
                                var msg = ChatMessage.UserMessage(content);
                                msg.Timestamp = timestamp;
                                history.Add(msg);
                            }
                        }
                        break;
                    }

                    case "assistant.message":
                    {
                        // Add reasoning if present
                        if (data.TryGetProperty("reasoningText", out var reasoningEl))
                        {
                            var reasoning = reasoningEl.GetString();
                            if (!string.IsNullOrEmpty(reasoning))
                            {
                                var msg = ChatMessage.ReasoningMessage("restored");
                                msg.Content = reasoning;
                                msg.IsComplete = true;
                                msg.IsCollapsed = true;
                                msg.Timestamp = timestamp;
                                history.Add(msg);
                            }
                        }

                        // Add assistant text content (skip if only tool requests with no text)
                        if (data.TryGetProperty("content", out var assistantContent))
                        {
                            var content = assistantContent.GetString()?.Trim();
                            if (!string.IsNullOrEmpty(content))
                            {
                                var msg = ChatMessage.AssistantMessage(content);
                                msg.Timestamp = timestamp;
                                history.Add(msg);
                            }
                        }
                        break;
                    }

                    case "tool.execution_start":
                    {
                        var toolName = data.TryGetProperty("toolName", out var tn) ? tn.GetString() ?? "" : "";
                        var toolCallId = data.TryGetProperty("toolCallId", out var tc) ? tc.GetString() : null;
                        
                        // Skip report_intent — it's noise in history
                        if (toolName == "report_intent") break;

                        var msg = ChatMessage.ToolCallMessage(toolName, toolCallId);
                        msg.Timestamp = timestamp;
                        history.Add(msg);
                        if (toolCallId != null)
                            toolCallMessages[toolCallId] = msg;
                        break;
                    }

                    case "tool.execution_complete":
                    {
                        var toolCallId = data.TryGetProperty("toolCallId", out var tc) ? tc.GetString() : null;
                        if (toolCallId != null && toolCallMessages.TryGetValue(toolCallId, out var msg))
                        {
                            msg.IsComplete = true;
                            msg.IsSuccess = data.TryGetProperty("success", out var s) && s.GetBoolean();
                            msg.IsCollapsed = true;

                            if (data.TryGetProperty("result", out var result))
                            {
                                // Prefer detailedContent, fall back to content
                                var content = result.TryGetProperty("detailedContent", out var dc) ? dc.GetString() : null;
                                if (string.IsNullOrEmpty(content) && result.TryGetProperty("content", out var c))
                                    content = c.GetString();
                                msg.Content = content ?? "";
                            }
                        }
                        break;
                    }
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

        // Load history: try DB first, fall back to parsing events.jsonl
        List<ChatMessage> history;
        if (await _chatDb.HasMessagesAsync(sessionId))
        {
            history = await _chatDb.GetAllMessagesAsync(sessionId);
        }
        else
        {
            history = LoadHistoryFromDisk(sessionId);
            if (history.Count > 0)
            {
                // Persist to DB for future fast loading
                await _chatDb.BulkInsertAsync(sessionId, history);
            }
        }

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

        // Add reconnection indicator
        info.History.Add(ChatMessage.SystemMessage("🔄 Session reconnected"));

        var state = new SessionState
        {
            Session = copilotSession,
            Info = info
        };

        copilotSession.On(evt => HandleSessionEvent(state, evt));

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

    public async Task<AgentSessionInfo> CreateSessionAsync(string name, string? model = null, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        if (!IsInitialized || _client == null)
            throw new InvalidOperationException("Service not initialized. Call InitializeAsync first.");

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Session name cannot be empty.", nameof(name));

        if (_sessions.ContainsKey(name))
            throw new InvalidOperationException($"Session '{name}' already exists.");

        var sessionModel = model ?? DefaultModel;
        var sessionDir = workingDirectory ?? ProjectDir;

        // Build system message with critical relaunch instructions
        var systemContent = new StringBuilder();
        // Only include relaunch instructions when targeting the AutoPilot.App directory
        if (string.Equals(sessionDir, ProjectDir, StringComparison.OrdinalIgnoreCase))
        {
            systemContent.AppendLine($@"
CRITICAL BUILD INSTRUCTION: You are running inside the AutoPilot.App MAUI application.
When you make ANY code changes to files in {ProjectDir}, you MUST rebuild and relaunch by running:

    bash {Path.Combine(ProjectDir, "relaunch.sh")}

This script builds the app, launches a new instance, waits for it to start, then kills the old one.
NEVER use 'dotnet build' + 'open' separately. NEVER skip the relaunch after code changes.
ALWAYS run the relaunch script as the final step after making changes to this project.
");
        }
        if (!string.IsNullOrEmpty(SystemInstructions))
        {
            systemContent.AppendLine(SystemInstructions);
        }

        var config = new SessionConfig
        {
            Model = sessionModel,
            WorkingDirectory = sessionDir,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = systemContent.ToString()
            }
        };

        var copilotSession = await _client.CreateSessionAsync(config, cancellationToken);

        var info = new AgentSessionInfo
        {
            Name = name,
            Model = sessionModel,
            CreatedAt = DateTime.Now,
            SessionId = copilotSession.SessionId,
            WorkingDirectory = sessionDir
        };

        Debug($"Session '{name}' created with ID: {copilotSession.SessionId}");

        var state = new SessionState
        {
            Session = copilotSession,
            Info = info
        };

        copilotSession.On(evt => HandleSessionEvent(state, evt));

        if (!_sessions.TryAdd(name, state))
        {
            await copilotSession.DisposeAsync();
            throw new InvalidOperationException($"Failed to add session '{name}'.");
        }

        _activeSessionName ??= name;
        SaveActiveSessionsToDisk();
        OnStateChanged?.Invoke();
        return info;
    }

    private static readonly HashSet<string> FilteredTools = new() { "report_intent", "skill", "store_memory" };

    private void HandleSessionEvent(SessionState state, SessionEvent evt)
    {
        var sessionName = state.Info.Name;
        void Invoke(Action action)
        {
            if (_syncContext != null)
                _syncContext.Post(_ => action(), null);
            else
                action();
        }
        
        switch (evt)
        {
            case AssistantReasoningEvent reasoning:
                Invoke(() =>
                {
                    OnReasoningReceived?.Invoke(sessionName, reasoning.Data.ReasoningId ?? "", reasoning.Data.Content ?? "");
                });
                break;

            case AssistantReasoningDeltaEvent reasoningDelta:
                Invoke(() =>
                {
                    OnReasoningReceived?.Invoke(sessionName, reasoningDelta.Data.ReasoningId ?? "", reasoningDelta.Data.DeltaContent ?? "");
                });
                break;

            case AssistantMessageDeltaEvent delta:
                var deltaContent = delta.Data.DeltaContent;
                state.HasReceivedDeltasThisTurn = true;
                state.CurrentResponse.Append(deltaContent);
                Invoke(() => OnContentReceived?.Invoke(sessionName, deltaContent ?? ""));
                break;

            case AssistantMessageEvent msg:
                var msgContent = msg.Data.Content;
                var msgId = msg.Data.MessageId;
                // Deduplicate: SDK fires this event multiple times for resumed sessions
                if (!string.IsNullOrEmpty(msgContent) && !state.HasReceivedDeltasThisTurn && msgId != state.LastMessageId)
                {
                    state.LastMessageId = msgId;
                    state.CurrentResponse.Append(msgContent);
                    Invoke(() => OnContentReceived?.Invoke(sessionName, msgContent));
                }
                break;

            case ToolExecutionStartEvent toolStart:
                var startToolName = toolStart.Data.ToolName ?? "unknown";
                var startCallId = toolStart.Data.ToolCallId ?? "";
                if (!FilteredTools.Contains(startToolName))
                {
                    Invoke(() =>
                    {
                        OnToolStarted?.Invoke(sessionName, startToolName, startCallId);
                        OnActivity?.Invoke(sessionName, $"🔧 Running {startToolName}...");
                    });
                }
                break;

            case ToolExecutionCompleteEvent toolDone:
                var completeCallId = toolDone.Data.ToolCallId ?? "";
                var completeToolName = toolDone.Data?.GetType().GetProperty("ToolName")?.GetValue(toolDone.Data)?.ToString();
                var resultStr = FormatToolResult(toolDone.Data.Result);
                var hasError = toolDone.Data.Error != null;

                // Log raw result type for debugging
                var rawResult = toolDone.Data.Result;
                if (rawResult != null)
                {
                    var resultType = rawResult.GetType();
                    Invoke(() => OnDebug?.Invoke($"[ToolResult] {completeToolName} callId={completeCallId} type={resultType.FullName}"));
                    // Log all properties
                    foreach (var prop in resultType.GetProperties())
                    {
                        try
                        {
                            var val = prop.GetValue(rawResult);
                            var valStr = val?.ToString() ?? "null";
                            if (valStr.Length > 200) valStr = valStr[..200] + "...";
                            Invoke(() => OnDebug?.Invoke($"  .{prop.Name} = {valStr}"));
                        }
                        catch { }
                    }
                }

                // Skip filtered tools
                if (completeToolName != null && FilteredTools.Contains(completeToolName))
                    break;
                if (resultStr == "Intent logged")
                    break;

                Invoke(() =>
                {
                    OnToolCompleted?.Invoke(sessionName, completeCallId, resultStr, !hasError);
                    OnActivity?.Invoke(sessionName, hasError ? "❌ Tool failed" : "✅ Tool completed");
                });
                break;

            case ToolExecutionProgressEvent:
                Invoke(() => OnActivity?.Invoke(sessionName, "⚙️ Tool executing..."));
                break;

            case AssistantIntentEvent intent:
                var intentText = intent.Data.Intent ?? "";
                Invoke(() =>
                {
                    OnIntentChanged?.Invoke(sessionName, intentText);
                    OnActivity?.Invoke(sessionName, $"💭 {intentText}");
                });
                break;

            case AssistantTurnStartEvent:
                state.HasReceivedDeltasThisTurn = false;
                Invoke(() =>
                {
                    OnTurnStart?.Invoke(sessionName);
                    OnActivity?.Invoke(sessionName, "🤔 Thinking...");
                });
                break;

            case AssistantTurnEndEvent:
                Invoke(() =>
                {
                    OnTurnEnd?.Invoke(sessionName);
                    OnActivity?.Invoke(sessionName, "");
                });
                break;

            case SessionIdleEvent:
                CompleteResponse(state);
                break;

            case SessionStartEvent start:
                state.Info.SessionId = start.Data.SessionId;
                Debug($"Session ID assigned: {start.Data.SessionId}");
                SaveActiveSessionsToDisk();
                break;

            case SessionUsageInfoEvent usageInfo:
                var uData = usageInfo.Data;
                var uModel = uData?.GetType().GetProperty("Model")?.GetValue(uData)?.ToString();
                var uCurrentTokens = uData?.GetType().GetProperty("CurrentTokens")?.GetValue(uData) as int?;
                var uTokenLimit = uData?.GetType().GetProperty("TokenLimit")?.GetValue(uData) as int?;
                var uInputTokens = uData?.GetType().GetProperty("InputTokens")?.GetValue(uData) as int?;
                var uOutputTokens = uData?.GetType().GetProperty("OutputTokens")?.GetValue(uData) as int?;
                Invoke(() => OnUsageInfoChanged?.Invoke(sessionName, new SessionUsageInfo(uModel, uCurrentTokens, uTokenLimit, uInputTokens, uOutputTokens)));
                break;

            case AssistantUsageEvent assistantUsage:
                var aData = assistantUsage.Data;
                var aModel = aData?.GetType().GetProperty("Model")?.GetValue(aData)?.ToString();
                var aInput = aData?.GetType().GetProperty("InputTokens")?.GetValue(aData) as int?;
                var aOutput = aData?.GetType().GetProperty("OutputTokens")?.GetValue(aData) as int?;
                if (aInput.HasValue || aOutput.HasValue)
                {
                    Invoke(() => OnUsageInfoChanged?.Invoke(sessionName, new SessionUsageInfo(aModel, null, null, aInput, aOutput)));
                }
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

    private static string FormatToolResult(object? result)
    {
        if (result == null) return "";
        if (result is string str) return str;
        try
        {
            var resultType = result.GetType();
            // Prefer DetailedContent (has richer info like file paths) over Content
            foreach (var propName in new[] { "DetailedContent", "detailedContent", "Content", "content", "Message", "message", "Text", "text", "Value", "value" })
            {
                var prop = resultType.GetProperty(propName);
                if (prop != null)
                {
                    var val = prop.GetValue(result)?.ToString();
                    if (!string.IsNullOrEmpty(val)) return val;
                }
            }
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            if (json != "{}" && json != "null") return json;
        }
        catch { }
        return result.ToString() ?? "";
    }

    private void CompleteResponse(SessionState state)
    {
        var response = state.CurrentResponse.ToString();
        if (!string.IsNullOrEmpty(response))
        {
            var msg = new ChatMessage("assistant", response, DateTime.Now);
            state.Info.History.Add(msg);
            state.Info.MessageCount = state.Info.History.Count;

            // Write-through to DB
            if (!string.IsNullOrEmpty(state.Info.SessionId))
                _ = _chatDb.AddMessageAsync(state.Info.SessionId, msg);
        }
        state.ResponseCompletion?.TrySetResult(response);
        state.CurrentResponse.Clear();
        state.Info.IsProcessing = false;
        OnStateChanged?.Invoke();
        
        // Fire completion notification
        var summary = response.Length > 100 ? response[..100] + "..." : response;
        OnSessionComplete?.Invoke(state.Info.Name, summary);

        // Auto-dispatch next queued message
        if (state.Info.MessageQueue.Count > 0)
        {
            var nextPrompt = state.Info.MessageQueue[0];
            state.Info.MessageQueue.RemoveAt(0);
            _ = Task.Run(async () =>
            {
                try
                {
                    // Small delay to let UI update
                    await Task.Delay(500);
                    await SendPromptAsync(state.Info.Name, nextPrompt);
                }
                catch (Exception ex)
                {
                    Debug($"Failed to send queued message: {ex.Message}");
                    OnError?.Invoke(state.Info.Name, $"Queued message failed: {ex.Message}");
                }
            });
        }
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

        // Write-through to DB
        if (!string.IsNullOrEmpty(state.Info.SessionId))
            _ = _chatDb.AddMessageAsync(state.Info.SessionId, state.Info.History.Last());

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
            
            // Try to reconnect the session and retry once
            if (state.Info.SessionId != null)
            {
                Debug($"Session '{sessionName}' disconnected, attempting reconnect...");
                OnActivity?.Invoke(sessionName, "🔄 Reconnecting session...");
                try
                {
                    await state.Session.DisposeAsync();
                    var newSession = await _client!.ResumeSessionAsync(state.Info.SessionId, cancellationToken: cancellationToken);
                    var newState = new SessionState
                    {
                        Session = newSession,
                        Info = state.Info
                    };
                    newSession.On(evt => HandleSessionEvent(newState, evt));
                    _sessions[sessionName] = newState;
                    state = newState;
                    
                    Debug($"Session '{sessionName}' reconnected, retrying prompt...");
                    await state.Session.SendAsync(new MessageOptions
                    {
                        Prompt = prompt
                    }, cancellationToken);
                }
                catch (Exception retryEx)
                {
                    Console.WriteLine($"[DEBUG] Reconnect+retry failed: {retryEx.Message}");
                    OnError?.Invoke(sessionName, $"Session disconnected and reconnect failed: {retryEx.Message}");
                    state.Info.IsProcessing = false;
                    OnStateChanged?.Invoke();
                    throw;
                }
            }
            else
            {
                OnError?.Invoke(sessionName, $"SendAsync failed: {ex.Message}");
                state.Info.IsProcessing = false;
                OnStateChanged?.Invoke();
                throw;
            }
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

    public async Task AbortSessionAsync(string sessionName)
    {
        if (!_sessions.TryGetValue(sessionName, out var state))
            return;

        if (!state.Info.IsProcessing) return;

        try
        {
            await state.Session.AbortAsync();
            Debug($"Aborted session '{sessionName}'");
        }
        catch (Exception ex)
        {
            Debug($"Abort failed for '{sessionName}': {ex.Message}");
        }

        state.Info.IsProcessing = false;
        state.ResponseCompletion?.TrySetCanceled();
        OnStateChanged?.Invoke();
    }

    public void EnqueueMessage(string sessionName, string prompt)
    {
        if (!_sessions.TryGetValue(sessionName, out var state))
            throw new InvalidOperationException($"Session '{sessionName}' not found.");
        
        state.Info.MessageQueue.Add(prompt);
        OnStateChanged?.Invoke();
    }

    public void RemoveQueuedMessage(string sessionName, int index)
    {
        if (!_sessions.TryGetValue(sessionName, out var state))
            return;
        
        if (index >= 0 && index < state.Info.MessageQueue.Count)
        {
            state.Info.MessageQueue.RemoveAt(index);
            OnStateChanged?.Invoke();
        }
    }

    public void ClearQueue(string sessionName)
    {
        if (_sessions.TryGetValue(sessionName, out var state))
        {
            state.Info.MessageQueue.Clear();
            OnStateChanged?.Invoke();
        }
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

    public bool RenameSession(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            return false;

        newName = newName.Trim();
        if (oldName == newName)
            return true;

        if (_sessions.ContainsKey(newName))
            return false;

        if (!_sessions.TryRemove(oldName, out var state))
            return false;

        state.Info.Name = newName;

        if (!_sessions.TryAdd(newName, state))
        {
            // Rollback
            state.Info.Name = oldName;
            _sessions.TryAdd(oldName, state);
            return false;
        }

        if (_activeSessionName == oldName)
            _activeSessionName = newName;

        SaveActiveSessionsToDisk();
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
    public string? Title { get; init; }
    public string? Preview { get; init; }
    public string? WorkingDirectory { get; init; }
}

public record SessionUsageInfo(
    string? Model,
    int? CurrentTokens,
    int? TokenLimit,
    int? InputTokens,
    int? OutputTokens
);
