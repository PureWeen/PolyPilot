using System.Collections.Concurrent;
using System.Diagnostics;

namespace PolyPilot.Services;

public class PrLinkService
{
    private record CacheEntry(string? Url, DateTime ExpiresAt);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public async Task<string?> GetPrUrlForDirectoryAsync(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return null;

        if (_cache.TryGetValue(workingDirectory, out var entry) && DateTime.UtcNow < entry.ExpiresAt)
            return entry.Url;

        var url = await FetchPrUrlAsync(workingDirectory);
        _cache[workingDirectory] = new CacheEntry(url, DateTime.UtcNow + CacheTtl);
        return url;
    }

    /// <summary>Invalidates the cached result for a directory so the next call re-queries.</summary>
    public void Invalidate(string workingDirectory) => _cache.TryRemove(workingDirectory, out _);

    /// <summary>Returns the cached PR URL for a directory without fetching. Returns null if not cached or expired.</summary>
    public string? GetCachedPrUrl(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory)) return null;
        return _cache.TryGetValue(workingDirectory, out var entry) && DateTime.UtcNow < entry.ExpiresAt
            ? entry.Url : null;
    }

    private static async Task<string?> FetchPrUrlAsync(string workingDirectory)
    {
        Process? process = null;
        try
        {
            var branch = await RunGitAsync(workingDirectory, "rev-parse", "--abbrev-ref", "HEAD");
            if (string.IsNullOrEmpty(branch) || branch == "HEAD")
                return null;

            var psi = new ProcessStartInfo
            {
                FileName = "gh",
                ArgumentList = { "pr", "view", branch, "--json", "url", "--jq", ".url" },
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process = Process.Start(psi);
            if (process is null)
                return null;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var output = await process.StandardOutput.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            if (process.ExitCode != 0)
                return null;

            var url = output.Trim();
            return url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url : null;
        }
        catch
        {
            // gh not found, not a git repo, network error, timeout — all treated as "no PR"
            return null;
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited)
                    try { process.Kill(true); } catch { }
                process.Dispose();
            }
        }
    }

    private static async Task<string?> RunGitAsync(string workingDirectory, params string[] args)
    {
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            process = Process.Start(psi);
            if (process is null) return null;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var output = await process.StandardOutput.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch { return null; }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited)
                    try { process.Kill(true); } catch { }
                process.Dispose();
            }
        }
    }
}
