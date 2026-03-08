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
