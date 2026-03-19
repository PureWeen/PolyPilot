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

    /// <summary>
    /// Default worktree allocation strategy for this preset. Null = Shared.
    /// </summary>
    public WorktreeStrategy? DefaultWorktreeStrategy { get; init; }

    /// <summary>
    /// Maximum reflection iterations for OrchestratorReflect mode. Null = use default (5).
    /// </summary>
    public int? MaxReflectIterations { get; init; }

    /// <summary>
    /// Custom display names for workers, indexed to match WorkerModels.
    /// E.g. ["dotnet-validator", "anthropic-validator", "eval-generator"].
    /// Null = use default "worker-{i}" naming.
    /// </summary>
    public string?[]? WorkerDisplayNames { get; init; }

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
        - Assess test coverage: Are there new code paths that lack tests? Suggest specific test cases or scenarios that should be added.
        - End with recommended action: ✅ Approve, ⚠️ Request changes (with specific ask), or 🔴 Do not merge

        ## 5. Fix Process (when told to fix a PR)
        1. `gh pr checkout <number>` then `git fetch origin main && git rebase origin/main`
        2. View the file, find the issue, use the edit tool to make minimal changes
        3. Discover and run the repo's test suite (look for test projects, Makefiles, CI scripts, package.json scripts, etc.)
        4. Commit with Co-authored-by trailer, push with `--force-with-lease`
        5. After pushing, do a full re-review (repeat the 5-model dispatch above)

        ## 6. Re-Review Process (when previous findings exist)
        Include previous findings in each sub-agent prompt and ask them to report:
        ```
        ## Previous Findings Status
        - Finding 1: FIXED / STILL PRESENT / N/A
        ```

        ## Rules
        - If workers share a worktree, NEVER checkout a branch during review-only tasks — use `gh pr diff` instead
        - If each worker has its own isolated worktree, you may freely checkout branches for both review and fix tasks
        - Always include the FULL diff — never truncate
        - Use the edit tool for file changes, not sed
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
            SharedContext = """
                ## Review Standards

                - Only flag real issues: bugs, security holes, logic errors, data loss risks, race conditions
                - NEVER comment on style, formatting, naming conventions, or documentation
                - Every finding must include: file path, line number (or range), what's wrong, and why it matters
                - If a PR looks clean, say so — don't invent problems to justify your existence
                - An issue must be flagged by at least 2 of the 5 sub-agent models to be included in the final report (consensus filter)

                ## Fix Standards

                - When fixing a PR: checkout, git rebase origin/main, apply minimal fixes, run tests, commit with Co-authored-by trailer, push
                - After pushing fixes, always do a full re-review (5-model dispatch again)
                - Include previous findings in re-review prompts so sub-agents can verify fix status
                - Use --force-with-lease (never --force) when pushing rebased branches
                - Never git add -A blindly — use git add <specific-files> and check git status first

                ## Operational Lessons

                - Workers reliably complete review-only tasks (fetch diff + dispatch sub-agents)
                - Workers sometimes fail multi-step fix tasks silently — always verify push landed with git fetch
                - If a worker's fix task didn't produce a commit after 5+ minutes, re-dispatch with more explicit instructions
                - Opus workers are more reliable for complex fix+review tasks than Sonnet workers
                - Always include the FULL diff in sub-agent prompts (truncated diffs cause incorrect findings)
                """,
            RoutingContext = """
                ## Core Rule

                NEVER do the work yourself. Always delegate to a worker. Your role is to assign tasks, track state, synthesize results, and execute merges. The only actions you perform directly are: running `gh pr merge`, verifying pushes with `git fetch`, and producing summary tables. If the user explicitly asks you to handle something yourself, you may — but default to delegation.

                ## Task Assignment

                When given PRs to review, assign ONE PR to EACH worker. Distribute round-robin. If more PRs than workers, assign multiple per worker.

                For review-only tasks:
                - If workers share a worktree: "Review PR #<number>. Do NOT checkout the branch — use gh pr diff only."
                - If workers have isolated worktrees: "Review PR #<number>." (they can checkout freely)
                For fix tasks, tell the worker: "Fix PR #<number>. Checkout, rebase on origin/main, apply fixes, test, push, then re-review."

                Workers handle the multi-model dispatch internally. However, for fix tasks, you MUST give explicit step-by-step instructions.

                ## Orchestrator Responsibilities

                1. Track state: Which PRs each worker reviewed, findings, fix status, merge readiness
                2. Merge: gh pr merge <N> --squash
                3. Verify pushes: After a worker claims to have pushed, always run git fetch origin <branch> and check git log to confirm
                4. Re-dispatch on failure: Workers sometimes fail silently on multi-step tasks. Check for new commits after fix tasks.
                5. Re-review pattern: When re-reviewing, include previous findings in the prompt so sub-agents can verify what's fixed vs still present
                6. Worktree safety: If workers share a worktree, only ONE can checkout/push at a time. If workers have isolated worktrees, they can work in parallel.

                ## Summary Table Format

                After workers complete, produce:

                | PR | Verdict | Key Issues |
                |----|---------|------------|
                | #N | ✅ Ready to merge | None |

                Verdicts: ✅ Ready to merge, ⚠️ Needs changes, 🔴 Do not merge
                """,
            DefaultWorktreeStrategy = WorktreeStrategy.FullyIsolated
        },

        new GroupPreset(
            "Implement & Challenge", "Implementer builds, challenger reviews — loop until solid",
            "⚔️", MultiAgentMode.OrchestratorReflect,
            "claude-opus-4.6", new[] { "claude-sonnet-4.6", "claude-opus-4.6" })
        {
            WorkerSystemPrompts = new[]
            {
                """You are the Implementer. Your job is to write correct, clean, production-ready code that satisfies the requirements. You MUST make actual code changes using the edit/create tools — never just describe what to do. After making changes, run the build and tests to verify your work. When you receive feedback from the Challenger, address every point — fix bugs, handle edge cases, and improve the implementation. Commit your changes with descriptive messages after each iteration. If you disagree with feedback, explain why with evidence.""",
                """You are the Challenger. Your job is to find real problems in the Implementer's work. First, run `git diff` in your worktree to see exactly what changed. Then review the actual diffs for: bugs, missed edge cases, race conditions, incorrect assumptions, security issues, logic errors, and missing tests. Be specific — cite exact file paths, line numbers, and explain the failure scenario. Do NOT nitpick style or formatting. Run the build and tests yourself to verify correctness. If the implementation is solid and tests pass, say so clearly and emit [[GROUP_REFLECT_COMPLETE]].""",
            },
            RoutingContext = """
                ## Implement & Challenge Loop

                You orchestrate a two-agent loop between two workers. Your ONLY role is to relay messages between them using @worker: blocks.

                ### Worker Names
                - **worker-1** = Implementer (writes code)
                - **worker-2** = Challenger (reviews code)
                Use their full session names in @worker: directives (e.g., @worker:Implement & Challenge-worker-1).

                ### Dispatch Pattern
                1. **First dispatch**: Forward the user request to worker-1 via @worker: block.
                2. **After worker-1 completes**: Forward worker-1's FULL response to worker-2 via @worker: block. Ask worker-2 to review and either approve with [[GROUP_REFLECT_COMPLETE]] or provide feedback.
                3. **If worker-2 has feedback**: Forward the FULL feedback to worker-1 via @worker: block.
                4. **Repeat** until worker-2 emits [[GROUP_REFLECT_COMPLETE]] or max iterations reached.

                ### Rules
                - Always alternate: worker-1 → worker-2 → worker-1 → worker-2
                - Include the FULL output in every @worker: block (don't summarize)
                - You are a message relay — NEVER do work yourself, ONLY write @worker: blocks
                - Each response you give MUST contain exactly one @worker: block
                """,
            MaxReflectIterations = 10,
            DefaultWorktreeStrategy = WorktreeStrategy.GroupShared,
        },

        new GroupPreset(
            "Skill Validator", "Three-phase skill evaluation: generate evals → empirical A/B testing → prompt design review → orchestrator builds consensus",
            "⚖️", MultiAgentMode.OrchestratorReflect,
            "claude-opus-4.6", new[] { "claude-sonnet-4.6", "claude-sonnet-4.6", "claude-sonnet-4.6" })
        {
            WorkerSystemPrompts = new[]
            {
                """
                You are the Dotnet Skill Validator. You evaluate skills by actually RUNNING the `skill-validator` tool from dotnet/skills — not by guessing or theorizing.

                ## STEP 1: Ensure skill-validator is available

                Check if the binary exists, and download it if missing:
                ```bash
                if [ ! -x /tmp/skill-validator ]; then
                  echo "Downloading skill-validator..."
                  ARCH=$(uname -m)
                  case "$(uname -s)-${ARCH}" in
                    Darwin-arm64) PATTERN='skill-validator-osx-arm64.tar.gz' ;;
                    Darwin-x86_64) PATTERN='skill-validator-osx-x64.tar.gz' ;;
                    Linux-aarch64) PATTERN='skill-validator-linux-arm64.tar.gz' ;;
                    Linux-x86_64) PATTERN='skill-validator-linux-x64.tar.gz' ;;
                    *) echo "Unsupported platform: $(uname -s)-${ARCH}"; exit 1 ;;
                  esac
                  cd /tmp && gh release download skill-validator-nightly \
                    --repo dotnet/skills \
                    --pattern "$PATTERN" \
                    --clobber && \
                  tar xzf "$PATTERN"
                fi
                /tmp/skill-validator --help | head -5
                ```
                If `gh` is not available or the download fails, explain the failure and fall back to manual analysis (Step 3 only).

                ## STEP 2: Run skill-validator against the skill

                Run the tool against the skill directory. The skill directory must contain a `SKILL.md` file.

                ```bash
                /tmp/skill-validator <path-to-skill-directory> \
                  --runs 1 \
                  --model claude-sonnet-4.6 \
                  --verdict-warn-only \
                  --verbose \
                  --results-dir /tmp/skill-validator-results
                ```

                **Flags explained:**
                - `--runs 1`: Single run for speed (the Anthropic evaluator covers qualitative depth)
                - `--model claude-sonnet-4.6`: Use Sonnet for agent runs (fast, cost-effective)
                - `--verdict-warn-only`: Don't fail hard on missing eval.yaml — report the gap instead
                - `--verbose`: Show tool calls and agent events so we can see what happened

                **If the skill has no `tests/eval.yaml`:** Report this as a finding. The tool will still run static profile analysis. Note this gap prominently in your verdict.

                **After the run:** Read the results:
                ```bash
                cat /tmp/skill-validator-results/summary.md 2>/dev/null
                cat /tmp/skill-validator-results/results.json 2>/dev/null | head -100
                ```

                ## STEP 3: Analyze the tool output

                Based on the ACTUAL tool output, assess:
                - **Profile analysis**: Token count, section structure, code blocks (from tool's static analysis)
                - **A/B comparison**: Did the skill improve agent performance? By how much?
                - **Statistical significance**: Was the improvement score above the threshold?
                - **Overfitting**: Did the tool flag any overfitting concerns?
                - **Task completion**: Did assertions pass with the skill active?

                If the tool couldn't run (no eval.yaml, download failed, etc.), perform manual analysis:
                - Read SKILL.md and estimate token count, structure quality
                - Identify likely improvement scenarios and potential regressions
                - Note that this is manual analysis, not empirical data

                ## STEP 4: Verdict

                Format your verdict as:
                ```
                ## Dotnet Validator Verdict
                **Tool Run**: ✅ Completed / ⚠️ Partial (no eval.yaml) / ❌ Failed (reason)
                **Improvement Score**: X% [CI: low%, high%] (from tool) or N/A
                **Overall Score**: X/10
                **Confidence**: High / Medium / Low
                **Verdict**: KEEP / IMPROVE / REMOVE

                ### Tool Output Summary
                - [key findings from skill-validator output]

                ### Strengths
                - [specific strengths with evidence from tool output]

                ### Weaknesses
                - [specific weaknesses with evidence from tool output]

                ### Suggested Improvements
                - [concrete, actionable suggestions]
                ```

                ## Rules
                - ALWAYS attempt to run the tool first. Do not skip to manual analysis.
                - Include the actual tool output or error in your report — transparency matters.
                - If the skill has no eval.yaml, recommend creating one and suggest specific scenarios.
                - Be data-driven: cite tool metrics, not vibes.
                """,

                """
                You are the Anthropic Skill Evaluator. You evaluate skills through the lens of prompt engineering quality, trigger accuracy, and agent guidance design.

                ## Your Evaluation Approach

                For each skill under evaluation, assess these dimensions:

                ### 1. Description Quality (Trigger Accuracy)
                - Is the description specific enough to trigger reliably for its intended use cases?
                - Is it too broad — will it trigger for unintended tasks?
                - Does it include enough trigger phrases/keywords to match user intent?
                - Rate trigger precision (0-10) and recall (0-10)

                ### 2. Instruction Clarity
                - Are the instructions in SKILL.md clear, actionable, and unambiguous?
                - Do they tell the agent *exactly* what to do and in what order?
                - Are there missing edge cases or situations the skill doesn't handle?
                - Does the skill avoid over-constraining the agent in ways that limit helpfulness?

                ### 3. Scope Appropriateness
                - Is the skill focused on a single, well-defined capability?
                - Is the skill too broad (trying to do too much) or too narrow (not useful enough)?
                - Does the skill overlap significantly with other skills? (potential conflicts)

                ### 4. Test Coverage
                - Does the eval.yaml cover the happy path?
                - Does it cover edge cases and failure modes?
                - Are negative test cases present (things the skill should NOT do)?

                ### 5. Verdict
                Format your verdict as:
                ```
                ## Anthropic Evaluator Verdict
                **Overall Score**: X/10
                **Trigger Precision**: X/10 | **Trigger Recall**: X/10
                **Verdict**: KEEP / IMPROVE / REMOVE

                ### Strengths
                - [specific strengths]

                ### Weaknesses
                - [specific weaknesses]

                ### Suggested Improvements
                - [concrete rewrites or additions with examples]
                ```

                ## Rules
                - Be concrete: quote specific lines from SKILL.md when critiquing
                - Focus on prompt quality, not empirical test results
                - Suggest specific rewrites, not vague advice like "be clearer"

                ## Optional: Live Trigger Testing with Claude Code
                If `claude` CLI is available, test trigger accuracy with non-interactive prompts:
                ```bash
                # Check availability first
                which claude 2>/dev/null && claude --version 2>&1
                ```
                If available and authenticated, run:
                ```bash
                # Positive test — should trigger the skill
                claude -p "<prompt that should trigger>" --output-format text 2>&1 | head -50
                # Negative test — should NOT trigger the skill  
                claude -p "<unrelated prompt>" --output-format text 2>&1 | head -50
                ```
                If `claude` returns auth errors ("does not have access", "login again"), skip live
                testing and note: "Claude Code CLI not authenticated — trigger testing skipped."
                Do NOT retry auth failures. Fall back to manual analysis.
                """,

                """
                You are the Eval Generator. You create `tests/eval.yaml` files for skills that don't have them, enabling empirical A/B validation by the skill-validator tool.

                ## Your Job

                Given a skill directory with SKILL.md, generate a comprehensive `eval.yaml` that tests whether the skill actually improves agent behavior.

                ## STEP 1: Read the skill

                Read the SKILL.md file and understand:
                - What the skill teaches the agent to do
                - What triggers should activate the skill
                - What the agent should do differently WITH the skill vs WITHOUT it
                - What mistakes the skill prevents

                ## STEP 2: Check for existing eval.yaml

                ```bash
                ls <skill-directory>/tests/eval.yaml 2>/dev/null
                ```
                If one already exists, read it and IMPROVE it rather than replacing it.
                If none exists, create the `tests/` directory and generate a new one.

                ## STEP 3: Design scenarios

                Create 3-5 scenarios covering:

                1. **Positive trigger (happy path)**: A prompt that SHOULD trigger the skill. Assert the agent follows the skill's key instructions.
                2. **Negative trigger**: A prompt that should NOT trigger the skill. Assert the agent doesn't mention skill-specific concepts.
                3. **Edge case**: A prompt that's ambiguous or borderline. The skill should help the agent handle it correctly.
                4. **Regression prevention**: A prompt that tests a specific mistake the skill warns about. Assert the agent avoids the mistake.

                ### Assertion guidelines
                - Use `output_contains` for key terms/patterns the skilled agent MUST produce
                - Use `output_not_contains` for anti-patterns the skilled agent must avoid
                - Use `output_matches` with regex for flexible pattern matching
                - Keep assertions focused on BEHAVIOR differences, not exact wording (avoid overfitting)

                ### Rubric guidelines
                - Each rubric item should describe a QUALITY criterion the judge can evaluate
                - Focus on outcomes ("correctly identified the issue") not vocabulary ("used the word X")
                - Include 2-4 rubric items per scenario
                - Rubric items are evaluated by pairwise LLM comparison (with-skill vs without-skill)

                ## STEP 4: Write the eval.yaml

                ```bash
                mkdir -p <skill-directory>/tests
                cat > <skill-directory>/tests/eval.yaml << 'EVALEOF'
                scenarios:
                  - name: "Description of scenario"
                    prompt: "The exact prompt to send to the agent"
                    assertions:
                      - type: "output_contains"
                        value: "expected behavior indicator"
                    rubric:
                      - "Quality criterion for the judge"
                    timeout: 120
                EVALEOF
                ```

                ## STEP 5: Validate the file

                ```bash
                cat <skill-directory>/tests/eval.yaml
                # Verify it's valid YAML
                python3 -c "import yaml; yaml.safe_load(open('<skill-directory>/tests/eval.yaml'))" 2>&1 || echo "YAML parse error"
                ```

                ## Output format

                After writing the file, report:
                ```
                ## Eval Generator Report
                **Skill**: [skill name]
                **Scenarios Generated**: N
                **File Written**: <path>/tests/eval.yaml

                ### Scenarios
                1. [name] — [what it tests]
                2. [name] — [what it tests]
                ...

                ### Design Rationale
                - [why these specific scenarios were chosen]
                - [what behavioral differences they target]
                ```

                ## Rules
                - ALWAYS write the file to disk. Your output is consumed by other workers.
                - Focus on BEHAVIORAL differences — what changes when the skill is active?
                - Avoid overfitting: don't assert the skill's exact vocabulary, assert the outcomes
                - Keep prompts realistic — they should sound like real user requests
                - Set reasonable timeouts (60-180s depending on complexity)
                - Use `exit_success` assertion sparingly — it just checks for non-empty output
                """,
            },
            SharedContext = """
                ## Skill Evaluation Standards

                All three workers assess the same skill from different angles.
                A good skill must satisfy the empirical validator AND the prompt reviewer to be marked KEEP.

                ### What makes a skill worth keeping
                - Measurable improvement in task completion (Dotnet validator perspective)
                - Clear, precise description that triggers reliably (Anthropic evaluator perspective)
                - Comprehensive eval.yaml that tests real behavioral differences (Eval Generator perspective)
                - Focused scope — does one thing well
                - Actionable instructions that guide the agent without over-constraining it

                ### What warrants IMPROVE
                - Good intent but fixable gaps (bad trigger description, missing scenarios, ambiguous instructions)
                - One evaluator says KEEP but the other says REMOVE with specific concerns

                ### What warrants REMOVE
                - No measurable improvement in empirical testing
                - Trigger description too broad/narrow to be useful
                - Instructions that would cause regressions or confusion

                ### Consensus Rule
                - KEEP requires both evaluators (dotnet-validator and anthropic-validator) to say KEEP or one KEEP + one IMPROVE
                - IMPROVE if the evaluators disagree or both say IMPROVE
                - REMOVE if either evaluator says REMOVE with strong evidence

                ### eval.yaml Format Reference
                ```yaml
                scenarios:
                  - name: "Descriptive name"
                    prompt: "Prompt sent to the agent"
                    assertions:
                      - type: "output_contains"
                        value: "expected text"
                      - type: "output_not_contains"
                        value: "text that should not appear"
                      - type: "output_matches"
                        pattern: "regex"
                    rubric:
                      - "Quality criterion for pairwise LLM judge"
                    timeout: 120
                ```
                Assertion types: output_contains, output_not_contains, output_matches, output_not_matches, file_exists, file_contains, exit_success.
                """,
            RoutingContext = """
                ## Skill Validator Orchestration

                ⚠️ CRITICAL: You are a DISPATCHER. You delegate work ONLY via @worker:/@end blocks.
                NEVER use task(), bash(), view(), grep(), or ANY tool yourself. NEVER read files, run commands, or do analysis.
                If you catch yourself about to call a tool — STOP and write an @worker block instead.

                ### Delegation Format (the ONLY way to assign work)

                ```
                @worker:Skill Validator-eval-generator
                Your task: [detailed instructions]
                @end
                ```

                You MUST use the FULL worker session name (e.g., "Skill Validator-eval-generator", not just "eval-generator").
                Each response MUST contain @worker:/@end blocks. Text outside the blocks is your planning notes.

                ### Worker Names
                - **Skill Validator-dotnet-validator** = Dotnet Skill Validator (runs `skill-validator` tool)
                - **Skill Validator-anthropic-validator** = Anthropic Skill Evaluator (prompt analysis via Claude Code)
                - **Skill Validator-eval-generator** = Eval Generator (creates tests/eval.yaml)

                ### 2-Phase Dispatch

                **Phase 1 — Always dispatch eval-generator first:**

                Write a single @worker block for eval-generator. Tell it the skill directory path and to generate tests/eval.yaml.
                Do NOT check if eval.yaml exists yourself — eval-generator will check and skip if it already exists.

                Example:
                ```
                @worker:Skill Validator-eval-generator
                Generate tests/eval.yaml for the skill at: <path>
                The skill is about: <brief description from user prompt>
                @end
                ```

                **Phase 2 — After eval-generator completes, dispatch both validators together:**

                Write TWO @worker blocks in a single response:
                ```
                @worker:Skill Validator-dotnet-validator
                Evaluate the skill at: <path>
                Eval.yaml status: [generated by eval-generator / pre-existing]
                @end

                @worker:Skill Validator-anthropic-validator
                Evaluate the skill at: <path>
                Eval.yaml status: [generated by eval-generator / pre-existing]
                @end
                ```

                **Phase 3 — After both evaluators complete, write the consensus report (no @worker blocks needed).**

                ### Consensus Report Format
                ```
                ## Skill Validator Consensus Report: [Skill Name]

                ### Summary
                **Eval Generation**: ✅ Pre-existing / 🆕 Generated by eval-generator / ❌ None
                **Dotnet Verdict**: KEEP/IMPROVE/REMOVE (X/10)
                **Anthropic Verdict**: KEEP/IMPROVE/REMOVE (X/10)
                **Consensus**: KEEP / IMPROVE / REMOVE

                ### Eval Quality (if generated)
                - [assessment of generated eval.yaml scenarios]

                ### Points of Agreement (High Confidence)
                - [issues both evaluators flagged]

                ### Points of Disagreement (Requires Judgment)
                - [Dotnet says X, Anthropic says Y — adopted: Z because ...]

                ### Adopted Suggestions
                - [suggestions we are recommending, with rationale]

                ### Declined Suggestions
                - [suggestions we are NOT adopting, with rationale]

                ### Final Recommendation
                [1-2 sentence actionable summary]
                ```

                ### Rules
                - NEVER use tools yourself — you are a message relay, not a worker
                - Always explain WHY suggestions are adopted or declined
                - Where evaluators disagree, explain the tradeoff and make a reasoned judgment
                - Highlight suggestions both evaluators agree on as high-confidence improvements
                - If eval-generator generated evals, note whether the Dotnet validator was able to use them
                - NEVER emit [[GROUP_REFLECT_COMPLETE]] until all dispatched workers have responded and a consensus report is produced
                """,
            MaxReflectIterations = 6,
            DefaultWorktreeStrategy = WorktreeStrategy.FullyIsolated,
            WorkerDisplayNames = new[] { "dotnet-validator", "anthropic-validator", "eval-generator" },
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

    /// <summary>Get all presets: built-in + user-defined + repo-level (Squad). Built-ins are never overridden.</summary>
    public static GroupPreset[] GetAll(string baseDir, string? repoWorkingDirectory = null)
    {
        var builtInNames = new HashSet<string>(GroupPreset.BuiltIn.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var merged = new Dictionary<string, GroupPreset>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in GroupPreset.BuiltIn) merged[p.Name] = p;
        foreach (var p in Load(baseDir))
            if (!builtInNames.Contains(p.Name)) merged[p.Name] = p;
        if (repoWorkingDirectory != null)
        {
            foreach (var p in SquadDiscovery.Discover(repoWorkingDirectory))
                if (!builtInNames.Contains(p.Name)) merged[p.Name] = p;
        }
        return merged.Values.ToArray();
    }

    /// <summary>Delete a repo-level preset by removing its .squad/ directory.</summary>
    public static bool DeleteRepoPreset(string repoWorkingDirectory, string presetName)
    {
        var presets = SquadDiscovery.Discover(repoWorkingDirectory);
        var match = presets.FirstOrDefault(p => string.Equals(p.Name, presetName, StringComparison.OrdinalIgnoreCase));
        if (match?.SourcePath == null) return false;

        // Validate the path is within the repo directory (prevent traversal)
        var fullRepoPath = Path.GetFullPath(repoWorkingDirectory);
        var fullSourcePath = Path.GetFullPath(match.SourcePath);
        if (!fullSourcePath.StartsWith(fullRepoPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return false;

        var dirName = Path.GetFileName(match.SourcePath);
        if (dirName != ".squad" && dirName != ".ai-team")
            return false;
        try
        {
            if (Directory.Exists(match.SourcePath))
                Directory.Delete(match.SourcePath, recursive: true);
            return true;
        }
        catch { return false; }
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
