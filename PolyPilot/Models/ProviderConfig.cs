using System.Text.Json.Serialization;

namespace PolyPilot.Models;

/// <summary>
/// The type of AI provider backing a session.
/// </summary>
public enum ProviderType
{
    /// <summary>GitHub Copilot via the Copilot SDK (default, always available).</summary>
    Copilot,
    /// <summary>Anthropic API (Claude models) via direct API key.</summary>
    Anthropic,
    /// <summary>OpenAI API via direct API key.</summary>
    OpenAI,
    /// <summary>Ollama local inference server.</summary>
    Ollama,
    /// <summary>Any OpenAI-compatible endpoint (LM Studio, vLLM, etc.).</summary>
    OpenAICompatible
}

/// <summary>
/// Capability flags advertised by a provider. Drives UI degradation —
/// features are hidden when the provider doesn't support them.
/// </summary>
[Flags]
public enum ProviderCapabilities
{
    None = 0,
    Streaming = 1,
    ToolExecution = 2,
    UsageTracking = 4,
    ImageInput = 8,
    SessionResume = 16,
    Reasoning = 32
}

/// <summary>
/// Configuration for a user-added AI provider. Persisted in settings.json.
/// Copilot is always implicitly available and doesn't need a ProviderConfig entry.
/// </summary>
public class ProviderConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public ProviderType Type { get; set; }
    public bool Enabled { get; set; }
    public string? ApiKey { get; set; }
    public string? Endpoint { get; set; }
    public string? DefaultModel { get; set; }
    public List<string> AvailableModels { get; set; } = new();

    /// <summary>
    /// Returns the default capabilities for a given provider type.
    /// Copilot has full capabilities; generic providers start chat-only.
    /// </summary>
    [JsonIgnore]
    public ProviderCapabilities Capabilities => Type switch
    {
        ProviderType.Copilot => ProviderCapabilities.Streaming | ProviderCapabilities.ToolExecution
            | ProviderCapabilities.UsageTracking | ProviderCapabilities.ImageInput
            | ProviderCapabilities.SessionResume | ProviderCapabilities.Reasoning,
        ProviderType.Anthropic => ProviderCapabilities.Streaming | ProviderCapabilities.UsageTracking
            | ProviderCapabilities.ImageInput,
        ProviderType.OpenAI => ProviderCapabilities.Streaming | ProviderCapabilities.UsageTracking,
        ProviderType.Ollama => ProviderCapabilities.Streaming,
        ProviderType.OpenAICompatible => ProviderCapabilities.Streaming,
        _ => ProviderCapabilities.None
    };

    /// <summary>
    /// Returns the default endpoint placeholder for UI display.
    /// </summary>
    [JsonIgnore]
    public string DefaultEndpoint => Type switch
    {
        ProviderType.Anthropic => "https://api.anthropic.com",
        ProviderType.OpenAI => "https://api.openai.com",
        ProviderType.Ollama => "http://localhost:11434",
        ProviderType.OpenAICompatible => "http://localhost:8080",
        _ => ""
    };

    /// <summary>Whether this provider type requires an API key.</summary>
    [JsonIgnore]
    public bool RequiresApiKey => Type is ProviderType.Anthropic or ProviderType.OpenAI;

    /// <summary>Whether this provider type requires an endpoint URL.</summary>
    [JsonIgnore]
    public bool RequiresEndpoint => Type is ProviderType.Ollama or ProviderType.OpenAICompatible;
}
