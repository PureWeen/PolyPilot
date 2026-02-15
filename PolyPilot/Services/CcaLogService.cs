using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using PolyPilot.Models;

namespace PolyPilot.Services;

/// <summary>
/// Fetches CCA run logs from GitHub Actions, parses the agent conversation,
/// and assembles context for loading into a local CLI session.
/// </summary>
public partial class CcaLogService
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T[\d:.]+Z\s*")]
    private static partial Regex TimestampRegex();

    // Lines before this are runner boilerplate (setup, firewall, etc.)
    private const int BoilerplateEndLine = 175;
    // Max characters to include in the parsed prompt (keeps within context window)
    private const int MaxPromptChars = 300_000;
    // Max characters for inlined PR diff
    private const int MaxDiffChars = 50_000;

    /// <summary>
    /// Fetches and parses CCA run logs + PR data, assembles a context prompt,
    /// and saves full raw data to files in the specified directory.
    /// </summary>
    public async Task<CcaContext> LoadCcaContextAsync(
        string ownerRepo, CcaRun run, string contextDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(contextDir);

        // Fetch all data in parallel
        var logsTask = FetchRunLogsAsync(ownerRepo, run.Id, ct);
        var diffTask = run.PrNumber.HasValue
            ? FetchPrDiffAsync(ownerRepo, run.PrNumber.Value, ct)
            : Task.FromResult("");
        var prDataTask = run.PrNumber.HasValue
            ? FetchPrDataAsync(ownerRepo, run.PrNumber.Value, ct)
            : Task.FromResult(new PrData("", "", new List<string>()));
        var commentsTask = run.PrNumber.HasValue
            ? FetchPrCommentsAsync(ownerRepo, run.PrNumber.Value, ct)
            : Task.FromResult("");

        await Task.WhenAll(logsTask, diffTask, prDataTask, commentsTask);

        var rawLog = await logsTask;
        var diff = await diffTask;
        var prData = await prDataTask;
        var comments = await commentsTask;

        // Parse the agent conversation from raw logs
        var parsedLog = ParseAgentConversation(rawLog);

        // Save full raw data to files for on-demand access
        var rawLogPath = Path.Combine(contextDir, $"cca-run-{run.Id}.log");
        var diffPath = Path.Combine(contextDir, "pr-diff.patch");
        var commentsPath = Path.Combine(contextDir, "pr-comments.txt");
        await File.WriteAllTextAsync(rawLogPath, rawLog, ct);
        if (!string.IsNullOrEmpty(diff))
            await File.WriteAllTextAsync(diffPath, diff, ct);
        if (!string.IsNullOrEmpty(comments))
            await File.WriteAllTextAsync(commentsPath, comments, ct);

        // Assemble the context prompt
        var prompt = AssembleContextPrompt(run, parsedLog, diff, prData, comments, contextDir);

        return new CcaContext
        {
            Run = run,
            Prompt = prompt,
            RawLogPath = rawLogPath,
            DiffPath = !string.IsNullOrEmpty(diff) ? diffPath : null,
            CommentsPath = !string.IsNullOrEmpty(comments) ? commentsPath : null,
            ParsedLogLength = parsedLog.Length,
            RawLogLength = rawLog.Length
        };
    }

    /// <summary>
    /// Finds an existing session that was loaded from a CCA run.
    /// Matches by CcaRunId first, then falls back to matching the session name
    /// pattern "CCA: PR #{number}". When matched by name, backfills CCA metadata.
    /// </summary>
    public static AgentSessionInfo? FindExistingCcaSession(
        IEnumerable<AgentSessionInfo> sessions, CcaRun run)
    {
        // Primary: match by CcaRunId
        var match = sessions.FirstOrDefault(s => s.CcaRunId == run.Id);
        if (match != null) return match;

        // Fallback: match by session name prefix "CCA: PR #{number}"
        if (run.PrNumber.HasValue)
        {
            var prPrefix = $"CCA: PR #{run.PrNumber}";
            match = sessions.FirstOrDefault(s =>
                s.Name.StartsWith(prPrefix, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                // Backfill CCA metadata so future checks use the fast path
                match.CcaRunId = run.Id;
                match.CcaPrNumber = run.PrNumber;
                match.CcaBranch = run.HeadBranch;
                return match;
            }
        }

        return null;
    }

    /// <summary>
    /// Parse the raw CCA log to extract the agent conversation, stripping
    /// boilerplate, timestamps, and verbose tool output.
    /// </summary>
    internal static string ParseAgentConversation(string rawLog)
    {
        if (string.IsNullOrEmpty(rawLog)) return "";

        var lines = rawLog.Split('\n');
        var sb = new StringBuilder();
        var inRelevantSection = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Strip timestamp prefix (e.g. "2026-02-15T16:16:13.6839997Z ")
            var stripped = StripTimestamp(line);

            // Skip boilerplate at the start
            if (!inRelevantSection)
            {
                // Look for the "Processing requests..." marker or agent conversation start
                if (stripped.Contains("Processing requests") ||
                    stripped.Contains("Solving problem:") ||
                    stripped.Contains("Problem statement:") ||
                    stripped.StartsWith("copilot:"))
                {
                    inRelevantSection = true;
                }
                else
                {
                    continue;
                }
            }

            // Skip cleanup/post-processing at the end
            if (stripped.Contains("##[group]Run echo \"Cleaning up...\""))
                break;

            // Skip GitHub Actions group markers
            if (stripped.StartsWith("##[group]") || stripped.StartsWith("##[endgroup]"))
                continue;

            // Skip verbose firewall/networking noise
            if (stripped.Contains("kind: http-rule") || stripped.Contains("kind: ip-rule") ||
                stripped.Contains("url: { domain:") || stripped.Contains("url: { scheme:"))
                continue;

            // Truncate very long tool result lines (e.g., full file contents)
            if (stripped.Length > 2000)
            {
                sb.AppendLine(stripped[..2000] + " [... truncated]");
                continue;
            }

            sb.AppendLine(stripped);
        }

        var result = sb.ToString();

        // If still too large, take the beginning and end
        if (result.Length > MaxPromptChars)
        {
            var half = MaxPromptChars / 2;
            result = result[..half] +
                     "\n\n[... middle portion omitted for size — full log available on disk ...]\n\n" +
                     result[^half..];
        }

        return result;
    }

    private static string StripTimestamp(string line)
    {
        // Match "2026-02-15T16:15:47.0457981Z " pattern
        if (line.Length > 30 && line[4] == '-' && line[7] == '-' && line[10] == 'T' && line[^1] != 'Z')
        {
            var idx = line.IndexOf('Z');
            if (idx > 20 && idx < 35 && idx + 1 < line.Length && line[idx + 1] == ' ')
                return line[(idx + 2)..];
        }
        // Simpler: just strip leading timestamp pattern
        var match = TimestampRegex().Match(line);
        if (match.Success)
            return line[match.Length..];
        return line;
    }

    private string AssembleContextPrompt(
        CcaRun run, string parsedLog, string diff, PrData prData, string comments, string contextDir)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are continuing work on a task that was started by a GitHub Copilot Coding Agent (CCA) in a cloud environment.");
        sb.AppendLine();

        // PR summary
        if (run.PrNumber.HasValue)
        {
            sb.AppendLine($"**PR #{run.PrNumber}**: {run.PrTitle ?? run.DisplayTitle}");
            sb.AppendLine($"**Branch**: `{run.HeadBranch}`");
            sb.AppendLine($"**Status**: CCA run {run.Conclusion ?? run.Status}");
            if (!string.IsNullOrEmpty(run.PrUrl))
                sb.AppendLine($"**PR URL**: {run.PrUrl}");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine($"**Task**: {run.DisplayTitle}");
            sb.AppendLine($"**Branch**: `{run.HeadBranch}`");
            sb.AppendLine($"**Status**: CCA run {run.Conclusion ?? run.Status}");
            sb.AppendLine();
        }

        // PR description
        if (!string.IsNullOrEmpty(prData.Body))
        {
            sb.AppendLine("<pr_description>");
            sb.AppendLine(prData.Body);
            sb.AppendLine("</pr_description>");
            sb.AppendLine();
        }

        // Changed files list
        if (prData.Files.Count > 0)
        {
            sb.AppendLine($"**Files changed** ({prData.Files.Count}):");
            foreach (var file in prData.Files)
                sb.AppendLine($"  - {file}");
            sb.AppendLine();
        }

        // Recent CCA conversation (last portion if log is very long)
        sb.AppendLine("<cca_conversation>");
        if (parsedLog.Length > MaxPromptChars)
            sb.AppendLine(parsedLog[^MaxPromptChars..]);
        else
            sb.AppendLine(parsedLog);
        sb.AppendLine("</cca_conversation>");
        sb.AppendLine();

        // PR diff (truncated if needed)
        if (!string.IsNullOrEmpty(diff))
        {
            sb.AppendLine("<pr_diff>");
            if (diff.Length > MaxDiffChars)
            {
                sb.AppendLine(diff[..MaxDiffChars]);
                sb.AppendLine($"[... diff truncated at {MaxDiffChars} chars — full diff at {Path.GetFileName(contextDir)}/pr-diff.patch ...]");
            }
            else
            {
                sb.AppendLine(diff);
            }
            sb.AppendLine("</pr_diff>");
            sb.AppendLine();
        }

        // Review comments
        if (!string.IsNullOrEmpty(comments))
        {
            sb.AppendLine("<review_comments>");
            sb.AppendLine(comments);
            sb.AppendLine("</review_comments>");
            sb.AppendLine();
        }

        // File references
        sb.AppendLine("**Full data saved to disk for on-demand access:**");
        sb.AppendLine($"  - Full CCA log: `{contextDir}/cca-run-{run.Id}.log`");
        if (!string.IsNullOrEmpty(diff))
            sb.AppendLine($"  - Full PR diff: `{contextDir}/pr-diff.patch`");
        if (!string.IsNullOrEmpty(comments))
            sb.AppendLine($"  - Review comments: `{contextDir}/pr-comments.txt`");
        sb.AppendLine();

        // Instructions
        if (run.IsActive)
        {
            sb.AppendLine("⚠️ **NOTE**: The CCA run is still in progress. Be careful not to push conflicting changes to the same branch.");
            sb.AppendLine();
        }

        sb.AppendLine("You now have full context of what the CCA did. Wait for the user's instructions on how to proceed.");

        return sb.ToString();
    }

    // --- GitHub data fetching ---

    private static async Task<string> FetchRunLogsAsync(string ownerRepo, long runId, CancellationToken ct)
    {
        var tempZip = Path.GetTempFileName();
        try
        {
            // Download logs zip
            var psi = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = $"api /repos/{ownerRepo}/actions/runs/{runId}/logs",
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi)!;
            using var fs = File.Create(tempZip);
            await process.StandardOutput.BaseStream.CopyToAsync(fs, ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                Console.WriteLine($"[CcaLogService] Failed to download logs for run {runId}");
                return "";
            }

            // Extract the main log file from the zip
            using var archive = ZipFile.OpenRead(tempZip);
            // Look for the main copilot log (usually "0_copilot.txt")
            var entry = archive.Entries.FirstOrDefault(e =>
                e.Name.EndsWith("copilot.txt", StringComparison.OrdinalIgnoreCase) &&
                !e.FullName.Contains("system.txt"));
            if (entry == null)
                entry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".txt"));

            if (entry != null)
            {
                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync(ct);
            }

            return "";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CcaLogService] Error fetching run logs: {ex.Message}");
            return "";
        }
        finally
        {
            try { File.Delete(tempZip); } catch { }
        }
    }

    private static async Task<string> FetchPrDiffAsync(string ownerRepo, int prNumber, CancellationToken ct)
    {
        try
        {
            return await RunGhAsync($"pr diff {prNumber} --repo {ownerRepo}", ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CcaLogService] Error fetching PR diff: {ex.Message}");
            return "";
        }
    }

    private record PrData(string Body, string Title, List<string> Files);

    private static async Task<PrData> FetchPrDataAsync(string ownerRepo, int prNumber, CancellationToken ct)
    {
        try
        {
            var json = await RunGhAsync(
                $"pr view {prNumber} --repo {ownerRepo} --json body,title,files", ct);
            if (string.IsNullOrEmpty(json)) return new PrData("", "", new List<string>());

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var body = doc.RootElement.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var title = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var files = new List<string>();
            if (doc.RootElement.TryGetProperty("files", out var f))
            {
                foreach (var file in f.EnumerateArray())
                {
                    if (file.TryGetProperty("path", out var p))
                        files.Add(p.GetString() ?? "");
                }
            }
            return new PrData(body, title, files);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CcaLogService] Error fetching PR data: {ex.Message}");
            return new PrData("", "", new List<string>());
        }
    }

    private static async Task<string> FetchPrCommentsAsync(string ownerRepo, int prNumber, CancellationToken ct)
    {
        try
        {
            var json = await RunGhAsync(
                $"pr view {prNumber} --repo {ownerRepo} --json comments,reviews", ct);
            if (string.IsNullOrEmpty(json)) return "";

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var sb = new StringBuilder();

            if (doc.RootElement.TryGetProperty("comments", out var comments))
            {
                foreach (var comment in comments.EnumerateArray())
                {
                    var author = comment.TryGetProperty("author", out var a) &&
                                 a.TryGetProperty("login", out var l) ? l.GetString() : "unknown";
                    var body = comment.TryGetProperty("body", out var cb) ? cb.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(body))
                        sb.AppendLine($"**{author}**: {body}\n");
                }
            }

            if (doc.RootElement.TryGetProperty("reviews", out var reviews))
            {
                foreach (var review in reviews.EnumerateArray())
                {
                    var author = review.TryGetProperty("author", out var a) &&
                                 a.TryGetProperty("login", out var l) ? l.GetString() : "unknown";
                    var body = review.TryGetProperty("body", out var rb) ? rb.GetString() ?? "" : "";
                    var state = review.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(body))
                        sb.AppendLine($"**{author}** ({state}): {body}\n");
                }
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CcaLogService] Error fetching PR comments: {ex.Message}");
            return "";
        }
    }

    private static async Task<string> RunGhAsync(string arguments, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "gh",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        var stdout = await stdoutTask;
        await stderrTask;
        await process.WaitForExitAsync(ct);
        return process.ExitCode == 0 ? stdout : "";
    }
}

/// <summary>
/// Result of loading CCA context for a session.
/// </summary>
public class CcaContext
{
    public CcaRun Run { get; set; } = null!;
    public string Prompt { get; set; } = "";
    public string RawLogPath { get; set; } = "";
    public string? DiffPath { get; set; }
    public string? CommentsPath { get; set; }
    public int ParsedLogLength { get; set; }
    public int RawLogLength { get; set; }
}
