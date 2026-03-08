using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PolyPilot.Services;

/// <summary>
/// Codespace lifecycle management: listing, state queries, starting, and copilot headless setup.
/// </summary>
public partial class CodespaceService
{
    public record CodespaceInfo(
        string Name,
        string Repository,
        string State  // "Available", "Creating", "Starting", "Stopping", "Stopped", "Deleting"
    );

    /// <summary>
    /// List all codespaces for the authenticated user (no SSH probing — fast).
    /// </summary>
    public async Task<List<CodespaceInfo>> ListCodespacesAsync()
    {
        try
        {
            var output = await RunGhCommandAsync("cs", "list", "--json", "name,state,repository");
            if (string.IsNullOrEmpty(output))
                return new();

            var codespaces = JsonSerializer.Deserialize<List<GhCodespace>>(output) ?? new();
            return codespaces
                .Select(cs => new CodespaceInfo(cs.Name, cs.Repository, cs.State))
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CodespaceService] Failed to list codespaces: {ex.Message}");
            return new();
        }
    }

    /// <summary>
    /// Gets the current state of a single codespace ("Available", "Stopped", etc.).
    /// Returns null if the codespace is not found or gh fails.
    /// </summary>
    public async Task<string?> GetCodespaceStateAsync(string codespaceName)
    {
        try
        {
            // Sanitize codespace name to prevent jq injection (escape backslashes and quotes)
            var sanitized = codespaceName.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var output = await RunGhCommandAsync(10, "cs", "list", "--json", "name,state", "-q",
                $".[] | select(.name == \"{sanitized}\") | .state");
            // gh with -q (jq filter) returns just the state string
            if (!string.IsNullOrWhiteSpace(output))
                return output.Trim().Trim('"');
            // Fallback: parse full JSON
            var fullOutput = await RunGhCommandAsync(10, "cs", "list", "--json", "name,state");
            if (string.IsNullOrEmpty(fullOutput)) return null;
            var codespaces = JsonSerializer.Deserialize<List<GhCodespace>>(fullOutput);
            return codespaces?.FirstOrDefault(cs => cs.Name == codespaceName)?.State;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Starts a stopped/shutdown codespace via the GitHub API and polls until it reaches "Available".
    /// </summary>
    /// <returns><c>true</c> if the codespace is now Available; <c>false</c> on timeout or error.</returns>
    public async Task<bool> StartCodespaceAsync(string codespaceName, int timeoutSeconds = 120, CancellationToken cancellationToken = default)
    {
        try
        {
            // Trigger start via the REST API (gh cs has no 'start' subcommand)
            var startResult = await RunGhCommandAsync(30, "api", "-X", "POST",
                $"/user/codespaces/{codespaceName}/start", "--jq", ".state");
            Console.WriteLine($"[CodespaceService] Start API response for '{codespaceName}': {startResult?.Trim()}");

            // Poll until Available or timeout
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(3000, cancellationToken);

                var state = await GetCodespaceStateAsync(codespaceName);
                if (state == "Available")
                    return true;
                if (state is "Shutdown" or "Stopped" or "Deleting" or "Deleted")
                {
                    Console.WriteLine($"[CodespaceService] Codespace '{codespaceName}' is {state} — start may have failed");
                    return false;
                }
                // "Starting", "Queued", etc. — keep polling
            }
            Console.WriteLine($"[CodespaceService] Timeout waiting for codespace '{codespaceName}' to become Available");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CodespaceService] Failed to start codespace '{codespaceName}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Starts copilot --headless in the codespace via SSH.
    /// Checks multiple common paths for the copilot binary and installs via npm if not found.
    /// </summary>
    /// <returns>
    /// <c>true</c> if copilot was successfully started;
    /// <c>false</c> if SSH is not available in the container (devcontainer has no sshd).
    /// </returns>
    public async Task<bool> StartCopilotHeadlessAsync(string codespaceName, int remotePort = 4321, int sshTimeoutSeconds = 30)
    {
        // Verify SSH is available before attempting anything else.
        // Some devcontainer images don't include sshd — gh cs ssh fails in those cases.
        // Use a generous timeout: first SSH to a waking codespace can take 30-60s.
        var sshCheck = await RunGhCommandAsync(sshTimeoutSeconds, "cs", "ssh", "-c", codespaceName, "--", "echo", "ok");
        if (sshCheck == null)
            return false;

        // Resolve the copilot binary path AND authentication tokens using a login shell.
        // bash -l is only used here for discovery — nvm and env vars are only in PATH with a login shell.
        // We do NOT use bash -l for the start command because some codespace .bash_profiles run `nohup`
        // without arguments (e.g. for nohup alias expansion), which corrupts our nohup redirect.
        // We capture GITHUB_TOKEN and GITHUB_CODESPACE_TOKEN — the copilot binary needs both for auth.
        var findCmd = "env | grep -E '^GITHUB_TOKEN=|^GITHUB_CODESPACE_TOKEN='; " +
                      "command -v copilot 2>/dev/null || " +
                      "test -x /usr/local/share/nvm/current/bin/copilot && echo /usr/local/share/nvm/current/bin/copilot || " +
                      "test -x /usr/local/bin/copilot && echo /usr/local/bin/copilot || " +
                      "test -x /home/codespace/.local/bin/copilot && echo /home/codespace/.local/bin/copilot || " +
                      "test -x /home/vscode/.local/bin/copilot && echo /home/vscode/.local/bin/copilot || " +
                      "echo ''";
        var discoveryOutput = await RunGhCommandAsync(10, "cs", "ssh", "-c", codespaceName, "--", "bash", "-l", "-c", findCmd);
        
        // Parse the tokens and binary path from discovery output
        var envExports = new List<string>();
        var copilotBin = "";
        if (discoveryOutput != null)
        {
            foreach (var line in discoveryOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("GITHUB_TOKEN=") || trimmed.StartsWith("GITHUB_CODESPACE_TOKEN="))
                {
                    var eqIdx = trimmed.IndexOf('=');
                    var key = trimmed[..eqIdx];
                    var val = trimmed[(eqIdx + 1)..].Replace("'", "'\\''");
                    envExports.Add($"export {key}='{val}'");
                }
                else if (string.IsNullOrEmpty(copilotBin) && (trimmed.StartsWith("/") || trimmed == "copilot"))
                    copilotBin = trimmed;
            }
        }
        if (string.IsNullOrWhiteSpace(copilotBin))
            copilotBin = "/usr/local/share/nvm/current/bin/copilot"; // best-effort fallback

        // Kill any existing headless copilot on this port before starting a fresh one.
        // Uses fuser (more reliable than pkill for port-bound processes).
        var killCmd = $"fuser -k {remotePort}/tcp 2>/dev/null; sleep 2; echo KILLED";
        await RunGhCommandAsync(10, "cs", "ssh", "-c", codespaceName, "--", "bash", "--norc", "--noprofile", "-c", killCmd);

        // Write a launch script that exports auth tokens and starts copilot.
        // We use a script file instead of inline env vars because nohup in some codespace
        // shells doesn't properly inherit exported variables. The script uses exec so the
        // copilot process replaces the bash wrapper (clean process tree).
        var scriptLines = new List<string> { "#!/bin/bash" };
        foreach (var kv in envExports)
            scriptLines.Add(kv);
        scriptLines.Add($"exec {copilotBin} --headless --port {remotePort}");
        var scriptContent = string.Join("\n", scriptLines);

        // Write script via heredoc — avoids escaping issues with special chars in tokens.
        // Use a randomized delimiter to prevent injection if scriptContent ever contains the marker.
        var nonce = Guid.NewGuid().ToString("N")[..8];
        var delimiter = $"POLYPILOT_EOF_{nonce}";
        var writeCmd = $"cat > /tmp/polypilot-launch.sh << '{delimiter}'\n{scriptContent}\n{delimiter}\nchmod +x /tmp/polypilot-launch.sh";
        await RunGhCommandAsync(10, "cs", "ssh", "-c", codespaceName, "--", "bash", "--norc", "--noprofile", "-c", writeCmd);

        // Launch the script in background. Use bash + disown instead of nohup (which fails
        // in some codespace shells due to alias/profile interference).
        var startCmd = $"bash /tmp/polypilot-launch.sh </dev/null > /tmp/polypilot-copilot.log 2>&1 & disown $!; " +
                       $"sleep 3; " +
                       $"ss -tlnp 2>/dev/null | grep -q {remotePort} && echo STARTED || echo FAILED";
        var result = await RunGhCommandAsync(15, "cs", "ssh", "-c", codespaceName, "--", "bash", "--norc", "--noprofile", "-c", startCmd);

        if (result != null && result.Contains("STARTED"))
        {
            Console.WriteLine($"[CodespaceService] Copilot headless started successfully in '{codespaceName}'");
            return true;
        }

        Console.WriteLine($"[CodespaceService] Copilot may have failed to start in '{codespaceName}': {result?.Trim()}");
        return true; // Still return true (SSH worked) — let the tunnel probe determine if it's listening
    }

    private class GhCodespace
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("repository")]
        public string Repository { get; set; } = string.Empty;

        [JsonPropertyName("state")]
        public string State { get; set; } = "Unknown";
    }
}