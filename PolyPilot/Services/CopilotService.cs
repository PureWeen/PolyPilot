using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using PolyPilot.Models;
using GitHub.Copilot.SDK;

namespace PolyPilot.Services;

public partial class CopilotService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new();
    // Sessions optimistically added during remote create/resume — protected from removal by SyncRemoteSessions
    private readonly ConcurrentDictionary<string, byte> _pendingRemoteSessions = new();
    // Old names from optimistic renames — protected from re-addition by SyncRemoteSessions
    private readonly ConcurrentDictionary<string, byte> _pendingRemoteRenames = new();
    // Sessions recently closed in remote mode — prevents SyncRemoteSessions from re-adding them
    // before the server processes the close and removes them from its broadcast
    private readonly ConcurrentDictionary<string, byte> _recentlyClosedRemoteSessions = new();
    // Sessions currently receiving streaming content via bridge events — history sync skipped to avoid duplicates
    private readonly ConcurrentDictionary<string, byte> _remoteStreamingSessions = new();
    // Sessions for which history has already been requested — prevents duplicate request storms
    private readonly ConcurrentDictionary<string, byte> _requestedHistorySessions = new();
    // Session IDs explicitly closed by the user — excluded from merge-back during SaveActiveSessionsToDisk
    private readonly ConcurrentDictionary<string, byte> _closedSessionIds = new();
    private readonly ConcurrentDictionary<string, byte> _closedSessionNames = new();
    // Image paths queued alongside messages when session is busy (keyed by session name, list per queued message)
    private readonly ConcurrentDictionary<string, List<List<string>>> _queuedImagePaths = new();
    private readonly ConcurrentDictionary<string, List<string?>> _queuedAgentModes = new();
    private readonly object _imageQueueLock = new();
    private static readonly object _diagnosticLogLock = new();
    // Debounce timers for disk I/O — coalesce rapid-fire saves into a single write
    private Timer? _saveSessionsDebounce;
    private Timer? _saveOrgDebounce;
    private Timer? _saveUiStateDebounce;
    private UiState? _pendingUiState;
    private readonly object _uiStateLock = new();
    private readonly IChatDatabase _chatDb;
    private readonly IServerManager _serverManager;
    private readonly IWsBridgeClient _bridgeClient;
    private readonly IDemoService _demoService;
    private readonly IServiceProvider? _serviceProvider;
    private readonly UsageStatsService? _usageStats;
    private CopilotClient? _client;
    // Per-codespace-group clients keyed by SessionGroup.Id
    private readonly ConcurrentDictionary<string, CopilotClient> _codespaceClients = new();
    // Port-forward tunnel handles keyed by SessionGroup.Id — kept alive while group is active
    private readonly ConcurrentDictionary<string, CodespaceService.TunnelHandle> _tunnelHandles = new();
    // Codespace health-check background task
    private CancellationTokenSource? _codespaceHealthCts;
    private Task? _codespaceHealthTask;
    // Cached dotfiles status — checked once when first SetupRequired state is encountered
    private CodespaceService.DotfilesStatus? _dotfilesStatus;
    private ConnectionSettings? _currentSettings;
    private string? _activeSessionName;
    private SynchronizationContext? _syncContext;
    
    private static readonly object _pathLock = new();
    private static string? _copilotBaseDir;
    private static string CopilotBaseDir { get { lock (_pathLock) return _copilotBaseDir ??= GetCopilotBaseDir(); } }
    
    private static string? _polyPilotBaseDir;
    private static string PolyPilotBaseDir { get { lock (_pathLock) return _polyPilotBaseDir ??= GetPolyPilotBaseDir(); } }
    internal static string BaseDir => PolyPilotBaseDir;

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
    
    private static string GetPolyPilotBaseDir()
    {
        try
        {
#if ANDROID
            var home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(home))
                home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            if (string.IsNullOrEmpty(home))
                home = Android.App.Application.Context.FilesDir?.AbsolutePath ?? Path.GetTempPath();
            return Path.Combine(home, ".polypilot");
#else
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
                home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(home))
                home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            return Path.Combine(home, ".polypilot");
#endif
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), ".polypilot");
        }
    }

    private static string? _sessionStatePath;
    private static string SessionStatePath { get { lock (_pathLock) return _sessionStatePath ??= Path.Combine(CopilotBaseDir, "session-state"); } }

    private static string? _activeSessionsFile;
    private static string ActiveSessionsFile { get { lock (_pathLock) return _activeSessionsFile ??= Path.Combine(PolyPilotBaseDir, "active-sessions.json"); } }

    private static string? _sessionAliasesFile;
    private static string SessionAliasesFile { get { lock (_pathLock) return _sessionAliasesFile ??= Path.Combine(PolyPilotBaseDir, "session-aliases.json"); } }

    private static string? _uiStateFile;
    private static string UiStateFile { get { lock (_pathLock) return _uiStateFile ??= Path.Combine(PolyPilotBaseDir, "ui-state.json"); } }

    private static string? _organizationFile;
    private static string OrganizationFile { get { lock (_pathLock) return _organizationFile ??= Path.Combine(PolyPilotBaseDir, "organization.json"); } }

    /// <summary>
    /// Override base directory for tests to prevent writing to real ~/.polypilot/.
    /// Clears all derived path caches so they re-resolve from the new base.
    /// </summary>
    internal static void SetBaseDirForTesting(string path)
    {
        lock (_pathLock)
        {
            _polyPilotBaseDir = path;
            _activeSessionsFile = null;
            _sessionAliasesFile = null;
            _uiStateFile = null;
            _organizationFile = null;
            _copilotBaseDir = null;
            _sessionStatePath = null;
            _pendingOrchestrationFile = null;
        }
    }

    private static string? _projectDir;
    private static string ProjectDir { get { lock (_pathLock) return _projectDir ??= FindProjectDir(); } }

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
    public bool IsInitialized { get; private set; }
    public bool IsRestoring { get; private set; }
    public bool NeedsConfiguration { get; private set; }
    public bool IsRemoteMode { get; private set; }
    public bool IsBridgeConnected => _bridgeClient.IsConnected;
    public string? ServerMachineName => _bridgeClient.ServerMachineName;
    public bool IsDemoMode { get; private set; }
    public string? ActiveSessionName => _activeSessionName;
    public IChatDatabase ChatDb => _chatDb;
    public ConnectionMode CurrentMode { get; private set; } = ConnectionMode.Embedded;
    public List<string> AvailableModels { get; private set; } = new();

    private readonly RepoManager _repoManager;
    private readonly CodespaceService _codespaceService;
    
    public CopilotService(IChatDatabase chatDb, IServerManager serverManager, IWsBridgeClient bridgeClient, RepoManager repoManager, IServiceProvider serviceProvider, CodespaceService codespaceService)
    : this(chatDb, serverManager, bridgeClient, repoManager, serviceProvider, new DemoService(), codespaceService)
    {
    }

    internal CopilotService(IChatDatabase chatDb, IServerManager serverManager, IWsBridgeClient bridgeClient, RepoManager repoManager, IServiceProvider serviceProvider, IDemoService demoService, CodespaceService? codespaceService = null)
    {
        _chatDb = chatDb;
        _serverManager = serverManager;
        _bridgeClient = bridgeClient;
        _repoManager = repoManager;
        _serviceProvider = serviceProvider;
        _demoService = demoService;
        _codespaceService = codespaceService ?? new CodespaceService();
        try { _usageStats = serviceProvider?.GetService(typeof(UsageStatsService)) as UsageStatsService; } catch { }
    }

    // Debug info
    public string LastDebugMessage { get; private set; } = "";

    // Transient notice shown when the service fell back from the user's preferred mode
    public string? FallbackNotice { get; private set; }
    public void ClearFallbackNotice() => FallbackNotice = null;

    // GitHub user info
    public string? GitHubAvatarUrl { get; private set; }
    public string? GitHubLogin { get; private set; }

    // UI preferences
    public ChatLayout ChatLayout { get; set; } = ChatLayout.Default;
    public ChatStyle ChatStyle { get; set; } = ChatStyle.Normal;
    public UiTheme Theme { get; set; } = UiTheme.System;
    public VsCodeVariant Editor { get; set; } = VsCodeVariant.Stable;
    private bool _codespacesEnabled;
    public bool CodespacesEnabled
    {
        get => _codespacesEnabled;
        set
        {
            if (_codespacesEnabled == value) return;
            _codespacesEnabled = value;
            if (value)
            {
                StartCodespaceHealthCheck();
            }
            else
            {
                // Stop health check first (awaits cancellation), then clean up resources.
                // Sequential: health check must exit before we clear the dictionaries it reads.
                _ = Task.Run(async () =>
                {
                    await StopCodespaceHealthCheckAsync();
                    // Health check has exited — safe to dispose resources
                    foreach (var kv in _codespaceClients)
                        try { await kv.Value.DisposeAsync(); } catch { }
                    _codespaceClients.Clear();
                    foreach (var kv in _tunnelHandles)
                        try { await kv.Value.DisposeAsync(); } catch { }
                    _tunnelHandles.Clear();
                });
            }
        }
    }

    /// <summary>In-memory flag: user dismissed the holiday theme for this app session.</summary>
    public bool HolidayThemeDismissed { get; set; }

    // Session organization (groups, pinning, sorting)
    // Organization.Sessions and Organization.Groups are plain List<T> — NOT thread-safe.
    // ALL reads and writes to these lists MUST hold _organizationLock when accessed from
    // background threads. UI-thread code should use the locked helpers below so that
    // background snapshot readers never see a torn list. Off-thread reads MUST use
    // SnapshotSessionMetas() / SnapshotGroups().
    public OrganizationState Organization { get; internal set; } = new();
    private readonly object _organizationLock = new();

    #region Organization lock: snapshot readers

    /// <summary>
    /// Returns a thread-safe snapshot of Organization.Sessions for use from
    /// non-UI threads (timer callbacks, background tasks, health checks).
    /// </summary>
    internal List<SessionMeta> SnapshotSessionMetas()
    {
        lock (_organizationLock) return Organization.Sessions.ToList();
    }

    /// <summary>
    /// Returns a thread-safe snapshot of Organization.Groups for use from
    /// non-UI threads (health checks, background tasks).
    /// </summary>
    internal List<SessionGroup> SnapshotGroups()
    {
        lock (_organizationLock) return Organization.Groups.ToList();
    }

    #endregion

    #region Organization lock: mutation helpers

    /// <summary>Thread-safe: adds a SessionMeta to Organization.Sessions.</summary>
    internal void AddSessionMeta(SessionMeta meta)
    {
        lock (_organizationLock) Organization.Sessions.Add(meta);
    }

    /// <summary>Thread-safe: removes all matching SessionMeta entries.</summary>
    internal int RemoveSessionMetasWhere(Predicate<SessionMeta> match)
    {
        lock (_organizationLock) return Organization.Sessions.RemoveAll(match);
    }

    /// <summary>Thread-safe: removes a specific SessionMeta instance.</summary>
    internal bool RemoveSessionMeta(SessionMeta meta)
    {
        lock (_organizationLock) return Organization.Sessions.Remove(meta);
    }

    /// <summary>Thread-safe: adds a SessionGroup to Organization.Groups.</summary>
    internal void AddGroup(SessionGroup group)
    {
        lock (_organizationLock) Organization.Groups.Add(group);
    }

    /// <summary>Thread-safe: inserts a SessionGroup at the specified index.</summary>
    internal void InsertGroup(int index, SessionGroup group)
    {
        lock (_organizationLock) Organization.Groups.Insert(index, group);
    }

    /// <summary>Thread-safe: removes all matching SessionGroup entries.</summary>
    internal int RemoveGroupsWhere(Predicate<SessionGroup> match)
    {
        lock (_organizationLock) return Organization.Groups.RemoveAll(match);
    }

    #endregion

    public event Action? OnStateChanged;
    public void NotifyStateChanged() => OnStateChanged?.Invoke();
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
        public required CopilotSession Session { get; set; }
        public required AgentSessionInfo Info { get; init; }
        public TaskCompletionSource<string>? ResponseCompletion { get; set; }
        public StringBuilder CurrentResponse { get; } = new();
        /// <summary>Accumulates text that FlushCurrentResponse moved to history mid-turn.
        /// CompleteResponse combines this with CurrentResponse for the TCS result so
        /// orchestrator dispatch gets the full response text.</summary>
        public StringBuilder FlushedResponse { get; } = new();
        public bool HasReceivedDeltasThisTurn { get; set; }
        public bool HasReceivedEventsSinceResume;
        public string? LastMessageId { get; set; }
        public bool SkipReflectionEvaluationOnce { get; set; }
        public long LastEventAtTicks = DateTime.UtcNow.Ticks;
        public CancellationTokenSource? ProcessingWatchdog { get; set; }
        /// <summary>
        /// CancellationTokenSource for the delayed CompleteResponse fallback started on AssistantTurnEndEvent.
        /// Cancelled when SessionIdleEvent fires (normal completion), AssistantTurnStartEvent fires
        /// (another tool round is starting), or a new SendPromptAsync call starts.
        /// This handles the SDK bug where session.idle is never emitted after assistant.turn_end.
        /// Must be a field (not property) so it can be used with Interlocked.Exchange.
        /// </summary>
        public CancellationTokenSource? TurnEndIdleCts;
        /// <summary>Number of tool calls started but not yet completed this turn.</summary>
        public int ActiveToolCallCount;
        /// <summary>True if any tool call has started during the current processing cycle.
        /// Unlike ActiveToolCallCount which resets on AssistantTurnStartEvent, this stays
        /// true until the response completes — so the watchdog uses the longer tool timeout
        /// even between tool rounds when the model is thinking.</summary>
        public bool HasUsedToolsThisTurn;
        /// <summary>
        /// Count of tools that completed successfully (no permission denial, no error) this turn.
        /// Used to gate auto-resend on recovery: if tools already succeeded, resend is skipped
        /// to avoid re-executing side-effectful work.
        /// Reset on each new turn alongside HasUsedToolsThisTurn.
        /// </summary>
        public int SuccessfulToolCountThisTurn;
        /// <summary>True if this session belongs to a multi-agent group at the time the
        /// current prompt was sent. Cached at send time (UI thread) so the watchdog can
        /// read it safely from a background thread without accessing Organization lists.</summary>
        public bool IsMultiAgentSession;
        /// <summary>
        /// Monotonically increasing counter incremented each time a new prompt is sent.
        /// Used by CompleteResponse to avoid completing a different turn than the one
        /// that produced the SessionIdleEvent (race between SEND and queued COMPLETE).
        /// </summary>
        public long ProcessingGeneration;
        /// <summary>
        /// Atomic flag for SendPromptAsync entry. Prevents TOCTOU race where two
        /// concurrent callers both see IsProcessing=false and both enter.
        /// 0 = idle, 1 = sending. Set via Interlocked.CompareExchange.
        /// </summary>
        public int SendingFlag;
        /// <summary>
        /// Tracks reasoning messages that have been created but not yet added to History
        /// (pending InvokeOnUI). Prevents duplicate creation when rapid deltas arrive
        /// for the same reasoningId before the UI thread posts the History.Add.
        /// Cleared on CompleteResponse and SendPromptAsync.
        /// </summary>
        public ConcurrentDictionary<string, ChatMessage> PendingReasoningMessages { get; } = new();
    }

    private void Debug(string message)
    {
        LastDebugMessage = message;
        Console.WriteLine($"[DEBUG] {message}");
        OnDebug?.Invoke(message);

        // Persist lifecycle diagnostics to file for post-mortem analysis (DEBUG builds only)
#if DEBUG
        if (message.StartsWith("[EVT") || message.StartsWith("[IDLE") ||
            message.StartsWith("[COMPLETE") || message.StartsWith("[SEND") ||
            message.StartsWith("[RECONNECT") || message.StartsWith("[UI-ERR") ||
            message.StartsWith("[DISPATCH") || message.StartsWith("[WATCHDOG") ||
            message.StartsWith("[HEALTH") ||
            message.Contains("watchdog"))
        {
            try
            {
                lock (_diagnosticLogLock)
                {
                    var logPath = Path.Combine(PolyPilotBaseDir, "event-diagnostics.log");
                    // Rotate at 10 MB to prevent unbounded growth
                    var fi = new FileInfo(logPath);
                    if (fi.Exists && fi.Length > 10 * 1024 * 1024)
                        try { File.Delete(logPath); } catch { }
                    File.AppendAllText(logPath,
                        $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
                }
            }
            catch { /* Don't let logging failures cascade */ }
        }
#endif
    }

    internal void InvokeOnUI(Action action)
    {
        if (_syncContext != null)
            _syncContext.Post(_ =>
            {
                try { action(); }
                catch (Exception ex)
                {
                    Debug($"[UI-ERR] InvokeOnUI callback threw: {ex}");
                }
            }, null);
        else
        {
            try { action(); }
            catch (Exception ex)
            {
                Debug($"[UI-ERR] InvokeOnUI inline callback threw: {ex}");
            }
        }
    }

    /// <summary>
    /// Awaitable version of InvokeOnUI — completes after the action runs on the UI thread.
    /// Use when subsequent code depends on state mutated by the action.
    /// </summary>
    internal Task InvokeOnUIAsync(Action action)
    {
        if (_syncContext == null)
        {
            try { action(); }
            catch (Exception ex) { Debug($"[UI-ERR] InvokeOnUIAsync inline threw: {ex}"); }
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _syncContext.Post(_ =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                Debug($"[UI-ERR] InvokeOnUIAsync callback threw: {ex}");
                tcs.SetException(ex);
            }
        }, null);
        return tcs.Task;
    }

    /// <summary>
    /// Awaitable version of InvokeOnUI for async operations — runs the async func on the UI thread
    /// and completes when it finishes. Use for operations like CreateSessionWithWorktreeAsync that
    /// assume UI thread context for Organization.Sessions mutations.
    /// </summary>
    internal Task InvokeOnUIAsync(Func<Task> asyncAction)
    {
        if (_syncContext == null)
        {
            try { return asyncAction(); }
            catch (Exception ex)
            {
                Debug($"[UI-ERR] InvokeOnUIAsync(Func<Task>) inline threw: {ex}");
                return Task.FromException(ex);
            }
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _syncContext.Post(async _ =>
        {
            try
            {
                await asyncAction();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                Debug($"[UI-ERR] InvokeOnUIAsync(Func<Task>) callback threw: {ex}");
                tcs.SetException(ex);
            }
        }, null);
        return tcs.Task;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsInitialized) return;

        // Capture the sync context for marshaling events back to UI thread
        _syncContext = SynchronizationContext.Current;
        Debug($"SyncContext captured: {_syncContext?.GetType().Name ?? "null"}");

        var settings = ConnectionSettings.Load();
        _currentSettings = settings;
        CurrentMode = settings.Mode;
        ChatLayout = settings.ChatLayout;
        ChatStyle = settings.ChatStyle;
        Theme = settings.Theme;
        Editor = settings.Editor;
        // Codespaces only supported in Embedded mode — tunnels die with the app,
        // matching Embedded's lifecycle. Prevent confusion in Persistent/Remote mode.
        CodespacesEnabled = settings.CodespacesEnabled && settings.Mode == ConnectionMode.Embedded;

        // On mobile with Remote mode and no URL configured, skip initialization
        if (settings.Mode == ConnectionMode.Remote && string.IsNullOrWhiteSpace(settings.RemoteUrl) && string.IsNullOrWhiteSpace(settings.LanUrl))
        {
            Debug("Remote mode with no URL configured — waiting for settings");
            NeedsConfiguration = true;
            OnStateChanged?.Invoke();
            return;
        }

        // Remote mode: connect via WsBridgeClient (state-sync, not CopilotClient)
        if (settings.Mode == ConnectionMode.Remote && (!string.IsNullOrWhiteSpace(settings.RemoteUrl) || !string.IsNullOrWhiteSpace(settings.LanUrl)))
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
        // In Persistent mode, auto-start the server if not already running
        if (settings.Mode == ConnectionMode.Persistent)
        {
            if (!_serverManager.CheckServerRunning("127.0.0.1", settings.Port))
            {
                Debug($"Persistent server not running, auto-starting on port {settings.Port}...");
                var started = await _serverManager.StartServerAsync(settings.Port);
                if (!started)
                {
                    Debug("Failed to auto-start server, falling back to Embedded mode");
                    settings.Mode = ConnectionMode.Embedded;
                    CurrentMode = ConnectionMode.Embedded;
                    FallbackNotice = "Persistent server couldn't start — fell back to Embedded mode. Your sessions won't persist across restarts. Go to Settings to fix.";
                }
            }
            else
            {
                Debug($"Persistent server already running on port {settings.Port}");
            }
        }

        _client = CreateClient(settings);

        try
        {
            await _client.StartAsync(cancellationToken);
            IsInitialized = true;
            NeedsConfiguration = false;
            Debug($"Copilot client started in {settings.Mode} mode");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (settings.Mode == ConnectionMode.Persistent
            && ex.Message.Contains("version mismatch", StringComparison.OrdinalIgnoreCase))
        {
            // The persistent server is running an older/newer protocol version than the SDK.
            // Kill it, restart with the current CLI, and retry once.
            Debug($"Protocol version mismatch with persistent server — restarting: {ex.Message}");
            try { await _client.DisposeAsync(); } catch { }
            _client = null;

            _serverManager.StopServer();

            // Wait for the old server to fully release the port (Kill is async)
            for (int i = 0; i < 20; i++)
            {
                if (!_serverManager.CheckServerRunning("127.0.0.1", settings.Port))
                    break;
                await Task.Delay(250, cancellationToken);
            }

            var restarted = await _serverManager.StartServerAsync(settings.Port);
            if (restarted)
            {
                Debug("Server restarted, retrying connection...");
                _client = CreateClient(settings);
                try
                {
                    await _client.StartAsync(cancellationToken);
                    IsInitialized = true;
                    NeedsConfiguration = false;
                    Debug($"Copilot client started after server restart in {settings.Mode} mode");
                    OnStateChanged?.Invoke();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception retryEx)
                {
                    Debug($"Failed to start Copilot client after server restart: {retryEx.Message}");
                    try { await _client.DisposeAsync(); } catch { }
                    _client = null;
                    IsInitialized = false;
                    NeedsConfiguration = true;
                    FallbackNotice = $"Protocol version mismatch and restart failed — go to Settings to reconnect.";
                    OnStateChanged?.Invoke();
                    return;
                }
            }
            else
            {
                Debug("Server restart failed, falling back to Embedded mode");
                CurrentMode = ConnectionMode.Embedded;
                FallbackNotice = "Persistent server had a version mismatch and couldn't restart — fell back to Embedded mode.";
                var embeddedSettings = new ConnectionSettings { Mode = ConnectionMode.Embedded, Host = settings.Host, Port = settings.Port };
                _client = CreateClient(embeddedSettings);
                try
                {
                    await _client.StartAsync(cancellationToken);
                    IsInitialized = true;
                    NeedsConfiguration = false;
                    Debug($"Copilot client started in Embedded fallback mode");
                    OnStateChanged?.Invoke();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception fallbackEx)
                {
                    Debug($"Embedded fallback also failed: {fallbackEx.Message}");
                    try { await _client.DisposeAsync(); } catch { }
                    _client = null;
                    IsInitialized = false;
                    NeedsConfiguration = true;
                    OnStateChanged?.Invoke();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Debug($"Failed to start Copilot client: {ex.Message}");
            try { await _client.DisposeAsync(); } catch { }
            _client = null;
            IsInitialized = false;
            NeedsConfiguration = true;
            OnStateChanged?.Invoke();
            return;
        }
        // Note: copilot-instructions.md is automatically loaded by the CLI from .github/ in the working directory.
        // We don't need to manually load and inject it here.

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

        // Start health check loop for any codespace groups (regardless of whether sessions were restored)
        if (CodespacesEnabled)
            StartCodespaceHealthCheck();

        // Reconcile now that all sessions are restored
        ReconcileOrganization();
        OnStateChanged?.Invoke();

        // Resume any pending orchestration dispatch that was interrupted by a relaunch
        _ = ResumeOrchestrationIfPendingAsync(cancellationToken);

        // Initialize any registered providers (from DI / plugin loader)
        await InitializeProvidersAsync(cancellationToken);
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
        _currentSettings = settings;

        StopConnectivityMonitoring();
        await StopCodespaceHealthCheckAsync();

        // Dispose existing sessions and client
        foreach (var state in _sessions.Values)
        {
            CancelProcessingWatchdog(state);
            CancelTurnEndFallback(state);
            try { if (state.Session != null) await state.Session.DisposeAsync(); } catch { }
        }
        _sessions.Clear();
        _closedSessionIds.Clear();
        _closedSessionNames.Clear();
        lock (_imageQueueLock)
        {
            _queuedImagePaths.Clear();
        }
        _queuedAgentModes.Clear();
        _activeSessionName = null;

        if (_client != null)
        {
            try { await _client.DisposeAsync(); } catch { }
            _client = null;
        }
        foreach (var kv in _codespaceClients)
        {
            try { await kv.Value.DisposeAsync(); } catch { }
        }
        _codespaceClients.Clear();
        foreach (var kv in _tunnelHandles)
        {
            try { await kv.Value.DisposeAsync(); } catch { }
        }
        _tunnelHandles.Clear();
        _bridgeClient.Stop();

        IsInitialized = false;
        IsRemoteMode = false;
        IsDemoMode = false;
        FallbackNotice = null; // Clear any previous fallback notice
        CurrentMode = settings.Mode;
        CodespacesEnabled = settings.CodespacesEnabled && settings.Mode == ConnectionMode.Embedded;
        OnStateChanged?.Invoke();

        // Demo mode: local mock responses
        if (settings.Mode == ConnectionMode.Demo)
        {
            InitializeDemo();
            return;
        }

        // Remote mode uses WsBridgeClient state-sync
        if (settings.Mode == ConnectionMode.Remote && (!string.IsNullOrWhiteSpace(settings.RemoteUrl) || !string.IsNullOrWhiteSpace(settings.LanUrl)))
        {
            await InitializeRemoteAsync(settings, cancellationToken);
            return;
        }

        _client = CreateClient(settings);

        try
        {
            await _client.StartAsync(cancellationToken);
            IsInitialized = true;
            NeedsConfiguration = false;
            Debug($"Reconnected in {settings.Mode} mode");
            OnStateChanged?.Invoke();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Debug($"Failed to reconnect Copilot client: {ex.Message}");
            try { await _client.DisposeAsync(); } catch { }
            _client = null;
            IsInitialized = false;
            NeedsConfiguration = true;
            OnStateChanged?.Invoke();
            return;
        }

        // Restore previous sessions
        LoadOrganization();
        await RestorePreviousSessionsAsync(cancellationToken);
        if (CodespacesEnabled)
            StartCodespaceHealthCheck();
        ReconcileOrganization();
        OnStateChanged?.Invoke();

        // Resume any pending orchestration dispatch
        _ = ResumeOrchestrationIfPendingAsync(cancellationToken);

        // Re-initialize providers after reconnect
        await InitializeProvidersAsync(cancellationToken);
    }

    private CopilotClient CreateClient(ConnectionSettings settings)
    {
        // Remote mode is handled by InitializeRemoteAsync, not here
        // Note: Don't set Cwd here - each session sets its own WorkingDirectory in SessionConfig
        var options = new CopilotClientOptions();

        if (settings.Mode == ConnectionMode.Persistent)
        {
            // Connect to the existing headless server via TCP instead of spawning a child process.
            // Must clear auto-discovered CliPath and UseStdio first —
            // CliUrl is mutually exclusive with both (SDK throws ArgumentException).
            options.CliPath = null;
            options.UseStdio = false;
            options.AutoStart = false;
            options.CliUrl = $"http://{settings.Host}:{settings.Port}";
        }
        else
        {
            // Embedded mode: spawn copilot as a child process via stdio
            var cliPath = ResolveCopilotCliPath(settings.CliSource);
            if (cliPath != null)
                options.CliPath = cliPath;

            // Pass additional MCP server configs via CLI args.
            // The CLI auto-reads ~/.copilot/mcp-config.json, but mcp-servers.json
            // uses a different format that needs to be passed explicitly.
            var mcpArgs = GetMcpCliArgs();
            if (mcpArgs.Length > 0)
                options.CliArgs = mcpArgs;
        }

        return new CopilotClient(options);
    }


    /// <summary>
    /// Resolves the copilot CLI path based on user preference.
    /// BuiltIn: bundled binary first, then system fallback.
    /// System: system-installed binary first, then bundled fallback.
    /// </summary>
    internal static string? ResolveCopilotCliPath(CliSourceMode source = CliSourceMode.BuiltIn)
    {
        if (source == CliSourceMode.System)
        {
            // Prefer system CLI, fall back to built-in
            var systemPath = ResolveSystemCliPath();
            if (systemPath != null) return systemPath;
            return ResolveBundledCliPath();
        }

        // Default: prefer built-in, fall back to system
        var bundledPath = ResolveBundledCliPath();
        if (bundledPath != null) return bundledPath;
        return ResolveSystemCliPath();
    }

    /// <summary>
    /// Resolves the bundled CLI path (shipped with the app).
    /// </summary>
    internal static string? ResolveBundledCliPath()
    {
        // 1. SDK bundled path (runtimes/{rid}/native/copilot)
        var bundledPath = GetBundledCliPath();
        if (bundledPath != null && File.Exists(bundledPath))
            return bundledPath;

        // 2. MonoBundle/copilot (MAUI flattens runtimes/ into MonoBundle on Mac Catalyst)
        try
        {
            var assemblyDir = Path.GetDirectoryName(typeof(CopilotClient).Assembly.Location);
            if (assemblyDir != null)
            {
                var binaryName = OperatingSystem.IsWindows() ? "copilot.exe" : "copilot";
                var monoBundlePath = Path.Combine(assemblyDir, binaryName);
                if (File.Exists(monoBundlePath))
                    return monoBundlePath;
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Resolves a system-installed CLI (PATH, homebrew, npm).
    /// </summary>
    private static string? ResolveSystemCliPath()
    {
        // 1. Check well-known system install paths
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var windowsPaths = new[]
            {
                Path.Combine(appData, "npm", "node_modules", "@github", "copilot", "node_modules", "@github", "copilot-win-x64", "copilot.exe"),
                Path.Combine(localAppData, "npm", "node_modules", "@github", "copilot", "node_modules", "@github", "copilot-win-x64", "copilot.exe"),
            };
            foreach (var path in windowsPaths)
            {
                if (File.Exists(path)) return path;
            }
        }
        else
        {
            var unixPaths = new[]
            {
                "/opt/homebrew/lib/node_modules/@github/copilot/node_modules/@github/copilot-darwin-arm64/copilot",
                "/usr/local/lib/node_modules/@github/copilot/node_modules/@github/copilot-darwin-arm64/copilot",
                "/usr/local/bin/copilot",
            };
            foreach (var path in unixPaths)
            {
                if (File.Exists(path)) return path;
            }
        }

        // 2. Try to find copilot on PATH
        try
        {
            var binaryName = OperatingSystem.IsWindows() ? "copilot.exe" : "copilot";
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrEmpty(dir)) continue;
                var candidate = Path.Combine(dir, binaryName);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Resolves the path where the SDK expects a bundled copilot binary.
    /// Pattern: {assembly-dir}/runtimes/{rid}/native/copilot
    /// </summary>
    private static string? GetBundledCliPath()
    {
        try
        {
            var assemblyDir = Path.GetDirectoryName(typeof(CopilotClient).Assembly.Location);
            if (assemblyDir == null) return null;
            var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
            var binaryName = OperatingSystem.IsWindows() ? "copilot.exe" : "copilot";
            return Path.Combine(assemblyDir, "runtimes", rid, "native", binaryName);
        }
        catch { return null; }
    }

    /// <summary>
    /// Gets version string from a CLI binary by running --version.
    /// </summary>
    public static string? GetCliVersion(string? cliPath)
    {
        if (cliPath == null || !File.Exists(cliPath)) return null;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(cliPath, "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(3000);
            // Extract version number from output like "GitHub Copilot CLI 0.0.409."
            var match = System.Text.RegularExpressions.Regex.Match(output, @"(\d+\.\d+\.\d+)");
            return match.Success ? match.Groups[1].Value : output;
        }
        catch { return null; }
    }

    /// <summary>
    /// Gets info about available CLI sources for display in settings.
    /// </summary>
    public static (string? builtInPath, string? builtInVersion, string? systemPath, string? systemVersion) GetCliSourceInfo()
    {
        var builtInPath = ResolveBundledCliPath();
        var systemPath = ResolveSystemCliPath();
        return (
            builtInPath, GetCliVersion(builtInPath),
            systemPath, GetCliVersion(systemPath)
        );
    }

    /// <summary>
    /// Build CLI args to pass additional MCP server configs.
    /// Also writes a merged mcp-config.json that the CLI auto-reads at startup,
    /// which is more reliable than --additional-mcp-config for persistent servers.
    /// </summary>
    internal static string[] GetMcpCliArgs()
    {
        var args = new List<string>();
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var copilotDir = Path.Combine(home, ".copilot");
            var allServers = new Dictionary<string, JsonElement>();

            // mcp-servers.json (flat format: { "name": { "command": ..., "args": [...] } })
            var serversPath = Path.Combine(copilotDir, "mcp-servers.json");
            if (File.Exists(serversPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(serversPath));
                foreach (var prop in doc.RootElement.EnumerateObject())
                    allServers[prop.Name] = prop.Value.Clone();
            }

            // mcp-config.json (wrapped format — CLI auto-reads this)
            var configPath = Path.Combine(copilotDir, "mcp-config.json");
            if (File.Exists(configPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                if (doc.RootElement.TryGetProperty("mcpServers", out var mcpServers))
                {
                    foreach (var prop in mcpServers.EnumerateObject())
                    {
                        if (!allServers.ContainsKey(prop.Name))
                            allServers[prop.Name] = prop.Value.Clone();
                    }
                }
            }

            // Installed plugin .mcp.json files (wrapped format)
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
                            if (doc.RootElement.TryGetProperty("mcpServers", out var mcpServers))
                            {
                                foreach (var prop in mcpServers.EnumerateObject())
                                {
                                    if (!allServers.ContainsKey(prop.Name))
                                        allServers[prop.Name] = prop.Value.Clone();
                                }
                            }
                        }
                        catch { }
                    }
                }
            }

            if (allServers.Count == 0) return args.ToArray();

            // Write merged config back to mcp-config.json so the CLI auto-reads it.
            // This is more reliable than --additional-mcp-config for persistent servers.
            var merged = new Dictionary<string, object> { ["mcpServers"] = allServers };
            var json = JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MCP] Failed to build MCP CLI args: {ex.Message}");
        }
        return args.ToArray();
    }

    /// <summary>
    /// Load MCP server configurations for per-session registration via SessionConfig.McpServers.
    /// Merges servers from ~/.copilot/mcp-servers.json, ~/.copilot/mcp-config.json, and installed plugins.
    /// Returns McpLocalServerConfig objects that the SDK can serialize properly.
    /// Skips servers in the disabled list.
    /// </summary>
    internal static Dictionary<string, object>? LoadMcpServers(IReadOnlyCollection<string>? disabledServers = null, IReadOnlyCollection<string>? disabledPlugins = null)
    {
        var servers = new Dictionary<string, object>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var copilotDir = Path.Combine(home, ".copilot");
        var disabled = disabledServers ?? Array.Empty<string>();

        void AddServersFromJson(JsonElement root, bool isWrapped)
        {
            var element = isWrapped && root.TryGetProperty("mcpServers", out var inner) ? inner : root;
            if (element.ValueKind != JsonValueKind.Object) return;
            foreach (var prop in element.EnumerateObject())
            {
                if (servers.ContainsKey(prop.Name)) continue;
                if (disabled.Contains(prop.Name)) continue;
                servers[prop.Name] = ParseMcpServerConfig(prop.Value);
            }
        }

        // Read ~/.copilot/mcp-servers.json (flat format)
        try
        {
            var path = Path.Combine(copilotDir, "mcp-servers.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                AddServersFromJson(doc.RootElement, isWrapped: false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MCP] Failed to read mcp-servers.json: {ex.Message}");
        }

        // Read ~/.copilot/mcp-config.json (wrapped format)
        try
        {
            var path = Path.Combine(copilotDir, "mcp-config.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                AddServersFromJson(doc.RootElement, isWrapped: true);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MCP] Failed to read mcp-config.json: {ex.Message}");
        }

        // Read plugin .mcp.json files from ~/.copilot/installed-plugins/
        try
        {
            var pluginsDir = Path.Combine(copilotDir, "installed-plugins");
            if (Directory.Exists(pluginsDir))
            {
                foreach (var marketDir in Directory.GetDirectories(pluginsDir))
                {
                    foreach (var pluginDir in Directory.GetDirectories(marketDir))
                    {
                        var pluginName = Path.GetFileName(pluginDir);
                        if (disabledPlugins?.Contains(pluginName) == true) continue;
                        var mcpFile = Path.Combine(pluginDir, ".mcp.json");
                        if (!File.Exists(mcpFile)) continue;
                        try
                        {
                            using var doc = JsonDocument.Parse(File.ReadAllText(mcpFile));
                            AddServersFromJson(doc.RootElement, isWrapped: true);
                        }
                        catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MCP] Failed to read plugin .mcp.json files: {ex.Message}");
        }

        return servers.Count > 0 ? servers : null;
    }

    /// <summary>
    /// Discover skill directories from installed plugins for SessionConfig.SkillDirectories.
    /// Scans ~/.copilot/installed-plugins/ for plugins containing a skills/ subdirectory.
    /// </summary>
    internal static List<string>? LoadSkillDirectories(IReadOnlyCollection<string>? disabledPlugins = null)
    {
        var dirs = new List<string>();
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var pluginsDir = Path.Combine(home, ".copilot", "installed-plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            foreach (var marketDir in Directory.GetDirectories(pluginsDir))
            {
                foreach (var pluginDir in Directory.GetDirectories(marketDir))
                {
                    var pluginName = Path.GetFileName(pluginDir);
                    if (disabledPlugins?.Contains(pluginName) == true) continue;
                    var skillsDir = Path.Combine(pluginDir, "skills");
                    if (Directory.Exists(skillsDir))
                        dirs.Add(skillsDir);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Skills] Failed to scan plugin skill directories: {ex.Message}");
        }
        return dirs.Count > 0 ? dirs : null;
    }

    /// <summary>
    /// Discover all available skills from installed plugins and project-level skill directories.
    /// Returns a list of (Name, Description, Source) tuples.
    /// </summary>
    public static List<SkillInfo> DiscoverAvailableSkills(string? workingDirectory = null)
    {
        var skills = new List<SkillInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Project-level skills (.claude/skills/ or .copilot/skills/)
            if (!string.IsNullOrEmpty(workingDirectory))
            {
                foreach (var subdir in new[] { ".claude/skills", ".copilot/skills", ".github/skills" })
                {
                    var projSkillsDir = Path.Combine(workingDirectory, subdir);
                    if (Directory.Exists(projSkillsDir))
                        ScanSkillDirectory(projSkillsDir, "project", skills, seen);
                }
            }

            // Plugin-level skills (~/.copilot/installed-plugins/*/skills/)
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var pluginsDir = Path.Combine(home, ".copilot", "installed-plugins");
            if (Directory.Exists(pluginsDir))
            {
                foreach (var marketDir in Directory.GetDirectories(pluginsDir))
                {
                    foreach (var pluginDir in Directory.GetDirectories(marketDir))
                    {
                        var skillsDir = Path.Combine(pluginDir, "skills");
                        if (Directory.Exists(skillsDir))
                        {
                            var pluginName = Path.GetFileName(pluginDir);
                            ScanSkillDirectory(skillsDir, pluginName, skills, seen);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Skills] Discovery failed: {ex.Message}");
        }

        return skills;
    }

    private static void ScanSkillDirectory(string skillsDir, string source, List<SkillInfo> skills, HashSet<string> seen)
    {
        foreach (var skillDir in Directory.GetDirectories(skillsDir))
        {
            var skillMd = Path.Combine(skillDir, "SKILL.md");
            if (!File.Exists(skillMd)) continue;

            try
            {
                var content = File.ReadAllText(skillMd);
                var (name, description) = ParseSkillFrontmatter(content);
                if (string.IsNullOrEmpty(name)) name = Path.GetFileName(skillDir);
                if (seen.Add(name))
                    skills.Add(new SkillInfo(name, description ?? "", source));
            }
            catch { }
        }
    }

    private static (string? name, string? description) ParseSkillFrontmatter(string content)
    {
        if (!content.StartsWith("---")) return (null, null);
        var endIdx = content.IndexOf("---", 3, StringComparison.Ordinal);
        if (endIdx < 0) return (null, null);

        var frontmatter = content[3..endIdx];
        string? name = null, description = null;

        foreach (var line in frontmatter.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("name:"))
                name = trimmed[5..].Trim().Trim('"', '\'');
            else if (trimmed.StartsWith("description:"))
            {
                var desc = trimmed[12..].Trim();
                if (desc.StartsWith(">")) continue; // multiline, skip for now
                description = desc.Trim('"', '\'');
            }
        }

        return (name, description);
    }

    public List<AgentInfo> DiscoverAvailableAgents(string? workingDirectory)
    {
        var agents = new List<AgentInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!string.IsNullOrEmpty(workingDirectory))
            {
                foreach (var subdir in new[] { ".github/agents", ".claude/agents", ".copilot/agents" })
                {
                    var agentsDir = Path.Combine(workingDirectory, subdir);
                    if (Directory.Exists(agentsDir))
                        ScanAgentDirectory(agentsDir, "project", agents, seen);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Agents] Discovery failed: {ex.Message}");
        }

        return agents;
    }

    private static void ScanAgentDirectory(string agentsDir, string source, List<AgentInfo> agents, HashSet<string> seen)
    {
        foreach (var file in Directory.GetFiles(agentsDir, "*.md"))
        {
            try
            {
                var content = File.ReadAllText(file);
                var (name, description) = ParseSkillFrontmatter(content);
                if (string.IsNullOrEmpty(name)) name = Path.GetFileNameWithoutExtension(file);
                if (seen.Add(name))
                    agents.Add(new AgentInfo(name, description ?? "", source));
            }
            catch { }
        }
    }

    /// <summary>
    /// Parse a JSON element into a McpLocalServerConfig so the SDK serializes it correctly.
    /// </summary>
    private static McpLocalServerConfig ParseMcpServerConfig(JsonElement element)
    {
        var config = new McpLocalServerConfig();
        if (element.TryGetProperty("command", out var cmd))
            config.Command = cmd.GetString() ?? "";
        if (element.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
            config.Args = args.EnumerateArray().Select(a => a.GetString() ?? "").ToList();
        if (element.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
            config.Env = env.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
        if (element.TryGetProperty("cwd", out var cwd))
            config.Cwd = cwd.GetString() ?? "";
        if (element.TryGetProperty("type", out var type))
            config.Type = type.GetString() ?? "";
        if (element.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
            config.Tools = tools.EnumerateArray().Select(t => t.GetString() ?? "").ToList();
        if (element.TryGetProperty("timeout", out var timeout) && timeout.TryGetInt32(out var tv))
            config.Timeout = tv;
        return config;
    }

    /// <summary>
    /// Resume an existing session by its GUID
    /// </summary>
    public async Task<AgentSessionInfo> ResumeSessionAsync(string sessionId, string displayName, string? workingDirectory = null, string? model = null, CancellationToken cancellationToken = default, string? lastPrompt = null, string? groupId = null)
    {
        // In remote mode, delegate to WsBridgeClient
        if (IsRemoteMode)
        {
            var remoteWorkingDirectory = workingDirectory ?? GetSessionWorkingDirectory(sessionId);
            var remoteInfo = new AgentSessionInfo
            {
                Name = displayName,
                SessionId = sessionId,
                Model = GetSessionModelFromDisk(sessionId) ?? model ?? DefaultModel,
                WorkingDirectory = remoteWorkingDirectory
            };
            // Set up optimistic state BEFORE sending bridge message to prevent race with SyncRemoteSessions
            _pendingRemoteSessions[displayName] = 0;
            _sessions[displayName] = new SessionState { Session = null!, Info = remoteInfo };
            if (!Organization.Sessions.Any(m => m.SessionName == displayName))
                AddSessionMeta(new SessionMeta { SessionName = displayName, GroupId = SessionGroup.DefaultId });
            _activeSessionName = displayName;
            OnStateChanged?.Invoke();
            // Now send the bridge message — server may respond before this returns
            await _bridgeClient.ResumeSessionAsync(sessionId, displayName, cancellationToken);
            _ = Task.Delay(30_000).ContinueWith(t => { _pendingRemoteSessions.TryRemove(displayName, out _); });
            return remoteInfo;
        }

        // For codespace sessions, verify the group is connected before attempting resume.
        // This MUST come before the _client == null guard below, because codespace sessions
        // use _codespaceClients (not _client). Without this check, the "Service not initialized"
        // error fires and maps to the misleading "Copilot is not connected yet" user-facing message.
        if (groupId != null)
        {
            var group = Organization.Groups.FirstOrDefault(g => g.Id == groupId);
            if (group?.IsCodespace == true && !_codespaceClients.ContainsKey(groupId))
                throw new InvalidOperationException(
                    $"Codespace '{group.Name}' is not connected yet. The health check will reconnect automatically. Please retry in a moment.");
        }

        if (!IsInitialized || _client == null)
            throw new InvalidOperationException("Service not initialized. Call InitializeAsync first.");

        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));

        if (!Guid.TryParse(sessionId, out _))
            throw new ArgumentException("Session ID must be a valid GUID.", nameof(sessionId));

        // De-duplicate display name if already taken (persisted sessions may share
        // the same title derived from their first message)
        if (_sessions.ContainsKey(displayName))
        {
            var baseName = displayName;
            for (int i = 2; i <= 99; i++)
            {
                var candidate = $"{baseName} ({i})";
                if (!_sessions.ContainsKey(candidate))
                {
                    displayName = candidate;
                    break;
                }
            }
            if (_sessions.ContainsKey(displayName))
                throw new InvalidOperationException($"Session '{baseName}' already exists (too many duplicates).");
        }

        // Load history: always parse events.jsonl as source of truth, then sync to DB
        List<ChatMessage> history = LoadHistoryFromDisk(sessionId);

        if (history.Count > 0)
        {
            // Replace DB contents with fresh parse (events.jsonl may have grown since last DB sync)
            await _chatDb.BulkInsertAsync(sessionId, history);
        }

        var resumeWorkingDirectory = workingDirectory ?? GetSessionWorkingDirectory(sessionId);

        // Override for codespace sessions — the local worktree path doesn't exist inside
        // the codespace; use /workspaces/{repo} instead.
        if (groupId != null)
        {
            var csGroup = Organization.Groups.FirstOrDefault(g => g.Id == groupId);
            if (csGroup?.IsCodespace == true && csGroup.CodespaceWorkingDirectory != null)
                resumeWorkingDirectory = csGroup.CodespaceWorkingDirectory;
        }

        // Resume the session using the SDK — pass model and working directory so backend context is preserved
        var resumeModel = Models.ModelHelper.NormalizeToSlug(model ?? GetSessionModelFromDisk(sessionId) ?? DefaultModel);
        if (string.IsNullOrEmpty(resumeModel)) resumeModel = DefaultModel;
        Debug($"Resuming session '{displayName}' with model: '{resumeModel}', cwd: '{resumeWorkingDirectory}'");
        var resumeConfig = new ResumeSessionConfig { Model = resumeModel, WorkingDirectory = resumeWorkingDirectory, Tools = new List<Microsoft.Extensions.AI.AIFunction> { ShowImageTool.CreateFunction() }, OnPermissionRequest = AutoApprovePermissions };
        var copilotSession = await GetClientForGroup(groupId).ResumeSessionAsync(sessionId, resumeConfig, cancellationToken);

        var isStillProcessing = IsSessionStillProcessing(sessionId);

        var info = new AgentSessionInfo
        {
            Name = displayName,
            Model = resumeModel,
            CreatedAt = DateTime.UtcNow,
            SessionId = sessionId,
            IsResumed = isStillProcessing,
            WorkingDirectory = resumeWorkingDirectory
        };
        info.GitBranch = GetGitBranch(info.WorkingDirectory);

        // Add loaded history to the session info
        foreach (var msg in history)
        {
            info.History.Add(msg);
        }
        info.MessageCount = info.History.Count;
        info.LastReadMessageCount = info.History.Count;

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
        var reconnectMsg = $"🔄 Session reconnected at {DateTime.Now.ToShortTimeString()}";
        if (isStillProcessing)
        {
            var (lastTool, lastContent) = GetLastSessionActivity(sessionId);
            if (!string.IsNullOrEmpty(lastTool))
                reconnectMsg += $" — running {lastTool}";
            if (!string.IsNullOrEmpty(lastContent))
                reconnectMsg += $"\n💬 Last: {(lastContent.Length > 100 ? lastContent[..100] + "…" : lastContent)}";
            if (!string.IsNullOrEmpty(lastPrompt))
            {
                var truncated = lastPrompt.Length > 80 ? lastPrompt[..80] + "…" : lastPrompt;
                reconnectMsg += $"\n📝 Last message: \"{truncated}\"";
            }
        }
        info.History.Add(ChatMessage.SystemMessage(reconnectMsg));

        // Set processing state if session was mid-turn when app died
        info.IsProcessing = isStillProcessing;
        if (isStillProcessing)
        {
            // Set phase based on last event so UI shows correct status instead of "Sending"
            var (lastTool, _) = GetLastSessionActivity(sessionId);
            info.ProcessingPhase = !string.IsNullOrEmpty(lastTool) ? 3 : 2; // 3=Working, 2=Thinking
            info.ProcessingStartedAt = DateTime.UtcNow;
        }

        var state = new SessionState
        {
            Session = copilotSession,
            Info = info
        };

        // Cache multi-agent membership for the watchdog timeout tier.
        // Must be set BEFORE StartProcessingWatchdog — otherwise the watchdog uses the
        // 120s inactivity timeout instead of the 600s tool timeout, killing workers prematurely.
        // IsSessionInMultiAgentGroup reads Organization.Sessions which was loaded from disk
        // by LoadOrganization() before RestorePreviousSessionsAsync runs.
        state.IsMultiAgentSession = IsSessionInMultiAgentGroup(displayName);

        // Wire up event handler BEFORE starting watchdog/timeout so events
        // arriving immediately after SDK resume are not missed.
        copilotSession.On(evt => HandleSessionEvent(state, evt));

        // If still processing, set up ResponseCompletion so events flow properly.
        // The processing watchdog (30s resume quiescence / 120s inactivity / 600s tool timeout)
        // handles stuck sessions — see RunProcessingWatchdogAsync for the three-tier logic.
        if (isStillProcessing)
        {
            state.ResponseCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Debug($"Session '{displayName}' is still processing (was mid-turn when app restarted)");

            // If events.jsonl was recently modified, the server was actively processing
            // right before the restart. Pre-seed HasReceivedEventsSinceResume to bypass
            // the 30s quiescence timeout — that timeout is for sessions that had already
            // finished, not for genuinely active ones where the SDK just needs time to reconnect.
            var (isRecentlyActive, hadToolActivity) = GetEventsFileRestoreHints(sessionId);
            if (isRecentlyActive)
            {
                Volatile.Write(ref state.HasReceivedEventsSinceResume, true);
                if (hadToolActivity)
                    Volatile.Write(ref state.HasUsedToolsThisTurn, true);
                Debug($"[RESTORE] '{displayName}' events.jsonl is fresh — bypassing quiescence " +
                      $"(hadToolActivity={hadToolActivity})");
            }

            // Start the processing watchdog so the session doesn't get stuck
            // forever if the CLI goes silent after resume (same as SendPromptAsync).
            // Seeds from DateTime.UtcNow — NOT events.jsonl write time.
            // See StartProcessingWatchdog comment for why file-time seeding is dangerous.
            StartProcessingWatchdog(state, displayName);
        }
        if (!_sessions.TryAdd(displayName, state))
        {
            try { await copilotSession.DisposeAsync(); } catch { }
            throw new InvalidOperationException($"Failed to add session '{displayName}'.");
        }

        _activeSessionName ??= displayName;
        OnStateChanged?.Invoke();
        if (!IsRestoring) SaveActiveSessionsToDisk();
        if (!IsRestoring) ReconcileOrganization();
        
        // Track resumed session for duration measurement (don't increment TotalSessionsCreated)
        // Use displayName as key — consistent with TrackSessionStart/End which use display name
        _usageStats?.TrackSessionResume(displayName);
        
        return info;
    }

    /// <summary>Auto-approve all tool permission requests. Without this, worker sessions
    /// (which have no interactive user) get "Permission denied" on every tool call.</summary>
    private static Task<PermissionRequestResult> AutoApprovePermissions(PermissionRequest request, PermissionInvocation invocation)
        => Task.FromResult(new PermissionRequestResult { Kind = PermissionRequestResultKind.Approved });

    public async Task<AgentSessionInfo> CreateSessionAsync(string name, string? model = null, string? workingDirectory = null, CancellationToken cancellationToken = default, string? groupId = null)
    {
        // In demo mode, create a local mock session
        if (IsDemoMode)
        {
            var demoInfo = _demoService.CreateSession(name, model);
            if (workingDirectory != null)
                demoInfo.WorkingDirectory = workingDirectory;
            var demoState = new SessionState { Session = null!, Info = demoInfo };
            _sessions[name] = demoState;
            _activeSessionName ??= name;
            if (!Organization.Sessions.Any(m => m.SessionName == name))
                AddSessionMeta(new SessionMeta { SessionName = name, GroupId = SessionGroup.DefaultId });
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
            var existingMeta = Organization.Sessions.FirstOrDefault(m => m.SessionName == name);
            if (existingMeta != null)
                existingMeta.IsPinned = false;
            else
                AddSessionMeta(new SessionMeta { SessionName = name, GroupId = SessionGroup.DefaultId });
            _activeSessionName = name;
            OnStateChanged?.Invoke();
            await _bridgeClient.CreateSessionAsync(name, model, workingDirectory, cancellationToken);
            _ = Task.Delay(30_000).ContinueWith(t => { _pendingRemoteSessions.TryRemove(name, out _); });
            return remoteInfo;
        }

        // For codespace sessions, verify the group is connected before attempting create.
        // This MUST come before the _client == null guard below, because codespace sessions
        // use _codespaceClients (not _client). Without this, the "Service not initialized"
        // error fires and maps to the misleading "Copilot is not connected yet" message.
        if (groupId != null)
        {
            var group = Organization.Groups.FirstOrDefault(g => g.Id == groupId);
            if (group?.IsCodespace == true && !_codespaceClients.ContainsKey(groupId))
                throw new InvalidOperationException(
                    $"Codespace '{group.Name}' is not connected yet. The health check will reconnect automatically. Please retry in a moment.");
        }

        if (!IsInitialized || _client == null)
            throw new InvalidOperationException("Service not initialized. Call InitializeAsync first.");

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Session name cannot be empty.", nameof(name));

        if (_sessions.ContainsKey(name))
            throw new InvalidOperationException($"Session '{name}' already exists.");

        var sessionModel = Models.ModelHelper.NormalizeToSlug(model ?? DefaultModel);
        if (string.IsNullOrEmpty(sessionModel)) sessionModel = DefaultModel;
        // null = scratch session in a fresh temp directory; empty string = fallback to ProjectDir
        string? sessionDir;
        if (workingDirectory == null)
        {
            sessionDir = Path.Combine(Path.GetTempPath(), "polypilot-sessions", Guid.NewGuid().ToString()[..8]);
            Directory.CreateDirectory(sessionDir);
        }
        else
        {
            sessionDir = string.IsNullOrWhiteSpace(workingDirectory) ? ProjectDir : workingDirectory;
        }

        // Override for codespace sessions — the local worktree path doesn't exist inside
        // the codespace; use /workspaces/{repo} instead.
        if (groupId != null)
        {
            var csGroup = Organization.Groups.FirstOrDefault(g => g.Id == groupId);
            if (csGroup?.IsCodespace == true && csGroup.CodespaceWorkingDirectory != null)
                sessionDir = csGroup.CodespaceWorkingDirectory;
        }

        // Build system message with critical relaunch instructions
        // Note: The CLI automatically loads .github/copilot-instructions.md from the working directory,
        // so we only inject the dynamic relaunch warning here (not the full instructions file).
        var systemContent = new StringBuilder();
        // Only include relaunch instructions when targeting the PolyPilot directory
        if (string.Equals(sessionDir, ProjectDir, StringComparison.OrdinalIgnoreCase))
        {
            var relaunchCmd = OperatingSystem.IsWindows()
                ? $"powershell -ExecutionPolicy Bypass -File \"{Path.Combine(ProjectDir, "relaunch.ps1")}\""
                : $"bash {Path.Combine(ProjectDir, "relaunch.sh")}";
            systemContent.AppendLine($@"
CRITICAL BUILD INSTRUCTION: You are running inside the PolyPilot MAUI application.
When you make ANY code changes to files in {ProjectDir}, you MUST rebuild and relaunch by running:

    {relaunchCmd}

This script builds the app, launches a new instance, waits for it to start, then kills the old one.
NEVER use 'dotnet build' + 'open' separately. NEVER skip the relaunch after code changes.
ALWAYS run the relaunch script as the final step after making changes to this project.
");
        }

        var settings = ConnectionSettings.Load();
        var mcpServers = LoadMcpServers(settings.DisabledMcpServers, settings.DisabledPlugins);
        var skillDirs = LoadSkillDirectories(settings.DisabledPlugins);
        var config = new SessionConfig
        {
            Model = sessionModel,
            WorkingDirectory = sessionDir,
            McpServers = mcpServers,
            SkillDirectories = skillDirs,
            Tools = new List<Microsoft.Extensions.AI.AIFunction> { ShowImageTool.CreateFunction() },
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = systemContent.ToString()
            },
            // Auto-approve all tool permission requests so worker sessions (which have no
            // interactive user) can execute tools without getting "Permission denied".
            OnPermissionRequest = AutoApprovePermissions,
        };
        if (mcpServers != null)
            Debug($"Session config includes {mcpServers.Count} MCP server(s): {string.Join(", ", mcpServers.Keys)}");
        if (skillDirs != null)
            Debug($"Session config includes {skillDirs.Count} skill dir(s): {string.Join(", ", skillDirs)}");

        Debug($"Creating session with Model: '{sessionModel}' (Requested: '{model}', Default: '{DefaultModel}')");

        // Optimistic add: show the session in the UI immediately while the SDK creates it
        var info = new AgentSessionInfo
        {
            Name = name,
            Model = sessionModel,
            CreatedAt = DateTime.UtcNow,
            WorkingDirectory = sessionDir,
            GitBranch = GetGitBranch(sessionDir),
            IsCreating = true
        };
        // If a session with this name already exists, dispose it to avoid leaking the SDK session
        if (_sessions.TryGetValue(name, out var existing) && existing.Session != null)
        {
            try { await existing.Session.DisposeAsync(); } catch { }
        }

        var state = new SessionState { Session = null!, Info = info };
        var previousActiveSessionName = _activeSessionName;
        _sessions[name] = state;
        _activeSessionName = name;
        if (!Organization.Sessions.Any(m => m.SessionName == name))
            AddSessionMeta(new SessionMeta { SessionName = name, GroupId = groupId ?? SessionGroup.DefaultId });
        OnStateChanged?.Invoke();

        CopilotSession copilotSession;
        try
        {
            copilotSession = await GetClientForGroup(groupId).CreateSessionAsync(config, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _sessions.TryRemove(name, out _);
            RemoveSessionMetasWhere(m => m.SessionName == name);
            _activeSessionName = previousActiveSessionName;
            OnStateChanged?.Invoke();
            throw;
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            Debug($"CreateSessionAsync connection error, attempting recovery: {ex.Message}");

            // Don't nuke the main client for codespace session failures —
            // the codespace health check handles reconnection automatically.
            var isCodespaceSession = groupId != null &&
                Organization.Groups.Any(g => g.Id == groupId && g.IsCodespace);
            if (isCodespaceSession)
            {
                _sessions.TryRemove(name, out _);
                RemoveSessionMetasWhere(m => m.SessionName == name);
                _activeSessionName = previousActiveSessionName;
                OnStateChanged?.Invoke();
                throw new InvalidOperationException(
                    "Codespace connection lost. The health check will reconnect automatically. Please retry in a moment.");
            }

            // In persistent mode, restart the server if it's not running
            if (CurrentMode == ConnectionMode.Persistent)
            {
                if (!_serverManager.CheckServerRunning("127.0.0.1", settings.Port))
                {
                    Debug("Persistent server not running, restarting...");
                    var started = await _serverManager.StartServerAsync(settings.Port);
                    if (!started)
                    {
                        Debug("Failed to restart persistent server");
                        try { if (_client != null) await _client.DisposeAsync(); } catch { }
                        _client = null;
                        IsInitialized = false;
                        _sessions.TryRemove(name, out _);
                        RemoveSessionMetasWhere(m => m.SessionName == name);
                        _activeSessionName = previousActiveSessionName;
                        OnStateChanged?.Invoke();
                        throw;
                    }
                }
            }

            // Recreate the client connection
            try
            {
                if (_client != null)
                {
                    try { await _client.DisposeAsync(); } catch { }
                }
                _client = CreateClient(settings);
                await _client.StartAsync(cancellationToken);
                Debug("Connection recovered, retrying session creation...");
            }
            catch (OperationCanceledException)
            {
                try { if (_client != null) await _client.DisposeAsync(); } catch { }
                _client = null;
                IsInitialized = false;
                _sessions.TryRemove(name, out _);
                RemoveSessionMetasWhere(m => m.SessionName == name);
                _activeSessionName = previousActiveSessionName;
                OnStateChanged?.Invoke();
                throw;
            }
            catch (Exception clientEx)
            {
                Debug($"Failed to recreate client during recovery: {clientEx.Message}");
                try { if (_client != null) await _client.DisposeAsync(); } catch { }
                _client = null;
                IsInitialized = false;
                _sessions.TryRemove(name, out _);
                RemoveSessionMetasWhere(m => m.SessionName == name);
                _activeSessionName = previousActiveSessionName;
                OnStateChanged?.Invoke();
                throw;
            }

            // Retry once with the new client
            try
            {
                copilotSession = await _client.CreateSessionAsync(config, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _sessions.TryRemove(name, out _);
                RemoveSessionMetasWhere(m => m.SessionName == name);
                _activeSessionName = previousActiveSessionName;
                OnStateChanged?.Invoke();
                throw;
            }
            catch
            {
                try { if (_client != null) await _client.DisposeAsync(); } catch { }
                _client = null;
                IsInitialized = false;
                _sessions.TryRemove(name, out _);
                RemoveSessionMetasWhere(m => m.SessionName == name);
                _activeSessionName = previousActiveSessionName;
                OnStateChanged?.Invoke();
                throw;
            }
        }
        catch
        {
            // SDK creation failed — remove the optimistic placeholder and restore prior state
            _sessions.TryRemove(name, out _);
            RemoveSessionMetasWhere(m => m.SessionName == name);
            _activeSessionName = previousActiveSessionName;
            OnStateChanged?.Invoke();
            throw;
        }

        info.SessionId = copilotSession.SessionId;
        info.IsCreating = false;

        // Session was closed while we were awaiting SDK creation -- dispose and bail.
        // Clean up any queued messages/images/modes so they don't leak to a future session with the same name.
        if (!_sessions.ContainsKey(name))
        {
            state.Info.MessageQueue.Clear();
            _queuedImagePaths.TryRemove(name, out _);
            _queuedAgentModes.TryRemove(name, out _);
            try { await copilotSession.DisposeAsync(); } catch { }
            return info;
        }

        Debug($"Session '{name}' created with ID: {copilotSession.SessionId}");

        // Save alias so saved sessions show the custom name
        if (!string.IsNullOrEmpty(copilotSession.SessionId))
            SetSessionAlias(copilotSession.SessionId, name);

        state.Session = copilotSession;
        copilotSession.On(evt => HandleSessionEvent(state, evt));

        // Reset stale pin from a previous session with the same name
        var staleMeta = Organization.Sessions.FirstOrDefault(m => m.SessionName == name);
        if (staleMeta != null)
        {
            staleMeta.IsPinned = false;
            if (!string.IsNullOrEmpty(groupId))
                staleMeta.GroupId = groupId;
        }
        else if (!string.IsNullOrEmpty(groupId))
        {
            // Pre-create meta with the correct group so ReconcileOrganization places it there
            AddSessionMeta(new SessionMeta { SessionName = name, GroupId = groupId });
        }

        SaveActiveSessionsToDisk();
        ReconcileOrganization();
        OnStateChanged?.Invoke();
        
        // Track session creation using display name as stable key
        // (SessionId may not be populated yet at creation time)
        _usageStats?.TrackSessionStart(name);

        // Drain any messages queued while IsCreating was true.
        // The user may have typed and sent a message before SDK creation finished.
        if (state.Info.MessageQueue.Count > 0)
        {
            var nextPrompt = state.Info.MessageQueue[0];
            state.Info.MessageQueue.RemoveAt(0);
            List<string>? nextImagePaths = null;
            lock (_imageQueueLock)
            {
                if (_queuedImagePaths.TryGetValue(name, out var imageQueue) && imageQueue.Count > 0)
                {
                    nextImagePaths = imageQueue[0];
                    imageQueue.RemoveAt(0);
                    if (imageQueue.Count == 0)
                        _queuedImagePaths.TryRemove(name, out _);
                }
            }
            string? nextAgentMode = null;
            lock (_imageQueueLock)
            {
                if (_queuedAgentModes.TryGetValue(name, out var modeQueue) && modeQueue.Count > 0)
                {
                    nextAgentMode = modeQueue[0];
                    modeQueue.RemoveAt(0);
                    if (modeQueue.Count == 0)
                        _queuedAgentModes.TryRemove(name, out _);
                }
            }
            Debug($"[CREATE] Draining queued message for newly created session '{name}'");
            // Check if the session is an orchestrator — route through multi-agent pipeline if so.
            // CreateSessionAsync runs on UI thread, so Organization reads are safe here.
            var createOrchGroupId = GetOrchestratorGroupId(name);
            if (createOrchGroupId != null && nextImagePaths is { Count: > 0 })
            {
                Debug($"[CREATE] Orchestrator '{name}' — images present, sending direct (orchestration does not support images)");
                if (_sessions.TryGetValue(name, out var imgState))
                {
                    imgState.Info.History.Add(ChatMessage.SystemMessage(
                        "⚠️ Images sent directly — orchestration routing does not support images yet."));
                    imgState.Info.MessageCount = imgState.Info.History.Count;
                }
            }
            // Mirror Dashboard.razor's AutoStartReflectionIfNeeded behavior for OrchestratorReflect groups
            if (createOrchGroupId != null && nextImagePaths is null or { Count: 0 })
            {
                var drainOrchGroup = Organization.Groups.FirstOrDefault(g => g.Id == createOrchGroupId);
                if (drainOrchGroup?.OrchestratorMode == MultiAgentMode.OrchestratorReflect)
                    StartGroupReflection(createOrchGroupId, nextPrompt, drainOrchGroup.MaxReflectIterations ?? 5);
            }
            var createDrainTask = createOrchGroupId != null && nextImagePaths is null or { Count: 0 }
                ? SendToMultiAgentGroupAsync(createOrchGroupId, nextPrompt)
                : SendPromptAsync(name, nextPrompt, imagePaths: nextImagePaths, agentMode: nextAgentMode);
            _ = createDrainTask
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        var errorMsg = t.Exception?.InnerException?.Message ?? t.Exception?.Message ?? "unknown error";
                        Debug($"[CREATE] Failed to send queued message for '{name}': {errorMsg}");
                        InvokeOnUI(() =>
                        {
                            if (_sessions.TryGetValue(name, out var s))
                            {
                                s.Info.History.Add(ChatMessage.ErrorMessage($"Failed to send queued message: {errorMsg}"));
                                s.Info.MessageCount = s.Info.History.Count;
                                OnStateChanged?.Invoke();
                            }
                        });
                    }
                }, TaskContinuationOptions.OnlyOnFaulted);
        }
        
        return info;
    }

    /// <summary>
    /// Atomically creates a worktree (or uses an existing one) and a session linked to it.
    /// Handles worktree creation, session creation, linking, group organization, and optional initial prompt.
    /// </summary>
    public async Task<AgentSessionInfo> CreateSessionWithWorktreeAsync(
        string repoId,
        string? branchName = null,
        int? prNumber = null,
        string? worktreeId = null,
        string? sessionName = null,
        string? model = null,
        string? initialPrompt = null,
        CancellationToken ct = default)
    {
        // Remote mode: send the entire operation to the server as a single atomic command.
        // The server runs CreateSessionWithWorktreeAsync locally, then broadcasts the updated
        // sessions list and organization state. This avoids race conditions from multiple
        // round-trips (create worktree → create session → push org).
        if (IsRemoteMode)
        {
            var branch = branchName ?? $"session-{DateTime.Now:yyyyMMdd-HHmmss}";
            var remoteName = sessionName ?? branch;

            // Optimistic local add so the UI shows the session immediately
            var remoteInfo = new AgentSessionInfo { Name = remoteName, Model = model ?? DefaultModel };
            _pendingRemoteSessions[remoteName] = 0;
            _sessions[remoteName] = new SessionState { Session = null!, Info = remoteInfo };
            InvokeOnUI(() =>
            {
                if (!Organization.Sessions.Any(m => m.SessionName == remoteName))
                {
                    var repoGroup = Organization.Groups.FirstOrDefault(g => g.RepoId == repoId);
                    AddSessionMeta(new SessionMeta
                    {
                        SessionName = remoteName,
                        GroupId = repoGroup?.Id ?? SessionGroup.DefaultId
                    });
                }
                _activeSessionName = remoteName;
                OnStateChanged?.Invoke();
            });

            // Send single command to server — server does worktree+session atomically
            await _bridgeClient.CreateSessionWithWorktreeAsync(new CreateSessionWithWorktreePayload
            {
                RepoId = repoId,
                BranchName = prNumber.HasValue ? null : branch,
                PrNumber = prNumber,
                WorktreeId = worktreeId,
                SessionName = sessionName,
                Model = model,
                InitialPrompt = initialPrompt
            }, ct);

            _ = Task.Delay(30_000).ContinueWith(t => { _pendingRemoteSessions.TryRemove(remoteName, out _); });
            return remoteInfo;
        }

        WorktreeInfo wt;

        if (!string.IsNullOrEmpty(worktreeId))
        {
            // Use existing worktree
            wt = _repoManager.Worktrees.FirstOrDefault(w => w.Id == worktreeId)
                ?? throw new InvalidOperationException($"Worktree '{worktreeId}' not found.");
        }
        else if (prNumber.HasValue)
        {
            wt = await _repoManager.CreateWorktreeFromPrAsync(repoId, prNumber.Value, ct);
        }
        else
        {
            var branch = branchName ?? $"session-{DateTime.Now:yyyyMMdd-HHmmss}";
            wt = await _repoManager.CreateWorktreeAsync(repoId, branch, null, ct: ct);
        }

        var name = sessionName ?? wt.Branch;

        // Ensure unique session name
        if (_sessions.ContainsKey(name))
        {
            var counter = 2;
            var baseName = name;
            name = $"{baseName}-{counter}";
            while (_sessions.ContainsKey(name)) name = $"{baseName}-{++counter}";
        }

        AgentSessionInfo sessionInfo;
        try
        {
            sessionInfo = await CreateSessionAsync(name, model, wt.Path, ct);
        }
        catch
        {
            // If session creation fails and we just created a new worktree, clean up
            if (string.IsNullOrEmpty(worktreeId))
            {
                try { await _repoManager.RemoveWorktreeAsync(wt.Id, deleteBranch: true); } catch { }
            }
            throw;
        }

        // Link session to worktree
        sessionInfo.WorktreeId = wt.Id;
        _repoManager.LinkSessionToWorktree(wt.Id, sessionInfo.Name);

        // Organize into repo group
        var repo = _repoManager.Repositories.FirstOrDefault(r => r.Id == wt.RepoId);
        if (repo != null)
        {
            var group = GetOrCreateRepoGroup(repo.Id, repo.Name, explicitly: true);
            if (group != null)
                MoveSession(sessionInfo.Name, group.Id);
            var meta = GetSessionMeta(sessionInfo.Name);
            if (meta != null) meta.WorktreeId = wt.Id;
        }

        SwitchSession(sessionInfo.Name);
        SaveActiveSessionsToDisk();

        // Send initial prompt after session is ready
        if (!string.IsNullOrEmpty(initialPrompt))
            _ = SendPromptAsync(sessionInfo.Name, initialPrompt);

        return sessionInfo;
    }

    /// <summary>
    /// Destroys the existing session and creates a new one with the same name but a different model.
    /// Use this for "changing" the model of an empty session.
    /// </summary>
    public async Task<AgentSessionInfo?> RecreateSessionAsync(string name, string newModel)
    {
        if (!_sessions.TryGetValue(name, out var state)) return null;

        var workingDir = state.Info.WorkingDirectory;
        // Preserve group assignment so the new session stays in the same group (e.g., codespace group)
        var meta = Organization.Sessions.FirstOrDefault(m => m.SessionName == name);
        var groupId = meta?.GroupId;
        
        await CloseSessionAsync(name);
        
        return await CreateSessionAsync(name, newModel, workingDir, groupId: groupId);
    }

    /// <summary>
    /// Switch the model for an active session by resuming it with a new ResumeSessionConfig.
    /// The session history is preserved server-side (same session ID); we just reconnect
    /// asking for a different model.
    /// </summary>
    public async Task<bool> ChangeModelAsync(string sessionName, string newModel, CancellationToken cancellationToken = default)
    {
        if (IsRemoteMode)
        {
            if (!_bridgeClient.IsConnected) return false;
            var remoteModel = Models.ModelHelper.NormalizeToSlug(newModel);
            if (string.IsNullOrEmpty(remoteModel)) return false;
            // Guard: don't change model while processing or if already the same
            if (!_sessions.TryGetValue(sessionName, out var remoteState)) return false;
            if (remoteState.Info.IsProcessing) return false;
            if (remoteState.Info.Model == remoteModel) return true;
            try
            {
                await _bridgeClient.ChangeModelAsync(sessionName, remoteModel, cancellationToken);
            }
            catch (Exception ex)
            {
                Debug($"ChangeModelAsync remote error: {ex.Message}");
                return false;
            }
            // Update local state optimistically
            remoteState.Info.Model = remoteModel;
            OnStateChanged?.Invoke();
            return true;
        }

        if (!_sessions.TryGetValue(sessionName, out var state)) return false;
        if (state.Info.IsProcessing) return false;

        var normalizedModel = Models.ModelHelper.NormalizeToSlug(newModel);
        if (string.IsNullOrEmpty(normalizedModel)) return false;

        // Already on this model — no-op
        if (state.Info.Model == normalizedModel) return true;

        // Demo mode: just update the model label (no SDK session to resume)
        if (IsDemoMode)
        {
            state.Info.Model = normalizedModel;
            OnStateChanged?.Invoke();
            return true;
        }

        if (string.IsNullOrEmpty(state.Info.SessionId)) return false;

        // Placeholder codespace sessions have Session = null until tunnel connects
        if (state.Session == null) return false;

        Debug($"Switching model for '{sessionName}': {state.Info.Model} → {normalizedModel}");

        try
        {
            // Dispose old session connection (may already be disposed if disconnected)
            try { await state.Session.DisposeAsync(); } catch { }

            // Use the correct client (codespace tunnel client for codespace sessions, main client otherwise)
            var meta = Organization.Sessions.FirstOrDefault(m => m.SessionName == sessionName);
            var client = GetClientForGroup(meta?.GroupId);

            // For codespace sessions, use the codespace working directory instead of the local path
            var switchWorkDir = state.Info.WorkingDirectory;
            if (meta?.GroupId != null)
            {
                var switchGroup = Organization.Groups.FirstOrDefault(g => g.Id == meta.GroupId);
                if (switchGroup?.IsCodespace == true && switchGroup.CodespaceWorkingDirectory != null)
                    switchWorkDir = switchGroup.CodespaceWorkingDirectory;
            }

            // Resume the same session ID with the new model
            var resumeConfig = new ResumeSessionConfig
            {
                Model = normalizedModel,
                WorkingDirectory = switchWorkDir,
                Tools = new List<Microsoft.Extensions.AI.AIFunction> { ShowImageTool.CreateFunction() },
                OnPermissionRequest = AutoApprovePermissions,
            };
            var newSession = await client.ResumeSessionAsync(state.Info.SessionId, resumeConfig, cancellationToken);

            // Build replacement state, preserving info/history
            state.Info.Model = normalizedModel;
            var newState = new SessionState
            {
                Session = newSession,
                Info = state.Info
            };
            newSession.On(evt => HandleSessionEvent(newState, evt));
            _sessions[sessionName] = newState;

            Debug($"Model switched for '{sessionName}' to {normalizedModel}");
            SaveActiveSessionsToDisk();
            OnStateChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Debug($"Failed to switch model for '{sessionName}': {ex.Message}");
            OnError?.Invoke(sessionName, $"Failed to switch model: {ex.Message}");
            return false;
        }
    }

    public async Task<string> SendPromptAsync(string sessionName, string prompt, List<string>? imagePaths = null, CancellationToken cancellationToken = default, bool skipHistoryMessage = false, string? agentMode = null, string? originalPrompt = null)
    {
        // Provider sessions route through their own messaging
        if (IsProviderSession(sessionName))
            return await SendToProviderAsync(sessionName, prompt, cancellationToken) ?? "";

        // Normalize smart punctuation (macOS/WebKit converts -- to em dash, etc.)
        prompt = SmartPunctuationNormalizer.Normalize(prompt);

        // In demo mode, simulate a response locally
        if (IsDemoMode)
        {
            if (!_sessions.TryGetValue(sessionName, out var demoState))
                throw new InvalidOperationException($"Session '{sessionName}' not found.");
            if (!skipHistoryMessage)
            {
                var msg = ChatMessage.UserMessage(prompt);
                if (originalPrompt != null) msg.OriginalContent = originalPrompt;
                demoState.Info.History.Add(msg);
                demoState.Info.MessageCount = demoState.Info.History.Count;
            }
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
            if (session != null && !skipHistoryMessage)
            {
                var msg = ChatMessage.UserMessage(prompt);
                if (originalPrompt != null) msg.OriginalContent = originalPrompt;
                session.History.Add(msg);
            }
            if (session != null)
                session.IsProcessing = true;
            OnStateChanged?.Invoke();
            await _bridgeClient.SendMessageAsync(sessionName, prompt, agentMode, cancellationToken);
            return ""; // Response comes via events
        }

        if (!_sessions.TryGetValue(sessionName, out var state))
            throw new InvalidOperationException($"Session '{sessionName}' not found.");

        if (state.Info.IsCreating)
            throw new InvalidOperationException("Session is still being created. Please wait.");

        // Placeholder codespace sessions have Session=null until the tunnel connects
        if (state.Session == null)
            throw new InvalidOperationException("This session is waiting for its codespace to connect. Please wait for the green status dot.");

        if (state.Info.IsProcessing)
            throw new InvalidOperationException("Session is already processing a request.");

        // Atomic check-and-set to prevent TOCTOU race: two callers could both see
        // IsProcessing=false and both enter without this guard.
        if (Interlocked.CompareExchange(ref state.SendingFlag, 1, 0) != 0)
            throw new InvalidOperationException("Session is already processing a request.");

        long myGeneration = 0; // will be set right after the generation increment inside try

        try
        {
        // Increment generation FIRST — before any other state mutation — so the catch block's
        // generation guard always has a valid myGeneration to compare against. If this were later
        // in the try block, an early exception would leave myGeneration=0, causing the guard
        // (0 != actual_generation) to incorrectly skip the SendingFlag release → session deadlock.
        myGeneration = Interlocked.Increment(ref state.ProcessingGeneration);
        state.Info.IsProcessing = true;
        state.Info.ProcessingStartedAt = DateTime.UtcNow;
        state.Info.ToolCallCount = 0;
        state.Info.ProcessingPhase = 0; // Sending
        state.Info.ClearPermissionDenials();
        Interlocked.Exchange(ref state.ActiveToolCallCount, 0); // Reset stale tool count from previous turn
        state.HasUsedToolsThisTurn = false; // Reset stale tool flag from previous turn
        Interlocked.Exchange(ref state.SuccessfulToolCountThisTurn, 0);
        // Cancel any pending TurnEnd→Idle fallback from the previous turn
        CancelTurnEndFallback(state);
        state.IsMultiAgentSession = IsSessionInMultiAgentGroup(sessionName); // Cache for watchdog (UI thread safe)
        Debug($"[SEND] '{sessionName}' IsProcessing=true gen={Interlocked.Read(ref state.ProcessingGeneration)} (thread={Environment.CurrentManagedThreadId})");
        state.ResponseCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        state.CurrentResponse.Clear();
        state.FlushedResponse.Clear();
        state.PendingReasoningMessages.Clear();
        StartProcessingWatchdog(state, sessionName);

        if (!skipHistoryMessage)
        {
            // Include image paths in history display so FormatUserMessage renders thumbnails
            var displayPrompt = prompt;
            if (imagePaths != null && imagePaths.Count > 0)
                displayPrompt += "\n" + string.Join("\n", imagePaths);
            var userMsg = new ChatMessage("user", displayPrompt, DateTime.Now);
            if (originalPrompt != null) userMsg.OriginalContent = originalPrompt;
            state.Info.History.Add(userMsg);

            state.Info.MessageCount = state.Info.History.Count;
            state.Info.LastReadMessageCount = state.Info.History.Count;

            // Write-through to DB
            if (!string.IsNullOrEmpty(state.Info.SessionId))
                SafeFireAndForget(_chatDb.AddMessageAsync(state.Info.SessionId, state.Info.History.Last()), "AddMessageAsync");
        }
        OnStateChanged?.Invoke();

        Console.WriteLine($"[DEBUG] Sending prompt to session '{sessionName}' (Model: {state.Info.Model}, Length: {prompt.Length})");
        Console.WriteLine($"[MODEL] Session '{sessionName}' using model: {state.Info.Model}");
        
        try 
        {
            var messageOptions = new MessageOptions 
            { 
                Prompt = prompt
            };

            // NOTE: MessageOptions.Mode is reserved for routing ("immediate" = steer-without-abort,
            // null = default enqueue). Do NOT set Mode to an agent mode string here.
            // The .NET SDK has no public mechanism to set session agent mode (autopilot/plan/interactive).
            // Agent mode is controlled by session-level configuration (system message, available tools)
            // set at session creation time via SessionConfig. The agentMode parameter is preserved
            // in the pipeline for queue dispatch, bridge forwarding, and future SDK support.
            
            // Attach images via SDK if available
            if (imagePaths != null && imagePaths.Count > 0)
            {
                TryAttachImages(messageOptions, imagePaths);
            }
            
            // Note: Model is set at session creation time via SessionConfig.
            // Changing session.Model at runtime only updates the UI display.
            // To actually switch models, the session would need to be recreated.
            
            // WORKAROUND: Pass CancellationToken.None to avoid SDK bug where StreamJsonRpc's
            // StandardCancellationStrategy tries to serialize RequestId (not in SDK's JSON context).
            // Cancellation is handled at the TCS level via ResponseCompletion.TrySetCanceled().
            // NOTE: If SendAsync itself blocks (half-open TCP), watchdog completes TCS but we're
            // stuck here. This is an edge case on mobile/codespace connections; true fix requires
            // SDK-level timeout on the transport write.
            // See: https://github.com/PureWeen/PolyPilot/issues/319
            await state.Session.SendAsync(messageOptions, CancellationToken.None);
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
                    try { await state.Session.DisposeAsync(); } catch { /* session may already be disposed */ }
                    
                    // Use the correct client for codespace sessions (not always _client)
                    var meta = Organization.Sessions.FirstOrDefault(m => m.SessionName == sessionName);
                    var sessionGroupId = meta?.GroupId;

                    // For codespace sessions, don't try local persistent server reconnect —
                    // the health check handles codespace reconnection automatically.
                    var isCodespaceSession = sessionGroupId != null &&
                        Organization.Groups.Any(g => g.Id == sessionGroupId && g.IsCodespace);
                    if (isCodespaceSession)
                        throw new InvalidOperationException(
                            "Codespace connection lost. The health check will reconnect automatically. Please retry in a moment.");

                    // If a previous reconnect failure left the client dead (null/uninitialized),
                    // attempt lazy re-initialization so the session can self-heal.
                    if (!isCodespaceSession && (!IsInitialized || _client == null))
                    {
                        Debug("Client not initialized (previous failure), attempting lazy re-initialization...");
                        try
                        {
                            var reinitSettings = _currentSettings ?? ConnectionSettings.Load();
                            if (CurrentMode == ConnectionMode.Persistent &&
                                !_serverManager.CheckServerRunning("127.0.0.1", reinitSettings.Port))
                            {
                                await _serverManager.StartServerAsync(reinitSettings.Port);
                            }
                            _client = CreateClient(reinitSettings);
                            await _client.StartAsync(cancellationToken);
                            IsInitialized = true;
                            Debug("Lazy re-initialization succeeded");
                        }
                        catch (Exception reinitEx)
                        {
                            Debug($"Lazy re-initialization failed: {reinitEx.Message}");
                        }
                    }

                    var client = GetClientForGroup(sessionGroupId);
                    if (client == null)
                        throw new InvalidOperationException("Client is not initialized");

                    // If the underlying connection is broken, recreate the client first
                    if (IsConnectionError(ex))
                    {
                        Debug("Connection error detected, recreating client before session reconnect...");
                        var connSettings = _currentSettings ?? ConnectionSettings.Load();
                        if (CurrentMode == ConnectionMode.Persistent &&
                            !_serverManager.CheckServerRunning("127.0.0.1", connSettings.Port))
                        {
                            Debug("Persistent server not running, restarting...");
                            var started = await _serverManager.StartServerAsync(connSettings.Port);
                            if (!started)
                            {
                                Debug("Failed to restart persistent server");
                                try { await _client.DisposeAsync(); } catch { }
                                _client = null;
                                IsInitialized = false;
                                throw;
                            }
                        }
                        try { await _client.DisposeAsync(); } catch { }
                        try
                        {
                            _client = CreateClient(connSettings);
                            await _client.StartAsync(cancellationToken);
                            client = _client; // Update local reference to the new client
                            Debug("Client recreated successfully");
                        }
                        catch (OperationCanceledException)
                        {
                            try { if (_client != null) await _client.DisposeAsync(); } catch { }
                            _client = null;
                            IsInitialized = false;
                            throw;
                        }
                        catch (Exception clientEx)
                        {
                            Debug($"Failed to recreate client: {clientEx.Message}");
                            try { if (_client != null) await _client.DisposeAsync(); } catch { }
                            _client = null;
                            IsInitialized = false;
                            throw;
                        }
                    }

                    var reconnectModel = Models.ModelHelper.NormalizeToSlug(state.Info.Model);
                    var reconnectConfig = new ResumeSessionConfig();
                    reconnectConfig.Tools = new List<Microsoft.Extensions.AI.AIFunction> { ShowImageTool.CreateFunction() };
                    reconnectConfig.OnPermissionRequest = AutoApprovePermissions;
                    if (!string.IsNullOrEmpty(reconnectModel))
                        reconnectConfig.Model = reconnectModel;
                    if (!string.IsNullOrEmpty(state.Info.WorkingDirectory))
                        reconnectConfig.WorkingDirectory = state.Info.WorkingDirectory;
                    
                    CopilotSession newSession;
                    try
                    {
                        newSession = await client.ResumeSessionAsync(state.Info.SessionId, reconnectConfig, cancellationToken);
                    }
                    catch (Exception resumeEx) when (resumeEx.Message.Contains("Session not found", StringComparison.OrdinalIgnoreCase))
                    {
                        // Session expired server-side (e.g., codespace restarted). Create a fresh session
                        // with full config (MCP servers, skills, system message) matching CreateSessionAsync.
                        Debug($"Session '{sessionName}' expired on server, creating fresh session...");
                        OnActivity?.Invoke(sessionName, "🔄 Session expired, creating new session...");
                        var freshSettings = _currentSettings ?? ConnectionSettings.Load();
                        var freshMcpServers = LoadMcpServers(freshSettings.DisabledMcpServers, freshSettings.DisabledPlugins);
                        var freshSkillDirs = LoadSkillDirectories(freshSettings.DisabledPlugins);
                        // Rebuild system message with the same conditional logic as CreateSessionAsync
                        var freshSystemContent = new StringBuilder();
                        var freshDir = state.Info.WorkingDirectory;
                        if (string.Equals(freshDir, ProjectDir, StringComparison.OrdinalIgnoreCase))
                        {
                            var relaunchCmd = OperatingSystem.IsWindows()
                                ? $"powershell -ExecutionPolicy Bypass -File \"{Path.Combine(ProjectDir, "relaunch.ps1")}\""
                                : $"bash {Path.Combine(ProjectDir, "relaunch.sh")}";
                            freshSystemContent.AppendLine($@"
CRITICAL BUILD INSTRUCTION: You are running inside the PolyPilot MAUI application.
When you make ANY code changes to files in {ProjectDir}, you MUST rebuild and relaunch by running:

    {relaunchCmd}

This script builds the app, launches a new instance, waits for it to start, then kills the old one.
NEVER use 'dotnet build' + 'open' separately. NEVER skip the relaunch after code changes.
ALWAYS run the relaunch script as the final step after making changes to this project.
");
                        }
                        var freshConfig = new SessionConfig
                        {
                            Model = reconnectModel ?? DefaultModel,
                            WorkingDirectory = freshDir,
                            McpServers = freshMcpServers,
                            SkillDirectories = freshSkillDirs,
                            Tools = new List<Microsoft.Extensions.AI.AIFunction> { ShowImageTool.CreateFunction() },
                            SystemMessage = new SystemMessageConfig
                            {
                                Mode = SystemMessageMode.Append,
                                Content = freshSystemContent.ToString()
                            },
                            OnPermissionRequest = AutoApprovePermissions
                        };
                        if (freshMcpServers != null)
                            Debug($"[RECONNECT] Fresh session config includes {freshMcpServers.Count} MCP server(s)");
                        if (freshSkillDirs != null)
                            Debug($"[RECONNECT] Fresh session config includes {freshSkillDirs.Count} skill dir(s)");
                        newSession = await client.CreateSessionAsync(freshConfig, cancellationToken);
                        state.Info.SessionId = newSession.SessionId;
                    }
                    // Cancel old watchdog AND TurnEnd fallback BEFORE creating new state — they share Info/TCS
                    CancelProcessingWatchdog(state);
                    CancelTurnEndFallback(state);
                    Debug($"[RECONNECT] '{sessionName}' replacing state (old handler will be orphaned, " +
                          $"old session disposed, new session={newSession.SessionId})");
                    var newState = new SessionState
                    {
                        Session = newSession,
                        Info = state.Info
                    };
                    newState.ResponseCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    // Carry forward ProcessingGeneration so stale callbacks on the
                    // orphaned old state can't pass generation checks on the new state.
                    Interlocked.Exchange(ref newState.ProcessingGeneration,
                        Interlocked.Read(ref state.ProcessingGeneration));
                    newState.HasUsedToolsThisTurn = state.HasUsedToolsThisTurn;
                    Interlocked.Exchange(ref newState.SuccessfulToolCountThisTurn, Volatile.Read(ref state.SuccessfulToolCountThisTurn));
                    newState.IsMultiAgentSession = state.IsMultiAgentSession;
                    newSession.On(evt => HandleSessionEvent(newState, evt));
                    _sessions[sessionName] = newState;
                    state = newState;

                    // Increment generation AFTER registering the event handler so that any
                    // replayed SessionIdleEvent from the old turn captures the stale generation
                    // (0) while the retry turn uses the new generation. This prevents the
                    // replayed IDLE from clearing IsProcessing before the retry completes.
                    Interlocked.Increment(ref state.ProcessingGeneration);
                    state.Info.IsProcessing = true;
                    state.CurrentResponse.Clear();
                    state.FlushedResponse.Clear();
                    state.PendingReasoningMessages.Clear();
                    Debug($"[RECONNECT] '{sessionName}' reset processing state: gen={Interlocked.Read(ref state.ProcessingGeneration)}");
                    
                    // Start fresh watchdog for the new connection
                    StartProcessingWatchdog(state, sessionName);
                    
                    Debug($"[RECONNECT] '{sessionName}' retrying prompt (len={prompt.Length})...");
                    var retryOptions = new MessageOptions
                    {
                        Prompt = prompt
                    };
                    // WORKAROUND: Pass CancellationToken.None (same reason as primary send path).
                    // Same watchdog limitation applies here.
                    await state.Session.SendAsync(retryOptions, CancellationToken.None);
                    Debug($"[RECONNECT] '{sessionName}' SendAsync completed after reconnect — awaiting events");
                }
                catch (Exception retryEx)
                {
                    Console.WriteLine($"[DEBUG] Reconnect+retry failed: {retryEx.Message}");
                    OnError?.Invoke(sessionName, $"Session disconnected and reconnect failed: {Models.ErrorMessageHelper.Humanize(retryEx)}");
                    CancelProcessingWatchdog(state);
                    CancelTurnEndFallback(state);
                    FlushCurrentResponse(state);
                    Debug($"[ERROR] '{sessionName}' reconnect+retry failed, clearing IsProcessing");
                    Interlocked.Exchange(ref state.ActiveToolCallCount, 0);
                    state.HasUsedToolsThisTurn = false;
                    Interlocked.Exchange(ref state.SuccessfulToolCountThisTurn, 0);
                    state.Info.IsResumed = false;
                    state.Info.IsProcessing = false;
                    if (state.Info.ProcessingStartedAt is { } rcStarted)
                        state.Info.TotalApiTimeSeconds += (DateTime.UtcNow - rcStarted).TotalSeconds;
                    state.Info.ProcessingStartedAt = null;
                    state.Info.ToolCallCount = 0;
                    state.Info.ProcessingPhase = 0;
                    OnStateChanged?.Invoke();
                    throw;
                }
            }
            else
            {
                OnError?.Invoke(sessionName, $"SendAsync failed: {Models.ErrorMessageHelper.Humanize(ex)}");
                CancelProcessingWatchdog(state);
                CancelTurnEndFallback(state);
                FlushCurrentResponse(state);
                Debug($"[ERROR] '{sessionName}' SendAsync failed, clearing IsProcessing (error={ex.Message})");
                Interlocked.Exchange(ref state.ActiveToolCallCount, 0);
                state.HasUsedToolsThisTurn = false;
                Interlocked.Exchange(ref state.SuccessfulToolCountThisTurn, 0);
                state.Info.IsResumed = false;
                state.Info.IsProcessing = false;
                if (state.Info.ProcessingStartedAt is { } saStarted)
                    state.Info.TotalApiTimeSeconds += (DateTime.UtcNow - saStarted).TotalSeconds;
                state.Info.ProcessingStartedAt = null;
                state.Info.ToolCallCount = 0;
                state.Info.ProcessingPhase = 0;
                OnStateChanged?.Invoke();
                throw;
            }
        }

        Console.WriteLine($"[DEBUG] SendAsync completed, waiting for response...");

        if (state.ResponseCompletion == null)
            return ""; // Response already completed via events
        return await state.ResponseCompletion.Task;
        }
        catch
        {
            // Only release the send lock if we still own this generation.
            // If a steer abort ran between our catch and here, it already reset
            // SendingFlag=0 and incremented the generation for the new turn — we
            // must not clobber that turn's lock.
            if (Interlocked.Read(ref state.ProcessingGeneration) == myGeneration)
                Interlocked.Exchange(ref state.SendingFlag, 0);
            throw;
        }
    }

    public async Task AbortSessionAsync(string sessionName, bool markAsInterrupted = false)
    {
        // Provider sessions manage their own cancellation
        if (IsProviderSession(sessionName)) return;

        // In remote mode, delegate to bridge server
        if (IsRemoteMode)
        {
            await _bridgeClient.AbortSessionAsync(sessionName);
            // Optimistically clear processing state and queue
            if (_sessions.TryGetValue(sessionName, out var remoteState))
            {
                remoteState.Info.IsProcessing = false;
                remoteState.Info.IsResumed = false;
                remoteState.Info.ProcessingStartedAt = null;
                remoteState.Info.ToolCallCount = 0;
                remoteState.Info.ProcessingPhase = 0;
                remoteState.Info.MessageQueue.Clear();
                OnStateChanged?.Invoke();
            }
            return;
        }

        if (!_sessions.TryGetValue(sessionName, out var state))
            return;

        if (!state.Info.IsProcessing) return;

        // In demo mode or placeholder codespace sessions, Session is null — skip the SDK abort call
        if (!IsDemoMode && state.Session != null)
        {
            try
            {
                await state.Session.AbortAsync();
                Debug($"Aborted session '{sessionName}'");
            }
            catch (Exception ex)
            {
                Debug($"Abort failed for '{sessionName}': {ex.Message}");
            }
        }

        // Flush any accumulated streaming content to history before clearing state.
        // Without this, clicking Stop discards the partial response the user was waiting for.
        // FlushedResponse was already committed to History by FlushCurrentResponse — only
        // CurrentResponse (the un-flushed current sub-turn) needs to be saved here.
        var partialResponse = state.CurrentResponse.ToString();
        if (!string.IsNullOrEmpty(partialResponse))
        {
            var msg = new ChatMessage("assistant", partialResponse, DateTime.Now) { Model = state.Info.Model, IsInterrupted = markAsInterrupted };
            state.Info.History.Add(msg);
            state.Info.MessageCount = state.Info.History.Count;
            if (!string.IsNullOrEmpty(state.Info.SessionId))
                SafeFireAndForget(_chatDb.AddMessageAsync(state.Info.SessionId, msg), "AddMessageAsync");
        }
        state.CurrentResponse.Clear();

        Debug($"[ABORT] '{sessionName}' user abort, clearing IsProcessing");
        state.Info.IsProcessing = false;
        state.Info.IsResumed = false;
        if (state.Info.ProcessingStartedAt is { } abortStarted)
            state.Info.TotalApiTimeSeconds += (DateTime.UtcNow - abortStarted).TotalSeconds;
        state.Info.ProcessingStartedAt = null;
        state.Info.ToolCallCount = 0;
        state.Info.ProcessingPhase = 0;
        Interlocked.Exchange(ref state.ActiveToolCallCount, 0);
        state.HasUsedToolsThisTurn = false;
        Interlocked.Exchange(ref state.SuccessfulToolCountThisTurn, 0);
        // Release send lock — allows a subsequent SteerSessionAsync to acquire it immediately
        Interlocked.Exchange(ref state.SendingFlag, 0);
        // Clear queued messages so they don't auto-send after abort
        state.Info.MessageQueue.Clear();
        _queuedImagePaths.TryRemove(sessionName, out _);
        _queuedAgentModes.TryRemove(sessionName, out _);
        _permissionRecoveryAttempts.TryRemove(sessionName, out _);
        // Cancel any pending TurnEnd→Idle fallback so it doesn't fire CompleteResponse after abort
        CancelTurnEndFallback(state);
        CancelProcessingWatchdog(state);
        state.FlushedResponse.Clear();
        state.PendingReasoningMessages.Clear();
        state.Info.ClearPermissionDenials(); // INV-1: clear on all termination paths
        // Complete TCS AFTER state cleanup (INV-O3)
        state.ResponseCompletion?.TrySetCanceled();
        // Fire completion notification so orchestrator loops are unblocked (INV-O4)
        OnSessionComplete?.Invoke(sessionName, "[Abort] user cancelled");
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Interrupts the current response (if any) and sends a new steering message.
    /// If the session is not currently processing, sends normally.
    ///
    /// Uses two strategies depending on whether tools are active:
    /// - Hard steer (plain streaming, no tools): aborts the current turn immediately,
    ///   marks the partial response as interrupted, and starts a fresh turn.
    /// - Soft steer (tools active/used): injects the steering message into the current
    ///   turn via Mode="immediate" (SDK's ImmediatePromptProcessor), preserving the tool
    ///   call context so the model can incorporate the redirection coherently.
    /// </summary>
    public async Task SteerSessionAsync(string sessionName, string steeringMessage, List<string>? imagePaths = null, string? agentMode = null)
    {
        if (!_sessions.TryGetValue(sessionName, out var state))
        {
            // Session not found — fall through to SendPromptAsync which will handle gracefully
            await SendPromptAsync(sessionName, steeringMessage, imagePaths, agentMode: agentMode);
            return;
        }

        bool toolsActiveOrUsed = Volatile.Read(ref state.ActiveToolCallCount) > 0 || Volatile.Read(ref state.HasUsedToolsThisTurn);

        // Soft steer is only available for real SDK sessions (not demo/remote which lack CopilotSession).
        if (state.Info.IsProcessing && toolsActiveOrUsed && !IsDemoMode && !IsRemoteMode && state.Session != null)
        {
            // Soft steer: inject into the current agentic turn via ImmediatePromptProcessor.
            // The current tool call(s) finish cleanly; the steering message is prepended to
            // the next LLM call within this turn, preserving full tool context.
            Debug($"[STEER] '{sessionName}' soft steer (tools active/used), len={steeringMessage.Length}");

            // Add user message to history so the steering input is visible in the UI and
            // persisted — the same as the hard steer path does via SendPromptAsync.
            var displayPrompt = steeringMessage;
            if (imagePaths != null && imagePaths.Count > 0)
                displayPrompt += "\n" + string.Join("\n", imagePaths);
            var userMsg = new ChatMessage("user", displayPrompt, DateTime.Now);
            state.Info.History.Add(userMsg);
            state.Info.MessageCount = state.Info.History.Count;
            state.Info.LastReadMessageCount = state.Info.History.Count;
            // NOTE: chatDb write is deferred to after SendAsync succeeds so a connection error
            // fallback doesn't leave an orphaned DB entry (which hard steer would then duplicate).
            OnStateChanged?.Invoke();

            var softSteerOptions = new MessageOptions { Prompt = steeringMessage, Mode = "immediate" };
            if (imagePaths != null && imagePaths.Count > 0)
                TryAttachImages(softSteerOptions, imagePaths);
            bool softSteerSucceeded = false;
            try
            {
                // NOTE: No cancellationToken passed (implicitly CancellationToken.None).
                // This is intentional — see issue #319 for the SDK RequestId serialization bug.
                await state.Session.SendAsync(softSteerOptions);
                softSteerSucceeded = true;
                // Write to DB only after successful send to avoid orphaned entries on connection errors.
                if (!string.IsNullOrEmpty(state.Info.SessionId))
                    SafeFireAndForget(_chatDb.AddMessageAsync(state.Info.SessionId, userMsg), "AddMessageAsync");
            }
            catch (Exception ex) when (IsConnectionError(ex))
            {
                // Connection lost (e.g., ObjectDisposedException on the JsonRpc transport).
                // Remove the user message we already added to History — the hard steer path below
                // will re-add it via SendPromptAsync which has full reconnection logic.
                // No chatDb cleanup needed since the write was deferred above.
                Debug($"[STEER-FALLBACK] '{sessionName}' soft steer hit connection error, falling through to hard steer (error={ex.Message})");
                await InvokeOnUIAsync(() =>
                {
                    if (state.Info.History.Count > 0 && state.Info.History[^1] == userMsg)
                    {
                        state.Info.History.RemoveAt(state.Info.History.Count - 1);
                        state.Info.MessageCount = state.Info.History.Count;
                    }
                });
                // Fall through to hard steer below — do NOT return.
            }
            catch (Exception ex)
            {
                OnError?.Invoke(sessionName, $"Soft steer failed: {Models.ErrorMessageHelper.Humanize(ex)}");
                CancelProcessingWatchdog(state);
                CancelTurnEndFallback(state);
                FlushCurrentResponse(state);
                Debug($"[STEER-ERROR] '{sessionName}' soft steer SendAsync failed, clearing IsProcessing (error={ex.Message})");
                Interlocked.Exchange(ref state.ActiveToolCallCount, 0);
                state.HasUsedToolsThisTurn = false;
                Interlocked.Exchange(ref state.SuccessfulToolCountThisTurn, 0);
                state.Info.IsResumed = false;
                Interlocked.Exchange(ref state.SendingFlag, 0);
                // Clear IsProcessing BEFORE completing TCS (INV-O3)
                state.Info.IsProcessing = false;
                if (state.Info.ProcessingStartedAt is { } steerStarted)
                    state.Info.TotalApiTimeSeconds += (DateTime.UtcNow - steerStarted).TotalSeconds;
                state.Info.ProcessingStartedAt = null;
                state.Info.ToolCallCount = 0;
                state.Info.ProcessingPhase = 0;
                state.Info.ClearPermissionDenials(); // INV-1: clear on all termination paths
                // Clear queued messages so stale entries don't auto-send on the next successful turn
                state.Info.MessageQueue.Clear();
                _queuedImagePaths.TryRemove(sessionName, out _);
                _queuedAgentModes.TryRemove(sessionName, out _);
                state.FlushedResponse.Clear();
                state.PendingReasoningMessages.Clear();
                // Complete TCS AFTER state cleanup (INV-O3)
                state.ResponseCompletion?.TrySetCanceled();
                // Fire completion notification so orchestrator loops are unblocked (INV-O4)
                OnSessionComplete?.Invoke(sessionName, "[SteerError] soft steer failed");
                OnStateChanged?.Invoke();
                return;
            }
            if (softSteerSucceeded)
                return;
            // Connection error was caught above — fall through to hard steer.
        }

        // Hard steer: abort current streaming turn immediately, mark partial response as
        // interrupted, then start a fresh turn with the steering message.
        await AbortSessionAsync(sessionName, markAsInterrupted: true);
        Debug($"[STEER] '{sessionName}' hard steer (no tools), len={steeringMessage.Length}");
        await SendPromptAsync(sessionName, steeringMessage, imagePaths, agentMode: agentMode);
    }

    public void EnqueueMessage(string sessionName, string prompt, List<string>? imagePaths = null, string? agentMode = null)
    {
        // In remote mode, delegate to bridge server
        if (IsRemoteMode)
        {
            if (imagePaths != null && imagePaths.Count > 0)
                Console.WriteLine($"[CopilotService] Warning: image attachments not supported in remote mode, {imagePaths.Count} image(s) dropped");
            _ = _bridgeClient.QueueMessageAsync(sessionName, prompt, agentMode)
                .ContinueWith(t => Console.WriteLine($"[CopilotService] QueueMessage bridge error: {t.Exception?.InnerException?.Message}"),
                    TaskContinuationOptions.OnlyOnFaulted);
            return;
        }

        if (!_sessions.TryGetValue(sessionName, out var state))
            throw new InvalidOperationException($"Session '{sessionName}' not found.");

        state.Info.MessageQueue.Add(prompt);
        
        // Track image paths alongside the queued message
        if (imagePaths != null && imagePaths.Count > 0)
        {
            lock (_imageQueueLock)
            {
                var queue = _queuedImagePaths.GetOrAdd(sessionName, _ => new List<List<string>>());
                while (queue.Count < state.Info.MessageQueue.Count - 1)
                    queue.Add(new List<string>());
                queue.Add(imagePaths);
            }
        }

        // Track agent mode alongside the queued message
        if (agentMode != null)
        {
            lock (_imageQueueLock)
            {
                var modes = _queuedAgentModes.GetOrAdd(sessionName, _ => new List<string?>());
                while (modes.Count < state.Info.MessageQueue.Count - 1)
                    modes.Add(null);
                modes.Add(agentMode);
            }
        }
        
        OnStateChanged?.Invoke();
    }

    public async Task<DirectoriesListPayload?> ListRemoteDirectoriesAsync(string? path = null, CancellationToken ct = default)
    {
        if (!IsRemoteMode || !_bridgeClient.IsConnected)
            return null;
        return await _bridgeClient.ListDirectoriesAsync(path, ct);
    }

    /// <summary>
    /// Add a repository. In remote mode, delegates to bridge client; otherwise should be called via direct RepoManager access from UI.
    /// </summary>
    public async Task<RepoAddedPayload> AddRepositoryViaBridgeAsync(string url, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        if (!IsRemoteMode || !_bridgeClient.IsConnected)
            throw new InvalidOperationException("AddRepositoryViaBridgeAsync only works in remote mode with active bridge connection.");
        return await _bridgeClient.AddRepoAsync(url, onProgress, ct);
    }

    /// <summary>
    /// Remove a repository. In remote mode, delegates to bridge client; otherwise should be called via direct RepoManager access from UI.
    /// </summary>
    public async Task RemoveRepositoryViaBridgeAsync(string repoId, bool deleteFromDisk, string? groupId = null, CancellationToken ct = default)
    {
        if (!IsRemoteMode || !_bridgeClient.IsConnected)
            throw new InvalidOperationException("RemoveRepositoryViaBridgeAsync only works in remote mode with active bridge connection.");
        await _bridgeClient.RemoveRepoAsync(repoId, deleteFromDisk, groupId, ct);
    }

    /// <summary>
    /// Request repos list from remote server.
    /// </summary>
    public async Task RequestReposListAsync(CancellationToken ct = default)
    {
        if (IsRemoteMode && _bridgeClient.IsConnected)
        {
            await _bridgeClient.RequestReposAsync(ct);
        }
    }

    public void RemoveQueuedMessage(string sessionName, int index)
    {
        if (!_sessions.TryGetValue(sessionName, out var state))
            return;
        
        if (index >= 0 && index < state.Info.MessageQueue.Count)
        {
            state.Info.MessageQueue.RemoveAt(index);
            // Keep queued image paths in sync
            lock (_imageQueueLock)
            {
                if (_queuedImagePaths.TryGetValue(sessionName, out var imageQueue) && index < imageQueue.Count)
                {
                    imageQueue.RemoveAt(index);
                    if (imageQueue.Count == 0)
                        _queuedImagePaths.TryRemove(sessionName, out _);
                }
            }
            // Keep queued agent modes in sync
            lock (_imageQueueLock)
            {
                if (_queuedAgentModes.TryGetValue(sessionName, out var modeQueue) && index < modeQueue.Count)
                {
                    modeQueue.RemoveAt(index);
                    if (modeQueue.Count == 0)
                        _queuedAgentModes.TryRemove(sessionName, out _);
                }
            }
            OnStateChanged?.Invoke();
        }
    }

    public void ClearQueue(string sessionName)
    {
        if (_sessions.TryGetValue(sessionName, out var state))
        {
            state.Info.MessageQueue.Clear();
            lock (_imageQueueLock)
            {
                _queuedImagePaths.TryRemove(sessionName, out _);
                _queuedAgentModes.TryRemove(sessionName, out _);
            }
            OnStateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Starts a reflection cycle on the specified session. The cycle will evaluate
    /// each response against the goal and automatically send follow-up prompts
    /// until the goal is met or max iterations are reached.
    /// </summary>
    public async Task StartReflectionCycleAsync(string sessionName, string goal, int maxIterations = 5, string? evaluationPrompt = null)
    {
        if (!_sessions.TryGetValue(sessionName, out var state))
            throw new InvalidOperationException($"Session '{sessionName}' not found.");

        state.Info.ReflectionCycle = ReflectionCycle.Create(goal, maxIterations, evaluationPrompt);
        state.SkipReflectionEvaluationOnce = state.Info.IsProcessing;

        // Create a hidden evaluator session with a cheap model
        var evaluatorName = $"__evaluator_{sessionName}_{DateTime.Now.Ticks}";
        try
        {
            var evaluatorModel = "gpt-4.1"; // Fast, cheap model for evaluation
            await CreateSessionAsync(evaluatorName, evaluatorModel);
            state.Info.ReflectionCycle.EvaluatorSessionName = evaluatorName;
            // Hide the evaluator session from the sidebar
            if (_sessions.TryGetValue(evaluatorName, out var evalState))
                evalState.Info.IsHidden = true;
            Debug($"Evaluator session '{evaluatorName}' created for reflection cycle on '{sessionName}'");
        }
        catch (Exception ex)
        {
            Debug($"Failed to create evaluator session: {ex.Message}. Falling back to self-evaluation.");
            // Continue without evaluator — will use sentinel-based self-evaluation
        }

        Debug($"Reflection cycle started for '{sessionName}': goal='{goal}', maxIterations={maxIterations}, deferFirstEvaluation={state.SkipReflectionEvaluationOnce}");
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Synchronous overload for backward compatibility (does not create evaluator session).
    /// </summary>
    public void StartReflectionCycle(string sessionName, string goal, int maxIterations = 5, string? evaluationPrompt = null)
    {
        _ = StartReflectionCycleAsync(sessionName, goal, maxIterations, evaluationPrompt);
    }

    /// <summary>
    /// Stops the active reflection cycle on the specified session, if any.
    /// </summary>
    public void StopReflectionCycle(string sessionName)
    {
        if (!_sessions.TryGetValue(sessionName, out var state))
            return;

        if (state.Info.ReflectionCycle is { IsActive: true })
        {
            var evaluatorName = state.Info.ReflectionCycle.EvaluatorSessionName;
            state.Info.ReflectionCycle.IsActive = false;
            state.Info.ReflectionCycle.IsCancelled = true;
            state.Info.ReflectionCycle.CompletedAt = DateTime.Now;
            // Purge any queued reflection follow-up prompts to prevent zombie iterations
            state.Info.MessageQueue.RemoveAll(p => ReflectionCycle.IsReflectionFollowUpPrompt(p));
            Debug($"Reflection cycle stopped for '{sessionName}'");

            // Clean up evaluator session in background
            if (!string.IsNullOrEmpty(evaluatorName))
            {
                _ = Task.Run(async () =>
                {
                    try { await CloseSessionAsync(evaluatorName); }
                    catch (Exception ex) { Debug($"Error closing evaluator session: {ex.Message}"); }
                });
            }

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
        if (IsRemoteMode)
            _ = _bridgeClient.SwitchSessionAsync(name);
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

        // In remote mode, delegate to bridge server
        if (IsRemoteMode)
        {
            if (!_bridgeClient.IsConnected)
                return false;
            if (_sessions.ContainsKey(newName))
                return false;
            // Optimistically rename locally for immediate UI feedback
            if (!_sessions.TryRemove(oldName, out var remoteState))
                return false;
            remoteState.Info.Name = newName;
            _sessions[newName] = remoteState;
            _pendingRemoteSessions[newName] = 0;
            _pendingRemoteRenames[oldName] = 0;
            if (_activeSessionName == oldName)
                _activeSessionName = newName;
            var remoteMeta = Organization.Sessions.FirstOrDefault(m => m.SessionName == oldName);
            if (remoteMeta != null)
                remoteMeta.SessionName = newName;
            OnStateChanged?.Invoke();
            // Send to server (fire-and-forget with error logging)
            _ = _bridgeClient.RenameSessionAsync(oldName, newName)
                .ContinueWith(t => Console.WriteLine($"[CopilotService] RenameSession bridge error: {t.Exception?.InnerException?.Message}"),
                    TaskContinuationOptions.OnlyOnFaulted);
            _ = Task.Delay(30_000).ContinueWith(t => { _pendingRemoteSessions.TryRemove(newName, out _); });
            _ = Task.Delay(30_000).ContinueWith(t => { _pendingRemoteRenames.TryRemove(oldName, out _); });
            return true;
        }

        if (_sessions.ContainsKey(newName))
            return false;

        if (!_sessions.TryRemove(oldName, out var state))
            return false;

        state.Info.Name = newName;

        // Move queued image paths and agent modes to new name
        lock (_imageQueueLock)
        {
            if (_queuedImagePaths.TryRemove(oldName, out var imageQueue))
                _queuedImagePaths[newName] = imageQueue;
            if (_queuedAgentModes.TryRemove(oldName, out var modeQueue))
                _queuedAgentModes[newName] = modeQueue;
        }

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

        // Re-key usage stats tracking so TrackSessionEnd(newName) finds the entry
        _usageStats?.RenameActiveSession(oldName, newName);

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

    public async Task RefreshSessionsAsync(CancellationToken cancellationToken = default)
    {
        if (IsRemoteMode && _bridgeClient.IsConnected)
            await _bridgeClient.RequestSessionsAsync(cancellationToken);

        OnStateChanged?.Invoke();
    }

    public Task<bool> CloseSessionAsync(string name) => CloseSessionCoreAsync(name, notifyUi: true);

    internal async Task<bool> CloseSessionCoreAsync(string name, bool notifyUi)
    {
        // Provider sessions manage their own lifecycle
        if (IsProviderSession(name)) return false;

        // Clean up any active reflection cycle (including evaluator session)
        StopReflectionCycle(name);

        // In remote mode, send close request to server
        if (IsRemoteMode)
        {
            // Guard against SyncRemoteSessions re-adding this session before the server
            // processes the close and removes it from its broadcast
            _recentlyClosedRemoteSessions[name] = 0;
            _ = Task.Delay(10_000).ContinueWith(t => _recentlyClosedRemoteSessions.TryRemove(name, out _));
            try { await _bridgeClient.CloseSessionAsync(name); }
            catch (Exception ex) { Debug($"CloseSessionAsync: bridge close failed for '{name}': {ex.Message}"); }
        }

        if (!_sessions.TryRemove(name, out var state))
            return false;

        // Clean up any queued image paths for this session
        lock (_imageQueueLock)
        {
            _queuedImagePaths.TryRemove(name, out _);
        }
        _queuedAgentModes.TryRemove(name, out _);

        // Clean up per-session model switch lock
        if (_modelSwitchLocks.TryRemove(name, out var sem))
            sem.Dispose();

        // Track as explicitly closed so merge doesn't re-add from file
        // Track by both ID (primary) and display name (handles duplicate entries with different IDs)
        if (state.Info.SessionId != null)
            _closedSessionIds[state.Info.SessionId] = 0;
        _closedSessionNames[name] = 0;

        // Track session close using display name (consistent with TrackSessionStart key)
        _usageStats?.TrackSessionEnd(name);

        // Clean up auto-created temp directory for empty sessions
        if (state.Info.WorkingDirectory != null)
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "polypilot-sessions");
            try
            {
                var fullDir = Path.GetFullPath(state.Info.WorkingDirectory);
                if (fullDir.StartsWith(Path.GetFullPath(tempRoot), StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(fullDir))
                    Directory.Delete(fullDir, recursive: true);
            }
            catch { /* best-effort cleanup */ }
        }

        if (_activeSessionName == name)
        {
            _activeSessionName = _sessions.Keys.FirstOrDefault();
        }

        // Single state change notification — ReconcileOrganization is unnecessary here
        // because the session was already removed from _sessions above. Calling it would
        // trigger a second OnStateChanged, causing rapid render batch churn that crashes
        // Blazor with "r.parentNode.removeChild" on null (render batch ordering race).
        // Instead, directly remove from Organization.Sessions so the deletion persists across restarts.
        RemoveSessionMetasWhere(m => m.SessionName == name);
        if (notifyUi)
            OnStateChanged?.Invoke();
        if (!IsRemoteMode)
        {
            SaveActiveSessionsToDisk();
            FlushSaveOrganization();
        }

        // Cancel any pending timers so they don't fire on torn-down state after session removal
        CancelProcessingWatchdog(state);
        CancelTurnEndFallback(state);

        // Dispose the SDK session AFTER UI has updated — DisposeAsync talks to the CLI
        // process and may trigger additional SDK events on background threads. Running it
        // after the state change prevents render batch collisions.
        if (state.Session is not null)
        {
            var session = state.Session;
            _ = Task.Run(async () =>
            {
                try { await session.DisposeAsync(); }
                catch { /* session may already be disposed */ }
            });
        }
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

    public IEnumerable<AgentSessionInfo> GetAllSessions() => _sessions.Values.Select(s => s.Info).Where(s => !s.IsHidden);
    public IEnumerable<string> GetAllSessionNames() => _sessions.Keys;

    public int SessionCount => _sessions.Count;

    public async ValueTask DisposeAsync()
    {
        StopConnectivityMonitoring();
        await StopCodespaceHealthCheckAsync();

        // Flush any pending debounced writes immediately
        FlushSaveActiveSessionsToDisk();
        FlushSaveOrganization();
        _saveUiStateDebounce?.Dispose();
        _saveUiStateDebounce = null;
        FlushUiState();
        
        foreach (var state in _sessions.Values)
        {
            CancelProcessingWatchdog(state);
            CancelTurnEndFallback(state);
            if (state.Session is not null)
                try { await state.Session.DisposeAsync(); } catch { }
        }
        _sessions.Clear();

        // Shut down all registered providers
        await ShutdownProvidersAsync();

        foreach (var kv in _codespaceClients)
        {
            try { await kv.Value.DisposeAsync(); } catch { }
        }
        _codespaceClients.Clear();
        foreach (var kv in _tunnelHandles)
        {
            try { await kv.Value.DisposeAsync(); } catch { }
        }
        _tunnelHandles.Clear();

        if (_client != null)
        {
            try { await _client.DisposeAsync(); } catch { }
        }
    }
}

public class UiState
{
    public string CurrentPage { get; set; } = "/";
    public string? ActiveSession { get; set; }
    public int FontSize { get; set; } = 20;
    public string? SelectedModel { get; set; }
    public bool ExpandedGrid { get; set; }
    public string? ExpandedSession { get; set; }
    public Dictionary<string, string> InputModes { get; set; } = new();
    public HashSet<string> CompletedTutorials { get; set; } = new();
}

public class ActiveSessionEntry
{
    public string SessionId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Model { get; set; } = "";
    public string? WorkingDirectory { get; set; }
    public string? LastPrompt { get; set; }
    public string? GroupId { get; set; }
    // Usage stats persisted across reconnects
    public int TotalInputTokens { get; set; }
    public int TotalOutputTokens { get; set; }
    public int? ContextCurrentTokens { get; set; }
    public int? ContextTokenLimit { get; set; }
    public int PremiumRequestsUsed { get; set; }
    public double TotalApiTimeSeconds { get; set; }
    public DateTime? CreatedAt { get; set; }
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
    int? OutputTokens,
    QuotaInfo? PremiumQuota = null
);

public record QuotaInfo(
    bool IsUnlimited,
    int EntitlementRequests,
    int UsedRequests,
    int RemainingPercentage,
    string? ResetDate
);

public record SkillInfo(string Name, string Description, string Source);
public record AgentInfo(string Name, string Description, string Source);
