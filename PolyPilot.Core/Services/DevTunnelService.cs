using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
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

    public DevTunnelService(WsBridgeServer bridge, CopilotService copilot, RepoManager repoManager, PrLinkService? prLinkService = null, AuditLogService? auditLog = null)
    {
        _bridge = bridge;
        _copilot = copilot;
        _repoManager = repoManager;
        _auditLog = auditLog;
        if (prLinkService != null) _bridge.SetPrLinkService(prLinkService);
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
            _bridge.ServerPassword = settings.ServerPassword;

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
                var lastError = "Tunnel started but no connect token was issued";
                Stop(cleanClose: false);
                SetError(lastError);
                return false;
            }

            _bridge.AccessToken = _accessToken;

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
        ProcessHelper.SafeKillAndDispose(_hostProcess);
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

        // Capture in local variable — fire-and-forget tasks must not access _hostProcess
        // field, which can be nulled/disposed by Stop() or a subsequent TryHostTunnelAsync().
        var process = _hostProcess;
        var urlFound = new TaskCompletionSource<bool>();
        var lastErrorLine = "";

        _ = Task.Run(async () =>
        {
            try
            {
                while (!ProcessHelper.SafeHasExited(process))
                {
                    var line = await process.StandardOutput.ReadLineAsync();
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
                while (!ProcessHelper.SafeHasExited(process))
                {
                    var line = await process.StandardError.ReadLineAsync();
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
            var token = await TryIssueAccessTokenAsync(tunnelArg, useJsonOutput: true)
                ?? await TryIssueAccessTokenAsync(tunnelArg, useJsonOutput: false);

            if (token != null)
            {
                Console.WriteLine($"[DevTunnel] Access token issued ({token.Length} chars)");
                _ = _auditLog?.LogDevtunnelTokenAcquired(null, _tunnelId, token.Length);
                return token;
            }

            Console.WriteLine("[DevTunnel] Token output did not contain a usable access token");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DevTunnel] Token error: {ex.Message}");
            return null;
        }
    }

    private static async Task<string?> TryIssueAccessTokenAsync(string tunnelArg, bool useJsonOutput)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolveDevTunnel(),
            Arguments = useJsonOutput
                ? $"token {tunnelArg} --scopes connect -j"
                : $"token {tunnelArg} --scopes connect",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var p = Process.Start(psi);
        if (p == null) return null;

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        await p.WaitForExitAsync();

        var output = await stdoutTask;
        var error = await stderrTask;

        if (p.ExitCode != 0)
        {
            Console.WriteLine($"[DevTunnel] Token error{(useJsonOutput ? " (json)" : "")}: {SummarizeTokenCommandOutput(error, output)}");
            return null;
        }

        // Prefer stdout; only fall back to stderr when stdout was truly empty/whitespace,
        // to avoid picking up error messages or warnings as false-positive tokens.
        var token = ParseAccessTokenOutput(output);
        if (token != null)
            return token;
        return string.IsNullOrWhiteSpace(output) ? ParseAccessTokenOutput(error) : null;
    }

    internal static string? ParseAccessTokenOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        var trimmed = output.Trim();

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var jsonToken = TryExtractTokenFromJson(doc.RootElement);
            if (!string.IsNullOrWhiteSpace(jsonToken))
                return jsonToken;
        }
        catch (JsonException)
        {
        }

        foreach (var line in trimmed.Split('\n'))
        {
            if (line.StartsWith("Token:", StringComparison.OrdinalIgnoreCase)
                && !line.StartsWith("Token ", StringComparison.OrdinalIgnoreCase))
            {
                var token = line["Token:".Length..].Trim();
                return string.IsNullOrEmpty(token) ? null : token;
            }
        }

        var lines = trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return null;

        var lastLine = lines[^1].Trim();
        return LooksLikeAccessToken(lastLine) ? lastLine : null;
    }

    private static string? TryExtractTokenFromJson(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
            {
                var value = element.GetString()?.Trim();
                return LooksLikeAccessToken(value) ? value : null;
            }

            case JsonValueKind.Object:
            {
                foreach (var propertyName in new[] { "token", "accessToken", "access_token" })
                {
                    if (element.TryGetProperty(propertyName, out var namedValue))
                    {
                        var token = TryExtractTokenFromJson(namedValue);
                        if (!string.IsNullOrWhiteSpace(token))
                            return token;
                    }
                }

                foreach (var property in element.EnumerateObject())
                {
                    var token = TryExtractTokenFromJson(property.Value);
                    if (!string.IsNullOrWhiteSpace(token))
                        return token;
                }

                break;
            }

            case JsonValueKind.Array:
            {
                foreach (var item in element.EnumerateArray())
                {
                    var token = TryExtractTokenFromJson(item);
                    if (!string.IsNullOrWhiteSpace(token))
                        return token;
                }

                break;
            }
        }

        return null;
    }

    private static bool LooksLikeAccessToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (trimmed.Contains("://", StringComparison.Ordinal)
            || trimmed.Contains('/')
            || trimmed.Contains('\\')
            || trimmed.Contains(':')
            || trimmed.Any(char.IsWhiteSpace))
            return false;

        // Tokens with dots (JWT-style "header.payload.signature") must be at least 32 chars
        // to avoid matching version strings like "1.0.0-preview.260212.1" (22 chars).
        var dotCount = trimmed.Count(c => c == '.');
        if (dotCount >= 1 && trimmed.Length >= 32)
            return true;

        // Opaque tokens (no dots) must be at least 24 chars and consist only of
        // base64url-safe characters to avoid matching error messages or file paths.
        if (dotCount == 0 && trimmed.Length >= 24)
            return trimmed.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '+' || c == '=');

        return false;
    }

    private static string SummarizeTokenCommandOutput(string primary, string secondary)
    {
        foreach (var candidate in new[] { primary, secondary })
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate.Trim().ReplaceLineEndings(" ");
        }

        return "(no output)";
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
            if (!ProcessHelper.SafeHasExited(_hostProcess))
                Console.WriteLine("[DevTunnel] Host process killed");
            ProcessHelper.SafeKillAndDispose(_hostProcess);
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
