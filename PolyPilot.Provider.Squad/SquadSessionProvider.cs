using System.Collections.Concurrent;
using System.Text.Json;

namespace PolyPilot.Provider.Squad;

/// <summary>
/// Session provider that connects to Squad's RemoteBridge WebSocket server.
/// Maps Squad's RC protocol events to ISessionProvider/IPermissionAwareProvider interfaces.
///
/// Usage: Start Squad's RemoteBridge with `squad rc --port 4242`, then configure
/// this provider with the host, port, and session token.
///
/// Architecture: PP is the UI companion. Squad owns session lifecycle, agent orchestration,
/// and tool execution. This provider is a transparent bridge — it doesn't try to replicate
/// Squad's coordinator or agent management.
/// </summary>
public class SquadSessionProvider : IPermissionAwareProvider, IAsyncDisposable
{
    // ── Identity & Branding ─────────────────────────────────
    public string ProviderId => "squad";
    public string DisplayName => "Squad";
    public string Icon => "🫡";
    public string AccentColor => "#6366f1"; // Squad indigo
    public string GroupName => "🫡 Squad";
    public string GroupDescription => _statusInfo != null
        ? $"Connected to {_statusInfo.Repo} ({_statusInfo.Branch}) on {_statusInfo.Machine}"
        : "Connect to a Squad RemoteBridge to control your AI team";

    // ── Lifecycle ────────────────────────────────────────────
    public bool IsInitialized { get; private set; }
    public bool IsInitializing { get; private set; }

    // ── Leader Session ──────────────────────────────────────
    public string LeaderDisplayName => "Squad Coordinator";
    public string LeaderIcon => "🫡";
    public bool IsProcessing { get; private set; }
    public IReadOnlyList<ProviderChatMessage> History => _history;

    // ── Configuration ────────────────────────────────────────
    private readonly string _host;
    private readonly int _port;
    private readonly string _sessionToken;
    private readonly IPluginLogger? _logger;

    // ── State ────────────────────────────────────────────────
    private SquadBridgeClient? _client;
    private RCStatusEvent? _statusInfo;
    private readonly List<ProviderChatMessage> _history = [];
    private readonly List<ProviderMember> _members = [];
    private readonly ConcurrentDictionary<string, ProviderPermissionRequest> _pendingPermissions = new();

    // Track which agents have active turns for member event routing
    private readonly ConcurrentDictionary<string, bool> _activeTurns = new();

    // Track active tool calls for callId correlation between running → completed/error
    private readonly ConcurrentDictionary<(string Agent, string Tool), string> _activeToolCalls = new();

    // Server protocol version (set on HandleStatus)
    public string? ServerVersion { get; private set; }

    // ── Events ───────────────────────────────────────────────
    public event Action? OnMembersChanged;
    public event Action<string>? OnContentReceived;
    // Reasoning/intent events — Squad's RC protocol doesn't emit these.
    // They're required by ISessionProvider but remain unwired.
#pragma warning disable CS0067
    public event Action<string, string>? OnReasoningReceived;
    public event Action<string>? OnReasoningComplete;
    public event Action<string>? OnIntentChanged;
#pragma warning restore CS0067
    public event Action<string, string, string?>? OnToolStarted;
    public event Action<string, string, bool>? OnToolCompleted;
    public event Action? OnTurnStart;
    public event Action? OnTurnEnd;
    public event Action<string>? OnError;
    public event Action? OnStateChanged;

    public event Action<string, string>? OnMemberContentReceived;
    public event Action<string>? OnMemberTurnStart;
    public event Action<string>? OnMemberTurnEnd;
    public event Action<string, string>? OnMemberError;

    // Permission events
    public event Action<ProviderPermissionRequest>? OnPermissionRequested;
    public event Action<string>? OnPermissionResolved;

    public SquadSessionProvider(string host, int port, string sessionToken, IPluginLogger? logger = null)
    {
        _host = host;
        _port = port;
        _sessionToken = sessionToken;
        _logger = logger;
    }

    // ── Lifecycle ────────────────────────────────────────────

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (IsInitialized || IsInitializing) return;
        IsInitializing = true;
        OnStateChanged?.Invoke();

        try
        {
            _client = new SquadBridgeClient(_host, _port, _logger);
            _client.OnEvent += HandleEvent;
            _client.OnConnected += () =>
            {
                _logger?.Info("Connected to Squad RemoteBridge");
                OnStateChanged?.Invoke();
            };
            _client.OnDisconnected += reason =>
            {
                _logger?.Warning($"Disconnected from Squad: {reason}");
                IsProcessing = false;
                OnError?.Invoke($"Disconnected from Squad: {reason}");
                OnStateChanged?.Invoke();
            };

            await _client.ConnectAsync(_sessionToken, ct);

            IsInitialized = true;
            _logger?.Info("SquadSessionProvider initialized");
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to connect to Squad RemoteBridge: {ex.Message}", ex);
            throw;
        }
        finally
        {
            IsInitializing = false;
            OnStateChanged?.Invoke();
        }
    }

    public async Task ShutdownAsync()
    {
        if (_client != null)
        {
            await _client.DisconnectAsync();
            _client.OnEvent -= HandleEvent;
        }
        IsInitialized = false;
        IsProcessing = false;
        _logger?.Info("SquadSessionProvider shut down");
    }

    /// <summary>
    /// Disconnect and reconnect to the RemoteBridge. Clears local state
    /// (members, history, pending permissions) and re-initializes.
    /// </summary>
    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        _logger?.Info("Reconnecting to Squad RemoteBridge...");
        await ShutdownAsync();

        _members.Clear();
        _history.Clear();
        _pendingPermissions.Clear();
        _activeTurns.Clear();
        _activeToolCalls.Clear();
        _statusInfo = null;
        ServerVersion = null;

        await InitializeAsync(ct);
    }

    // ── Messaging ────────────────────────────────────────────

    public async Task<string> SendMessageAsync(string message, CancellationToken ct = default)
    {
        if (_client == null || !_client.IsConnected)
            throw new InvalidOperationException("Not connected to Squad RemoteBridge");

        IsProcessing = true;

        _history.Add(new ProviderChatMessage
        {
            Role = "user",
            Content = message,
            Timestamp = DateTime.UtcNow,
            Type = ProviderMessageType.User
        });

        OnTurnStart?.Invoke();
        OnStateChanged?.Invoke();

        await _client.SendPromptAsync(message, ct);
        return message;
    }

    public async Task<string> SendToMemberAsync(string memberId, string message, CancellationToken ct = default)
    {
        if (_client == null || !_client.IsConnected)
            throw new InvalidOperationException("Not connected to Squad RemoteBridge");

        OnMemberTurnStart?.Invoke(memberId);
        await _client.SendDirectAsync(memberId, message, ct);
        return message;
    }

    public IReadOnlyList<ProviderMember> GetMembers() => _members;

    // ── Custom Actions ───────────────────────────────────────

    public IReadOnlyList<ProviderAction> GetActions() =>
    [
        new() { Id = "status", Label = "📊 Status", Tooltip = "Show Squad status" },
        new() { Id = "nap", Label = "😴 Nap", Tooltip = "Compress agent history (squad nap)" },
        new() { Id = "cast", Label = "🎭 Cast", Tooltip = "Show current session cast" },
        new() { Id = "economy", Label = "💰 Economy", Tooltip = "Toggle economy mode" },
    ];

    public async Task<string?> ExecuteActionAsync(string actionId, CancellationToken ct = default)
    {
        if (_client == null || !_client.IsConnected) return "Not connected to Squad";

        switch (actionId)
        {
            case "status":
                await _client.SendCommandAsync("status", ct: ct);
                return "Running squad status...";
            case "nap":
                await _client.SendCommandAsync("nap", ct: ct);
                return "Running squad nap...";
            case "cast":
                await _client.SendCommandAsync("cast", ct: ct);
                return "Running squad cast...";
            case "economy":
                await _client.SendCommandAsync("economy", ct: ct);
                return "Toggling economy mode...";
            default:
                return null;
        }
    }

    // ── Permissions ──────────────────────────────────────────

    public IReadOnlyList<ProviderPermissionRequest> GetPendingPermissions() =>
        _pendingPermissions.Values.ToList();

    public async Task ApprovePermissionAsync(string permissionId, CancellationToken ct = default)
    {
        if (_client == null) return;
        await _client.SendPermissionResponseAsync(permissionId, approved: true, ct);
        if (_pendingPermissions.TryRemove(permissionId, out _))
            OnPermissionResolved?.Invoke(permissionId);
    }

    public async Task DenyPermissionAsync(string permissionId, CancellationToken ct = default)
    {
        if (_client == null) return;
        await _client.SendPermissionResponseAsync(permissionId, approved: false, ct);
        if (_pendingPermissions.TryRemove(permissionId, out _))
            OnPermissionResolved?.Invoke(permissionId);
    }

    // ── Event Handling ───────────────────────────────────────

    private void HandleEvent(RCEvent evt)
    {
        switch (evt)
        {
            case RCStatusEvent status:
                HandleStatus(status);
                break;
            case RCHistoryEvent history:
                HandleHistory(history);
                break;
            case RCDeltaEvent delta:
                HandleDelta(delta);
                break;
            case RCCompleteEvent complete:
                HandleComplete(complete);
                break;
            case RCAgentsEvent agents:
                HandleAgents(agents);
                break;
            case RCToolCallEvent toolCall:
                HandleToolCall(toolCall);
                break;
            case RCPermissionEvent permission:
                HandlePermission(permission);
                break;
            case RCUsageEvent usage:
                HandleUsage(usage);
                break;
            case RCErrorEvent error:
                HandleError(error);
                break;
            // RCPongEvent — no action needed
        }
    }

    private void HandleStatus(RCStatusEvent status)
    {
        _statusInfo = status;
        ServerVersion = status.Version;
        _logger?.Info($"Squad status: {status.Repo} ({status.Branch}) on {status.Machine}, protocol v{status.Version}");

        // Protocol version check — warn if major version differs
        var serverMajor = status.Version.Split('.').FirstOrDefault();
        var clientMajor = SquadProtocol.ProtocolVersion.Split('.').FirstOrDefault();
        if (serverMajor != clientMajor)
        {
            var msg = $"Protocol version mismatch: server v{status.Version}, client v{SquadProtocol.ProtocolVersion}. " +
                      "Some features may not work correctly.";
            _logger?.Warning(msg);
            OnError?.Invoke(msg);
        }

        OnStateChanged?.Invoke();
    }

    private void HandleHistory(RCHistoryEvent history)
    {
        _history.Clear();
        foreach (var msg in history.Messages)
        {
            _history.Add(new ProviderChatMessage
            {
                Role = msg.Role == "user" ? "user" : "assistant",
                Content = msg.Content,
                Timestamp = DateTime.TryParse(msg.Timestamp, out var ts) ? ts : DateTime.UtcNow,
                Type = msg.Role == "user" ? ProviderMessageType.User
                     : msg.Role == "system" ? ProviderMessageType.System
                     : ProviderMessageType.Assistant,
            });
        }
        OnStateChanged?.Invoke();
    }

    private void HandleDelta(RCDeltaEvent delta)
    {
        // Route to the correct member or leader
        var memberId = FindMemberId(delta.AgentName);
        if (memberId != null)
        {
            // Ensure turn started
            if (_activeTurns.TryAdd(memberId, true))
                OnMemberTurnStart?.Invoke(memberId);

            OnMemberContentReceived?.Invoke(memberId, delta.Content);
        }
        else
        {
            // Route to leader (coordinator or unknown agent)
            OnContentReceived?.Invoke(delta.Content);
        }
    }

    private void HandleComplete(RCCompleteEvent complete)
    {
        var msg = complete.Message;
        _history.Add(new ProviderChatMessage
        {
            Role = msg.Role == "user" ? "user" : "assistant",
            Content = msg.Content,
            Timestamp = DateTime.TryParse(msg.Timestamp, out var ts) ? ts : DateTime.UtcNow,
            Type = msg.Role == "user" ? ProviderMessageType.User : ProviderMessageType.Assistant,
        });

        // Check if this is from a member
        var memberId = msg.AgentName != null ? FindMemberId(msg.AgentName) : null;
        if (memberId != null)
        {
            _activeTurns.TryRemove(memberId, out _);
            OnMemberTurnEnd?.Invoke(memberId);
        }
        else
        {
            // Leader turn complete
            IsProcessing = false;
            OnTurnEnd?.Invoke();
        }
        OnStateChanged?.Invoke();
    }

    private void HandleAgents(RCAgentsEvent agents)
    {
        _members.Clear();
        foreach (var agent in agents.Agents)
        {
            _members.Add(new ProviderMember
            {
                Id = agent.Name,
                Name = agent.Name,
                Role = agent.Role,
                Icon = agent.Status switch
                {
                    "working" => "⚡",
                    "streaming" => "✍️",
                    "error" => "❌",
                    _ => "👤"
                },
                IsActive = agent.Status is "working" or "streaming",
                StatusText = agent.Status,
            });
        }
        OnMembersChanged?.Invoke();
        OnStateChanged?.Invoke();
    }

    private void HandleToolCall(RCToolCallEvent toolCall)
    {
        var key = (toolCall.AgentName, toolCall.Tool);
        var argsStr = toolCall.Args?.ValueKind == JsonValueKind.Object
            ? toolCall.Args.Value.ToString()
            : null;

        switch (toolCall.Status)
        {
            case "running":
                var callId = $"{toolCall.AgentName}:{toolCall.Tool}:{DateTime.UtcNow.Ticks}";
                _activeToolCalls[key] = callId;
                OnToolStarted?.Invoke(callId, toolCall.Tool, argsStr);
                break;
            case "completed":
                if (_activeToolCalls.TryRemove(key, out var completedId))
                    OnToolCompleted?.Invoke(completedId, toolCall.Tool, true);
                break;
            case "error":
                if (_activeToolCalls.TryRemove(key, out var errorId))
                    OnToolCompleted?.Invoke(errorId, toolCall.Tool, false);
                break;
        }
        OnStateChanged?.Invoke();
    }

    private void HandlePermission(RCPermissionEvent permission)
    {
        var argsStr = permission.Args?.ValueKind == JsonValueKind.Object
            ? permission.Args.Value.ToString()
            : null;

        var request = new ProviderPermissionRequest
        {
            Id = permission.Id,
            AgentId = permission.AgentName,
            AgentName = permission.AgentName,
            ToolName = permission.Tool,
            Description = permission.Description,
            Arguments = argsStr,
        };

        _pendingPermissions[permission.Id] = request;
        OnPermissionRequested?.Invoke(request);
    }

    private void HandleUsage(RCUsageEvent usage)
    {
        _logger?.Debug($"Usage: {usage.Model} in={usage.InputTokens} out={usage.OutputTokens} cost={usage.Cost:F4}");
        // Token usage is informational — could be surfaced in a future status panel
    }

    private void HandleError(RCErrorEvent error)
    {
        if (error.AgentName != null)
        {
            var memberId = FindMemberId(error.AgentName);
            if (memberId != null)
            {
                OnMemberError?.Invoke(memberId, error.Message);
                return;
            }
        }
        OnError?.Invoke(error.Message);
    }

    /// <summary>
    /// Maps an RC agent name to a member ID. Returns null if the agent is unknown or is the coordinator.
    /// </summary>
    private string? FindMemberId(string? agentName)
    {
        if (string.IsNullOrEmpty(agentName)) return null;
        return _members.Any(m => m.Id == agentName) ? agentName : null;
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
        if (_client != null)
            await _client.DisposeAsync();
    }
}
