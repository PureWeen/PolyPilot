using System.Diagnostics;

namespace PolyPilot.Services;

/// <summary>
/// Diagnostics and utilities: dotfiles configuration checking, GitHub CLI helpers, command execution.
/// </summary>
public partial class CodespaceService
{
    /// <summary>
    /// Result of checking the user's dotfiles configuration for codespaces.
    /// </summary>
    public record DotfilesStatus(bool IsConfigured, string? Repository, bool HasSshdInstall);

    /// <summary>
    /// Checks whether the user has dotfiles configured for Codespaces and whether
    /// the install script includes SSHD setup.
    /// </summary>
    public async Task<DotfilesStatus> CheckDotfilesConfiguredAsync()
    {
        try
        {
            // Check if the user has a dotfiles repo via gh config
            var dotfilesRepo = await RunGhCommandAsync(10, "api", "user", "--jq", ".login");
            if (string.IsNullOrEmpty(dotfilesRepo))
                return new DotfilesStatus(false, null, false);

            var login = dotfilesRepo.Trim();
            // Check if a dotfiles repo exists for this user
            var repoCheck = await RunGhCommandAsync(10, "api", $"repos/{login}/dotfiles", "--jq", ".full_name");
            if (string.IsNullOrEmpty(repoCheck))
                return new DotfilesStatus(false, null, false);

            var repoName = repoCheck.Trim();

            // Check if the install script mentions sshd/openssh-server
            var installScript = await RunGhCommandAsync(10, "api",
                $"repos/{repoName}/contents/install.sh",
                "--jq", ".content");

            bool hasSshd = false;
            if (!string.IsNullOrEmpty(installScript))
            {
                try
                {
                    var decoded = System.Text.Encoding.UTF8.GetString(
                        Convert.FromBase64String(installScript.Trim().Replace("\n", "")));
                    hasSshd = decoded.Contains("openssh-server", StringComparison.OrdinalIgnoreCase)
                           || decoded.Contains("sshd", StringComparison.OrdinalIgnoreCase);
                }
                catch { }
            }

            return new DotfilesStatus(true, repoName, hasSshd);
        }
        catch
        {
            return new DotfilesStatus(false, null, false);
        }
    }

    /// <summary>
    /// Gets the local GitHub auth token from <c>gh auth token</c>.
    /// Used to inject authentication into codespace SSH sessions where gh is not logged in.
    /// </summary>
    private async Task<string?> GetLocalGhTokenAsync()
    {
        try
        {
            var result = await RunGhCommandAsync(5, "auth", "token");
            return result?.Trim();
        }
        catch
        {
            return null;
        }
    }

    internal static string FindGhPath()
    {
        foreach (var candidate in new[] { "/opt/homebrew/bin/gh", "/usr/local/bin/gh", "gh" })
        {
            if (candidate == "gh" || File.Exists(candidate))
                return candidate;
        }
        return "gh";
    }

    private Task<string?> RunGhCommandAsync(params string[] args) =>
        RunGhCommandAsync(10, args);

    private async Task<string?> RunGhCommandAsync(int timeoutSeconds, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = FindGhPath(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process == null)
                return null;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                // Read both streams concurrently to prevent deadlock when stderr
                // buffer fills (blocks process from closing stdout).
                var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
                var output = await outputTask;
                var stderr = await stderrTask;
                await process.WaitForExitAsync(cts.Token);
                if (process.ExitCode != 0)
                {
                    if (!string.IsNullOrWhiteSpace(stderr))
                        Console.WriteLine($"[CodespaceService] gh {string.Join(" ", args)} failed (exit {process.ExitCode}): {stderr.Trim()}");
                    return null;
                }
                return output;
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CodespaceService] gh command failed: {ex.Message}");
            return null;
        }
    }
}