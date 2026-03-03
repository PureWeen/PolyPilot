using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using PolyPilot.Models;

namespace PolyPilot.Services.Providers;

/// <summary>
/// Manages ISessionProvider instances based on ConnectionSettings.Providers.
/// Copilot is NOT managed here — it uses the existing CopilotService path.
/// Only non-Copilot providers (Anthropic, OpenAI, Ollama, Custom) are registered.
/// </summary>
public class ProviderRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ISessionProvider> _providers = new();
    private readonly ConcurrentDictionary<string, List<string>> _modelCache = new();

    /// <summary>
    /// Refresh providers from settings. Creates/disposes providers as needed.
    /// </summary>
    public async Task RefreshFromSettingsAsync(List<ProviderConfig> configs)
    {
        var activeIds = new HashSet<string>();

        foreach (var config in configs.Where(c => c.Enabled && c.Type != ProviderType.Copilot))
        {
            activeIds.Add(config.Id);

            if (!_providers.ContainsKey(config.Id))
            {
                var provider = CreateProvider(config);
                if (provider != null)
                    _providers[config.Id] = provider;
            }
        }

        // Dispose providers that are no longer in settings
        foreach (var id in _providers.Keys.Except(activeIds).ToList())
        {
            if (_providers.TryRemove(id, out var removed))
                await removed.DisposeAsync();
            _modelCache.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Get a provider by ID. Returns null if not found (caller should use Copilot path).
    /// </summary>
    public ISessionProvider? Get(string? providerId)
    {
        if (string.IsNullOrEmpty(providerId))
            return null;
        _providers.TryGetValue(providerId, out var provider);
        return provider;
    }

    /// <summary>
    /// Get all active providers (excludes Copilot).
    /// </summary>
    public IReadOnlyCollection<ISessionProvider> GetAll() => _providers.Values.ToList();

    /// <summary>
    /// Get available models for a provider, with caching.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetModelsAsync(string providerId, CancellationToken ct = default)
    {
        if (_modelCache.TryGetValue(providerId, out var cached))
            return cached;

        var provider = Get(providerId);
        if (provider == null)
            return Array.Empty<string>();

        try
        {
            var models = await provider.ListModelsAsync(ct);
            var list = models.ToList();
            _modelCache[providerId] = list;
            return list;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Clear the model cache for a provider (e.g., after settings change).
    /// </summary>
    public void InvalidateModelCache(string? providerId = null)
    {
        if (providerId != null)
            _modelCache.TryRemove(providerId, out _);
        else
            _modelCache.Clear();
    }

    private static ISessionProvider? CreateProvider(ProviderConfig config)
    {
        try
        {
            var chatClient = CreateChatClient(config);
            if (chatClient == null) return null;

            return new GenericChatProvider(
                config.Id,
                config.Type,
                chatClient,
                config.Capabilities);
        }
        catch
        {
            return null;
        }
    }

    private static IChatClient? CreateChatClient(ProviderConfig config)
    {
        // Phase 1: Create IChatClient instances based on provider type.
        // The actual NuGet packages (Microsoft.Extensions.AI.OpenAI, etc.) will be added
        // in Phase 2. For now, we support OpenAI-compatible endpoints via the OpenAI client.
        return config.Type switch
        {
            ProviderType.Anthropic => CreateOpenAICompatibleClient(
                config.ApiKey, config.Endpoint ?? "https://api.anthropic.com/v1", config.DefaultModel),
            ProviderType.OpenAI => CreateOpenAICompatibleClient(
                config.ApiKey, config.Endpoint ?? "https://api.openai.com/v1", config.DefaultModel),
            ProviderType.Ollama => CreateOpenAICompatibleClient(
                null, config.Endpoint ?? "http://localhost:11434/v1", config.DefaultModel),
            ProviderType.OpenAICompatible => CreateOpenAICompatibleClient(
                config.ApiKey, config.Endpoint ?? "http://localhost:8080/v1", config.DefaultModel),
            _ => null
        };
    }

    /// <summary>
    /// Creates an IChatClient that talks to any OpenAI-compatible API endpoint.
    /// Most providers (OpenAI, Anthropic via proxy, Ollama, LM Studio, vLLM) support this.
    /// </summary>
    private static IChatClient? CreateOpenAICompatibleClient(string? apiKey, string endpoint, string? model)
    {
        // Use OpenAI SDK's IChatClient with custom endpoint.
        // This requires the Microsoft.Extensions.AI.OpenAI package (Phase 2).
        // For now, return null — providers will be enabled once the NuGet package is added.
        // TODO: Phase 2 — add `new OpenAI.OpenAIClient(apiKey, new() { Endpoint = new Uri(endpoint) }).AsChatClient(model)`
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers.Values)
            await provider.DisposeAsync();
        _providers.Clear();
        _modelCache.Clear();
    }
}
