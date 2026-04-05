using GitHub.Copilot.SDK;
using PolyPilot.Models;
using PolyPilot.Provider;

namespace PolyPilot.Services;

/// <summary>
/// Exposes host connection settings to providers so they can create
/// CopilotClients configured identically to the host.
/// </summary>
public class ProviderHostContext : IProviderHostContext
{
    private readonly ConnectionSettings _settings;

    public ProviderHostContext(ConnectionSettings settings)
    {
        _settings = settings;
    }

    public CopilotClientOptions CreateCopilotClientOptions(string? workingDirectory = null)
    {
        var options = new CopilotClientOptions();

        switch (_settings.Mode)
        {
            case Models.ConnectionMode.Embedded:
                options.UseStdio = true;
                options.AutoStart = true;
                options.AutoRestart = true;
                options.CliPath = _settings.CliSource == CliSourceMode.BuiltIn
                    ? CopilotService.ResolveBundledCliPath()
                    : null;
                break;

            case Models.ConnectionMode.Persistent:
                options.CliPath = null;
                options.UseStdio = false;
                options.AutoStart = false;
                options.CliUrl = $"http://{_settings.Host}:{_settings.Port}";
                options.Port = _settings.Port;
                break;

            case Models.ConnectionMode.Remote:
                options.CliPath = null;
                options.UseStdio = false;
                options.AutoStart = false;
                options.CliUrl = ConnectionSettings.NormalizeRemoteUrl(_settings.RemoteUrl)
                    ?? $"http://{_settings.Host}:{_settings.Port}";
                break;
        }

        if (workingDirectory != null)
            options.Cwd = workingDirectory;

        // Forward the full system environment so the CLI child process can find
        // binaries, spawn shells (ConPTY needs COMSPEC, SystemRoot, etc.), and
        // access auth state. MAUI apps don't inherit terminal PATH, so we also
        // ensure common tool directories are present.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var isWindows = OperatingSystem.IsWindows();
        var pathSeparator = isWindows ? ';' : ':';

        // Start with the full inherited environment.
        // Windows env var names are case-insensitive (Path vs PATH), so use
        // OrdinalIgnoreCase to avoid duplicate keys when augmenting PATH.
        var env = new Dictionary<string, string>(
            isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string val)
                env[key] = val;
        }

        // Ensure common tool directories are on PATH
        var envPath = env.GetValueOrDefault("PATH", "");
        var pathParts = new HashSet<string>(
            envPath.Split(pathSeparator, StringSplitOptions.RemoveEmptyEntries));

        if (!isWindows)
        {
            foreach (var p in new[]
            {
                "/opt/homebrew/bin",
                "/usr/local/bin",
                "/usr/bin",
                "/bin",
                "/usr/sbin",
                "/sbin",
            })
                pathParts.Add(p);
        }

        pathParts.Add(Path.Combine(home, ".dotnet", "tools"));
        env["PATH"] = string.Join(pathSeparator, pathParts);

        // Ensure HOME and AZURE_CONFIG_DIR are set (TryAdd preserves user overrides)
        env.TryAdd("HOME", home);
        env.TryAdd("AZURE_CONFIG_DIR", Path.Combine(home, ".azure"));

        options.Environment = env;

        return options;
    }

    public ProviderConnectionMode ConnectionMode => _settings.Mode switch
    {
        Models.ConnectionMode.Embedded => Provider.ProviderConnectionMode.Embedded,
        Models.ConnectionMode.Persistent => Provider.ProviderConnectionMode.Persistent,
        Models.ConnectionMode.Remote => Provider.ProviderConnectionMode.Remote,
        Models.ConnectionMode.Demo => Provider.ProviderConnectionMode.Demo,
        _ => Provider.ProviderConnectionMode.Embedded
    };

    public ProviderCliSource CliSource => _settings.CliSource switch
    {
        CliSourceMode.BuiltIn => ProviderCliSource.BuiltIn,
        CliSourceMode.System => ProviderCliSource.System,
        _ => ProviderCliSource.BuiltIn
    };

    public IReadOnlyDictionary<string, string> Settings { get; } =
        new Dictionary<string, string>();
}
