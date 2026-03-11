using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PolyPilot.Models;

namespace PolyPilot.Services;

public enum TunnelState
{
    NotStarted,
    Authenticating,
    Starting,
    Running,
    Stopping,
    Error
}

public partial class DevTunnelService : IDisposable
{
    private readonly WsBridgeServer _bridge;
    private readonly CopilotService _copilot;
    private readonly RepoManager _repoManager;
    private readonly AuditLogService? _auditLog;
    private Process? _hostProcess;
    private string? _tunnelUrl;
    private string? _tunnelId;
    private string? _accessToken;
    private TunnelState _state = TunnelState.NotStarted;
    private string? _errorMessage;

    public const int BridgePort = 4322;

    public DevTunnelService(WsBridgeServer bridge, CopilotService copilot, RepoManager repoManager, AuditLogService? auditLog = null)
    {
        _bridge = bridge;
        _copilot = copilot;
        _repoManager = repoManager;
        _auditLog = auditLog;
    }

    public TunnelState State => _state;
    public string? TunnelUrl => _tunnelUrl;
    public string? TunnelId => _tunnelId;
    public string? AccessToken => _accessToken;
    public string? ErrorMessage => _errorMessage;

    public event Action? OnStateChanged;

    private static string ResolveDevTunnel()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.AddRange(new[]
            {
                Path.Combine(localAppData, "Microsoft", "DevTunnels", "devtunnel.exe"),
                Path.Combine(home, ".devtunnels", "bin", "devtunnel.exe"),
                Path.Combine(home, "bin", "devtunnel.exe"),
            });
        }
        else
        {
            candidates.AddRange(new[]
            {
                Path.Combine(home, "bin", "devtunnel"),
                Path.Combine(home, ".local", "bin", "devtunnel"),
                "/usr/local/bin/devtunnel",
                "/opt/homebrew/bin/devtunnel",
            });
        }

        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        // Fallback: rely on PATH
        return OperatingSystem.IsWindows() ? "devtunnel.exe" : "devtunnel";
    }

    [GeneratedRegex(@"(https?://\S+\.devtunnels\.ms\S*)", RegexOptions.IgnoreCase)]
    private static partial Regex TunnelUrlRegex();

    [GeneratedRegex(@"Connect via browser:\s*(https?://\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectUrlRegex();

    [GeneratedRegex(@"Tunnel\s+ID\s*:\s*(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex TunnelIdRegex();

    // Also match "... for tunnel: <id>" or "... for tunnel <id>" pattern
    [GeneratedRegex(@"for tunnel:?\s+(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex TunnelIdAltRegex();

    /// <summary>
    /// Check if devtunnel CLI is available
    /// </summary>
    public static bool IsCliAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ResolveDevTunnel(),
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Check if user is logged in to devtunnel
    /// </summary>
    public async Task<bool> IsLoggedInAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ResolveDevTunnel(),
                Arguments = "user show",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return p.ExitCode == 0
                && !output.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
                && !output.Contains("token expired", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Login via GitHub auth (interactive — opens browser)
    /// </summary>
    public async Task<bool> LoginAsync()
    {
        SetState(TunnelState.Authenticating);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ResolveDevTunnel(),
                Arguments = "user login -g",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null)
            {
                SetError("Failed to start devtunnel login");
                return false;
            }
            await p.WaitForExitAsync();
            if (p.ExitCode == 0)
            {
                SetState(TunnelState.NotStarted);
                return true;
            }
            var err = await p.StandardError.ReadToEndAsync();
            SetError($"Login failed: {err}");
            return false;
        }
        catch (Exception ex)
        {
            SetError($"Login error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Host a tunnel on the given port (long-running process).
    /// Starts a WebSocket bridge on BridgePort that proxies to the copilot TCP port,
    /// then tunnels the bridge port via DevTunnel.
    /// </summary>
    public async Task<bool> HostAsync(int copilotPort)
    {
        if (_state == TunnelState.Running)
        {
            Console.WriteLine("[DevTunnel] Already running");
            return true;
        }

        SetState(TunnelState.Starting);
        _tunnelUrl = null;
        var hostStopwatch = Stopwatch.StartNew();

        // Load saved tunnel ID for reuse (keeps same URL across restarts)
        var settings = ConnectionSettings.Load();
        if (_tunnelId == null && !string.IsNullOrEmpty(settings.TunnelId))
        {
            _tunnelId = settings.TunnelId;
            Console.WriteLine($"[DevTunnel] Reusing saved tunnel ID: {_tunnelId}");
        }

        try
        {
            // Hook bridge to CopilotService for state sync
            _bridge.SetCopilotService(_copilot);
            _bridge.SetRepoManager(_repoManager);

            // Set a temporary random token before starting the bridge so connections
            // are rejected during the tunnel setup window (before the real token is issued).
            _bridge.AccessToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

            // Start WebSocket bridge: WS on BridgePort for remote viewer clients
            _bridge.Start(BridgePort, copilotPort);
            if (!_bridge.IsRunning)
            {
                SetError("Failed to start WebSocket bridge");
                return false;
            }
            Console.WriteLine($"[DevTunnel] WsBridge started on {BridgePort}");

            var success = await TryHostTunnelAsync(settings);

            // If hosting with saved tunnel ID failed, clear it and retry with a new tunnel
            if (!success && _tunnelId != null)
            {
                Console.WriteLine($"[DevTunnel] Saved tunnel ID '{_tunnelId}' failed — creating new tunnel");
                _tunnelId = null;
                _tunnelUrl = null;
                settings.TunnelId = null;
                settings.Save();
                SetState(TunnelState.Starting);
                success = await TryHostTunnelAsync(settings);
            }

            if (!success)
            {
                var lastError = _errorMessage;
                Stop(cleanClose: false);
                // Stop() clears _errorMessage via SetState(NotStarted).
                // Restore the error (or a generic fallback) so the user sees what went wrong.
                SetError(lastError ?? "DevTunnel failed to start");
                return false;
            }

            // Wait briefly for the tunnel ID line to be parsed
            for (int i = 0; i < 10 && _tunnelId == null; i++)
                await Task.Delay(500);

            // Save tunnel ID for reuse across restarts
            if (_tunnelId != null)
            {
                settings.TunnelId = _tunnelId;
                settings.AutoStartTunnel = true;
                settings.Save();
                Console.WriteLine($"[DevTunnel] Saved tunnel ID: {_tunnelId}");
            }

            // Issue a connect-scoped access token
            _accessToken = await IssueAccessTokenAsync();
            if (_accessToken == null)
            {
                // Could not issue a real token — clear the random placeholder so the bridge
                // reverts to unauthenticated local-only mode rather than being permanently
                // locked with an opaque token no client can ever know.
                _bridge.AccessToken = null;
                Console.WriteLine("[DevTunnel] Warning: could not issue access token — bridge running in local-only mode");
            }
            else
            {
                _bridge.AccessToken = _accessToken;
            }

            SetState(TunnelState.Running);
            hostStopwatch.Stop();
            _ = _auditLog?.LogDevtunnelConnectionEstablished(null, _tunnelId, _tunnelUrl, hostStopwatch.ElapsedMilliseconds);
            return true;
        }
        catch (Exception ex)
        {
            Stop(cleanClose: false);
            SetError($"Host error: {ex.Message}");
            _ = _auditLog?.LogDevtunnelConnectionFailed(null, _tunnelId, ex.Message);
            return false;
        }
    }

    private async Task<bool> TryHostTunnelAsync(ConnectionSettings settings)
    {
        // Kill any existing host process from a previous attempt
        if (_hostProcess != null && !_hostProcess.HasExited)
        {
            try { _hostProcess.Kill(entireProcessTree: true); } catch { }
        }
        _hostProcess?.Dispose();
        _hostProcess = null;

        var hostArgs = _tunnelId != null
            ? $"host {_tunnelId}"
            : $"host -p {BridgePort}";

        var psi = new ProcessStartInfo
        {
            FileName = ResolveDevTunnel(),
            Arguments = hostArgs,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        _hostProcess = Process.Start(psi);
        if (_hostProcess == null)
        {
            SetError("Failed to start devtunnel host");
            return false;
        }

        var urlFound = new TaskCompletionSource<bool>();
        var lastErrorLine = "";

        _ = Task.Run(async () =>
        {
            try
            {
                while (!_hostProcess.HasExited)
                {
                    var line = await _hostProcess.StandardOutput.ReadLineAsync();
                    if (line == null) break;
                    Console.WriteLine($"[DevTunnel] {line}");
                    if (!string.IsNullOrWhiteSpace(line))
                        lastErrorLine = line;
                    TryExtractInfo(line, urlFound);
                }
            }
            catch { }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                while (!_hostProcess.HasExited)
                {
                    var line = await _hostProcess.StandardError.ReadLineAsync();
                    if (line == null) break;
                    Console.WriteLine($"[DevTunnel ERR] {line}");
                    if (!string.IsNullOrWhiteSpace(line))
                        lastErrorLine = line;
                    TryExtractInfo(line, urlFound);
                }
            }
            catch { }

            // Process exited unexpectedly
            if (_state == TunnelState.Running || _state == TunnelState.Starting)
            {
                var detail = string.IsNullOrWhiteSpace(lastErrorLine) ? "" : $": {lastErrorLine}";
                SetError($"Tunnel process exited unexpectedly{detail}");
                urlFound.TrySetResult(false);
            }
        });

        // Wait up to 30 seconds for URL
        var timeout = Task.Delay(TimeSpan.FromSeconds(30));
        var result = await Task.WhenAny(urlFound.Task, timeout);

        if (result == urlFound.Task && urlFound.Task.Result)
            return true;

        if (_state != TunnelState.Error)
            SetError("Timed out waiting for tunnel URL");
        return false;
    }

    private void TryExtractInfo(string line, TaskCompletionSource<bool> urlFound)
    {
        // Try to extract tunnel ID
        if (_tunnelId == null)
        {
            var idMatch = TunnelIdRegex().Match(line);
            if (!idMatch.Success)
                idMatch = TunnelIdAltRegex().Match(line);
            if (idMatch.Success)
            {
                _tunnelId = idMatch.Groups[1].Value.Trim();
                Console.WriteLine($"[DevTunnel] Tunnel ID found: {_tunnelId}");
            }
        }

        // Try to extract URL
        if (_tunnelUrl != null) return;

        var match = ConnectUrlRegex().Match(line);
        if (!match.Success)
            match = TunnelUrlRegex().Match(line);

        if (match.Success)
        {
            _tunnelUrl = match.Groups[1].Value.TrimEnd('/');
            Console.WriteLine($"[DevTunnel] URL found: {_tunnelUrl}");
            urlFound.TrySetResult(true);
        }
    }

    /// <summary>
    /// Issue a connect-scoped access token for the current tunnel.
    /// </summary>
    private async Task<string?> IssueAccessTokenAsync()
    {
        // Try using stored tunnel ID, fallback to last-used tunnel
        var tunnelArg = _tunnelId ?? "";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ResolveDevTunnel(),
                Arguments = $"token {tunnelArg} --scopes connect",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0)
            {
                var err = await p.StandardError.ReadToEndAsync();
                Console.WriteLine($"[DevTunnel] Token error: {err}");
                return null;
            }
            // Output has multiple lines like "Token tunnel ID: ...\nToken: <jwt>"
            // Extract just the token value
            var token = "";
            foreach (var line in output.Split('\n'))
            {
                if (line.StartsWith("Token:", StringComparison.OrdinalIgnoreCase) && !line.StartsWith("Token ", StringComparison.OrdinalIgnoreCase))
                {
                    token = line["Token:".Length..].Trim();
                    break;
                }
            }
            // Fallback: if no "Token:" prefix found, try the last non-empty line (might be raw token)
            if (string.IsNullOrEmpty(token))
            {
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                token = lines.Length > 0 ? lines[^1].Trim() : "";
            }
            Console.WriteLine($"[DevTunnel] Access token issued ({token.Length} chars)");
            _ = _auditLog?.LogDevtunnelTokenAcquired(null, _tunnelId, token.Length);
            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DevTunnel] Token error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Stop the hosted tunnel
    /// </summary>
    public void Stop(bool cleanClose = true)
    {
        SetState(TunnelState.Stopping);
        _ = _auditLog?.LogSessionClosed(null, 0, cleanClose, cleanClose ? "DevTunnel stopped" : "DevTunnel stopped after error");
        try
        {
            if (_hostProcess != null && !_hostProcess.HasExited)
            {
                _hostProcess.Kill(entireProcessTree: true);
                Console.WriteLine("[DevTunnel] Host process killed");
            }
            _hostProcess?.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DevTunnel] Error stopping: {ex.Message}");
        }
        _hostProcess = null;
        _tunnelUrl = null;
        _accessToken = null;
        _bridge.Stop();
        SetState(TunnelState.NotStarted);
    }

    private void SetState(TunnelState state)
    {
        _state = state;
        if (state != TunnelState.Error)
            _errorMessage = null;
        OnStateChanged?.Invoke();
    }

    private void SetError(string message)
    {
        _errorMessage = message;
        _state = TunnelState.Error;
        Console.WriteLine($"[DevTunnel] Error: {message}");
        OnStateChanged?.Invoke();
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
