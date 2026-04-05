using System.Text.Json;

namespace PolyPilot.Services;

/// <summary>
/// Manages LLM efficiency analysis for Copilot sessions.
/// Extracts metrics in-process via SessionMetricsExtractor (C#),
/// then sends the pre-computed JSON data with the full analysis skill prompt.
/// </summary>
public class EfficiencyAnalysisService
{
    private readonly CopilotService _copilotService;

    public EfficiencyAnalysisService(CopilotService copilotService)
    {
        _copilotService = copilotService;
    }

    /// <summary>
    /// Creates a new analysis session for the given session and auto-sends the analysis prompt
    /// with pre-extracted metrics. Returns the name of the newly created session.
    /// </summary>
    public async Task<string> AnalyzeSessionAsync(string sessionName)
    {
        var targetSession = _copilotService.GetAllSessions()
            .FirstOrDefault(s => s.Name == sessionName)
            ?? throw new InvalidOperationException($"Session '{sessionName}' not found");

        var sessionId = targetSession.SessionId
            ?? throw new InvalidOperationException($"Session '{sessionName}' has no SessionId");

        // Extract metrics in-process
        var sessionDir = SessionMetricsExtractor.FindSessionDir(sessionId)
            ?? throw new InvalidOperationException($"Session directory not found for '{sessionId}'");

        // Extract metrics in-process and write to disk (offloaded to avoid blocking the UI thread)
        var (metrics, metricsPath) = await Task.Run(() =>
        {
            var m = SessionMetricsExtractor.Extract(sessionDir);
            var tempDir = Path.Combine(Path.GetTempPath(), "polypilot-efficiency-metrics");
            Directory.CreateDirectory(tempDir);
            var path = Path.Combine(tempDir, $"metrics-{sessionId}-{Guid.NewGuid():N}.json");
            var json = JsonSerializer.Serialize(m, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
            File.WriteAllText(path, json);
            return (m, path);
        });

        // Build unique analysis session name
        var analysisName = $"📊 {sessionName}";
        var finalName = analysisName;
        var workDir = targetSession.WorkingDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var counter = 1;
        // Optimization: skip known existing names
        var existing = _copilotService.GetAllSessions().Select(s => s.Name).ToHashSet();
        if (existing.Contains(finalName))
        {
            counter = 2;
            finalName = $"{analysisName} ({counter})";
            while (existing.Contains(finalName))
            {
                counter++;
                finalName = $"{analysisName} ({counter})";
            }
        }

        // Create session with retry for TOCTOU safety
        while (true)
        {
            try
            {
                await _copilotService.CreateSessionAsync(finalName, null, workDir);
                break;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already exists"))
            {
                counter++;
                finalName = $"{analysisName} ({counter})";
                if (counter > 100) throw; // Prevent infinite loop
            }
        }

        // Send prompt referencing the metrics file
        var prompt = BuildPrompt(sessionName, sessionDir, metricsPath);
        try
        {
            await _copilotService.SendPromptAsync(finalName, prompt);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EfficiencyAnalysis] SendPromptAsync failed: {ex.Message}");
            try { await _copilotService.CloseSessionAsync(finalName); } catch { }
            throw;
        }

        return finalName;
    }

    private static string BuildPrompt(string sessionName, string sessionDir, string metricsPath)
    {
        return $"""
            # LLM Efficiency Analysis Skill — Copilot CLI

            ## Speed Contract

            Complete in **≤ 3 tool-call rounds**:
            1. **Round 1** — Read the extracted metrics JSON and the session events file for additional context if needed
            2. **Round 2** — Do targeted investigation if needed (e.g., read specific events for context)
            3. **Round 3** — Write efficiency report and print summary

            Do NOT ask the user for metadata. The extracted metrics provide the data you need.

            ---

            ## Rules

            - **Read-only** — do not modify any session files, logs, or state.
            - **No quality scoring** — focus only on LLM resource consumption, not task quality.
            - **No user prompts** — extract everything from session data.
            - **≤ 5 waste findings** — this is a focused efficiency audit, not a full review.
            - **Always quantify** — every finding must include estimated calls/tokens/cost that could be saved.

            ---

            ## Workflow

            ### Step 1: Load the data

            The session metrics have already been extracted and saved to a JSON file.
            Start by reading this file to get all the data you need:

            - **Extracted metrics JSON:** `{metricsPath}`
            - **Session name:** {sessionName}
            - **Session state directory:** {sessionDir}
            - **Events file:** {sessionDir}/events.jsonl
            - **Workspace metadata:** {sessionDir}/workspace.yaml

            Read the metrics JSON file first. If it shows `llmCallsFound: 0`, the process log was not found
            or contained no matching telemetry. In that case, read the events.jsonl file directly and estimate
            tokens from `assistant.message` content fields (divide character count by 4 for ~tokens). Flag the
            report as using estimated tokens.

            You can also grep/search the events.jsonl for additional context about specific events.

            ### Step 2: Analyze and classify waste patterns

            Using the extracted JSON, check for these patterns. Only report patterns that are **evidenced by the data** — do not speculate.

            #### Waste Patterns

            | Pattern | Detection | Savings estimate |
            |---------|-----------|-----------------|
            | **Low cache hit rate** | `cache_hit_rate_pct` < 30% | Estimate: tokens that could be cached with better context reuse. Formula: `(30% - actual%) × total_input_tokens` |
            | **Token bloat per turn** | `total_output_tokens / user_turns` > 4000 avg output per user turn | Estimate: excess tokens vs. 2000-token baseline per turn |
            | **Excessive agentic turns** | `agent_turns / user_turns` ratio > 15 | Estimate: calls beyond expected 8-12 agent turns per user turn |
            | **Context window pressure** | Any compaction events found (`total_compactions > 0`) | Flag: `preCompactionTokens` shows how full the context got. Each compaction loses conversation fidelity |
            | **Sub-agent overhead** | Many sub-agents spawned for simple lookups that grep/glob could handle | Estimate: token cost of sub-agent context setup (~5K tokens each) |
            | **Tool call sprawl** | High tool-to-turn ratio (> 20 tools per turn average) with many sequential groups | Estimate: extra LLM calls needed to process sequential results vs. parallel |
            | **Long-running tool calls** | Tool executions > 60s in `long_running` array | Flag: blocked LLM progress, wasted wall-clock time |
            | **Single model for all calls** | Only one premium model in `models_used`, including sub-agent and naming calls | Estimate: re-price sub-agent/simple calls at cheaper model tier |
            | **Repeated context across turns** | Multiple consecutive LLM calls with `input_tokens` within 5% of each other | Estimate: duplicate context tokens across turns that could be reduced with `/compact` or narrower context |
            | **Retry/error loops** | `total_errors > 0` with subsequent retry patterns | Estimate: token cost of error + retry calls |

            #### Dev Loop Waste Patterns

            | Pattern | Detection | Savings estimate |
            |---------|-----------|-----------------|
            | **Build failure loops** | `build_failures > 2` or `fix_cycles` detected — consecutive failed builds with edits in between | Estimate: LLM calls spent in fix cycles × avg_call_cost. Each fix cycle = 2–4 extra LLM calls |
            | **Low build success rate** | `build_success_rate_pct < 70%` | Flag: agent is generating code that doesn't compile; suggests missing context or wrong patterns |
            | **Test failure loops** | `test_failures > 2` or test runs showing repeated failures | Estimate: LLM calls spent diagnosing + fixing × avg_call_cost |
            | **Redundant builds** | `redundant_builds > 0` — same build command run consecutively with no code changes | Estimate: wasted build time + unnecessary LLM round-trip to check results |
            | **Unvalidated code generation** | `edits_without_validation > 5` — many file edits without build/test validation | Flag: batch editing without validation risks compounding errors, leading to expensive fix cycles later |
            | **Excessive build time** | `total_build_time_ms` > 50% of `session_duration_ms` | Flag: build overhead dominating session time; consider incremental builds or pre-build step |

            ### Step 3: Write the efficiency report

            Compose and print the report. Use this format:

            ```markdown
            # LLM Efficiency Report — <SessionName> (<MM/DD/YYYY>)

            ## LLM Usage Summary

            | Metric | Value |
            |--------|-------|
            | **Session** | <summary from workspace.yaml> |
            | **Session ID** | `<id>` |
            | **Repository** | <repository> |
            | **Branch** | <branch> |
            | **Copilot Version** | <copilotVersion> |
            | **Primary Model** | <most-used model> |
            | **Total LLM calls** | <N> |
            | **User turns** | <N> |
            | **Agent turns** | <N> (ratio: <X>:1 agent:user) |
            | **Total session duration** | <X min Y sec> |
            | **Aggregate LLM time** | <~X min Y sec> (sum of all call durations) |
            | **Total tokens** | <N> input + <N> output = **<N> total** |
            | **Cached tokens** | <N> (<X%> of input) |
            | **Estimated cost** | 💸 **$X.XX** — [see Cost Methodology](#cost-methodology) |
            | **Avg call duration** | <X sec> |
            | **Longest call** | <X sec> (<model>, <initiator>) |
            | **Tool executions** | <N> total (<top 3 tools by count>) |
            | **Sub-agents spawned** | <N> |
            | **Context compactions** | <N> |
            | **Errors** | <N> |

            ### Dev Loop Summary

            | Metric | Value |
            |--------|-------|
            | **Builds** | <N> total (<M> succeeded, <K> failed) |
            | **Tests** | <N> total (<M> passed runs, <K> failed runs) |
            | **Build success rate** | <X%> |
            | **Fix cycles** | <N> (consecutive build failures requiring code fixes) |
            | **Redundant builds** | <N> (same build run consecutively with no changes) |
            | **Total build time** | <X min Y sec> (<Z%> of session duration) |
            | **Total test time** | <X min Y sec> |
            | **Unvalidated edits** | <N> edits without subsequent build/test |
            | **Test results (last run)** | <N> passed, <M> failed |

            (Include this section when `total_builds > 0` or `total_tests > 0`. Omit if the session had no build/test activity.)

            ### LLM Call Distribution

            | Model | Calls | Input Tokens | Output Tokens | Cache Read | % of Cost |
            |-------|-------|-------------|---------------|------------|-----------|
            | <model1> | <N> | <N> | <N> | <N> | <X%> |
            | <model2> | <N> | <N> | <N> | <N> | <X%> |
            | **Total** | **<N>** | **<N>** | **<N>** | **<N>** | **100%** |

            ### LLM Call Breakdown (user-initiated turns only)

            | # | Initiator | Model | Duration | Output Tokens | Notes |
            |---|-----------|-------|----------|---------------|-------|
            | 1 | user | <model> | <Xs> | <N> | <brief note: what prompted this turn> |
            | 2 | agent | <model> | <Xs> | <N> | <brief note: tool decisions, code gen, etc.> |

            (List up to 20 individual LLM calls from the telemetry data. For sessions with many calls, show the first 5, longest 5, and last 5 calls. Bold the longest call. Include initiator, model, duration, output tokens, and a brief note.
            For sessions with ≤ 25 calls, show all calls.)

            ### Call Categorization by Output Size

            | Category | Calls | Avg Output Tokens | % of Total Cost | Recommended Model |
            |----------|-------|-------------------|-----------------|-------------------|
            | Lightweight (output ≤ 200) | <N> | <N> | <X%> | Haiku / GPT-5-mini |
            | Medium (output 201–1000) | <N> | <N> | <X%> | Sonnet / GPT-4.1 |
            | Heavy (output > 1000) | <N> | <N> | <X%> | Current model (keep) |

            ## Efficiency Verdict: <emoji> <one-line summary>

            <1-3 sentences: was this session efficient, wasteful, or somewhere in between? Quantify the waste. Reference the dominant cost driver and potential savings.>

            ## Waste Findings

            ### Finding N: <title>

            - **What happened:** <describe the waste pattern — cite data evidence>
            - **Estimated waste:** <N calls / N tokens / $X.XX that could be saved>
            - **Reduction strategy:** <concrete, actionable change>
            - **Where to fix:** <`Model selection` | `Prompt design` | `Workflow` | `Tool usage` | etc.>

            (Repeat for each finding. Max 5. Use 💸 emoji prefix when the finding has a quantified dollar waste.)

            ## Cost Estimate 💸

            | Category | Calls | Input Tokens | Output Tokens | Current Cost | Optimized Cost | Savings |
            |----------|-------|-------------|---------------|-------------|----------------|---------|
            | Heavy generation | <N> | <N> | <N> | $X.XX | $X.XX (keep current) | $0.00 |
            | Medium tasks | <N> | <N> | <N> | $X.XX | $X.XX (Sonnet/GPT-4.1) | **$X.XX** |
            | Lightweight | <N> | <N> | <N> | $X.XX | $X.XX (Haiku/GPT-5-mini) | **$X.XX** |
            | **Total** | **<N>** | **<N>** | **<N>** | **$X.XX** | **$X.XX** | **$X.XX (Y%)** |

            ### Model pricing used (per 1M tokens)

            | Model | Input | Output | Cached Input | Recommended for |
            |-------|-------|--------|-------------|----------------|
            | Claude Opus 4.6 | $15.00 | $75.00 | $1.50 | Complex code generation only |
            | Claude Sonnet 4.6 | $3.00 | $15.00 | $0.30 | Medium tasks, fix iterations |
            | Claude Haiku 4.5 | $0.80 | $4.00 | $0.08 | Lightweight, sub-agents |
            | GPT-4.1 | $2.00 | $8.00 | $0.50 | Medium tasks (alternative) |
            | GPT-5-mini | $0.40 | $1.60 | $0.10 | Session naming, simple lookups |

            ### At-scale impact

            | Metric | Per session | 10 sessions/day | Monthly (200 sessions) |
            |--------|------------|-----------------|----------------------|
            | Current cost | $X.XX | $X.XX | $X.XX |
            | Optimized cost | $X.XX | $X.XX | $X.XX |
            | **Savings** | **$X.XX** | **$X.XX** | **$X.XX** |

            ## Reduction Strategies Summary

            | # | Strategy | Estimated savings | Effort |
            |---|----------|------------------|--------|
            | 1 | <strategy> | <$X.XX/session (Y%) or time saved> | <Low/Medium/High> |
            | 2 | ... | ... | ... |

            (Rank by savings descending. Low = config change, Medium = workflow change, High = architecture change.)

            ## Efficiency Metrics vs. Baseline

            | Metric | This Session | Baseline (simple task) | Baseline (complex task) | Assessment |
            |--------|-------------|----------------------|------------------------|------------|
            | LLM calls | <N> | 5–15 | 50–200 | <✅/⚠️/❌> |
            | User turns | <N> | 1–3 | 5–15 | <✅/⚠️/❌> |
            | Agent:User ratio | <X>:1 | 3–8:1 | 8–15:1 | <✅/⚠️/❌> |
            | Total tokens | <N> | 50K–150K | 500K–2M | <✅/⚠️/❌> |
            | Cache hit rate | <X%> | 30–50% | 50–80% | <✅/⚠️/❌> |
            | Tool calls | <N> | 10–50 | 100–500 | <✅/⚠️/❌> |
            | Sub-agents | <N> | 0–2 | 3–10 | <✅/⚠️/❌> |
            | Compactions | <N> | 0 | 0–1 | <✅/⚠️/❌> |
            | Errors | <N> | 0 | 0–2 | <✅/⚠️/❌> |
            | Builds | <N> | 1–5 | 5–30 | <✅/⚠️/❌> |
            | Build success rate | <X%> | 90–100% | 70–90% | <✅/⚠️/❌> |
            | Fix cycles | <N> | 0 | 0–3 | <✅/⚠️/❌> |
            | Test runs | <N> | 1–3 | 3–10 | <✅/⚠️/❌> |
            | Estimated cost | $X.XX | $0.50–$5 | $5–$50 | <✅/⚠️/❌> |
            | Duration | <X min> | 1–10 min | 10–120 min | <✅/⚠️/❌> |

            ## Cost Methodology

            The cost estimates in this report are **approximate and intended for relative comparison**, not billing predictions.

            ### Token source

            Token data was extracted from the **Copilot CLI process log** (`~/.copilot/logs/process-*.log`), which captures `assistant_usage` telemetry events with `input_tokens`, `output_tokens`, `cache_read_tokens`, and `cache_write_tokens` for every LLM API call.

            ### Call categorization

            API calls were classified by **output token count** as a proxy for call complexity:

            | Category | Output tokens | Rationale |
            |----------|--------------|-----------|
            | **Lightweight** | ≤ 200 | Short responses: session naming, report_intent, simple tool decisions |
            | **Medium** | 201–1000 | Moderate responses: analysis, planning, multi-tool orchestration |
            | **Heavy** | > 1000 | Substantial output: code generation, large edits, detailed explanations |

            ### Pricing model

            Costs use **publicly available list prices** (as of early 2026). These are **not** the actual prices charged through Copilot subscriptions, which are bundled into premium request quotas.

            ### Formula

            ```
            cost = (fresh_input × input_rate / 1M) + (cached_input × cached_rate / 1M) + (output × output_rate / 1M)
            where fresh_input = input_tokens - cache_read_tokens
            ```

            ### Token estimation fallback

            If the process log cannot be found (e.g., log was rotated or deleted), estimate tokens from the `events.jsonl` content:
            - **Output tokens:** Count characters in `assistant.message` content fields, divide by 4 (~4 chars per token)
            - **Input tokens:** Estimate at 3–5× output tokens for typical code tasks
            - Flag the report as using estimated tokens and note reduced accuracy

            ### Limitations

            - **Not actual billing:** Copilot CLI uses premium request quotas, not per-token billing. Cost estimates show raw API cost for relative comparison.
            - **Process log may include other sessions:** If multiple sessions shared the same process, calls are filtered by session_id but edge cases may exist.
            - **Category heuristic:** Output-token-based classification is approximate.
            - **Prices change:** Use savings percentages (not dollar amounts) for durable comparisons.
            ```

            ---

            ## Baseline Expectations (Reference — do not include in output)

            | Scenario | Expected LLM calls | Expected duration | Expected tokens |
            |----------|-------------------|-------------------|-----------------|
            | Quick question (1 turn) | 3–8 | 30s–2 min | 30K–80K |
            | Single-file edit | 8–20 | 2–10 min | 80K–300K |
            | Multi-file refactor | 30–80 | 10–45 min | 300K–1M |
            | Complex project work | 80–200+ | 30–120+ min | 1M–3M |
            | Plan + implement cycle | 20–60 | 10–30 min | 200K–800K |

            Sessions significantly exceeding these baselines warrant waste findings.

            ---

            ## Common Reduction Strategies (Reference — do not include in output)

            | Strategy | When to recommend | Expected impact |
            |----------|------------------|----------------|
            | **Use cheaper model for sub-agents** | Premium model used for explore/task agents | 30–60% sub-agent cost reduction |
            | **Improve cache hit rate** | Cache < 30%, especially with repeated context | 20–40% input token reduction |
            | **Reduce context window usage** | Compaction events triggered | Prevents fidelity loss, reduces re-send cost |
            | **Parallelize tool calls** | Many sequential tool calls that could be batched | Fewer LLM round-trips, faster completion |
            | **Use `/compact` proactively** | Long sessions approaching context limits | Prevents forced compaction |
            | **Scope tasks narrowly** | Agent exploring many files for a focused task | 30–50% fewer tool calls |
            | **Provide clear instructions** | High agent:user turn ratio from ambiguous prompts | 20–40% fewer turns |
            | **Use plan mode** | Agent doing trial-and-error instead of planning | Reduces wasted code gen calls |
            | **Avoid large file reads** | Many `view` calls reading full large files | Reduces input token bloat |
            | **Pre-build before starting** | Build errors in first iteration are pre-existing, not agent-caused | 1–2 fewer fix cycles |
            | **Add custom instructions for build/test** | Agent makes framework/pattern mistakes leading to build failures | 10–30% fewer fix cycles |
            | **Validate incrementally** | Agent batches many edits then builds once, hitting many errors at once | Smaller fix cycles, faster convergence |
            | **Use faster builds** | Build time > 50% of session time | Use incremental builds, skip unnecessary restores |

            ---

            ## Style

            - **Quantify everything** — never say "too many calls" without a number
            - **Lead with the verdict** — user wants to know "was this efficient?" immediately
            - **Actionable strategies** — every finding must have a concrete "do this to save X"
            - **Tables over prose** — use the summary table for scannable results
            - **Emoji**: ✅ Efficient, ⚠️ Some waste, ❌ Significant waste, 💸 for cost callouts
            - **Always use Unicode emoji** (❌, ✅, ⚠️, 💸) — never shortcodes like `:x:`

            ---

            ## Data Files

            Start by reading the extracted metrics JSON file:
            `{metricsPath}`

            Then proceed with the analysis workflow above.
            """;
    }
}
