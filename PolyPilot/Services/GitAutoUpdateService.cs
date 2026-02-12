using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PolyPilot.Models;

namespace PolyPilot.Services;

public class GitAutoUpdateService : IDisposable
{
    private Timer? _timer;
    private string? _projectDir;
    private string? _gitRoot;
    private bool _isAvailable;
    private int _checking;
    private string _status = "";
    private string? _lastLocalCommit;
    private readonly ILogger<GitAutoUpdateService> _logger;
    private readonly SynchronizationContext? _syncCtx;

    public bool IsAvailable => _isAvailable;
    public bool IsEnabled { get; private set; }
    public string Status => _status;
    public string? Branch { get; private set; }

    public event Action? OnStateChanged;

    public GitAutoUpdateService(ILogger<GitAutoUpdateService> logger)
    {
        _logger = logger;
        _syncCtx = SynchronizationContext.Current;
        DetectSourceEnvironment();
    }

    public void Initialize()
    {
        if (!_isAvailable) return;
        var settings = ConnectionSettings.Load();
        if (settings.AutoUpdateFromMain)
            Start();
    }

    private void DetectSourceEnvironment()
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 20 && dir != null; i++)
            {
                dir = Path.GetDirectoryName(dir);
                if (dir == null) break;
                if (File.Exists(Path.Combine(dir, "PolyPilot.csproj")) &&
                    (File.Exists(Path.Combine(dir, "relaunch.sh")) ||
                     File.Exists(Path.Combine(dir, "relaunch.ps1"))))
                {
                    _projectDir = dir;
                    var gitRoot = Path.GetDirectoryName(dir);
                    if (gitRoot != null && Directory.Exists(Path.Combine(gitRoot, ".git")))
                    {
                        _gitRoot = gitRoot;
                        _isAvailable = true;
                        _logger.LogInformation("Auto-update available: project={Project}, git={Git}", _projectDir, _gitRoot);
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to detect source environment");
        }
    }

    public void Start()
    {
        if (!_isAvailable || IsEnabled) return;
        IsEnabled = true;
        _status = "Watching main...";
        _timer = new Timer(CheckForUpdates, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        _logger.LogInformation("Auto-update watcher started");
        NotifyChanged();
    }

    public void Stop()
    {
        if (!IsEnabled) return;
        IsEnabled = false;
        _timer?.Dispose();
        _timer = null;
        _status = "Stopped";
        _logger.LogInformation("Auto-update watcher stopped");
        NotifyChanged();
    }

    private async void CheckForUpdates(object? state)
    {
        if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0) return;
        try
        {
            var branch = (await RunGit("rev-parse --abbrev-ref HEAD")).Trim();
            Branch = branch;
            if (branch != "main")
            {
                _status = $"Not on main (on {branch})";
                NotifyChanged();
                return;
            }

            // Check for uncommitted changes
            var dirtyCheck = (await RunGit("status --porcelain")).Trim();
            if (!string.IsNullOrEmpty(dirtyCheck))
            {
                _status = "Working tree dirty — skipping";
                NotifyChanged();
                return;
            }

            await RunGit("fetch origin main --quiet");

            var local = (await RunGit("rev-parse HEAD")).Trim();
            var remote = (await RunGit("rev-parse origin/main")).Trim();

            if (local == remote)
            {
                _status = $"Up to date ({local[..7]})";
                if (_lastLocalCommit != local)
                {
                    _lastLocalCommit = local;
                    NotifyChanged();
                }
                return;
            }

            // Count commits behind
            var behindCount = (await RunGit("rev-list --count HEAD..origin/main")).Trim();
            _status = $"Updating ({behindCount} new commit{(behindCount == "1" ? "" : "s")})...";
            NotifyChanged();

            var pullResult = await RunGit("pull origin main --ff-only");
            _logger.LogInformation("Pulled updates: {Result}", pullResult.Trim());

            _status = "Rebuilding & relaunching...";
            NotifyChanged();

            await Relaunch();
        }
        catch (Exception ex)
        {
            _status = $"Error: {ex.Message}";
            _logger.LogError(ex, "Auto-update check failed");
            NotifyChanged();
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    private async Task<string> RunGit(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = _gitRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        SetPath(psi);

        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {args} failed: {error}");

        return output;
    }

    private Task Relaunch()
    {
        ProcessStartInfo psi;

        if (OperatingSystem.IsWindows())
        {
            // Windows: use PowerShell script or dotnet build + restart
            var ps1 = Path.Combine(_projectDir!, "relaunch.ps1");
            if (File.Exists(ps1))
            {
                psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-ExecutionPolicy Bypass -File \"{ps1}\"",
                    WorkingDirectory = _projectDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
            }
            else
            {
                // Fallback: rebuild and restart via dotnet run
                psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "build -f net10.0-windows10.0.19041.0",
                    WorkingDirectory = _projectDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
            }
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = "relaunch.sh",
                WorkingDirectory = _projectDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
        }

        SetPath(psi);
        Process.Start(psi);
        return Task.CompletedTask;
    }

    private static void SetPath(ProcessStartInfo psi)
    {
        if (OperatingSystem.IsWindows())
        {
            // On Windows, inherit the existing PATH — just ensure dotnet is available
            var existing = Environment.GetEnvironmentVariable("PATH") ?? "";
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var dotnetPath = Path.Combine(programFiles, "dotnet");
            if (!existing.Contains(dotnetPath, StringComparison.OrdinalIgnoreCase))
                psi.Environment["PATH"] = $"{dotnetPath};{existing}";
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            psi.Environment["PATH"] =
                $"{home}/.dotnet:/usr/local/share/dotnet:/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin";
            psi.Environment["HOME"] = home;
        }
    }

    private void NotifyChanged()
    {
        if (_syncCtx != null)
            _syncCtx.Post(_ => OnStateChanged?.Invoke(), null);
        else
            OnStateChanged?.Invoke();
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
