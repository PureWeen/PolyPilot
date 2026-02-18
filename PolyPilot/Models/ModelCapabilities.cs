namespace PolyPilot.Models;

/// <summary>
/// Lightweight model capability flags for multi-agent role assignment warnings.
/// No external API calls — purely static metadata based on known model families.
/// </summary>
[Flags]
public enum ModelCapability
{
    None = 0,
    CodeExpert = 1 << 0,
    ReasoningExpert = 1 << 1,
    Fast = 1 << 2,
    CostEfficient = 1 << 3,
    ToolUse = 1 << 4,
    Vision = 1 << 5,
    LargeContext = 1 << 6,
}

/// <summary>
/// Static registry of model capabilities for UX warnings during agent assignment.
/// </summary>
public static class ModelCapabilities
{
    private static readonly Dictionary<string, (ModelCapability Caps, string Strengths)> _registry = new(StringComparer.OrdinalIgnoreCase)
    {
        // Anthropic
        ["claude-opus-4.6"] = (ModelCapability.ReasoningExpert | ModelCapability.CodeExpert | ModelCapability.ToolUse | ModelCapability.LargeContext, "Best reasoning, complex orchestration"),
        ["claude-opus-4.5"] = (ModelCapability.ReasoningExpert | ModelCapability.CodeExpert | ModelCapability.ToolUse | ModelCapability.LargeContext, "Deep reasoning, creative coding"),
        ["claude-sonnet-4.5"] = (ModelCapability.CodeExpert | ModelCapability.ToolUse | ModelCapability.Fast, "Fast coding, good balance"),
        ["claude-sonnet-4"] = (ModelCapability.CodeExpert | ModelCapability.ToolUse | ModelCapability.Fast, "Fast coding, good balance"),
        ["claude-haiku-4.5"] = (ModelCapability.Fast | ModelCapability.CostEfficient | ModelCapability.ToolUse, "Quick tasks, cost-efficient"),

        // OpenAI
        ["gpt-5"] = (ModelCapability.ReasoningExpert | ModelCapability.CodeExpert | ModelCapability.ToolUse | ModelCapability.LargeContext, "Strong reasoning and coding"),
        ["gpt-5.1"] = (ModelCapability.ReasoningExpert | ModelCapability.CodeExpert | ModelCapability.ToolUse | ModelCapability.LargeContext, "Strong reasoning and coding"),
        ["gpt-5.1-codex"] = (ModelCapability.CodeExpert | ModelCapability.ToolUse | ModelCapability.Fast, "Optimized for code generation"),
        ["gpt-5.1-codex-mini"] = (ModelCapability.CodeExpert | ModelCapability.Fast | ModelCapability.CostEfficient, "Fast code, cost-efficient"),
        ["gpt-4.1"] = (ModelCapability.Fast | ModelCapability.CostEfficient | ModelCapability.ToolUse, "Fast and cheap, good for evaluation"),
        ["gpt-5-mini"] = (ModelCapability.Fast | ModelCapability.CostEfficient, "Quick tasks, budget-friendly"),

        // Google
        ["gemini-3-pro"] = (ModelCapability.ReasoningExpert | ModelCapability.LargeContext | ModelCapability.Vision, "Strong reasoning, large context, multimodal"),
        ["gemini-3-pro-preview"] = (ModelCapability.ReasoningExpert | ModelCapability.LargeContext | ModelCapability.Vision, "Strong reasoning, large context, multimodal"),
    };

    /// <summary>Get capabilities for a model. Returns None for unknown models.</summary>
    public static ModelCapability GetCapabilities(string modelSlug)
    {
        if (string.IsNullOrEmpty(modelSlug)) return ModelCapability.None;
        if (_registry.TryGetValue(modelSlug, out var entry)) return entry.Caps;

        // Fuzzy match by prefix
        foreach (var (key, val) in _registry)
            if (modelSlug.StartsWith(key, StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith(modelSlug, StringComparison.OrdinalIgnoreCase))
                return val.Caps;

        return ModelCapability.None;
    }

    /// <summary>Get a short description of model strengths.</summary>
    public static string GetStrengths(string modelSlug)
    {
        if (_registry.TryGetValue(modelSlug, out var entry)) return entry.Strengths;

        foreach (var (key, val) in _registry)
            if (modelSlug.StartsWith(key, StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith(modelSlug, StringComparison.OrdinalIgnoreCase))
                return val.Strengths;

        return "Unknown model";
    }

    /// <summary>
    /// Get warnings when assigning a model to a multi-agent role.
    /// Returns empty list if no issues detected.
    /// </summary>
    public static List<string> GetRoleWarnings(string modelSlug, MultiAgentRole role)
    {
        var warnings = new List<string>();
        var caps = GetCapabilities(modelSlug);

        if (caps == ModelCapability.None)
        {
            warnings.Add($"Unknown model '{modelSlug}' — capabilities not verified");
            return warnings;
        }

        if (role == MultiAgentRole.Orchestrator)
        {
            if (!caps.HasFlag(ModelCapability.ReasoningExpert))
                warnings.Add("⚠️ This model may lack strong reasoning for orchestration. Consider claude-opus or gpt-5.");
            if (caps.HasFlag(ModelCapability.CostEfficient) && !caps.HasFlag(ModelCapability.ReasoningExpert))
                warnings.Add("💰 Cost-efficient models may produce shallow plans. Best for workers, not orchestrators.");
        }

        if (role == MultiAgentRole.Worker)
        {
            if (!caps.HasFlag(ModelCapability.ToolUse) && !caps.HasFlag(ModelCapability.CodeExpert))
                warnings.Add("⚠️ This model may not support tool use well. Worker tasks may require tool interaction.");
        }

        return warnings;
    }
}

/// <summary>
/// Pre-configured multi-agent group templates for quick setup.
/// </summary>
public record GroupPreset(string Name, string Description, string Emoji, MultiAgentMode Mode,
    string OrchestratorModel, string[] WorkerModels)
{
    public static readonly GroupPreset[] BuiltIn = new[]
    {
        new GroupPreset(
            "Code Review Team", "Opus orchestrates, fast workers execute",
            "🔍", MultiAgentMode.Orchestrator,
            "claude-opus-4.6", new[] { "gpt-5.1-codex", "claude-sonnet-4.5" }),

        new GroupPreset(
            "Multi-Perspective Analysis", "Different models analyze the same problem",
            "🔬", MultiAgentMode.Broadcast,
            "claude-opus-4.6", new[] { "gpt-5", "gemini-3-pro", "claude-sonnet-4.5" }),

        new GroupPreset(
            "Fast Iteration Squad", "Cheap workers + smart evaluator for reflect loops",
            "🔄", MultiAgentMode.OrchestratorReflect,
            "claude-opus-4.6", new[] { "gpt-4.1", "gpt-4.1", "gpt-5.1-codex-mini" }),

        new GroupPreset(
            "Deep Research", "Strong reasoning models collaborate on complex problems",
            "🧠", MultiAgentMode.Orchestrator,
            "claude-opus-4.6", new[] { "gpt-5.1", "gemini-3-pro" }),
    };
}
