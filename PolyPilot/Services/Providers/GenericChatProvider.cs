using System.Text;
using Microsoft.Extensions.AI;
using PolyPilot.Models;

using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace PolyPilot.Services.Providers;

/// <summary>
/// A provider session backed by Microsoft.Extensions.AI IChatClient.
/// Supports any provider that implements IChatClient (OpenAI, Anthropic, Ollama, etc.).
/// Chat-only in Phase 1 — no tool execution or session persistence.
/// </summary>
public class GenericChatSession : IProviderSession
{
    private readonly IChatClient _client;
    private readonly List<AIChatMessage> _history = new();
    private CancellationTokenSource? _abortCts;

    public string SessionId { get; }
    public string Model { get; }

    public event EventHandler<ContentDeltaEventArgs>? ContentReceived;
    public event EventHandler<MessageCompleteEventArgs>? MessageCompleted;
    public event EventHandler<ToolEventArgs>? ToolStarted;
    public event EventHandler<ToolEventArgs>? ToolCompleted;
    public event EventHandler? TurnStarted;
    public event EventHandler? TurnEnded;
    public event EventHandler<ProviderErrorEventArgs>? ErrorOccurred;
    public event EventHandler<UsageEventArgs>? UsageUpdated;

    public GenericChatSession(IChatClient client, string model, string? systemMessage)
    {
        _client = client;
        Model = model;
        SessionId = Guid.NewGuid().ToString();

        if (!string.IsNullOrWhiteSpace(systemMessage))
            _history.Add(new AIChatMessage(ChatRole.System, systemMessage));
    }

    public async Task SendMessageAsync(string message, IReadOnlyList<string>? imageUrls, CancellationToken ct)
    {
        _abortCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linkedCt = _abortCts.Token;

        try
        {
            TurnStarted?.Invoke(this, EventArgs.Empty);

            _history.Add(new AIChatMessage(ChatRole.User, message));

            var fullResponse = new StringBuilder();
            var options = new ChatOptions { ModelId = Model };

            await foreach (var update in _client.GetStreamingResponseAsync(_history, options, linkedCt))
            {
                foreach (var content in update.Contents)
                {
                    if (content is TextContent textContent && textContent.Text != null)
                    {
                        fullResponse.Append(textContent.Text);
                        ContentReceived?.Invoke(this, new ContentDeltaEventArgs { Content = textContent.Text });
                    }
                }
            }

            var responseText = fullResponse.ToString();
            _history.Add(new AIChatMessage(ChatRole.Assistant, responseText));
            MessageCompleted?.Invoke(this, new MessageCompleteEventArgs { Content = responseText });
        }
        catch (OperationCanceledException)
        {
            // User abort — not an error
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, new ProviderErrorEventArgs
            {
                Message = ex.Message,
                Exception = ex,
                IsFatal = false
            });
        }
        finally
        {
            _abortCts?.Dispose();
            _abortCts = null;
            TurnEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    public Task AbortAsync(CancellationToken ct)
    {
        _abortCts?.Cancel();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _abortCts?.Dispose();
        if (_client is IAsyncDisposable asyncDisposable)
            return asyncDisposable.DisposeAsync();
        if (_client is IDisposable disposable)
            disposable.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Provider backed by Microsoft.Extensions.AI IChatClient.
/// Wraps any IChatClient implementation (OpenAI, Anthropic, Ollama, etc.).
/// </summary>
public class GenericChatProvider : ISessionProvider
{
    private readonly IChatClient _chatClient;

    public string ProviderId { get; }
    public ProviderType Type { get; }
    public ProviderCapabilities Capabilities { get; }

    public GenericChatProvider(string providerId, ProviderType type, IChatClient chatClient, ProviderCapabilities capabilities)
    {
        ProviderId = providerId;
        Type = type;
        _chatClient = chatClient;
        Capabilities = capabilities;
    }

    public Task<IProviderSession> CreateSessionAsync(ProviderSessionConfig config, CancellationToken ct)
    {
        IProviderSession session = new GenericChatSession(_chatClient, config.Model, config.SystemMessage);
        return Task.FromResult(session);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        // IChatClient doesn't have a standard ListModels API.
        // Subclasses or the registry will provide model lists.
        return Array.Empty<string>();
    }

    public ValueTask DisposeAsync()
    {
        if (_chatClient is IAsyncDisposable asyncDisposable)
            return asyncDisposable.DisposeAsync();
        if (_chatClient is IDisposable disposable)
            disposable.Dispose();
        return ValueTask.CompletedTask;
    }
}
