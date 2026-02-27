using System.Text;
using System.Text.Json;

namespace PolyPilot.Services;

/// <summary>
/// Manages LLM efficiency analysis for Copilot sessions.
/// Extracts metrics in-process via SessionMetricsExtractor (C#),
/// then sends the pre-computed JSON data with a prompt to a new Copilot session.
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

        var metrics = SessionMetricsExtractor.Extract(sessionDir);

        // Build unique analysis session name
        var analysisName = $"📊 {sessionName}";
        var existing = _copilotService.GetAllSessions().Select(s => s.Name).ToHashSet();
        var finalName = analysisName;
        var counter = 2;
        while (existing.Contains(finalName))
            finalName = $"{analysisName} ({counter++})";

        // Create session with target's working directory
        var workDir = targetSession.WorkingDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        await _copilotService.CreateSessionAsync(finalName, null, workDir);

        // Send prompt with pre-extracted data
        var prompt = BuildPrompt(sessionName, metrics);
        _ = _copilotService.SendPromptAsync(finalName, prompt);

        return finalName;
    }

    private static string BuildPrompt(string sessionName, SessionMetricsExtractor.SessionMetrics metrics)
    {
        var metricsJson = JsonSerializer.Serialize(metrics, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        var sb = new StringBuilder();
        sb.AppendLine($"# LLM Efficiency Analysis for \"{sessionName}\"");
        sb.AppendLine();
        sb.AppendLine("I've already extracted the session metrics. The full JSON data is below.");
        sb.AppendLine("Please analyze this data and produce a comprehensive efficiency report.");
        sb.AppendLine();
        sb.AppendLine("## Report Requirements");
        sb.AppendLine();
        sb.AppendLine("1. **LLM Usage Summary** — table with: session info, total LLM calls, user turns, agent turns (ratio),");
        sb.AppendLine("   session duration, total tokens (input/output/cached), cache hit rate, estimated cost, avg/longest call,");
        sb.AppendLine("   tool executions (top tools by count), sub-agents, compactions, errors");
        sb.AppendLine();
        sb.AppendLine("2. **Dev Loop Summary** (if builds/tests exist) — builds, tests, success rate, fix cycles, redundant builds");
        sb.AppendLine();
        sb.AppendLine("3. **LLM Call Distribution** — table by model: calls, input/output tokens, cache read, % of cost");
        sb.AppendLine();
        sb.AppendLine("4. **Efficiency Verdict** — emoji + one-line summary (✅ Efficient, ⚠️ Some waste, ❌ Significant waste)");
        sb.AppendLine();
        sb.AppendLine("5. **Waste Findings** (max 5) — each with: what happened, estimated waste (calls/tokens/$), reduction strategy.");
        sb.AppendLine("   Only report patterns evidenced by the data. Check for: low cache rate (<30%), token bloat (>4K output/turn),");
        sb.AppendLine("   excessive agent turns (>15:1 ratio), compactions, sub-agent overhead, tool sprawl (>20 tools/turn),");
        sb.AppendLine("   single premium model for all calls, build failure loops, redundant builds");
        sb.AppendLine();
        sb.AppendLine("6. **Cost Estimate** — table with current vs optimized cost by category (heavy/medium/lightweight output),");
        sb.AppendLine("   using these prices per 1M tokens:");
        sb.AppendLine("   - Opus: $15/$75/$1.50 (input/output/cached)");
        sb.AppendLine("   - Sonnet: $3/$15/$0.30");
        sb.AppendLine("   - Haiku: $0.80/$4/$0.08");
        sb.AppendLine("   - GPT-4.1: $2/$8/$0.50");
        sb.AppendLine("   - GPT-5-mini: $0.40/$1.60/$0.10");
        sb.AppendLine();
        sb.AppendLine("7. **Reduction Strategies Summary** — ranked table by savings");
        sb.AppendLine();
        sb.AppendLine("8. **Efficiency Metrics vs Baseline** — compare against: simple task (5-15 calls, 50K-150K tokens)");
        sb.AppendLine("   and complex task (50-200 calls, 500K-2M tokens) baselines with ✅/⚠️/❌ assessments");
        sb.AppendLine();
        sb.AppendLine("Use tables over prose. Quantify everything — never say \"too many\" without a number.");
        sb.AppendLine("Lead with the verdict. Use Unicode emoji (❌, ✅, ⚠️, 💸).");
        sb.AppendLine();
        sb.AppendLine("## Extracted Metrics JSON");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(metricsJson);
        sb.AppendLine("```");

        return sb.ToString();
    }
}
