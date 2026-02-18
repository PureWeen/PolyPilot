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

        // Name-pattern inference for new/unknown models
        return InferFromName(modelSlug);
    }

    /// <summary>
    /// Infer capabilities from model name patterns for unknown models.
    /// Handles new model releases gracefully without registry updates.
    /// </summary>
    internal static ModelCapability InferFromName(string slug)
    {
        var lower = slug.ToLowerInvariant();
        var caps = ModelCapability.None;

        // Family inference
        if (lower.Contains("opus")) caps |= ModelCapability.ReasoningExpert | ModelCapability.CodeExpert | ModelCapability.ToolUse;
        else if (lower.Contains("sonnet")) caps |= ModelCapability.CodeExpert | ModelCapability.ToolUse | ModelCapability.Fast;
        else if (lower.Contains("haiku")) caps |= ModelCapability.Fast | ModelCapability.CostEfficient;
        else if (lower.Contains("gemini")) caps |= ModelCapability.ReasoningExpert | ModelCapability.LargeContext | ModelCapability.Vision;

        // Variant inference
        if (lower.Contains("codex")) caps |= ModelCapability.CodeExpert;
        if (lower.Contains("mini")) caps |= ModelCapability.Fast | ModelCapability.CostEfficient;
        if (lower.Contains("max")) caps |= ModelCapability.ReasoningExpert;

        return caps;
    }

    /// <summary>Get a short description of model strengths.</summary>
    public static string GetStrengths(string modelSlug)
    {
        if (_registry.TryGetValue(modelSlug, out var entry)) return entry.Strengths;

        foreach (var (key, val) in _registry)
            if (modelSlug.StartsWith(key, StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith(modelSlug, StringComparison.OrdinalIgnoreCase))
                return val.Strengths;

        // Generate description from inferred capabilities
        var inferred = InferFromName(modelSlug);
        if (inferred != ModelCapability.None)
        {
            var parts = new List<string>();
            if (inferred.HasFlag(ModelCapability.ReasoningExpert)) parts.Add("reasoning");
            if (inferred.HasFlag(ModelCapability.CodeExpert)) parts.Add("code");
            if (inferred.HasFlag(ModelCapability.Fast)) parts.Add("fast");
            if (inferred.HasFlag(ModelCapability.CostEfficient)) parts.Add("cost-efficient");
            if (inferred.HasFlag(ModelCapability.Vision)) parts.Add("multimodal");
            if (inferred.HasFlag(ModelCapability.LargeContext)) parts.Add("large context");
            return $"Inferred: {string.Join(", ", parts)}";
        }

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
    /// <summary>Whether this is a user-created preset (vs built-in).</summary>
    public bool IsUserDefined { get; init; }

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
            "Quick Reflection Cycle", "Fast workers + smart evaluator for iterative refinement",
            "🔄", MultiAgentMode.OrchestratorReflect,
            "claude-opus-4.6", new[] { "gpt-4.1", "gpt-4.1", "gpt-5.1-codex-mini" }),

        new GroupPreset(
            "Deep Research", "Strong reasoning models collaborate on complex problems",
            "🧠", MultiAgentMode.Orchestrator,
            "claude-opus-4.6", new[] { "gpt-5.1", "gemini-3-pro" }),
    };
}

/// <summary>
/// Manages user-defined presets: save/load from ~/.polypilot/presets.json.
/// </summary>
public static class UserPresets
{
    private const string FileName = "presets.json";

    public static List<GroupPreset> Load(string baseDir)
    {
        try
        {
            var path = Path.Combine(baseDir, FileName);
            if (!File.Exists(path)) return new List<GroupPreset>();
            var json = File.ReadAllText(path);
            return System.Text.Json.JsonSerializer.Deserialize<List<GroupPreset>>(json) ?? new();
        }
        catch { return new List<GroupPreset>(); }
    }

    public static void Save(string baseDir, List<GroupPreset> presets)
    {
        try
        {
            Directory.CreateDirectory(baseDir);
            var json = System.Text.Json.JsonSerializer.Serialize(presets,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(baseDir, FileName), json);
        }
        catch { /* best-effort persistence */ }
    }

    /// <summary>Get all presets: built-in + user-defined.</summary>
    public static GroupPreset[] GetAll(string baseDir)
    {
        var user = Load(baseDir);
        return GroupPreset.BuiltIn.Concat(user).ToArray();
    }

    /// <summary>Save the current multi-agent group as a reusable preset.</summary>
    public static GroupPreset? SaveGroupAsPreset(string baseDir, string name, string description,
        string emoji, SessionGroup group, List<SessionMeta> members, Func<string, string> getEffectiveModel)
    {
        var orchestrator = members.FirstOrDefault(m => m.Role == MultiAgentRole.Orchestrator);
        var workers = members.Where(m => m.Role != MultiAgentRole.Orchestrator).ToList();

        if (orchestrator == null && workers.Count == 0) return null;

        var preset = new GroupPreset(
            name, description, emoji, group.OrchestratorMode,
            orchestrator != null ? getEffectiveModel(orchestrator.SessionName) : "claude-opus-4.6",
            workers.Select(w => getEffectiveModel(w.SessionName)).ToArray())
        { IsUserDefined = true };

        var existing = Load(baseDir);
        existing.RemoveAll(p => p.Name == name); // replace if same name
        existing.Add(preset);
        Save(baseDir, existing);
        return preset;
    }
}

/// <summary>
/// Detects conflicts and issues within a multi-agent group's model configuration.
/// </summary>
public static class GroupModelAnalyzer
{
    public record GroupDiagnostic(string Level, string Message); // Level: "error", "warning", "info"

    /// <summary>
    /// Analyze a multi-agent group for model conflicts and capability gaps.
    /// </summary>
    public static List<GroupDiagnostic> Analyze(SessionGroup group, List<(string Name, string Model, MultiAgentRole Role)> members)
    {
        var diags = new List<GroupDiagnostic>();
        if (members.Count == 0) return diags;

        var orchestrators = members.Where(m => m.Role == MultiAgentRole.Orchestrator).ToList();
        var workers = members.Where(m => m.Role == MultiAgentRole.Worker).ToList();

        // Check: orchestrator mode without orchestrator
        if ((group.OrchestratorMode == MultiAgentMode.Orchestrator || group.OrchestratorMode == MultiAgentMode.OrchestratorReflect)
            && orchestrators.Count == 0)
        {
            diags.Add(new("error", "⛔ Orchestrator mode requires at least one session with the Orchestrator role."));
        }

        // Check: orchestrator using weak model
        foreach (var orch in orchestrators)
        {
            var caps = ModelCapabilities.GetCapabilities(orch.Model);
            if (!caps.HasFlag(ModelCapability.ReasoningExpert))
                diags.Add(new("warning", $"⚠️ Orchestrator '{orch.Name}' uses {orch.Model} which lacks strong reasoning. Consider claude-opus or gpt-5."));
        }

        // Check: all workers same model in broadcast (less diverse perspectives)
        if (group.OrchestratorMode == MultiAgentMode.Broadcast && workers.Count > 1)
        {
            var uniqueModels = workers.Select(w => w.Model).Distinct().Count();
            if (uniqueModels == 1)
                diags.Add(new("info", "💡 All workers use the same model. For diverse perspectives, assign different models."));
        }

        // Check: expensive models as workers when cheaper ones suffice
        foreach (var w in workers)
        {
            var caps = ModelCapabilities.GetCapabilities(w.Model);
            if (caps.HasFlag(ModelCapability.ReasoningExpert) && !caps.HasFlag(ModelCapability.Fast))
                diags.Add(new("info", $"💰 Worker '{w.Name}' uses premium model {w.Model}. Consider a faster/cheaper model for worker tasks."));
        }

        // Check: OrchestratorReflect without enough workers
        if (group.OrchestratorMode == MultiAgentMode.OrchestratorReflect && workers.Count == 0)
            diags.Add(new("error", "⛔ OrchestratorReflect needs at least one worker to iterate on."));

        return diags;
    }
}
