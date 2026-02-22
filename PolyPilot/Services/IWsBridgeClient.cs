using PolyPilot.Models;

namespace PolyPilot.Services;

/// <summary>
/// Interface for the WebSocket bridge client used in remote mode.
/// </summary>
public interface IWsBridgeClient
{
    bool IsConnected { get; }
    bool HasReceivedSessionsList { get; }
    List<SessionSummary> Sessions { get; }
    string? ActiveSessionName { get; }
    System.Collections.Concurrent.ConcurrentDictionary<string, List<ChatMessage>> SessionHistories { get; }
    System.Collections.Concurrent.ConcurrentDictionary<string, bool> SessionHistoryHasMore { get; }
    List<PersistedSessionSummary> PersistedSessions { get; }
    string? GitHubAvatarUrl { get; }
    string? GitHubLogin { get; }

    // Events
    event Action? OnStateChanged;
    event Action<string, string>? OnContentReceived;
    event Action<string, string, string, string?>? OnToolStarted;
    event Action<string, string, string, bool>? OnToolCompleted;
    event Action<string, string, string>? OnReasoningReceived;
    event Action<string, string>? OnReasoningComplete;
    event Action<string, string, string?, string?>? OnImageReceived; // sessionName, callId, imageDataUri, caption
    event Action<string, string>? OnIntentChanged;
    event Action<string, SessionUsageInfo>? OnUsageInfoChanged;
    event Action<string>? OnTurnStart;
    event Action<string>? OnTurnEnd;
    event Action<string, string>? OnSessionComplete;
    event Action<string, string>? OnError;
    event Action<OrganizationState>? OnOrganizationStateReceived;
    event Action<AttentionNeededPayload>? OnAttentionNeeded;

    // Methods
    Task ConnectAsync(string wsUrl, string? authToken = null, CancellationToken ct = default);
    Task ConnectSmartAsync(string? tunnelWsUrl, string? tunnelToken, string? lanWsUrl, string? lanToken, CancellationToken ct = default);
    string? ActiveUrl { get; }
    void Stop();
    Task RequestSessionsAsync(CancellationToken ct = default);
    Task RequestHistoryAsync(string sessionName, int? limit = null, CancellationToken ct = default);
    Task SendMessageAsync(string sessionName, string message, string? agentMode = null, CancellationToken ct = default);
    Task CreateSessionAsync(string name, string? model = null, string? workingDirectory = null, CancellationToken ct = default);
    Task SwitchSessionAsync(string name, CancellationToken ct = default);
    Task QueueMessageAsync(string sessionName, string message, string? agentMode = null, CancellationToken ct = default);
    Task ResumeSessionAsync(string sessionId, string? displayName = null, CancellationToken ct = default);
    Task CloseSessionAsync(string name, CancellationToken ct = default);
    Task AbortSessionAsync(string sessionName, CancellationToken ct = default);
    Task ChangeModelAsync(string sessionName, string newModel, CancellationToken ct = default);
    Task RenameSessionAsync(string oldName, string newName, CancellationToken ct = default);
    Task SendOrganizationCommandAsync(OrganizationCommandPayload payload, CancellationToken ct = default);
    Task<DirectoriesListPayload> ListDirectoriesAsync(string? path = null, CancellationToken ct = default);

    // Repo operations
    event Action<ReposListPayload>? OnReposListReceived;
    Task<RepoAddedPayload> AddRepoAsync(string url, Action<string>? onProgress = null, CancellationToken ct = default);
    Task RemoveRepoAsync(string repoId, bool deleteFromDisk, string? groupId = null, CancellationToken ct = default);
    Task RequestReposAsync(CancellationToken ct = default);

    // Worktree operations
    Task<WorktreeCreatedPayload> CreateWorktreeAsync(string repoId, string? branchName, int? prNumber, CancellationToken ct = default);
    Task RemoveWorktreeAsync(string worktreeId, CancellationToken ct = default);

    // Image fetch
    Task<FetchImageResponsePayload> FetchImageAsync(string path, CancellationToken ct = default);
}
