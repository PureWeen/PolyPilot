namespace PolyPilot.Provider;

/// <summary>
/// Runtime contract for a session provider. Resolved from DI after the host is built.
/// Provides branding, lifecycle, messaging, and streaming events.
/// </summary>
public interface ISessionProvider
{
    // ── Identity & Branding ─────────────────────────────────
    string ProviderId { get; }
    string DisplayName { get; }
    string Icon { get; }
    string AccentColor { get; }
    string GroupName { get; }
    string GroupDescription { get; }

    // ── Lifecycle ────────────────────────────────────────────
    bool IsInitialized { get; }
    bool IsInitializing { get; }
    Task InitializeAsync(CancellationToken ct = default);
    Task ShutdownAsync();

    // ── Leader Session ──────────────────────────────────────
    string LeaderDisplayName { get; }
    string LeaderIcon { get; }
    bool IsProcessing { get; }
    IReadOnlyList<ProviderChatMessage> History { get; }
    Task<string> SendMessageAsync(string message, CancellationToken ct = default);

    // ── Members ─────────────────────────────────────────────
    IReadOnlyList<ProviderMember> GetMembers();
    event Action? OnMembersChanged;

    // ── Custom Actions (optional, default implementations) ──
    IReadOnlyList<ProviderAction> GetActions() => [];
    Task<string?> ExecuteActionAsync(string actionId, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    // ── Interaction Modes (optional) ────────────────────────
    /// <summary>
    /// Returns the interaction modes this provider supports.
    /// These appear in the group dropdown, replacing the built-in multi-agent modes.
    /// Default returns the leader as the only mode.
    /// </summary>
    IReadOnlyList<ProviderMode> GetModes() => [new ProviderMode { Id = "leader", Label = LeaderDisplayName, Icon = LeaderIcon }];

    /// <summary>
    /// Sends a message using the specified mode. The modeId corresponds to a ProviderMode.Id.
    /// Default routes to SendMessageAsync (leader).
    /// </summary>
    Task<string> SendToModeAsync(string modeId, string message, CancellationToken ct = default)
        => SendMessageAsync(message, ct);

    // ── Streaming Events ────────────────────────────────────
    event Action<string>? OnContentReceived;
    event Action<string, string>? OnReasoningReceived;
    event Action<string>? OnReasoningComplete;
    event Action<string, string, string?>? OnToolStarted;
    event Action<string, string, bool>? OnToolCompleted;
    event Action<string>? OnIntentChanged;
    event Action? OnTurnStart;
    event Action? OnTurnEnd;
    event Action<string>? OnError;
    event Action? OnStateChanged;
}
