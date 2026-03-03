using PolyPilot.Models;

namespace PolyPilot.Services.Providers;

/// <summary>
/// Configuration passed when creating a provider session.
/// </summary>
public class ProviderSessionConfig
{
    public required string Model { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? SystemMessage { get; init; }
}

/// <summary>
/// Normalized event args for content streaming deltas.
/// </summary>
public class ContentDeltaEventArgs : EventArgs
{
    public required string Content { get; init; }
}

/// <summary>
/// Normalized event args for a complete message.
/// </summary>
public class MessageCompleteEventArgs : EventArgs
{
    public required string Content { get; init; }
}

/// <summary>
/// Normalized event args for tool execution events.
/// </summary>
public class ToolEventArgs : EventArgs
{
    public required string ToolName { get; init; }
    public string? ToolCallId { get; init; }
    public string? Input { get; init; }
    public string? Output { get; init; }
    public bool IsSuccess { get; init; } = true;
}

/// <summary>
/// Normalized event args for provider errors.
/// </summary>
public class ProviderErrorEventArgs : EventArgs
{
    public required string Message { get; init; }
    public Exception? Exception { get; init; }
    public bool IsFatal { get; init; }
}

/// <summary>
/// Normalized event args for token usage updates.
/// </summary>
public class UsageEventArgs : EventArgs
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
}

/// <summary>
/// A provider-managed session. Each provider implementation wraps its own
/// session/client state behind this interface, firing normalized events
/// that CopilotService bridges to the UI.
/// </summary>
public interface IProviderSession : IAsyncDisposable
{
    string SessionId { get; }
    string Model { get; }

    Task SendMessageAsync(string message, IReadOnlyList<string>? imageUrls, CancellationToken ct);
    Task AbortAsync(CancellationToken ct);

    // Normalized events (~8 events, mapped from provider-specific events internally)
    event EventHandler<ContentDeltaEventArgs>? ContentReceived;
    event EventHandler<MessageCompleteEventArgs>? MessageCompleted;
    event EventHandler<ToolEventArgs>? ToolStarted;
    event EventHandler<ToolEventArgs>? ToolCompleted;
    event EventHandler? TurnStarted;
    event EventHandler? TurnEnded;
    event EventHandler<ProviderErrorEventArgs>? ErrorOccurred;
    event EventHandler<UsageEventArgs>? UsageUpdated;
}

/// <summary>
/// Factory interface for creating sessions backed by a specific AI provider.
/// Each configured provider gets one ISessionProvider instance managed by ProviderRegistry.
/// </summary>
public interface ISessionProvider : IAsyncDisposable
{
    string ProviderId { get; }
    ProviderType Type { get; }
    ProviderCapabilities Capabilities { get; }

    Task<IProviderSession> CreateSessionAsync(ProviderSessionConfig config, CancellationToken ct);
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct);
}
