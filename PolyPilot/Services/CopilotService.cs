using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PolyPilot.Models;
using GitHub.Copilot.SDK;

namespace PolyPilot.Services;

public partial class CopilotService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new();
    // Sessions optimistically added during remote create/resume — protected from removal by SyncRemoteSessions
    private readonly ConcurrentDictionary<string, byte> _pendingRemoteSessions = new();
    private readonly ChatDatabase _chatDb;
    private readonly ServerManager _serverManager;
    private readonly WsBridgeClient _bridgeClient;
    private readonly DemoService _demoService;
    private readonly ILogger<CopilotService> _logger;
    private CopilotClient? _client;
    private string? _activeSessionName;
    private SynchronizationContext? _syncContext;
    
    private static string? _copilotBaseDir;
    private static string CopilotBaseDir => _copilotBaseDir ??= GetCopilotBaseDir();

    private static string GetCopilotBaseDir()
    {
        try
        {
#if ANDROID
            var home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(home))
                home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            if (string.IsNullOrEmpty(home))
                home = Android.App.Application.Context.FilesDir?.AbsolutePath ?? Path.GetTempPath();
            return Path.Combine(home, ".copilot");
#else
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
                home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(home))
                home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            return Path.Combine(home, ".copilot");
#endif
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), ".copilot");
        }
    }

    private static string? _sessionStatePath;
    private static string SessionStatePath => _sessionStatePath ??= Path.Combine(CopilotBaseDir, "session-state");

    private static string? _activeSessionsFile;
    private static string ActiveSessionsFile => _activeSessionsFile ??= Path.Combine(CopilotBaseDir, "PolyPilot-active-sessions.json");

    private static string? _sessionAliasesFile;
    private static string SessionAliasesFile => _sessionAliasesFile ??= Path.Combine(CopilotBaseDir, "PolyPilot-session-aliases.json");

    private static string? _uiStateFile;
    private static string UiStateFile => _uiStateFile ??= Path.Combine(CopilotBaseDir, "PolyPilot-ui-state.json");

    private static string? _organizationFile;
    private static string OrganizationFile => _organizationFile ??= Path.Combine(CopilotBaseDir, "PolyPilot-organization.json");

    private static string? _projectDir;
    private static string ProjectDir => _projectDir ??= FindProjectDir();

    private static string FindProjectDir()
    {
        try
        {
            // Walk up from the base directory to find the .csproj (works from bin/Debug/... at runtime)
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            if (string.IsNullOrEmpty(dir)) return CopilotBaseDir;
            for (int i = 0; i < 10; i++)
            {
                if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.csproj").Length > 0)
                    return dir;
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
        }
        catch { }
        // Fallback
        return CopilotBaseDir;
    }

    public string DefaultModel { get; set; } = "claude-opus-4.6";
    public string? SystemInstructions { get; set; }
    public bool IsInitialized { get; private set; }
    public bool IsRestoring { get; private set; }
    public bool NeedsConfiguration { get; private set; }
    public bool IsRemoteMode { get; private set; }
    public bool IsBridgeConnected => _bridgeClient.IsConnected;
    public bool IsDemoMode { get; private set; }
    public string? ActiveSessionName => _activeSessionName;
    public ChatDatabase ChatDb => _chatDb;
    public ConnectionMode CurrentMode { get; private set; } = ConnectionMode.Embedded;
    public List<string> AvailableModels { get; private set; } = new();

    public CopilotService(ChatDatabase chatDb, ServerManager serverManager, WsBridgeClient bridgeClient, ILogger<CopilotService> logger)
    {
        _chatDb = chatDb;
        _serverManager = serverManager;
        _bridgeClient = bridgeClient;
        _demoService = new DemoService();
        _logger = logger;
    }

    // Debug info
    public string LastDebugMessage { get; private set; } = "";

    // GitHub user info
    public string? GitHubAvatarUrl { get; private set; }
    public string? GitHubLogin { get; private set; }

    // UI preferences
    public ChatLayout ChatLayout { get; set; } = ChatLayout.Default;
    public UiTheme Theme { get; set; } = UiTheme.PolyPilotDark;

    // Session organization (groups, pinning, sorting)
    public OrganizationState Organization { get; private set; } = new();

    public event Action? OnStateChanged;
    public event Action<string, string>? OnContentReceived; // sessionName, content
    public event Action<string, string>? OnError; // sessionName, error
    public event Action<string, string>? OnSessionComplete; // sessionName, summary
    public event Action<string, string>? OnActivity; // sessionName, activity description
    public event Action<string>? OnDebug; // debug messages

    // Rich event types
    public event Action<string, string, string, string?>? OnToolStarted; // sessionName, toolName, callId, inputSummary
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
        public bool HasReceivedEventsSinceResume { get; set; }
        public string? LastMessageId { get; set; }
    }

    private void Debug(string message)
    {
        LastDebugMessage = message;
        Console.WriteLine($"[DEBUG] {message}");
        _logger.LogInformation("[CopilotService] {Message}", message);
        OnDebug?.Invoke(message);
    }

    private void InvokeOnUI(Action action)
    {
        if (_syncContext != null)
            _syncContext.Post(_ => action(), null);
        else
            action();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsInitialized) return;

        // Capture the sync context for marshaling events back to UI thread
        _syncContext = SynchronizationContext.Current;
        Debug($"SyncContext captured: {_syncContext?.GetType().Name ?? "null"}");

        var settings = ConnectionSettings.Load();
        CurrentMode = settings.Mode;
        ChatLayout = settings.ChatLayout;
        Theme = settings.Theme;

        // On mobile with Remote mode and no URL configured, skip initialization
        if (settings.Mode == ConnectionMode.Remote && string.IsNullOrWhiteSpace(settings.RemoteUrl))
        {
            Debug("Remote mode with no URL configured — waiting for settings");
            NeedsConfiguration = true;
            OnStateChanged?.Invoke();
            return;
        }

        // Remote mode: connect via WsBridgeClient (state-sync, not CopilotClient)
        if (settings.Mode == ConnectionMode.Remote && !string.IsNullOrWhiteSpace(settings.RemoteUrl))
        {
            await InitializeRemoteAsync(settings, cancellationToken);
            return;
        }

        // Demo mode: local mock responses, no network needed
        if (settings.Mode == ConnectionMode.Demo)
        {
            InitializeDemo();
            return;
        }

#if ANDROID
        // Android can't run Copilot CLI locally — must connect to remote server
        settings.Mode = ConnectionMode.Persistent;
        CurrentMode = ConnectionMode.Persistent;
        if (settings.Host == "localhost" || settings.Host == "127.0.0.1")
        {
            Debug("Android detected with localhost — update Host in settings to your Mac's IP");
        }
        Debug($"Android: connecting to remote server at {settings.CliUrl}");
#endif
        // Persistent mode on desktop uses the same embedded SDK client as Embedded mode,
        // but sessions are persisted to disk and restored on app restart.
        // The headless server (CliUrl) approach has protocol compatibility issues
        // with the current SDK version, so we use embedded mode universally on desktop.
        Debug($"Creating CopilotClient for mode={settings.Mode}...");
        _client = CreateClient(settings);
        Debug("CopilotClient created successfully, calling StartAsync...");

        try
        {
            await _client.StartAsync(cancellationToken);
            IsInitialized = true;
            NeedsConfiguration = false;
            Debug($"Copilot client started in {settings.Mode} mode");
        }
        catch (Exception ex)
        {
            Debug($"CopilotClient.StartAsync FAILED: {ex.GetType().Name}: {ex.Message}");
            _logger.LogError(ex, "CopilotClient.StartAsync failed");
            throw;
        }

        // Load default system instructions from the project's copilot-instructions.md
        var instructionsPath = Path.Combine(ProjectDir, ".github", "copilot-instructions.md");
        if (File.Exists(instructionsPath) && string.IsNullOrEmpty(SystemInstructions))
        {
            SystemInstructions = await File.ReadAllTextAsync(instructionsPath, cancellationToken);
            Debug("Loaded system instructions from copilot-instructions.md");
        }

        OnStateChanged?.Invoke();

        // Fetch available models dynamically
        _ = FetchAvailableModelsAsync();

        // Fetch GitHub user info for avatar
        _ = FetchGitHubUserInfoAsync();

        // Load organization state FIRST (groups, pinning, sorting) so reconcile during restore doesn't wipe it
        LoadOrganization();

        // Restore previous sessions (includes subscribing to untracked server sessions in Persistent mode)
        IsRestoring = true;
        OnStateChanged?.Invoke();
        await RestorePreviousSessionsAsync(cancellationToken);
        IsRestoring = false;

        // Reconcile now that all sessions are restored
        ReconcileOrganization();
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Initialize in Demo mode: wire up DemoService events for local mock responses.
    /// </summary>
    private void InitializeDemo()
    {
        Debug("Demo mode: initializing with mock responses");

        _demoService.OnStateChanged += () => InvokeOnUI(() => OnStateChanged?.Invoke());
        _demoService.OnContentReceived += (s, c) =>
        {
            // Accumulate response in SessionState for history
            if (_sessions.TryGetValue(s, out var state))
                state.CurrentResponse.Append(c);
            InvokeOnUI(() => OnContentReceived?.Invoke(s, c));
        };
        _demoService.OnToolStarted += (s, tool, id) =>
        {
            if (_sessions.TryGetValue(s, out var state))
            {
                FlushCurrentResponse(state);
                state.Info.History.Add(ChatMessage.ToolCallMessage(tool, id));
            }
            InvokeOnUI(() => OnToolStarted?.Invoke(s, tool, id, null));
        };
        _demoService.OnToolCompleted += (s, id, result, success) =>
        {
            if (_sessions.TryGetValue(s, out var state))
            {
                var toolMsg = state.Info.History.LastOrDefault(m => m.ToolCallId == id);
                if (toolMsg != null) { toolMsg.IsComplete = true; toolMsg.IsSuccess = success; toolMsg.Content = result; }
            }
            InvokeOnUI(() => OnToolCompleted?.Invoke(s, id, result, success));
        };
        _demoService.OnIntentChanged += (s, i) => InvokeOnUI(() => OnIntentChanged?.Invoke(s, i));
        _demoService.OnTurnStart += (s) => InvokeOnUI(() => OnTurnStart?.Invoke(s));
        _demoService.OnTurnEnd += (s) =>
        {
            // Flush accumulated response into history (mirrors CompleteResponse)
            if (_sessions.TryGetValue(s, out var state))
            {
                CompleteResponse(state);
            }
            InvokeOnUI(() => OnTurnEnd?.Invoke(s));
        };

        IsInitialized = true;
        IsDemoMode = true;
        NeedsConfiguration = false;
        Debug("Demo mode initialized");
        OnStateChanged?.Invoke();
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
            try { if (state.Session != null) await state.Session.DisposeAsync(); } catch { }
        }
        _sessions.Clear();
        _activeSessionName = null;

        if (_client != null)
        {
            try { await _client.DisposeAsync(); } catch { }
            _client = null;
        }
        _bridgeClient.Stop();

        IsInitialized = false;
        IsRemoteMode = false;
        IsDemoMode = false;
        CurrentMode = settings.Mode;
        OnStateChanged?.Invoke();

        // Demo mode: local mock responses
        if (settings.Mode == ConnectionMode.Demo)
        {
            InitializeDemo();
            return;
        }

        // Remote mode uses WsBridgeClient state-sync
        if (settings.Mode == ConnectionMode.Remote && !string.IsNullOrWhiteSpace(settings.RemoteUrl))
        {
            await InitializeRemoteAsync(settings, cancellationToken);
            return;
        }

        _client = CreateClient(settings);

        await _client.StartAsync(cancellationToken);
        IsInitialized = true;
        NeedsConfiguration = false;
        Debug($"Reconnected in {settings.Mode} mode");
        OnStateChanged?.Invoke();

        // Restore previous sessions
        await RestorePreviousSessionsAsync(cancellationToken);
    }

    private CopilotClient CreateClient(ConnectionSettings settings)
    {
        // Both Embedded and Persistent modes use the default SDK client (stdio).
        // Persistent mode differs only in that sessions are saved/restored from disk.
        // Remote mode is handled by InitializeRemoteAsync, not here.
        var options = new CopilotClientOptions
        {
            CliPath = "copilot",
            Cwd = ProjectDir
        };

        // Pass additional MCP server configs via CLI args.
        // The CLI auto-reads ~/.copilot/mcp-config.json, but mcp-servers.json
        // uses a different format that needs to be passed explicitly.
        var mcpArgs = GetMcpCliArgs();
        if (mcpArgs.Length > 0)
            options.CliArgs = mcpArgs;

        return new CopilotClient(options);
    }

    /// <summary>
    /// Build CLI args to pass additional MCP server configs.
    /// Reads ~/.copilot/mcp-servers.json (simple format) and converts
    /// to the --additional-mcp-config format the CLI expects.
    /// </summary>
    internal static string[] GetMcpCliArgs()
    {
        var args = new List<string>();
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var copilotDir = Path.Combine(home, ".copilot");
            var merged = new Dictionary<string, JsonElement>();

            // Read ~/.copilot/mcp-servers.json (simple format without mcpServers wrapper)
            try
            {
                var serversPath = Path.Combine(copilotDir, "mcp-servers.json");
                if (File.Exists(serversPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(serversPath));
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        merged[prop.Name] = prop.Value.Clone();
                }
            }
            catch { }

            // Read plugin .mcp.json files (mcpServers wrapped format)
            try
            {
                var pluginsDir = Path.Combine(copilotDir, "installed-plugins");
                if (Directory.Exists(pluginsDir))
                {
                    foreach (var marketDir in Directory.GetDirectories(pluginsDir))
                    {
                        foreach (var pluginDir in Directory.GetDirectories(marketDir))
                        {
                            var mcpFile = Path.Combine(pluginDir, ".mcp.json");
                            if (!File.Exists(mcpFile)) continue;
                            try
                            {
                                using var doc = JsonDocument.Parse(File.ReadAllText(mcpFile));
                                if (doc.RootElement.TryGetProperty("mcpServers", out var servers))
                                {
                                    foreach (var prop in servers.EnumerateObject())
                                        merged.TryAdd(prop.Name, prop.Value.Clone());
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            if (merged.Count == 0) return args.ToArray();

            // Write single merged file with mcpServers envelope
            var tempPath = Path.Combine(copilotDir, "polypilot-mcp-servers.json");
            using var stream = File.Create(tempPath);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            writer.WritePropertyName("mcpServers");
            writer.WriteStartObject();
            foreach (var (name, value) in merged)
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.Flush();

            args.Add("--additional-mcp-config");
            args.Add($"@{tempPath}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MCP] Failed to build MCP config: {ex.Message}");
        }

        return args.ToArray();
    }

    /// <summary>
    /// Resume an existing session by its GUID
    /// </summary>
    public async Task<AgentSessionInfo> ResumeSessionAsync(string sessionId, string displayName, CancellationToken cancellationToken = default)
    {
        // In remote mode, delegate to WsBridgeClient
        if (IsRemoteMode)
        {
            var remoteInfo = new AgentSessionInfo { Name = displayName, SessionId = sessionId, Model = "resumed" };
            // Set up optimistic state BEFORE sending bridge message to prevent race with SyncRemoteSessions
            _pendingRemoteSessions[displayName] = 0;
            _sessions[displayName] = new SessionState { Session = null!, Info = remoteInfo };
            if (!Organization.Sessions.Any(m => m.SessionName == displayName))
                Organization.Sessions.Add(new SessionMeta { SessionName = displayName, GroupId = SessionGroup.DefaultId });
            _activeSessionName = displayName;
            OnStateChanged?.Invoke();
            // Now send the bridge message — server may respond before this returns
            await _bridgeClient.ResumeSessionAsync(sessionId, displayName, cancellationToken);
            _ = Task.Delay(30_000).ContinueWith(t => { _pendingRemoteSessions.TryRemove(displayName, out _); });
            return remoteInfo;
        }

        if (!IsInitialized || _client == null)
            throw new InvalidOperationException("Service not initialized. Call InitializeAsync first.");

        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));

        if (!Guid.TryParse(sessionId, out _))
            throw new ArgumentException("Session ID must be a valid GUID.", nameof(sessionId));

        if (_sessions.ContainsKey(displayName))
            throw new InvalidOperationException($"Session '{displayName}' already exists.");

        // Load history: always parse events.jsonl as source of truth, then sync to DB
        List<ChatMessage> history = LoadHistoryFromDisk(sessionId);
        if (history.Count > 0)
        {
            // Replace DB contents with fresh parse (events.jsonl may have grown since last DB sync)
            await _chatDb.BulkInsertAsync(sessionId, history);
        }

        // Resume the session using the SDK
        var copilotSession = await _client.ResumeSessionAsync(sessionId, cancellationToken: cancellationToken);

        var info = new AgentSessionInfo
        {
            Name = displayName,
            Model = "resumed", // Model info may not be immediately available
            CreatedAt = DateTime.Now,
            SessionId = sessionId,
            IsResumed = true,
            WorkingDirectory = GetSessionWorkingDirectory(sessionId)
        };
        info.GitBranch = GetGitBranch(info.WorkingDirectory);

        // Add loaded history to the session info
        foreach (var msg in history)
        {
            info.History.Add(msg);
        }
        info.MessageCount = info.History.Count;

        // Mark any stale incomplete tool calls as complete (from prior session)
        foreach (var msg in info.History.Where(m => m.MessageType == ChatMessageType.ToolCall && !m.IsComplete))
        {
            msg.IsComplete = true;
        }
        // Also mark incomplete reasoning as complete
        foreach (var msg in info.History.Where(m => m.MessageType == ChatMessageType.Reasoning && !m.IsComplete))
        {
            msg.IsComplete = true;
        }

        // Add reconnection indicator with status context
        var reconnectMsg = $"🔄 Session reconnected at {DateTime.Now:h:mm tt}";
        var isStillProcessing = IsSessionStillProcessing(sessionId);
        if (isStillProcessing)
        {
            var (lastTool, lastContent) = GetLastSessionActivity(sessionId);
            if (!string.IsNullOrEmpty(lastTool))
                reconnectMsg += $" — running {lastTool}";
            if (!string.IsNullOrEmpty(lastContent))
                reconnectMsg += $"\n💬 Last: {(lastContent.Length > 100 ? lastContent[..100] + "…" : lastContent)}";
        }
        info.History.Add(ChatMessage.SystemMessage(reconnectMsg));

        // Set processing state if session was mid-turn when app died
        info.IsProcessing = isStillProcessing;

        var state = new SessionState
        {
            Session = copilotSession,
            Info = info
        };

        // If still processing, set up ResponseCompletion so events flow properly
        // but add a timeout — if no new events arrive, the old turn is gone
        if (isStillProcessing)
        {
            state.ResponseCompletion = new TaskCompletionSource<string>();
            Debug($"Session '{displayName}' is still processing (was mid-turn when app restarted)");

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                if (state.Info.IsProcessing && !state.HasReceivedEventsSinceResume)
                {
                    Debug($"Session '{displayName}' processing timeout — no new events after resume, clearing stale state");
                    state.Info.IsProcessing = false;
                    state.ResponseCompletion?.TrySetResult("timeout");
                    state.Info.History.Add(ChatMessage.SystemMessage("⏹ Previous turn appears to have ended. Ready for new input."));
                    InvokeOnUI(() => OnStateChanged?.Invoke());
                }
            });
        }

        copilotSession.On(evt => HandleSessionEvent(state, evt));

        if (!_sessions.TryAdd(displayName, state))
        {
            await copilotSession.DisposeAsync();
            throw new InvalidOperationException($"Failed to add session '{displayName}'.");
        }

        _activeSessionName ??= displayName;
        OnStateChanged?.Invoke();
        SaveActiveSessionsToDisk();
        if (!IsRestoring) ReconcileOrganization();
        return info;
    }

    public async Task<AgentSessionInfo> CreateSessionAsync(string name, string? model = null, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        // In demo mode, create a local mock session
        if (IsDemoMode)
        {
            var demoInfo = _demoService.CreateSession(name, model);
            var demoState = new SessionState { Session = null!, Info = demoInfo };
            _sessions[name] = demoState;
            _activeSessionName ??= name;
            OnStateChanged?.Invoke();
            return demoInfo;
        }

        // In remote mode, delegate to WsBridgeClient
        if (IsRemoteMode)
        {
            var remoteInfo = new AgentSessionInfo { Name = name, Model = model ?? "claude-sonnet-4-20250514" };
            // Set up optimistic state BEFORE sending bridge message to prevent race with SyncRemoteSessions
            _pendingRemoteSessions[name] = 0;
            _sessions[name] = new SessionState { Session = null!, Info = remoteInfo };
            if (!Organization.Sessions.Any(m => m.SessionName == name))
                Organization.Sessions.Add(new SessionMeta { SessionName = name, GroupId = SessionGroup.DefaultId });
            _activeSessionName = name;
            OnStateChanged?.Invoke();
            await _bridgeClient.CreateSessionAsync(name, model, workingDirectory, cancellationToken);
            _ = Task.Delay(30_000).ContinueWith(t => { _pendingRemoteSessions.TryRemove(name, out _); });
            return remoteInfo;
        }

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
        // Only include relaunch instructions when targeting the PolyPilot directory
        if (string.Equals(sessionDir, ProjectDir, StringComparison.OrdinalIgnoreCase))
        {
            systemContent.AppendLine($@"
CRITICAL BUILD INSTRUCTION: You are running inside the PolyPilot MAUI application.
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
            WorkingDirectory = sessionDir,
            GitBranch = GetGitBranch(sessionDir)
        };

        Debug($"Session '{name}' created with ID: {copilotSession.SessionId}");

        // Save alias so saved sessions show the custom name
        if (!string.IsNullOrEmpty(copilotSession.SessionId))
            SetSessionAlias(copilotSession.SessionId, name);

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
        ReconcileOrganization();
        OnStateChanged?.Invoke();
        return info;
    }

    public async Task<string> SendPromptAsync(string sessionName, string prompt, List<string>? imagePaths = null, CancellationToken cancellationToken = default)
    {
        // In demo mode, simulate a response locally
        if (IsDemoMode)
        {
            if (!_sessions.TryGetValue(sessionName, out var demoState))
                throw new InvalidOperationException($"Session '{sessionName}' not found.");
            demoState.Info.History.Add(ChatMessage.UserMessage(prompt));
            demoState.Info.MessageCount = demoState.Info.History.Count;
            demoState.CurrentResponse.Clear();
            OnStateChanged?.Invoke();
            _ = Task.Run(() => _demoService.SimulateResponseAsync(sessionName, prompt, _syncContext, cancellationToken));
            return "";
        }

        // In remote mode, delegate to WsBridgeClient
        if (IsRemoteMode)
        {
            // Add user message locally for immediate UI feedback
            var session = GetRemoteSession(sessionName);
            if (session != null)
            {
                session.History.Add(ChatMessage.UserMessage(prompt));
                session.IsProcessing = true;
                OnStateChanged?.Invoke();
            }
            await _bridgeClient.SendMessageAsync(sessionName, prompt, cancellationToken);
            return ""; // Response comes via events
        }

        if (!_sessions.TryGetValue(sessionName, out var state))
            throw new InvalidOperationException($"Session '{sessionName}' not found.");

        if (state.Info.IsProcessing)
            throw new InvalidOperationException("Session is already processing a request.");

        state.Info.IsProcessing = true;
        state.ResponseCompletion = new TaskCompletionSource<string>();
        state.CurrentResponse.Clear();

        // Include image paths in history display so FormatUserMessage renders thumbnails
        var displayPrompt = prompt;
        if (imagePaths != null && imagePaths.Count > 0)
            displayPrompt += "\n" + string.Join("\n", imagePaths);
        state.Info.History.Add(new ChatMessage("user", displayPrompt, DateTime.Now));
        state.Info.MessageCount = state.Info.History.Count;
        OnStateChanged?.Invoke();

        // Write-through to DB
        if (!string.IsNullOrEmpty(state.Info.SessionId))
            _ = _chatDb.AddMessageAsync(state.Info.SessionId, state.Info.History.Last());

        Console.WriteLine($"[DEBUG] Sending prompt to session '{sessionName}': {prompt.Substring(0, Math.Min(50, prompt.Length))}...");
        
        try 
        {
            var messageOptions = new MessageOptions { Prompt = prompt };
            
            // Attach images via SDK if available
            if (imagePaths != null && imagePaths.Count > 0)
            {
                TryAttachImages(messageOptions, imagePaths);
            }
            
            await state.Session.SendAsync(messageOptions, cancellationToken);
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
                    if (_client == null)
                        throw new InvalidOperationException("Client is not initialized");
                    var newSession = await _client.ResumeSessionAsync(state.Info.SessionId, cancellationToken: cancellationToken);
                    var newState = new SessionState
                    {
                        Session = newSession,
                        Info = state.Info
                    };
                    newState.ResponseCompletion = state.ResponseCompletion;
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

        if (state.ResponseCompletion == null)
            return ""; // Response already completed via events
        return await state.ResponseCompletion.Task;
    }

    public async Task AbortSessionAsync(string sessionName)
    {
        // In remote mode, delegate to bridge server
        if (IsRemoteMode)
        {
            await _bridgeClient.AbortSessionAsync(sessionName);
            // Optimistically clear processing state
            if (_sessions.TryGetValue(sessionName, out var remoteState))
            {
                remoteState.Info.IsProcessing = false;
                OnStateChanged?.Invoke();
            }
            return;
        }

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

        // Update organization metadata to reflect new name
        var meta = Organization.Sessions.FirstOrDefault(m => m.SessionName == oldName);
        if (meta != null)
            meta.SessionName = newName;

        // Persist alias so saved sessions also show the custom name
        if (state.Info.SessionId != null)
            SetSessionAlias(state.Info.SessionId, newName);

        SaveActiveSessionsToDisk();
        ReconcileOrganization();
        OnStateChanged?.Invoke();
        return true;
    }

    public void SetActiveSession(string? name)
    {
        if (name == null)
        {
            _activeSessionName = null;
            OnStateChanged?.Invoke();
            return;
        }
        if (_sessions.ContainsKey(name))
        {
            _activeSessionName = name;
            if (IsRemoteMode)
                _ = _bridgeClient.SwitchSessionAsync(name);
        }
    }

    public async Task<bool> CloseSessionAsync(string name)
    {
        // In remote mode, send close request to server
        if (_bridgeClient != null && _bridgeClient.IsConnected)
        {
            await _bridgeClient.CloseSessionAsync(name);
        }

        if (!_sessions.TryRemove(name, out var state))
            return false;

        if (state.Session is not null)
            await state.Session.DisposeAsync();

        if (_activeSessionName == name)
        {
            _activeSessionName = _sessions.Keys.FirstOrDefault();
        }

        OnStateChanged?.Invoke();
        SaveActiveSessionsToDisk();
        ReconcileOrganization();
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

    public async ValueTask DisposeAsync()
    {
        SaveActiveSessionsToDisk();
        
        foreach (var state in _sessions.Values)
        {
            if (state.Session is not null)
                await state.Session.DisposeAsync();
        }
        _sessions.Clear();

        if (_client != null)
        {
            await _client.DisposeAsync();
        }
    }
}

public class UiState
{
    public string CurrentPage { get; set; } = "/";
    public string? ActiveSession { get; set; }
    public int FontSize { get; set; } = 20;
    public string? SelectedModel { get; set; }
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
