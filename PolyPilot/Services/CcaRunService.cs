using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
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
    public static string? ExtractOwnerRepo(string gitUrl)
    {
        try
        {
            gitUrl = gitUrl.Trim();
            // SSH: git@github.com:Owner/Repo.git
            if (gitUrl.Contains('@') && gitUrl.Contains(':'))
            {
                var path = gitUrl.Split(':').Last();
                path = path.TrimEnd('/').Replace(".git", "");
                var parts = path.Split('/');
                if (parts.Length == 2)
                    return $"{parts[0]}/{parts[1]}";
            }
            // HTTPS: https://github.com/Owner/Repo.git
            if (gitUrl.StartsWith("http://") || gitUrl.StartsWith("https://"))
            {
                var uri = new Uri(gitUrl);
                var segments = uri.AbsolutePath.Trim('/').Replace(".git", "").Split('/');
                if (segments.Length >= 2)
                    return $"{segments[0]}/{segments[1]}";
            }
            // Shorthand: owner/repo
            var shortParts = gitUrl.Split('/');
            if (shortParts.Length == 2 && !gitUrl.Contains('.') && !gitUrl.Contains(':'))
                return gitUrl;
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
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = $"api /repos/{ownerRepo}/actions/runs?actor=copilot-swe-agent%5Bbot%5D&per_page=10 --jq \".workflow_runs\"",
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
                runs.Add(new CcaRun
                {
                    Id = elem.GetProperty("id").GetInt64(),
                    DisplayTitle = elem.TryGetProperty("display_title", out var dt) ? dt.GetString() ?? "" : "",
                    HeadBranch = elem.TryGetProperty("head_branch", out var hb) ? hb.GetString() ?? "" : "",
                    Status = elem.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "",
                    Conclusion = elem.TryGetProperty("conclusion", out var cn) ? cn.GetString() : null,
                    CreatedAt = elem.TryGetProperty("created_at", out var ca) ? ca.GetDateTime() : DateTime.MinValue,
                    UpdatedAt = elem.TryGetProperty("updated_at", out var ua) ? ua.GetDateTime() : DateTime.MinValue,
                    HtmlUrl = elem.TryGetProperty("html_url", out var hu) ? hu.GetString() ?? "" : "",
                });
            }
            return runs;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CcaRunService] Error fetching CCA runs for {ownerRepo}: {ex.Message}");
            return new List<CcaRun>();
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
