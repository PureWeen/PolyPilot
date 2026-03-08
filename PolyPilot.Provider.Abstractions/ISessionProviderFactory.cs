using Microsoft.Extensions.DependencyInjection;

namespace PolyPilot.Provider;

/// <summary>
/// Factory that registers a provider's services into the DI container.
/// Discovered by the plugin loader via assembly scanning.
/// Must have a parameterless constructor.
/// </summary>
public interface ISessionProviderFactory
{
    /// <summary>
    /// Register this provider's services. Called during app startup,
    /// before the DI container is built. The factory should register
    /// its ISessionProvider implementation and any dependencies.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="pluginDirectory">
    /// Directory the plugin was loaded from. Providers can use this
    /// to find config files, data directories, etc. beside their DLL.
    /// </param>
    void ConfigureServices(IServiceCollection services, string pluginDirectory);
}
