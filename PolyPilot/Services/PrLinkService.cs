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

    public static int? ExtractPrNumber(string? prUrl)
    {
        if (string.IsNullOrWhiteSpace(prUrl) ||
            !Uri.TryCreate(prUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var i = 0; i < segments.Length - 1; i++)
        {
            if ((segments[i].Equals("pull", StringComparison.OrdinalIgnoreCase) ||
                 segments[i].Equals("pulls", StringComparison.OrdinalIgnoreCase)) &&
                int.TryParse(segments[i + 1], out var number) &&
                number > 0)
            {
                return number;
            }
        }

        return null;
    }

    public async Task<string> GetPrDiffAsync(string workingDirectory, int prNumber, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            throw new ArgumentException("A working directory is required to load a PR diff.", nameof(workingDirectory));
        if (prNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(prNumber), "PR number must be greater than zero.");

        var (output, error, exitCode) = await RunGhAsync(
            workingDirectory,
            cancellationToken,
            "pr", "diff", prNumber.ToString(), "--color", "never");

        if (exitCode != 0)
        {
            var reason = string.IsNullOrWhiteSpace(error)
                ? $"Failed to load the diff for PR #{prNumber}."
                : error.Trim();
            throw new InvalidOperationException(reason);
        }

        var diff = output.Trim();
        if (string.IsNullOrWhiteSpace(diff))
            throw new InvalidOperationException($"PR #{prNumber} does not have any diff content to display.");

        return diff;
    }

    protected virtual Task<(string Output, string Error, int ExitCode)> RunGhAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] args) =>
        RunGhProcessAsync(workingDirectory, cancellationToken, args);

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

    private static async Task<(string Output, string Error, int ExitCode)> RunGhProcessAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] args)
    {
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "gh",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start the GitHub CLI process.");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);

            return (await outputTask, await errorTask, process.ExitCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out while loading the PR diff from GitHub.");
        }
        finally
        {
            ProcessHelper.SafeKillAndDispose(process);
        }
    }
}
