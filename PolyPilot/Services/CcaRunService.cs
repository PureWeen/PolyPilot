using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using PolyPilot.Models;

namespace PolyPilot.Services;

/// <summary>
/// Discovers CCA (Copilot Coding Agent) runs for tracked repositories
/// by querying the GitHub API via the gh CLI.
/// </summary>
public class CcaRunService
{
    private readonly ConcurrentDictionary<string, CachedCcaResult> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Extracts "owner/repo" from a git URL.
    /// Handles HTTPS (https://github.com/owner/repo.git) and SSH (git@github.com:owner/repo.git).
    /// </summary>
    private static readonly Regex SafeOwnerRepoRegex = new(@"^[a-zA-Z0-9_.-]+/[a-zA-Z0-9_.-]+$");

    public static string? ExtractOwnerRepo(string gitUrl)
    {
        try
        {
            gitUrl = gitUrl.Trim();
            string? candidate = null;
            // SSH: git@github.com:Owner/Repo.git
            if (gitUrl.Contains('@') && gitUrl.Contains(':'))
            {
                var path = gitUrl.Split(':').Last();
                path = path.TrimEnd('/').Replace(".git", "");
                var parts = path.Split('/');
                if (parts.Length == 2)
                    candidate = $"{parts[0]}/{parts[1]}";
            }
            // HTTPS: https://github.com/Owner/Repo.git
            if (candidate == null && (gitUrl.StartsWith("http://") || gitUrl.StartsWith("https://")))
            {
                var uri = new Uri(gitUrl);
                var segments = uri.AbsolutePath.Trim('/').Replace(".git", "").Split('/');
                if (segments.Length >= 2)
                    candidate = $"{segments[0]}/{segments[1]}";
            }
            // Shorthand: owner/repo
            if (candidate == null)
            {
                var shortParts = gitUrl.Split('/');
                if (shortParts.Length == 2 && !gitUrl.Contains('.') && !gitUrl.Contains(':'))
                    candidate = gitUrl;
            }
            // Validate against safe pattern to prevent argument injection
            if (candidate != null && SafeOwnerRepoRegex.IsMatch(candidate))
                return candidate;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Fetches CCA runs for a repository. Results are cached for 60 seconds.
    /// </summary>
    public async Task<List<CcaRun>> GetCcaRunsAsync(string ownerRepo, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && _cache.TryGetValue(ownerRepo, out var cached) && !cached.IsExpired)
            return cached.Runs;

        var runs = await FetchFromGitHubAsync(ownerRepo, ct);
        _cache[ownerRepo] = new CachedCcaResult(runs);
        return runs;
    }

    /// <summary>
    /// Invalidates the cache for a specific repo.
    /// </summary>
    public void InvalidateCache(string ownerRepo) => _cache.TryRemove(ownerRepo, out _);

    private static async Task<List<CcaRun>> FetchFromGitHubAsync(string ownerRepo, CancellationToken ct)
    {
        try
        {
            // Fetch CCA runs and PRs in parallel
            var runsTask = FetchActionsRunsAsync(ownerRepo, ct);
            var prsTask = FetchPullRequestsAsync(ownerRepo, ct);
            await Task.WhenAll(runsTask, prsTask);

            var runs = await runsTask;
            var prs = await prsTask;

            // Join: match run.HeadBranch to PR headRefName
            // Use TryAdd to handle duplicate branch names (e.g. from forks)
            var prByBranch = new Dictionary<string, PrInfo>();
            foreach (var pr in prs)
                prByBranch.TryAdd(pr.Branch, pr);
            foreach (var run in runs)
            {
                if (prByBranch.TryGetValue(run.HeadBranch, out var pr))
                {
                    run.PrNumber = pr.Number;
                    run.PrState = pr.State;
                    run.PrUrl = pr.Url;
                    run.PrTitle = pr.Title;
                }
            }
            return runs;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CcaRunService] Error fetching CCA runs for {ownerRepo}: {ex.Message}");
            return new List<CcaRun>();
        }
    }

    private static async Task<List<CcaRun>> FetchActionsRunsAsync(string ownerRepo, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "gh",
            Arguments = $"api /repos/{ownerRepo}/actions/runs?actor=copilot-swe-agent%5Bbot%5D&per_page=30 --jq \".workflow_runs\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.Start();

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            Console.WriteLine($"[CcaRunService] gh api failed for {ownerRepo}: {stderr}");
            return new List<CcaRun>();
        }

        if (string.IsNullOrWhiteSpace(stdout))
            return new List<CcaRun>();

        var runs = new List<CcaRun>();
        using var doc = JsonDocument.Parse(stdout);
        foreach (var elem in doc.RootElement.EnumerateArray())
        {
            var run = new CcaRun
            {
                Id = elem.GetProperty("id").GetInt64(),
                Name = elem.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
                Event = elem.TryGetProperty("event", out var ev) ? ev.GetString() ?? "" : "",
                DisplayTitle = elem.TryGetProperty("display_title", out var dt) ? dt.GetString() ?? "" : "",
                HeadBranch = elem.TryGetProperty("head_branch", out var hb) ? hb.GetString() ?? "" : "",
                Status = elem.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "",
                Conclusion = elem.TryGetProperty("conclusion", out var cn) ? cn.GetString() : null,
                CreatedAt = elem.TryGetProperty("created_at", out var ca) ? ca.GetDateTime() : DateTime.MinValue,
                UpdatedAt = elem.TryGetProperty("updated_at", out var ua) ? ua.GetDateTime() : DateTime.MinValue,
                HtmlUrl = elem.TryGetProperty("html_url", out var hu) ? hu.GetString() ?? "" : "",
            };
            if (run.IsCodingAgent)
                runs.Add(run);
        }
        return runs;
    }

    private record PrInfo(int Number, string Branch, string State, string Url, string Title);

    private static async Task<List<PrInfo>> FetchPullRequestsAsync(string ownerRepo, CancellationToken ct)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = $"pr list --repo {ownerRepo} --state all --limit 30 --json number,title,headRefName,state,url",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.Start();

            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                return new List<PrInfo>();

            var prs = new List<PrInfo>();
            using var doc = JsonDocument.Parse(stdout);
            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                var branch = elem.TryGetProperty("headRefName", out var hb) ? hb.GetString() ?? "" : "";
                var state = elem.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "";
                // gh CLI returns OPEN, MERGED, CLOSED — normalize to lowercase
                state = state.ToLowerInvariant();
                prs.Add(new PrInfo(
                    Number: elem.TryGetProperty("number", out var n) ? n.GetInt32() : 0,
                    Branch: branch,
                    State: state,
                    Url: elem.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                    Title: elem.TryGetProperty("title", out var t) ? t.GetString() ?? "" : ""
                ));
            }
            return prs;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CcaRunService] Error fetching PRs for {ownerRepo}: {ex.Message}");
            return new List<PrInfo>();
        }
    }

    private class CachedCcaResult
    {
        public List<CcaRun> Runs { get; }
        private readonly DateTime _fetchedAt;
        public bool IsExpired => DateTime.UtcNow - _fetchedAt > CacheTtl;
        public CachedCcaResult(List<CcaRun> runs) { Runs = runs; _fetchedAt = DateTime.UtcNow; }
    }
}
