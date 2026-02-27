using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PolyPilot.Services;

/// <summary>
/// Extracts LLM efficiency metrics from a Copilot CLI session's events.jsonl
/// and process logs. Pure C# port of extract_session_metrics.py — no external dependencies.
/// </summary>
public static partial class SessionMetricsExtractor
{
    private static readonly Dictionary<string, ModelPricing> ModelPricingTable = new()
    {
        ["claude-opus-4.6"] = new(15.00, 75.00, 1.50),
        ["claude-opus-4.5"] = new(15.00, 75.00, 1.50),
        ["claude-sonnet-4.6"] = new(3.00, 15.00, 0.30),
        ["claude-sonnet-4.5"] = new(3.00, 15.00, 0.30),
        ["claude-sonnet-4"] = new(3.00, 15.00, 0.30),
        ["claude-haiku-4.5"] = new(0.80, 4.00, 0.08),
        ["gpt-5.3-codex"] = new(2.00, 8.00, 0.50),
        ["gpt-5.2-codex"] = new(2.00, 8.00, 0.50),
        ["gpt-5.2"] = new(2.00, 8.00, 0.50),
        ["gpt-5.1-codex-max"] = new(2.00, 8.00, 0.50),
        ["gpt-5.1-codex"] = new(2.00, 8.00, 0.50),
        ["gpt-5.1"] = new(2.00, 8.00, 0.50),
        ["gpt-5.1-codex-mini"] = new(0.40, 1.60, 0.10),
        ["gpt-5-mini"] = new(0.40, 1.60, 0.10),
        ["gpt-4.1"] = new(2.00, 8.00, 0.50),
        ["gemini-3-pro-preview"] = new(1.25, 10.00, 0.31),
    };

    private static readonly ModelPricing DefaultPricing = new(3.00, 15.00, 0.30);

    private static readonly string[] BuildKeywords =
    [
        "dotnet build", "dotnet msbuild", "msbuild", "npm run build",
        "cargo build", "go build", "make", "cmake", "gradle build",
        "mvn compile", "mvn package", "tsc", "webpack",
    ];

    private static readonly string[] TestKeywords =
    [
        "dotnet test", "npm test", "npm run test", "cargo test",
        "go test", "pytest", "jest", "mocha", "xunit", "nunit",
        "gradle test", "mvn test",
    ];

    private static readonly string[] BuildFailureKeywords =
    [
        "build failed", "error(s)", "compilation error",
        "failed!", "error cs", "error ts", "error:", "exited with exit code 1",
        "exited with exit code 2",
    ];

    /// <summary>
    /// Extract metrics from a session directory containing events.jsonl.
    /// </summary>
    public static SessionMetrics Extract(string sessionDir, string? processLogPath = null)
    {
        var eventsPath = Path.Combine(sessionDir, "events.jsonl");
        if (!File.Exists(eventsPath))
            throw new FileNotFoundException($"No events.jsonl found in {sessionDir}");

        var parsed = ParseEvents(eventsPath);
        var sessionId = parsed.SessionInfo.Id ?? Path.GetFileName(sessionDir);

        // Read workspace.yaml summary
        var workspaceYaml = Path.Combine(sessionDir, "workspace.yaml");
        if (File.Exists(workspaceYaml))
        {
            try
            {
                foreach (var line in File.ReadLines(workspaceYaml))
                {
                    if (line.StartsWith("summary:", StringComparison.Ordinal))
                    {
                        parsed.SessionInfo.Summary = line["summary:".Length..].Trim();
                        break;
                    }
                }
            }
            catch { /* ignore */ }
        }

        // Find and parse process log for LLM call telemetry
        var logPath = processLogPath ?? FindProcessLog(sessionId);
        var llmCalls = logPath != null ? ParseProcessLog(logPath, sessionId) : [];

        return BuildMetrics(parsed, llmCalls, logPath);
    }

    /// <summary>
    /// Find the session state directory for a given session ID (full or partial).
    /// </summary>
    public static string? FindSessionDir(string sessionId)
    {
        var sessionStateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot", "session-state");

        if (!Directory.Exists(sessionStateDir))
            return null;

        // Exact match
        var exact = Path.Combine(sessionStateDir, sessionId);
        if (Directory.Exists(exact) && File.Exists(Path.Combine(exact, "events.jsonl")))
            return exact;

        // Partial match
        foreach (var dir in Directory.EnumerateDirectories(sessionStateDir))
        {
            if (Path.GetFileName(dir).StartsWith(sessionId, StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(dir, "events.jsonl")))
                return dir;
        }

        return null;
    }

    private static ParsedEvents ParseEvents(string eventsPath)
    {
        var result = new ParsedEvents();
        ToolStartInfo? currentTurn = null;
        var toolStartMap = new Dictionary<string, ToolStartInfo>();

        foreach (var line in File.ReadLines(eventsPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl)) continue;
                var type = typeEl.GetString() ?? "";
                var ts = root.TryGetProperty("timestamp", out var tsEl) ? tsEl.GetString() ?? "" : "";
                var data = root.TryGetProperty("data", out var dataEl) ? dataEl : default;

                switch (type)
                {
                    case "session.start":
                        ParseSessionStart(data, ts, result);
                        break;

                    case "user.message":
                        var content = GetString(data, "content") ?? "";
                        result.UserMessages.Add(new UserMessage
                        {
                            ContentPreview = content.Length > 200 ? content[..200] : content,
                            Timestamp = ts,
                            FullLength = content.Length,
                        });
                        break;

                    case "assistant.turn_start":
                        currentTurn = new ToolStartInfo { Timestamp = ts };
                        break;

                    case "assistant.turn_end":
                        if (currentTurn != null)
                        {
                            result.Turns.Add(new TurnInfo
                            {
                                StartTime = currentTurn.Timestamp,
                                EndTime = ts,
                            });
                            currentTurn = null;
                        }
                        break;

                    case "tool.execution_start":
                        ParseToolStart(data, ts, toolStartMap, result);
                        break;

                    case "tool.execution_complete":
                        ParseToolComplete(data, ts, toolStartMap, result);
                        break;

                    case "subagent.started":
                        result.Subagents.Add(new SubagentInfo
                        {
                            ToolCallId = GetString(data, "toolCallId") ?? "",
                            Name = GetString(data, "agentName") ?? "",
                            DisplayName = GetString(data, "agentDisplayName") ?? "",
                            StartTime = ts,
                        });
                        break;

                    case "subagent.completed":
                        var tcid = GetString(data, "toolCallId") ?? "";
                        var sa = result.Subagents.FirstOrDefault(s => s.ToolCallId == tcid && s.EndTime == null);
                        if (sa != null) sa.EndTime = ts;
                        break;

                    case "session.compaction_start":
                        result.Compactions.Add(new CompactionInfo { StartTime = ts });
                        break;

                    case "session.compaction_complete":
                        var lastComp = result.Compactions.LastOrDefault();
                        if (lastComp is { EndTime: null })
                        {
                            lastComp.EndTime = ts;
                            lastComp.PreTokens = GetInt(data, "preCompactionTokens");
                            lastComp.Success = GetBool(data, "success");
                        }
                        break;

                    case "session.error":
                        var msg = GetString(data, "message") ?? "";
                        result.Errors.Add(new ErrorInfo
                        {
                            Type = GetString(data, "errorType") ?? "",
                            Message = msg.Length > 300 ? msg[..300] : msg,
                            Timestamp = ts,
                        });
                        break;
                }
            }
        }

        return result;
    }

    private static void ParseSessionStart(JsonElement data, string ts, ParsedEvents result)
    {
        if (data.ValueKind == JsonValueKind.Undefined) return;

        result.SessionInfo.Id = GetString(data, "sessionId") ?? "";
        result.SessionInfo.CopilotVersion = GetString(data, "copilotVersion") ?? "";
        result.SessionInfo.StartTime = GetString(data, "startTime") ?? ts;

        if (data.TryGetProperty("context", out var ctx))
        {
            result.SessionInfo.Cwd = GetString(ctx, "cwd") ?? "";
            result.SessionInfo.GitRoot = GetString(ctx, "gitRoot") ?? "";
            result.SessionInfo.Branch = GetString(ctx, "branch") ?? "";
            result.SessionInfo.Repository = GetString(ctx, "repository") ?? "";
        }
    }

    private static void ParseToolStart(JsonElement data, string ts,
        Dictionary<string, ToolStartInfo> toolStartMap, ParsedEvents result)
    {
        if (data.ValueKind == JsonValueKind.Undefined) return;

        var toolName = GetString(data, "toolName") ?? "";
        var toolCallId = GetString(data, "toolCallId") ?? "";

        var startInfo = new ToolStartInfo { Name = toolName, Timestamp = ts };
        toolStartMap[toolCallId] = startInfo;
        result.ToolStarts.Add(new ToolCallInfo { Name = toolName, Timestamp = ts, ToolCallId = toolCallId });

        // Track build/test for dev loop
        if (toolName == "bash" && data.TryGetProperty("arguments", out var args))
        {
            var cmd = GetString(args, "command") ?? "";
            var cmdLower = cmd.ToLowerInvariant();
            var isBuild = BuildKeywords.Any(kw => cmdLower.Contains(kw));
            var isTest = TestKeywords.Any(kw => cmdLower.Contains(kw));

            if (isBuild || isTest)
            {
                startInfo.DevLoop = new DevLoopEntry
                {
                    Type = isTest ? "test" : "build",
                    Command = cmd.Length > 200 ? cmd[..200] : cmd,
                    StartTime = ts,
                };
            }
        }
    }

    private static void ParseToolComplete(JsonElement data, string ts,
        Dictionary<string, ToolStartInfo> toolStartMap, ParsedEvents result)
    {
        if (data.ValueKind == JsonValueKind.Undefined) return;

        var toolCallId = GetString(data, "toolCallId") ?? "";
        var success = GetBool(data, "success") ?? false;
        toolStartMap.TryGetValue(toolCallId, out var startInfo);

        result.ToolCompletes.Add(new ToolCompleteInfo
        {
            ToolCallId = toolCallId,
            Name = startInfo?.Name ?? "",
            Success = success,
            StartTime = startInfo?.Timestamp ?? "",
            EndTime = ts,
        });

        if (startInfo?.DevLoop is { } dl)
        {
            var resultContent = "";
            if (data.TryGetProperty("result", out var resultEl))
            {
                resultContent = GetString(resultEl, "content") ?? GetString(resultEl, "detailedContent") ?? "";
            }
            var resultLower = resultContent.ToLowerInvariant();

            var buildFailed = BuildFailureKeywords.Any(kw => resultLower.Contains(kw));
            var errorMatch = ErrorCountRegex().Match(resultContent);
            if (errorMatch.Success)
                buildFailed = int.Parse(errorMatch.Groups[1].Value) > 0;

            var testFailed = false;
            var testMatch = TestResultRegex().Match(resultContent);
            if (testMatch.Success)
                testFailed = int.Parse(testMatch.Groups[1].Value) > 0;
            else if (resultLower.Contains("test run failed") || resultLower.Contains("tests failed"))
                testFailed = true;

            dl.EndTime = ts;
            dl.DurationMs = ComputeDurationMs(dl.StartTime, ts);
            dl.Success = success && !buildFailed && !testFailed;
            dl.ResultPreview = resultContent.Length > 200 ? resultContent[..200] : resultContent;
            if (testMatch.Success)
            {
                dl.TestFailed = int.Parse(testMatch.Groups[1].Value);
                dl.TestPassed = int.Parse(testMatch.Groups[2].Value);
            }
            result.DevLoop.Add(dl);
        }
    }

    private static string? FindProcessLog(string sessionId)
    {
        var logsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot", "logs");

        if (!Directory.Exists(logsDir)) return null;

        var logFiles = Directory.GetFiles(logsDir, "process-*.log")
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f));

        foreach (var logFile in logFiles)
        {
            try
            {
                var lineCount = 0;
                foreach (var line in File.ReadLines(logFile))
                {
                    if (lineCount++ > 30) break;
                    if (line.Contains(sessionId))
                        return logFile;
                }
            }
            catch { /* ignore */ }
        }

        return null;
    }

    private static List<LlmCallInfo> ParseProcessLog(string logPath, string sessionId)
    {
        var calls = new List<LlmCallInfo>();

        string content;
        try { content = File.ReadAllText(logPath); }
        catch { return calls; }

        var lines = content.Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            if (lines[i].Contains("[Telemetry] cli.telemetry:"))
            {
                i++;
                var jsonLines = new List<string>();
                var braceCount = 0;
                var started = false;

                while (i < lines.Length)
                {
                    var stripped = TimestampPrefixRegex().Replace(lines[i], "");
                    if (!started && stripped.TrimStart().StartsWith('{'))
                        started = true;

                    if (started)
                    {
                        jsonLines.Add(stripped);
                        braceCount += stripped.Count(c => c == '{') - stripped.Count(c => c == '}');
                        if (braceCount <= 0) break;
                    }
                    else if (started)
                        break;

                    i++;
                }

                if (jsonLines.Count > 0)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(string.Join("\n", jsonLines));
                        var root = doc.RootElement;
                        if (GetString(root, "kind") == "assistant_usage")
                        {
                            var blockSession = GetString(root, "session_id") ?? "";
                            if (!string.IsNullOrEmpty(sessionId) && blockSession != sessionId)
                            { i++; continue; }

                            var props = root.TryGetProperty("properties", out var p) ? p : default;
                            var metrics = root.TryGetProperty("metrics", out var m) ? m : default;

                            calls.Add(new LlmCallInfo
                            {
                                Model = GetString(props, "model") ?? "unknown",
                                Initiator = GetString(props, "initiator") ?? "unknown",
                                ApiCallId = GetString(props, "api_call_id") ?? "",
                                InputTokens = GetLong(metrics, "input_tokens"),
                                OutputTokens = GetLong(metrics, "output_tokens"),
                                CacheReadTokens = GetLong(metrics, "cache_read_tokens"),
                                CacheWriteTokens = GetLong(metrics, "cache_write_tokens"),
                                DurationMs = GetLong(metrics, "duration"),
                            });
                        }
                    }
                    catch (JsonException) { /* skip malformed */ }
                }
            }
            i++;
        }

        return calls;
    }

    private static SessionMetrics BuildMetrics(ParsedEvents parsed, List<LlmCallInfo> llmCalls, string? logPath)
    {
        var session = parsed.SessionInfo;

        // End time
        string? endTime = parsed.Turns.LastOrDefault()?.EndTime
            ?? parsed.UserMessages.LastOrDefault()?.Timestamp;
        session.EndTime = endTime;

        // LLM aggregates
        long totalInput = llmCalls.Sum(c => c.InputTokens);
        long totalOutput = llmCalls.Sum(c => c.OutputTokens);
        long totalCacheRead = llmCalls.Sum(c => c.CacheReadTokens);
        long totalCacheWrite = llmCalls.Sum(c => c.CacheWriteTokens);
        long totalDuration = llmCalls.Sum(c => c.DurationMs);
        double cacheHitRate = totalInput > 0 ? (double)totalCacheRead / totalInput * 100 : 0;

        var longest = llmCalls.Count > 0 ? llmCalls.MaxBy(c => c.DurationMs) : null;

        var modelsUsed = llmCalls.GroupBy(c => c.Model)
            .ToDictionary(g => g.Key, g => g.Count());

        double totalCost = llmCalls.Sum(c => ComputeCost(c.InputTokens, c.OutputTokens, c.CacheReadTokens, c.Model));

        int userTurns = parsed.UserMessages.Count;
        int totalTurns = parsed.Turns.Count;

        // Tool counts
        var toolCounts = parsed.ToolCompletes
            .GroupBy(t => t.Name)
            .ToDictionary(g => g.Key, g => g.Count());

        var longTools = parsed.ToolCompletes
            .Select(t => (t.Name, Duration: ComputeDurationMs(t.StartTime, t.EndTime)))
            .Where(t => t.Duration.HasValue && t.Duration.Value > 60_000)
            .Select(t => new LongRunningTool { Name = t.Name, DurationMs = t.Duration!.Value })
            .ToList();

        // Subagents
        var subagentSummary = parsed.Subagents.Select(sa => new SubagentSummary
        {
            Name = sa.Name,
            DisplayName = sa.DisplayName,
            DurationMs = ComputeDurationMs(sa.StartTime, sa.EndTime),
        }).ToList();

        long? sessionDurationMs = ComputeDurationMs(session.StartTime, endTime);

        // Dev loop
        var builds = parsed.DevLoop.Where(d => d.Type == "build").ToList();
        var tests = parsed.DevLoop.Where(d => d.Type == "test").ToList();
        var buildFailures = builds.Count(b => !b.Success);
        var testFailures = tests.Count(t => !t.Success);

        // Fix cycles
        var fixCycles = DetectFixCycles(builds);

        // Redundant builds
        var editTimestamps = parsed.ToolCompletes
            .Where(t => t.Name is "edit" or "create")
            .Select(t => t.EndTime)
            .ToHashSet();
        int redundantBuilds = DetectRedundantBuilds(builds, editTimestamps);

        int editCount = (toolCounts.GetValueOrDefault("edit") + toolCounts.GetValueOrDefault("create"));
        int editsWithoutValidation = editCount > 0 ? Math.Max(0, editCount - builds.Count - tests.Count) : 0;

        return new SessionMetrics
        {
            Session = session,
            LlmCalls = llmCalls,
            Turns = new TurnSummary { Total = totalTurns, UserInitiated = userTurns, AgentInitiated = totalTurns - userTurns },
            UserMessages = parsed.UserMessages,
            ToolCalls = new ToolCallSummary
            {
                Total = parsed.ToolCompletes.Count,
                ByName = toolCounts,
                LongRunning = longTools,
            },
            Subagents = subagentSummary,
            Compactions = parsed.Compactions,
            Errors = parsed.Errors,
            DevLoop = parsed.DevLoop,
            DevLoopSummary = new DevLoopSummary
            {
                TotalBuilds = builds.Count,
                TotalTests = tests.Count,
                BuildFailures = buildFailures,
                TestFailures = testFailures,
                BuildSuccessRatePct = builds.Count > 0 ? Math.Round((builds.Count - buildFailures) / (double)builds.Count * 100, 1) : null,
                FixCycles = fixCycles.Count,
                RedundantBuilds = redundantBuilds,
                TotalBuildTimeMs = builds.Sum(b => b.DurationMs ?? 0),
                TotalTestTimeMs = tests.Sum(t => t.DurationMs ?? 0),
                EditsWithoutValidation = editsWithoutValidation,
                TestPassed = tests.Sum(t => t.TestPassed),
                TestFailed = tests.Sum(t => t.TestFailed),
            },
            Summary = new MetricsSummary
            {
                TotalLlmCalls = llmCalls.Count,
                TotalInputTokens = totalInput,
                TotalOutputTokens = totalOutput,
                TotalCacheReadTokens = totalCacheRead,
                TotalCacheWriteTokens = totalCacheWrite,
                CacheHitRatePct = Math.Round(cacheHitRate, 1),
                TotalLlmDurationMs = totalDuration,
                AvgLlmDurationMs = llmCalls.Count > 0 ? totalDuration / llmCalls.Count : 0,
                LongestCall = longest != null ? new LongestCallInfo
                {
                    DurationMs = longest.DurationMs,
                    Model = longest.Model,
                    Initiator = longest.Initiator,
                } : null,
                ModelsUsed = modelsUsed,
                EstimatedCostUsd = Math.Round(totalCost, 4),
                SessionDurationMs = sessionDurationMs,
                TotalTurns = totalTurns,
                UserTurns = userTurns,
                AgentTurns = totalTurns - userTurns,
                TotalToolCalls = parsed.ToolCompletes.Count,
                TotalSubagents = parsed.Subagents.Count,
                TotalCompactions = parsed.Compactions.Count,
                TotalErrors = parsed.Errors.Count,
            },
            Meta = new MetaInfo
            {
                SessionDir = Path.GetDirectoryName(Path.Combine("dummy", "events.jsonl")) is { } _ ? Path.GetDirectoryName(Path.Combine(parsed.SessionInfo.Id ?? "", "events.jsonl")) : null,
                ProcessLog = logPath,
                LlmCallsFound = llmCalls.Count,
            },
        };
    }

    private static List<FixCycleInfo> DetectFixCycles(List<DevLoopEntry> builds)
    {
        var cycles = new List<FixCycleInfo>();
        var currentCycle = new List<DevLoopEntry>();

        foreach (var b in builds)
        {
            if (!b.Success)
            {
                currentCycle.Add(b);
            }
            else
            {
                if (currentCycle.Count >= 2)
                    cycles.Add(new FixCycleInfo { Failures = currentCycle.Count, FirstFailure = currentCycle[0].StartTime, Resolution = b.StartTime });
                currentCycle.Clear();
            }
        }
        if (currentCycle.Count >= 2)
            cycles.Add(new FixCycleInfo { Failures = currentCycle.Count, FirstFailure = currentCycle[0].StartTime });

        return cycles;
    }

    private static int DetectRedundantBuilds(List<DevLoopEntry> builds, HashSet<string> editTimestamps)
    {
        if (builds.Count < 2) return 0;
        int count = 0;
        DevLoopEntry? prev = null;

        foreach (var b in builds)
        {
            if (prev != null && b.Success)
            {
                var prevCmd = prev.Command.Length > 80 ? prev.Command[..80] : prev.Command;
                var currCmd = b.Command.Length > 80 ? b.Command[..80] : b.Command;
                if (prevCmd == currCmd)
                {
                    var prevEnd = prev.EndTime ?? prev.StartTime;
                    var edits = editTimestamps.Any(et =>
                        string.Compare(et, prevEnd, StringComparison.Ordinal) > 0 &&
                        string.Compare(et, b.StartTime, StringComparison.Ordinal) < 0);
                    if (!edits) count++;
                }
            }
            prev = b;
        }

        return count;
    }

    private static double ComputeCost(long inputTokens, long outputTokens, long cacheReadTokens, string model)
    {
        var pricing = ModelPricingTable.GetValueOrDefault(model, DefaultPricing);
        var freshInput = Math.Max(0, inputTokens - cacheReadTokens);
        return freshInput * pricing.Input / 1_000_000.0
             + cacheReadTokens * pricing.CachedInput / 1_000_000.0
             + outputTokens * pricing.Output / 1_000_000.0;
    }

    private static long? ComputeDurationMs(string? startTs, string? endTs)
    {
        var start = ParseTimestamp(startTs);
        var end = ParseTimestamp(endTs);
        if (start.HasValue && end.HasValue)
            return (long)(end.Value - start.Value).TotalMilliseconds;
        return null;
    }

    private static DateTimeOffset? ParseTimestamp(string? ts)
    {
        if (string.IsNullOrEmpty(ts)) return null;
        if (DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt;
        return null;
    }

    // Helper methods for safe JsonElement access
    private static string? GetString(JsonElement el, string prop) =>
        el.ValueKind != JsonValueKind.Undefined && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static long GetLong(JsonElement el, string prop) =>
        el.ValueKind != JsonValueKind.Undefined && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? (long)v.GetDouble() : 0;

    private static int? GetInt(JsonElement el, string prop) =>
        el.ValueKind != JsonValueKind.Undefined && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? (int)v.GetDouble() : null;

    private static bool? GetBool(JsonElement el, string prop)
    {
        if (el.ValueKind == JsonValueKind.Undefined || !el.TryGetProperty(prop, out var v))
            return null;
        return v.ValueKind == JsonValueKind.True ? true
             : v.ValueKind == JsonValueKind.False ? false
             : null;
    }

    [GeneratedRegex(@"(\d+)\s+Error\(s\)")]
    private static partial Regex ErrorCountRegex();

    [GeneratedRegex(@"Passed!\s*-\s*Failed:\s*(\d+),\s*Passed:\s*(\d+)")]
    private static partial Regex TestResultRegex();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T[\d:.]+Z\s*")]
    private static partial Regex TimestampPrefixRegex();

    // --- Data types ---

    public record ModelPricing(double Input, double Output, double CachedInput);

    public class SessionInfo
    {
        public string? Id { get; set; }
        public string? CopilotVersion { get; set; }
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
        public string? Cwd { get; set; }
        public string? GitRoot { get; set; }
        public string? Branch { get; set; }
        public string? Repository { get; set; }
        public string? Summary { get; set; }
    }

    public class UserMessage
    {
        public string ContentPreview { get; set; } = "";
        public string Timestamp { get; set; } = "";
        public int FullLength { get; set; }
    }

    public class TurnInfo
    {
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
    }

    public class ToolCallInfo
    {
        public string Name { get; set; } = "";
        public string Timestamp { get; set; } = "";
        public string ToolCallId { get; set; } = "";
    }

    public class ToolCompleteInfo
    {
        public string ToolCallId { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Success { get; set; }
        public string StartTime { get; set; } = "";
        public string EndTime { get; set; } = "";
    }

    private class ToolStartInfo
    {
        public string Name { get; set; } = "";
        public string Timestamp { get; set; } = "";
        public DevLoopEntry? DevLoop { get; set; }
    }

    public class SubagentInfo
    {
        public string ToolCallId { get; set; } = "";
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
    }

    public class CompactionInfo
    {
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
        public int? PreTokens { get; set; }
        public bool? Success { get; set; }
    }

    public class ErrorInfo
    {
        public string Type { get; set; } = "";
        public string Message { get; set; } = "";
        public string Timestamp { get; set; } = "";
    }

    public class DevLoopEntry
    {
        public string Type { get; set; } = "";
        public string Command { get; set; } = "";
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
        public long? DurationMs { get; set; }
        public bool Success { get; set; }
        public string? ResultPreview { get; set; }
        public int TestPassed { get; set; }
        public int TestFailed { get; set; }
    }

    public class LlmCallInfo
    {
        public string Model { get; set; } = "unknown";
        public string Initiator { get; set; } = "unknown";
        public string ApiCallId { get; set; } = "";
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheWriteTokens { get; set; }
        public long DurationMs { get; set; }
    }

    private class ParsedEvents
    {
        public SessionInfo SessionInfo { get; } = new();
        public List<UserMessage> UserMessages { get; } = [];
        public List<TurnInfo> Turns { get; } = [];
        public List<ToolCallInfo> ToolStarts { get; } = [];
        public List<ToolCompleteInfo> ToolCompletes { get; } = [];
        public List<SubagentInfo> Subagents { get; } = [];
        public List<CompactionInfo> Compactions { get; } = [];
        public List<ErrorInfo> Errors { get; } = [];
        public List<DevLoopEntry> DevLoop { get; } = [];
    }

    // --- Output types ---

    public class SessionMetrics
    {
        public SessionInfo Session { get; set; } = new();
        public List<LlmCallInfo> LlmCalls { get; set; } = [];
        public TurnSummary Turns { get; set; } = new();
        public List<UserMessage> UserMessages { get; set; } = [];
        public ToolCallSummary ToolCalls { get; set; } = new();
        public List<SubagentSummary> Subagents { get; set; } = [];
        public List<CompactionInfo> Compactions { get; set; } = [];
        public List<ErrorInfo> Errors { get; set; } = [];
        public List<DevLoopEntry> DevLoop { get; set; } = [];
        public DevLoopSummary DevLoopSummary { get; set; } = new();
        public MetricsSummary Summary { get; set; } = new();
        public MetaInfo Meta { get; set; } = new();
    }

    public class TurnSummary
    {
        public int Total { get; set; }
        public int UserInitiated { get; set; }
        public int AgentInitiated { get; set; }
    }

    public class ToolCallSummary
    {
        public int Total { get; set; }
        public Dictionary<string, int> ByName { get; set; } = [];
        public List<LongRunningTool> LongRunning { get; set; } = [];
    }

    public class LongRunningTool
    {
        public string Name { get; set; } = "";
        public long DurationMs { get; set; }
    }

    public class SubagentSummary
    {
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public long? DurationMs { get; set; }
    }

    public class DevLoopSummary
    {
        public int TotalBuilds { get; set; }
        public int TotalTests { get; set; }
        public int BuildFailures { get; set; }
        public int TestFailures { get; set; }
        public double? BuildSuccessRatePct { get; set; }
        public int FixCycles { get; set; }
        public int RedundantBuilds { get; set; }
        public long TotalBuildTimeMs { get; set; }
        public long TotalTestTimeMs { get; set; }
        public int EditsWithoutValidation { get; set; }
        public int TestPassed { get; set; }
        public int TestFailed { get; set; }
    }

    public class MetricsSummary
    {
        public int TotalLlmCalls { get; set; }
        public long TotalInputTokens { get; set; }
        public long TotalOutputTokens { get; set; }
        public long TotalCacheReadTokens { get; set; }
        public long TotalCacheWriteTokens { get; set; }
        public double CacheHitRatePct { get; set; }
        public long TotalLlmDurationMs { get; set; }
        public long AvgLlmDurationMs { get; set; }
        public LongestCallInfo? LongestCall { get; set; }
        public Dictionary<string, int> ModelsUsed { get; set; } = [];
        public double EstimatedCostUsd { get; set; }
        public long? SessionDurationMs { get; set; }
        public int TotalTurns { get; set; }
        public int UserTurns { get; set; }
        public int AgentTurns { get; set; }
        public int TotalToolCalls { get; set; }
        public int TotalSubagents { get; set; }
        public int TotalCompactions { get; set; }
        public int TotalErrors { get; set; }
    }

    public class LongestCallInfo
    {
        public long DurationMs { get; set; }
        public string Model { get; set; } = "";
        public string Initiator { get; set; } = "";
    }

    public class MetaInfo
    {
        public string? SessionDir { get; set; }
        public string? ProcessLog { get; set; }
        public int LlmCallsFound { get; set; }
    }

    private class FixCycleInfo
    {
        public int Failures { get; set; }
        public string? FirstFailure { get; set; }
        public string? Resolution { get; set; }
    }
}
