using Microsoft.Extensions.DependencyInjection;

namespace PolyPilot.Provider.Squad;

/// <summary>
/// Factory for registering the Squad session provider into DI.
///
/// Configuration is read from a squad-provider.json file in the plugin directory:
/// {
///   "host": "localhost",
///   "port": 4242,
///   "sessionToken": "your-squad-session-token"
/// }
///
/// Alternatively, environment variables override the config file:
///   SQUAD_BRIDGE_HOST (default: localhost)
///   SQUAD_BRIDGE_PORT (default: 4242)
///   SQUAD_BRIDGE_TOKEN (required)
/// </summary>
public class SquadProviderFactory : ISessionProviderFactory
{
    public void ConfigureServices(IServiceCollection services, string pluginDirectory, IPluginLogger? logger = null)
    {
        logger?.Info("SquadProviderFactory.ConfigureServices called");

        var config = LoadConfig(pluginDirectory, logger);

        services.AddSingleton<ISessionProvider>(sp =>
        {
            logger?.Info($"Creating SquadSessionProvider → {config.Host}:{config.Port}");
            return new SquadSessionProvider(config.Host, config.Port, config.SessionToken, logger);
        });
    }

    private static SquadProviderConfig LoadConfig(string pluginDirectory, IPluginLogger? logger)
    {
        var host = Environment.GetEnvironmentVariable("SQUAD_BRIDGE_HOST") ?? "localhost";
        var portStr = Environment.GetEnvironmentVariable("SQUAD_BRIDGE_PORT");
        var token = Environment.GetEnvironmentVariable("SQUAD_BRIDGE_TOKEN");

        var port = 4242;
        if (portStr != null && int.TryParse(portStr, out var parsedPort))
            port = parsedPort;

        // Try config file as fallback for token
        if (string.IsNullOrEmpty(token))
        {
            var configPath = Path.Combine(pluginDirectory, "squad-provider.json");
            if (File.Exists(configPath))
            {
                try
                {
                    var json = File.ReadAllText(configPath);
                    var fileConfig = System.Text.Json.JsonSerializer.Deserialize<SquadProviderConfig>(
                        json, SquadProtocol.JsonOptions);
                    if (fileConfig != null)
                    {
                        host = fileConfig.Host ?? host;
                        port = fileConfig.Port > 0 ? fileConfig.Port : port;
                        token = fileConfig.SessionToken ?? token;
                        logger?.Info($"Loaded config from {configPath}");
                    }
                }
                catch (Exception ex)
                {
                    logger?.Warning($"Failed to read squad-provider.json: {ex.Message}");
                }
            }
        }

        if (string.IsNullOrEmpty(token))
        {
            logger?.Warning("No SQUAD_BRIDGE_TOKEN env var or squad-provider.json token found. " +
                            "The provider will fail to authenticate. Run 'squad rc' and note the session token.");
            token = "";
        }

        return new SquadProviderConfig { Host = host, Port = port, SessionToken = token };
    }
}

internal class SquadProviderConfig
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 4242;
    public string SessionToken { get; set; } = "";
}
