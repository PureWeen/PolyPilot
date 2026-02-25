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
        // Handle SCP-style SSH: git@github.com:Owner/Repo.git (no :// protocol prefix)
        if (url.Contains('@') && url.Contains(':') && !url.Contains("://"))
        {
            var path = url.Split(':').Last();
            var id = path.Replace('/', '-').TrimEnd('/');
            if (id.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                id = id[..^4];
            return id;
        }
        // Handle HTTPS, ssh://, and other protocol URLs
        var uri = new Uri(url);
        var result = uri.AbsolutePath.Trim('/').Replace('/', '-');
        if (result.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            result = result[..^4];
        return result;
    }

    /// <summary>
    /// Normalizes a repository input. Accepts full URLs, SSH paths, or GitHub shorthand (e.g. "dotnet/maui").
    /// </summary>
    public static string NormalizeRepoUrl(string input)
    {
        input = input.Trim();
        // Already a full URL or SSH path
        if (input.StartsWith("http://") || input.StartsWith("https://") || input.Contains("@"))
            return input;
        // GitHub shorthand: owner/repo (no dots, no colons, exactly one slash)
        var parts = input.Split('/');
        if (parts.Length == 2 && !input.Contains('.') && !input.Contains(':')
            && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
            return $"https://github.com/{input}";
        return input;
    }

    /// <summary>
    /// Clone a repository as bare. Returns the RepositoryInfo.
    /// If already tracked, returns existing entry.
    /// </summary>
    public Task<RepositoryInfo> AddRepositoryAsync(string url, CancellationToken ct = default)
        => AddRepositoryAsync(url, null, ct);

    public async Task<RepositoryInfo> AddRepositoryAsync(string url, Action<string>? onProgress, CancellationToken ct = default)
    {
        url = NormalizeRepoUrl(url);
        EnsureLoaded();
        var id = RepoIdFromUrl(url);
        var existing = _state.Repositories.FirstOrDefault(r => r.Id == id);
        if (existing != null)
        {
            onProgress?.Invoke($"Fetching {id}…");
            try { await RunGitAsync(existing.BareClonePath, ct, "config", "remote.origin.fetch",
                "+refs/heads/*:refs/remotes/origin/*"); } catch { }
            await RunGitWithProgressAsync(existing.BareClonePath, onProgress, ct, "fetch", "--progress", "origin");
            // Ensure long paths are enabled for existing repos on Windows
            if (OperatingSystem.IsWindows())
            {
                try { await RunGitAsync(existing.BareClonePath, ct, "config", "core.longpaths", "true"); } catch { }
            }
            return existing;
        }

        Directory.CreateDirectory(ReposDir);
        var barePath = Path.Combine(ReposDir, $"{id}.git");

        if (Directory.Exists(barePath))
        {
            // Directory exists but not tracked in state — re-use it via fetch
            onProgress?.Invoke($"Fetching {id}…");
            try { await RunGitAsync(barePath, ct, "config", "remote.origin.fetch",
                "+refs/heads/*:refs/remotes/origin/*"); } catch { }
            await RunGitWithProgressAsync(barePath, onProgress, ct, "fetch", "--progress", "origin");
        }
        else
        {
            onProgress?.Invoke($"Cloning {url}…");
            await RunGitWithProgressAsync(null, onProgress, ct, "clone", "--bare", "--progress", url, barePath);

            // Set fetch refspec so `git fetch` updates remote-tracking refs
            // (bare clones don't set this by default)
            await RunGitAsync(barePath, ct, "config", "remote.origin.fetch",
                "+refs/heads/*:refs/remotes/origin/*");
            onProgress?.Invoke($"Fetching refs…");
            await RunGitWithProgressAsync(barePath, onProgress, ct, "fetch", "--progress", "origin");
        }

        // Enable long paths on Windows (repos like dotnet/maui exceed MAX_PATH)
        if (OperatingSystem.IsWindows())
        {
            try { await RunGitAsync(barePath, ct, "config", "core.longpaths", "true"); } catch { }
        }

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
    public virtual async Task<WorktreeInfo> CreateWorktreeAsync(string repoId, string branchName, string? baseBranch = null, bool skipFetch = false, CancellationToken ct = default)
    {
        EnsureLoaded();
        var repo = _state.Repositories.FirstOrDefault(r => r.Id == repoId)
            ?? throw new InvalidOperationException($"Repository '{repoId}' not found.");

        // Fetch latest from origin (prune to clean up deleted remote branches)
        if (!skipFetch)
            await RunGitAsync(repo.BareClonePath, ct, "fetch", "--prune", "origin");

        // Determine base ref
        var baseRef = baseBranch ?? await GetDefaultBranch(repo.BareClonePath, ct);
        Console.WriteLine($"[RepoManager] Creating worktree from base ref: {baseRef}");

        Directory.CreateDirectory(WorktreesDir);
        var worktreeId = Guid.NewGuid().ToString()[..8];
        var worktreePath = Path.Combine(WorktreesDir, $"{repoId}-{worktreeId}");

        try
        {
            await RunGitAsync(repo.BareClonePath, ct, "worktree", "add", worktreePath, "-b", branchName, baseRef);
        }
        catch
        {
            // git worktree add can leave a partial directory on failure — clean up
            if (Directory.Exists(worktreePath))
                try { Directory.Delete(worktreePath, recursive: true); } catch { }
            throw;
        }

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
    /// Create a worktree by checking out a GitHub PR's branch.
    /// Fetches the PR ref, discovers the actual branch name via gh CLI,
    /// sets up upstream tracking, and associates the remote.
    /// </summary>
    public async Task<WorktreeInfo> CreateWorktreeFromPrAsync(string repoId, int prNumber, CancellationToken ct = default)
    {
        EnsureLoaded();
        var repo = _state.Repositories.FirstOrDefault(r => r.Id == repoId)
            ?? throw new InvalidOperationException($"Repository '{repoId}' not found.");

        // Try to discover the PR's actual head branch name via gh CLI
        string? headBranch = null;
        string remoteName = "origin";
        try
        {
            var prJson = await RunGhAsync(repo.BareClonePath, ct, "pr", "view", prNumber.ToString(), "--json", "headRefName,baseRefName,headRepository,headRepositoryOwner");
            var prInfo = System.Text.Json.JsonDocument.Parse(prJson);
            headBranch = prInfo.RootElement.GetProperty("headRefName").GetString();
            Console.WriteLine($"[RepoManager] PR #{prNumber} head branch: {headBranch}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RepoManager] Could not query PR info via gh: {ex.Message}");
        }

        // Fetch the PR ref into a local branch
        var branchName = headBranch ?? $"pr-{prNumber}";
        
        // Check if the branch is already checked out in another worktree
        if (headBranch != null)
        {
            try
            {
                var worktreeList = await RunGitAsync(repo.BareClonePath, ct, "worktree", "list", "--porcelain");
                var branchRef = $"branch refs/heads/{headBranch}";
                var lines = worktreeList.Split('\n');
                if (lines.Any(line => line.Trim() == branchRef))
                {
                    Console.WriteLine($"[RepoManager] Branch '{headBranch}' already in use, using pr-{prNumber} instead");
                    branchName = $"pr-{prNumber}";
                }
            }
            catch { /* Non-fatal — proceed with the branch name */ }
        }
        
        await RunGitAsync(repo.BareClonePath, ct, "fetch", remoteName, $"+pull/{prNumber}/head:{branchName}");

        // Fetch the remote branch so refs/remotes/origin/<branch> exists for tracking
        // The bare clone's refspec (+refs/heads/*:refs/remotes/origin/*) handles the mapping
        if (headBranch != null)
        {
            try
            {
                await RunGitAsync(repo.BareClonePath, ct, "fetch", remoteName, headBranch);
            }
            catch
            {
                // Non-fatal — the remote branch may not exist if PR is from a fork
            }
        }

        Directory.CreateDirectory(WorktreesDir);
        var worktreeId = Guid.NewGuid().ToString()[..8];
        var worktreePath = Path.Combine(WorktreesDir, $"{repoId}-{worktreeId}");

        await RunGitAsync(repo.BareClonePath, ct, "worktree", "add", worktreePath, branchName);

        // Set upstream tracking so push/pull work in the worktree
        if (headBranch != null)
        {
            try
            {
                await RunGitAsync(worktreePath, ct, "branch", $"--set-upstream-to={remoteName}/{headBranch}", branchName);
                Console.WriteLine($"[RepoManager] Set upstream tracking: {branchName} -> {remoteName}/{headBranch}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RepoManager] Could not set upstream tracking: {ex.Message}");
            }
        }

        var wt = new WorktreeInfo
        {
            Id = worktreeId,
            RepoId = repoId,
            Branch = branchName,
            Path = worktreePath,
            PrNumber = prNumber,
            Remote = remoteName,
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
                    try { Directory.Delete(wt.Path, recursive: true); } catch { }
                try { await RunGitAsync(repo.BareClonePath, ct, "worktree", "prune"); } catch { }
            }
        }
        else if (Directory.Exists(wt.Path))
        {
            // No repo found — just delete the directory
            try { Directory.Delete(wt.Path, recursive: true); } catch { }
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
    /// Add a worktree to the in-memory list (for remote mode — tracks server worktrees without running git).
    /// </summary>
    public void AddRemoteWorktree(WorktreeInfo wt)
    {
        EnsureLoaded();
        if (!_state.Worktrees.Any(w => w.Id == wt.Id))
            _state.Worktrees.Add(wt);
    }

    /// <summary>
    /// Add a repo to the in-memory list (for remote mode — tracks server repos without cloning).
    /// </summary>
    public void AddRemoteRepo(RepositoryInfo repo)
    {
        EnsureLoaded();
        if (!_state.Repositories.Any(r => r.Id == repo.Id))
            _state.Repositories.Add(repo);
    }

    /// <summary>
    /// Remove a worktree from the in-memory list (for remote mode — reconcile with server state).
    /// </summary>
    public void RemoveRemoteWorktree(string worktreeId)
    {
        EnsureLoaded();
        _state.Worktrees.RemoveAll(w => w.Id == worktreeId);
    }

    /// <summary>
    /// Remove a repo from the in-memory list (for remote mode — reconcile with server state).
    /// </summary>
    public void RemoveRemoteRepo(string repoId)
    {
        EnsureLoaded();
        _state.Repositories.RemoveAll(r => r.Id == repoId);
    }

    /// <summary>
    /// Remove a tracked repository and optionally delete its bare clone from disk.
    /// Also removes all associated worktrees.
    /// </summary>
    public async Task RemoveRepositoryAsync(string repoId, bool deleteFromDisk, CancellationToken ct = default)
    {
        EnsureLoaded();
        var repo = _state.Repositories.FirstOrDefault(r => r.Id == repoId);
        if (repo == null) return;

        // Remove all worktrees for this repo
        var worktrees = _state.Worktrees.Where(w => w.RepoId == repoId).ToList();
        foreach (var wt in worktrees)
        {
            try { await RemoveWorktreeAsync(wt.Id, ct); } catch { }
        }

        _state.Repositories.RemoveAll(r => r.Id == repoId);
        _state.Worktrees.RemoveAll(w => w.RepoId == repoId);
        Save();

        if (deleteFromDisk && Directory.Exists(repo.BareClonePath))
        {
            try { Directory.Delete(repo.BareClonePath, recursive: true); } catch { }
        }

        OnStateChanged?.Invoke();
    }

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
    public virtual async Task FetchAsync(string repoId, CancellationToken ct = default)
    {
        EnsureLoaded();
        var repo = _state.Repositories.FirstOrDefault(r => r.Id == repoId)
            ?? throw new InvalidOperationException($"Repository '{repoId}' not found.");
        await RunGitAsync(repo.BareClonePath, ct, "fetch", "--prune", "origin");
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
            // Get the default branch name (e.g. "main")
            var headRef = await RunGitAsync(barePath, ct, "symbolic-ref", "HEAD");
            var branchName = headRef.Trim().Replace("refs/heads/", "");

            // Always prefer origin's latest for the base ref (local refs may be stale in bare repos)
            try
            {
                var originRef = (await RunGitAsync(barePath, ct,
                    "rev-parse", "--verify", $"refs/remotes/origin/{branchName}")).Trim();
                if (!string.IsNullOrEmpty(originRef))
                {
                    Console.WriteLine($"[RepoManager] Using origin ref: refs/remotes/origin/{branchName} (SHA: {originRef[..7]})");
                    return $"refs/remotes/origin/{branchName}";
                }
            }
            catch (Exception ex)
            {
                // Origin ref doesn't exist or git command failed
                Console.WriteLine($"[RepoManager] Could not resolve refs/remotes/origin/{branchName}: {ex.Message}");
            }

            // Fallback to local ref (may be stale)
            Console.WriteLine($"[RepoManager] Falling back to local ref: refs/heads/{branchName}");
            return $"refs/heads/{branchName}";
        }
        catch
        {
            // If we can't determine the default branch, try origin/main as last resort
            Console.WriteLine("[RepoManager] Could not determine default branch, using origin/main");
            return "origin/main";
        }
    }

    private static async Task<string> RunGitAsync(string? workDir, CancellationToken ct, params string[] args)
    {
        return await RunGitWithProgressAsync(workDir, null, ct, args);
    }

    /// <summary>
    /// Run the GitHub CLI (gh) and return stdout. Uses the same PATH setup as git.
    /// Sets GIT_DIR for bare repos so gh can discover the remote.
    /// </summary>
    private static async Task<string> RunGhAsync(string? workDir, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("gh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (workDir != null)
        {
            psi.WorkingDirectory = workDir;
            // Bare repos need GIT_DIR set explicitly for gh to find the remote
            if (workDir.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                psi.Environment["GIT_DIR"] = workDir;
        }
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        SetPath(psi);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start gh process.");
        // Read both streams concurrently to avoid deadlock if one buffer fills
        var outputTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errorTask = proc.StandardError.ReadToEndAsync(ct);
        try
        {
            await Task.WhenAll(outputTask, errorTask);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        var output = await outputTask;
        var error = await errorTask;
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"gh failed (exit {proc.ExitCode}): {error}");
        return output;
    }

    private static void SetPath(ProcessStartInfo psi)
    {
        if (OperatingSystem.IsWindows())
        {
            var existing = Environment.GetEnvironmentVariable("PATH") ?? "";
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var gitPath = Path.Combine(programFiles, "Git", "cmd");
            if (!existing.Contains("Git", StringComparison.OrdinalIgnoreCase))
                psi.Environment["PATH"] = $"{gitPath};{existing}";
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            psi.Environment["PATH"] =
                $"/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin";
            psi.Environment["HOME"] = home;
        }
    }

    private static async Task<string> RunGitWithProgressAsync(string? workDir, Action<string>? onProgress, CancellationToken ct, params string[] args)
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

        // Ensure git is discoverable when launched from a GUI app (limited default PATH)
        SetPath(psi);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process.");

        var outputTask = proc.StandardOutput.ReadToEndAsync(ct);

        // Stream stderr for progress reporting
        var errorLines = new System.Text.StringBuilder();
        var stderrTask = Task.Run(async () =>
        {
            var buffer = new char[256];
            int read;
            var lineBuf = new System.Text.StringBuilder();
            while ((read = await proc.StandardError.ReadAsync(buffer, ct)) > 0)
            {
                errorLines.Append(buffer, 0, read);
                if (onProgress != null)
                {
                    lineBuf.Append(buffer, 0, read);
                    var text = lineBuf.ToString();
                    // Git progress uses \r for in-place updates
                    var lastNewline = Math.Max(text.LastIndexOf('\r'), text.LastIndexOf('\n'));
                    if (lastNewline >= 0)
                    {
                        var line = text[..lastNewline].Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                        if (!string.IsNullOrWhiteSpace(line))
                            onProgress(line.Trim());
                        lineBuf.Clear();
                        if (lastNewline + 1 < text.Length)
                            lineBuf.Append(text[(lastNewline + 1)..]);
                    }
                }
            }
        }, ct);

        var output = await outputTask;
        await stderrTask;
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {errorLines}");

        return output;
    }
}
