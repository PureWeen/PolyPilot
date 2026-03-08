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

    /// <summary>
    /// Sends a message to a specific member session.
    /// Default routes to SendMessageAsync (leader).
    /// </summary>
    Task<string> SendToMemberAsync(string memberId, string message, CancellationToken ct = default)
        => SendMessageAsync(message, ct);

    // ── Custom Actions (optional, default implementations) ──
    IReadOnlyList<ProviderAction> GetActions() => [];
    Task<string?> ExecuteActionAsync(string actionId, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    // ── Streaming Events (leader session) ───────────────────
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

    // ── Member Streaming Events (per-member, include memberId) ──
    /// <summary>Fires when a member session receives content. Args: (memberId, content)</summary>
    event Action<string, string>? OnMemberContentReceived;
    /// <summary>Fires when a member session starts a turn. Args: (memberId)</summary>
    event Action<string>? OnMemberTurnStart;
    /// <summary>Fires when a member session ends a turn. Args: (memberId)</summary>
    event Action<string>? OnMemberTurnEnd;
    /// <summary>Fires when a member session encounters an error. Args: (memberId, error)</summary>
    event Action<string, string>? OnMemberError;
}
