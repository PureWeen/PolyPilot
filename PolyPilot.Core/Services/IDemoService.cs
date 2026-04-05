using PolyPilot.Models;

namespace PolyPilot.Services;

/// <summary>
/// Interface for the demo mode service.
/// </summary>
public interface IDemoService
{
    event Action? OnStateChanged;
    event Action<string, string>? OnContentReceived;
    event Action<string, string, string>? OnToolStarted;
    event Action<string, string, string, bool>? OnToolCompleted;
    event Action<string, string>? OnIntentChanged;
    event Action<string>? OnTurnStart;
    event Action<string>? OnTurnEnd;

    IReadOnlyDictionary<string, AgentSessionInfo> Sessions { get; }
    string? ActiveSessionName { get; }

    AgentSessionInfo CreateSession(string name, string? model = null);
    bool TryGetSession(string name, out AgentSessionInfo? info);
    void SetActiveSession(string name);
    Task SimulateResponseAsync(string sessionName, string prompt, SynchronizationContext? syncContext = null, CancellationToken ct = default);
}
