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

    /// <summary>Whether this preset was loaded from a repo-level team definition (.squad/).</summary>
    public bool IsRepoLevel { get; init; }

    /// <summary>Path to the source directory (e.g., ".squad/") for repo-level presets.</summary>
    public string? SourcePath { get; init; }

    /// <summary>
    /// Per-worker system prompts, indexed to match WorkerModels.
    /// Null or shorter array = remaining workers get generic prompt.
    /// </summary>
    public string?[]? WorkerSystemPrompts { get; init; }

    /// <summary>
    /// Shared context from decisions.md or similar, prepended to all worker prompts.
    /// </summary>
    public string? SharedContext { get; init; }

    /// <summary>
    /// Routing rules from routing.md, injected into orchestrator planning prompt.
    /// </summary>
    public string? RoutingContext { get; init; }

    private const string WorkerReviewPrompt = """
        You are a PR reviewer. When assigned a PR, follow this process:

        ## 1. Gather Context
        - Run `gh pr view <number>` to read the description, labels, milestone, and linked issues
        - Run `gh pr diff <number>` to get the full diff
        - Run `gh pr checks <number>` to check CI status — if builds failed, determine whether failures are PR-specific or pre-existing infra issues (same failures on the base branch = not PR-specific)
        - Run `gh pr view <number> --json reviews,comments` to check existing review comments — don't duplicate feedback already given

        ## 2. Verify Claims Against Code
        - Don't trust the PR description blindly — trace through the actual source code
        - If the PR references a prior fix or revert, check `git log --oneline --all -- <file>` to understand the history
        - If the change is scoped narrowly (e.g., "only affects streams, not files"), verify that claim by reading the surrounding code paths
        - Check for variable name typos, missing underscores, wrong method overloads — compilers catch some, but cross-platform #if blocks can hide build errors

        ## 3. Dispatch Multi-Model Reviews
        Dispatch 5 parallel reviews via the task tool using claude-opus-4.6, claude-opus-4.6, claude-sonnet-4.6, gemini-3-pro-preview, and gpt-5.3-codex. Include the full diff and any CI/review context in each prompt. Each sub-agent returns findings as:
        ```
        [SEVERITY] file:line — description
        ```
        Where SEVERITY is: 🔴 CRITICAL, 🟡 MODERATE, 🟢 MINOR

        ## 4. Synthesize Final Report
        - Only include issues flagged by 2+ models (consensus filter)
        - Rank by severity
        - Include file path and line numbers
        - Note CI status: ✅ passing, ❌ failing (PR-specific), ⚠️ failing (pre-existing)
        - Note if prior review comments were addressed or still outstanding
        - End with recommended action: ✅ Approve, ⚠️ Request changes (with specific ask), or 🔴 Do not merge
        """;

    public static readonly GroupPreset[] BuiltIn = new[]
    {
        new GroupPreset(
            "PR Review Squad", "5 reviewers with multi-model consensus (2+ models must agree)",
            "📋", MultiAgentMode.Orchestrator,
            "claude-opus-4.6", new[] { "claude-sonnet-4.6", "claude-sonnet-4.6", "claude-sonnet-4.6", "claude-sonnet-4.6", "claude-sonnet-4.6" })
        {
            WorkerSystemPrompts = new[]
            {
                WorkerReviewPrompt, WorkerReviewPrompt, WorkerReviewPrompt, WorkerReviewPrompt, WorkerReviewPrompt,
            },
            SharedContext = "## Review Standards\n\n- Only flag real issues: bugs, security holes, logic errors, data loss risks, race conditions\n- NEVER comment on style, formatting, naming conventions, or documentation\n- Every finding must include: file path, line number (or range), what's wrong, and why it matters\n- If a PR looks clean, say so — don't invent problems to justify your existence\n- An issue must be flagged by at least 2 of the 5 sub-agent models to be included in the final report (consensus filter)",
            RoutingContext = "When given a list of PRs to review, assign ONE PR to EACH worker. Distribute PRs round-robin across the available workers. If there are more PRs than workers, assign multiple PRs per worker.\n\nFor each PR assignment, just tell the worker: \"Review PR #<number>\"\n\nThe workers handle everything else — fetching the diff, dispatching multi-model sub-agents, and synthesizing results. Do NOT micromanage the review process.\n\nAfter all workers complete, produce a brief summary table:\n\n| PR | Verdict | Key Issues |\n|----|---------|------------|\n| #194 | ✅ Ready to merge | None |\n| #193 | ⚠️ Needs changes | Race condition in auth handler |\n\nVerdicts: ✅ Ready to merge, ⚠️ Needs changes, 🔴 Do not merge"
        },

        new GroupPreset(
            "Code Review Team", "Opus orchestrates, specialized reviewers execute",
            "🔍", MultiAgentMode.Orchestrator,
            "claude-opus-4.6", new[] { "gpt-5.1-codex", "claude-sonnet-4.5" })
        {
            WorkerSystemPrompts = new[]
            {
                "You are a code correctness reviewer. Focus on logic errors, edge cases, off-by-one bugs, null safety, and incorrect assumptions. Flag anything that could cause runtime failures or data corruption.",
                "You are a security and architecture reviewer. Focus on vulnerabilities (injection, auth flaws, data exposure), architectural anti-patterns, and maintainability issues. Suggest concrete fixes."
            }
        },

        new GroupPreset(
            "Multi-Perspective Analysis", "Different models analyze the same problem",
            "🔬", MultiAgentMode.Broadcast,
            "claude-opus-4.6", new[] { "gpt-5", "gemini-3-pro", "claude-sonnet-4.5" }),

        new GroupPreset(
            "Quick Reflection Cycle", "Fast workers + smart evaluator for iterative refinement",
            "🔄", MultiAgentMode.OrchestratorReflect,
            "claude-opus-4.6", new[] { "gpt-4.1", "gpt-4.1", "gpt-5.1-codex-mini" })
        {
            WorkerSystemPrompts = new[]
            {
                "You are an implementation specialist. Write clean, correct code. Focus on getting the logic right and handling edge cases.",
                "You are a testing and validation specialist. Review solutions for correctness, write test cases, and identify gaps in coverage.",
                "You are a documentation and UX specialist. Ensure code is well-documented, APIs are intuitive, and error messages are helpful."
            }
        },

        new GroupPreset(
            "Deep Research", "Strong reasoning models collaborate on complex problems",
            "🧠", MultiAgentMode.Orchestrator,
            "claude-opus-4.6", new[] { "gpt-5.1", "gemini-3-pro" })
        {
            WorkerSystemPrompts = new[]
            {
                "You are a deep reasoning analyst. Break down complex problems methodically. Provide thorough analysis with evidence and citations where possible.",
                "You are a creative problem solver. Explore unconventional approaches, challenge assumptions, and propose alternative solutions that others might miss."
            }
        },
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

    /// <summary>Get all presets: built-in + user-defined + repo-level (Squad). Repo overrides by name.</summary>
    public static GroupPreset[] GetAll(string baseDir, string? repoWorkingDirectory = null)
    {
        var merged = new Dictionary<string, GroupPreset>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in GroupPreset.BuiltIn) merged[p.Name] = p;
        foreach (var p in Load(baseDir)) merged[p.Name] = p;
        if (repoWorkingDirectory != null)
        {
            foreach (var p in SquadDiscovery.Discover(repoWorkingDirectory))
                merged[p.Name] = p;
        }
        return merged.Values.ToArray();
    }

    /// <summary>Save the current multi-agent group as a reusable preset.</summary>
    public static GroupPreset? SaveGroupAsPreset(string baseDir, string name, string description,
        string emoji, SessionGroup group, List<SessionMeta> members, Func<string, string> getEffectiveModel,
        string? worktreeRoot = null)
    {
        var orchestrator = members.FirstOrDefault(m => m.Role == MultiAgentRole.Orchestrator);
        var workers = members.Where(m => m.Role != MultiAgentRole.Orchestrator).ToList();

        if (orchestrator == null && workers.Count == 0) return null;

        var preset = new GroupPreset(
            name, description, emoji, group.OrchestratorMode,
            orchestrator != null ? getEffectiveModel(orchestrator.SessionName) : "claude-opus-4.6",
            workers.Select(w => getEffectiveModel(w.SessionName)).ToArray())
        {
            IsUserDefined = true,
            WorkerSystemPrompts = workers.Select(w => w.SystemPrompt).ToArray(),
            SharedContext = group.SharedContext,
            RoutingContext = group.RoutingContext,
        };

        // Write as .squad/ directory if worktree is available
        if (!string.IsNullOrEmpty(worktreeRoot) && Directory.Exists(worktreeRoot))
        {
            try
            {
                SquadWriter.WriteFromGroup(worktreeRoot, name, group, members, getEffectiveModel);
                preset = preset with { IsRepoLevel = true, SourcePath = Path.Combine(worktreeRoot, ".squad") };
            }
            catch { /* Fall through to JSON save */ }
        }

        // Always save to presets.json too (personal backup)
        var existing = Load(baseDir);
        existing.RemoveAll(p => p.Name == name);
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
