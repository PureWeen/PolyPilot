using System.Collections.Concurrent;
using PolyPilot.Models;
using GitHub.Copilot.SDK;

namespace PolyPilot.Services;

public record PersistedSessionInfo(string SessionId, DateTime LastModified, string Path);

public class SessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new();
    private readonly CopilotClient _client;
    private string? _activeSessionName;
    
    private static readonly string SessionStatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".copilot", "session-state");
    
    public string DefaultModel { get; set; } = "gpt-5.2";
    public bool IsInitialized { get; private set; }

    public SessionManager()
    {
        _client = new CopilotClient();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _client.StartAsync(cancellationToken);
        IsInitialized = true;
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
            .Select(di => new PersistedSessionInfo(di.Name, di.LastWriteTime, di.FullName))
            .OrderByDescending(s => s.LastModified);
    }

    /// <summary>
    /// Resume an existing session by its GUID
    /// </summary>
    public async Task<AgentSession> ResumeSessionAsync(string sessionId, string displayName, CancellationToken cancellationToken = default)
    {
        if (!IsInitialized)
            throw new InvalidOperationException("SessionManager is not initialized. Call InitializeAsync first.");

        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));

        if (_sessions.ContainsKey(displayName))
            throw new InvalidOperationException($"Session '{displayName}' already exists.");

        var copilotSession = await _client.ResumeSessionAsync(sessionId, cancellationToken: cancellationToken);

        var agentSession = new AgentSession(displayName, "resumed", copilotSession, sessionId, isResumed: true);
        
        if (!_sessions.TryAdd(displayName, agentSession))
        {
            await agentSession.DisposeAsync();
            throw new InvalidOperationException($"Failed to add session '{displayName}'.");
        }

        _activeSessionName ??= displayName;
        return agentSession;
    }

    public async Task<AgentSession> CreateSessionAsync(string name, string? model = null, CancellationToken cancellationToken = default)
    {
        if (!IsInitialized)
            throw new InvalidOperationException("SessionManager is not initialized. Call InitializeAsync first.");

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Session name cannot be empty.", nameof(name));

        if (_sessions.ContainsKey(name))
            throw new InvalidOperationException($"Session '{name}' already exists.");

        var sessionModel = model ?? DefaultModel;
        var copilotSession = await _client.CreateSessionAsync(new SessionConfig
        {
            Model = sessionModel
        }, cancellationToken);

        var agentSession = new AgentSession(name, sessionModel, copilotSession);
        
        if (!_sessions.TryAdd(name, agentSession))
        {
            await agentSession.DisposeAsync();
            throw new InvalidOperationException($"Failed to add session '{name}'.");
        }

        _activeSessionName ??= name;
        return agentSession;
    }

    public AgentSession? GetSession(string name)
    {
        _sessions.TryGetValue(name, out var session);
        return session;
    }

    public AgentSession? GetActiveSession()
    {
        if (_activeSessionName == null) return null;
        return GetSession(_activeSessionName);
    }

    public string? ActiveSessionName => _activeSessionName;

    public bool SwitchSession(string name)
    {
        if (!_sessions.ContainsKey(name))
            return false;
        
        _activeSessionName = name;
        return true;
    }

    public async Task<bool> CloseSessionAsync(string name)
    {
        if (!_sessions.TryRemove(name, out var session))
            return false;

        await session.DisposeAsync();

        if (_activeSessionName == name)
        {
            _activeSessionName = _sessions.Keys.FirstOrDefault();
        }

        return true;
    }

    public IEnumerable<AgentSession> GetAllSessions() => _sessions.Values;

    public int SessionCount => _sessions.Count;

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync();
        }
        _sessions.Clear();
        await _client.DisposeAsync();
    }
}
