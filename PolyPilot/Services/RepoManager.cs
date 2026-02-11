using System.Diagnostics;
using System.Text.Json;
using PolyPilot.Models;

namespace PolyPilot.Services;

/// <summary>
/// Manages bare git clones and worktrees for repository-centric sessions.
/// Repos live at ~/.polypilot/repos/<id>.git, worktrees at ~/.polypilot/worktrees/<id>/.
/// </summary>
public class RepoManager
{
    private static string? _reposDir;
    private static string ReposDir => _reposDir ??= GetReposDir();
    private static string? _worktreesDir;
    private static string WorktreesDir => _worktreesDir ??= GetWorktreesDir();
    private static string? _stateFile;
    private static string StateFile => _stateFile ??= GetStateFile();

    private RepositoryState _state = new();
    private bool _loaded;
    public IReadOnlyList<RepositoryInfo> Repositories { get { EnsureLoaded(); return _state.Repositories.AsReadOnly(); } }
    public IReadOnlyList<WorktreeInfo> Worktrees { get { EnsureLoaded(); return _state.Worktrees.AsReadOnly(); } }

    public event Action? OnStateChanged;

    private void EnsureLoaded()
    {
        if (!_loaded) Load();
    }

    private static string GetBaseDir()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
                home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(home, ".polypilot");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), ".polypilot");
        }
    }

    private static string GetReposDir() => Path.Combine(GetBaseDir(), "repos");
    private static string GetWorktreesDir() => Path.Combine(GetBaseDir(), "worktrees");
    private static string GetStateFile() => Path.Combine(GetBaseDir(), "repos.json");

    public void Load()
    {
        _loaded = true;
        try
        {
            if (File.Exists(StateFile))
            {
                var json = File.ReadAllText(StateFile);
                _state = JsonSerializer.Deserialize<RepositoryState>(json) ?? new RepositoryState();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RepoManager] Failed to load state: {ex.Message}");
            _state = new RepositoryState();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StateFile, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RepoManager] Failed to save state: {ex.Message}");
        }
    }

    /// <summary>
    /// Generates a repo ID from a git URL (e.g. "https://github.com/PureWeen/PolyPilot" → "PureWeen-PolyPilot").
    /// </summary>
    public static string RepoIdFromUrl(string url)
    {
        // Handle SSH: git@github.com:Owner/Repo.git
        if (url.Contains(':') && url.Contains('@'))
        {
            var path = url.Split(':').Last();
            return path.Replace('/', '-').TrimEnd('/').Replace(".git", "");
        }
        // Handle HTTPS: https://github.com/Owner/Repo.git
        var uri = new Uri(url);
        return uri.AbsolutePath.Trim('/').Replace('/', '-').Replace(".git", "");
    }

    /// <summary>
    /// Clone a repository as bare. Returns the RepositoryInfo.
    /// If already tracked, returns existing entry.
    /// </summary>
    public async Task<RepositoryInfo> AddRepositoryAsync(string url, CancellationToken ct = default)
    {
        EnsureLoaded();
        var id = RepoIdFromUrl(url);
        var existing = _state.Repositories.FirstOrDefault(r => r.Id == id);
        if (existing != null)
        {
            // Fetch latest
            await RunGitAsync(existing.BareClonePath, ct, "fetch", "--all", "--prune");
            return existing;
        }

        Directory.CreateDirectory(ReposDir);
        var barePath = Path.Combine(ReposDir, $"{id}.git");

        await RunGitAsync(null, ct, "clone", "--bare", url, barePath);

        var repo = new RepositoryInfo
        {
            Id = id,
            Name = id.Contains('-') ? id.Split('-').Last() : id,
            Url = url,
            BareClonePath = barePath,
            AddedAt = DateTime.UtcNow
        };
        _state.Repositories.Add(repo);
        Save();
        OnStateChanged?.Invoke();
        return repo;
    }

    /// <summary>
    /// Add a repository from an existing local path (non-bare). Creates a bare clone.
    /// </summary>
    public async Task<RepositoryInfo> AddRepositoryFromLocalAsync(string localPath, CancellationToken ct = default)
    {
        // Get remote URL
        var remoteUrl = (await RunGitAsync(localPath, ct, "remote", "get-url", "origin")).Trim();
        if (string.IsNullOrEmpty(remoteUrl))
            throw new InvalidOperationException($"No 'origin' remote found in {localPath}");

        return await AddRepositoryAsync(remoteUrl, ct);
    }

    /// <summary>
    /// Create a new worktree for a repository on a new branch from origin/main.
    /// </summary>
    public async Task<WorktreeInfo> CreateWorktreeAsync(string repoId, string branchName, string? baseBranch = null, CancellationToken ct = default)
    {
        EnsureLoaded();
        var repo = _state.Repositories.FirstOrDefault(r => r.Id == repoId)
            ?? throw new InvalidOperationException($"Repository '{repoId}' not found.");

        // Fetch latest
        await RunGitAsync(repo.BareClonePath, ct, "fetch", "--all", "--prune");

        // Determine base ref
        var baseRef = baseBranch ?? await GetDefaultBranch(repo.BareClonePath, ct);

        Directory.CreateDirectory(WorktreesDir);
        var worktreeId = Guid.NewGuid().ToString()[..8];
        var worktreePath = Path.Combine(WorktreesDir, $"{repoId}-{worktreeId}");

        await RunGitAsync(repo.BareClonePath, ct, "worktree", "add", worktreePath, "-b", branchName, baseRef);

        var wt = new WorktreeInfo
        {
            Id = worktreeId,
            RepoId = repoId,
            Branch = branchName,
            Path = worktreePath,
            CreatedAt = DateTime.UtcNow
        };
        _state.Worktrees.Add(wt);
        Save();
        OnStateChanged?.Invoke();
        return wt;
    }

    /// <summary>
    /// Remove a worktree and clean up.
    /// </summary>
    public async Task RemoveWorktreeAsync(string worktreeId, CancellationToken ct = default)
    {
        EnsureLoaded();
        var wt = _state.Worktrees.FirstOrDefault(w => w.Id == worktreeId);
        if (wt == null) return;

        var repo = _state.Repositories.FirstOrDefault(r => r.Id == wt.RepoId);
        if (repo != null)
        {
            try
            {
                await RunGitAsync(repo.BareClonePath, ct, "worktree", "remove", wt.Path, "--force");
            }
            catch
            {
                // Force cleanup if git worktree remove fails
                if (Directory.Exists(wt.Path))
                    Directory.Delete(wt.Path, recursive: true);
                await RunGitAsync(repo.BareClonePath, ct, "worktree", "prune");
            }
        }

        _state.Worktrees.RemoveAll(w => w.Id == worktreeId);
        Save();
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// List worktrees for a specific repository.
    /// </summary>
    public IEnumerable<WorktreeInfo> GetWorktrees(string repoId)
        => _state.Worktrees.Where(w => w.RepoId == repoId);

    /// <summary>
    /// Find which repository a session's working directory belongs to, if any.
    /// </summary>
    public RepositoryInfo? FindRepoForPath(string workingDirectory)
    {
        var wt = _state.Worktrees.FirstOrDefault(w =>
            workingDirectory.StartsWith(w.Path, StringComparison.OrdinalIgnoreCase));
        if (wt != null)
            return _state.Repositories.FirstOrDefault(r => r.Id == wt.RepoId);
        return null;
    }

    /// <summary>
    /// Associate a session name with a worktree.
    /// </summary>
    public void LinkSessionToWorktree(string worktreeId, string sessionName)
    {
        var wt = _state.Worktrees.FirstOrDefault(w => w.Id == worktreeId);
        if (wt != null)
        {
            wt.SessionName = sessionName;
            Save();
        }
    }

    /// <summary>
    /// Fetch latest from remote for a repository.
    /// </summary>
    public async Task FetchAsync(string repoId, CancellationToken ct = default)
    {
        EnsureLoaded();
        var repo = _state.Repositories.FirstOrDefault(r => r.Id == repoId)
            ?? throw new InvalidOperationException($"Repository '{repoId}' not found.");
        await RunGitAsync(repo.BareClonePath, ct, "fetch", "--all", "--prune");
    }

    /// <summary>
    /// Get branches for a repository.
    /// </summary>
    public async Task<List<string>> GetBranchesAsync(string repoId, CancellationToken ct = default)
    {
        EnsureLoaded();
        var repo = _state.Repositories.FirstOrDefault(r => r.Id == repoId)
            ?? throw new InvalidOperationException($"Repository '{repoId}' not found.");
        var output = await RunGitAsync(repo.BareClonePath, ct, "branch", "--list");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(b => b.TrimStart('*').Trim())
            .Where(b => !string.IsNullOrEmpty(b))
            .ToList();
    }

    private async Task<string> GetDefaultBranch(string barePath, CancellationToken ct)
    {
        try
        {
            // In bare repos, HEAD points directly to the default branch
            var output = await RunGitAsync(barePath, ct, "symbolic-ref", "HEAD");
            return output.Trim(); // e.g. refs/heads/main
        }
        catch
        {
            return "main";
        }
    }

    private static async Task<string> RunGitAsync(string? workDir, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (workDir != null)
            psi.WorkingDirectory = workDir;
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process.");

        var output = await proc.StandardOutput.ReadToEndAsync(ct);
        var error = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");

        return output;
    }
}
