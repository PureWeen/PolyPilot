using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Stub implementations of CopilotService dependencies for testing.
/// </summary>
internal class StubChatDatabase : IChatDatabase
{
    public List<(string SessionId, ChatMessage Message)> AddedMessages { get; } = new();
    public List<(string SessionId, List<ChatMessage> Messages)> BulkInserts { get; } = new();

    public Task<int> AddMessageAsync(string sessionId, ChatMessage message)
    {
        AddedMessages.Add((sessionId, message));
        return Task.FromResult(AddedMessages.Count);
    }

    public Task BulkInsertAsync(string sessionId, List<ChatMessage> messages)
    {
        BulkInserts.Add((sessionId, messages));
        return Task.CompletedTask;
    }

    public Task UpdateToolCompleteAsync(string sessionId, string toolCallId, string result, bool success)
        => Task.CompletedTask;

    public Task UpdateReasoningContentAsync(string sessionId, string reasoningId, string content, bool isComplete)
        => Task.CompletedTask;
}

#pragma warning disable CS0067 // Events declared but never used in stubs
internal class StubServerManager : IServerManager
{
    public bool IsServerRunning { get; set; }
    public int? ServerPid { get; set; }
    public int ServerPort { get; set; } = 4321;
    public bool StartServerResult { get; set; }

    public event Action? OnStatusChanged;

    public bool CheckServerRunning(string host = "localhost", int? port = null) => IsServerRunning;

    public Task<bool> StartServerAsync(int port)
    {
        ServerPort = port;
        return Task.FromResult(StartServerResult);
    }

    public void StopServer() { IsServerRunning = false; }
    public bool DetectExistingServer() => IsServerRunning;
}

internal class StubWsBridgeClient : IWsBridgeClient
{
    public bool IsConnected { get; set; }
    public bool HasReceivedSessionsList { get; set; }
    public List<SessionSummary> Sessions { get; set; } = new();
    public string? ActiveSessionName { get; set; }
    public System.Collections.Concurrent.ConcurrentDictionary<string, List<ChatMessage>> SessionHistories { get; } = new();
    public List<PersistedSessionSummary> PersistedSessions { get; set; } = new();
    public string? GitHubAvatarUrl { get; set; }
    public string? GitHubLogin { get; set; }

    public event Action? OnStateChanged;
    public event Action<string, string>? OnContentReceived;
    public event Action<string, string, string, string?>? OnToolStarted;
    public event Action<string, string, string, bool>? OnToolCompleted;
    public event Action<string, string, string>? OnReasoningReceived;
    public event Action<string, string>? OnReasoningComplete;
    public event Action<string, string>? OnIntentChanged;
    public event Action<string, SessionUsageInfo>? OnUsageInfoChanged;
    public event Action<string>? OnTurnStart;
    public event Action<string>? OnTurnEnd;
    public event Action<string, string>? OnSessionComplete;
    public event Action<string, string>? OnError;
    public event Action<OrganizationState>? OnOrganizationStateReceived;
    public event Action<AttentionNeededPayload>? OnAttentionNeeded;

    public Task ConnectAsync(string wsUrl, string? authToken = null, CancellationToken ct = default) => Task.CompletedTask;
    public void Stop() { IsConnected = false; }
    public int RequestSessionsCallCount { get; private set; }
    public Task RequestSessionsAsync(CancellationToken ct = default)
    {
        RequestSessionsCallCount++;
        return Task.CompletedTask;
    }
    public Task RequestHistoryAsync(string sessionName, CancellationToken ct = default) => Task.CompletedTask;
    public Task SendMessageAsync(string sessionName, string message, CancellationToken ct = default) => Task.CompletedTask;
    public Task CreateSessionAsync(string name, string? model = null, string? workingDirectory = null, CancellationToken ct = default) => Task.CompletedTask;
    public string? LastSwitchedSession { get; private set; }
    public int SwitchSessionCallCount { get; private set; }
    public Task SwitchSessionAsync(string name, CancellationToken ct = default)
    {
        LastSwitchedSession = name;
        SwitchSessionCallCount++;
        return Task.CompletedTask;
    }

    public void FireOnStateChanged() => OnStateChanged?.Invoke();
    public Task QueueMessageAsync(string sessionName, string message, CancellationToken ct = default) => Task.CompletedTask;
    public Task ResumeSessionAsync(string sessionId, string? displayName = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task CloseSessionAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
    public Task AbortSessionAsync(string sessionName, CancellationToken ct = default) => Task.CompletedTask;
    public string? LastChangedModelSession { get; private set; }
    public string? LastChangedModel { get; private set; }
    public int ChangeModelCallCount { get; private set; }
    public Task ChangeModelAsync(string sessionName, string newModel, CancellationToken ct = default)
    {
        LastChangedModelSession = sessionName;
        LastChangedModel = newModel;
        ChangeModelCallCount++;
        return Task.CompletedTask;
    }
    public Task SendOrganizationCommandAsync(OrganizationCommandPayload payload, CancellationToken ct = default) => Task.CompletedTask;
    public string? LastRenamedOldName { get; private set; }
    public string? LastRenamedNewName { get; private set; }
    public int RenameSessionCallCount { get; private set; }
    public Task RenameSessionAsync(string oldName, string newName, CancellationToken ct = default)
    {
        LastRenamedOldName = oldName;
        LastRenamedNewName = newName;
        RenameSessionCallCount++;
        return Task.CompletedTask;
    }
    public Task<DirectoriesListPayload> ListDirectoriesAsync(string? path = null, CancellationToken ct = default)
        => Task.FromResult(new DirectoriesListPayload());
}

internal class StubDemoService : IDemoService
{
    private readonly Dictionary<string, AgentSessionInfo> _sessions = new();

    public event Action? OnStateChanged;
    public event Action<string, string>? OnContentReceived;
    public event Action<string, string, string>? OnToolStarted;
    public event Action<string, string, string, bool>? OnToolCompleted;
    public event Action<string, string>? OnIntentChanged;
    public event Action<string>? OnTurnStart;
    public event Action<string>? OnTurnEnd;

    public IReadOnlyDictionary<string, AgentSessionInfo> Sessions => _sessions;
    public string? ActiveSessionName { get; private set; }

    public AgentSessionInfo CreateSession(string name, string? model = null)
    {
        var info = new AgentSessionInfo { Name = name, Model = model ?? "demo-model", SessionId = $"demo-{_sessions.Count}" };
        _sessions[name] = info;
        ActiveSessionName ??= name;
        return info;
    }

    public bool TryGetSession(string name, out AgentSessionInfo? info)
        => _sessions.TryGetValue(name, out info);

    public void SetActiveSession(string name) { if (_sessions.ContainsKey(name)) ActiveSessionName = name; }

    public async Task SimulateResponseAsync(string sessionName, string prompt, SynchronizationContext? syncContext = null, CancellationToken ct = default)
    {
        await Task.Delay(10, ct);
        OnTurnStart?.Invoke(sessionName);
        OnContentReceived?.Invoke(sessionName, "Demo response");
        if (_sessions.TryGetValue(sessionName, out var info))
        {
            info.History.Add(ChatMessage.AssistantMessage("Demo response"));
            info.IsProcessing = false;
        }
        OnTurnEnd?.Invoke(sessionName);
        OnStateChanged?.Invoke();
    }
}
#pragma warning restore CS0067
