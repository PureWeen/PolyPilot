using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using PolyPilot.Models;
using PolyPilot.Provider;
using Microsoft.Extensions.DependencyInjection;

namespace PolyPilot.Services;

/// <summary>
/// Discovers and loads provider plugins from ~/.polypilot/plugins/.
/// Each plugin lives in its own subdirectory with a plugin.json manifest
/// that declares the entry-point DLL, display name, description, and version.
/// Plugins are never auto-loaded — the user must explicitly approve each one in Settings → Plugins.
/// A SHA-256 hash check on the entry-point DLL prevents silent replacement.
/// All operations are logged to ~/.polypilot/logs/plugins/{name}/plugin.log.
/// </summary>
public static class PluginLoader
{
    private static string? _pluginsDir;
    private static string PluginsDir => _pluginsDir ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".polypilot", "plugins");

    /// <summary>
    /// Scans the plugins directory for subdirectories containing a plugin.json manifest.
    /// Returns metadata only — no assemblies are loaded.
    /// </summary>
    public static List<DiscoveredPlugin> DiscoverPlugins()
    {
        var systemLog = new PluginFileLogger("_system");
        var plugins = new List<DiscoveredPlugin>();

        if (!Directory.Exists(PluginsDir))
        {
            systemLog.Info($"Plugins directory does not exist: {PluginsDir}");
            return plugins;
        }

        systemLog.Info($"Scanning plugins directory: {PluginsDir}");

        foreach (var dir in Directory.GetDirectories(PluginsDir))
        {
            var manifestPath = Path.Combine(dir, "plugin.json");
            if (!File.Exists(manifestPath))
                continue;

            try
            {
                var json = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<PluginManifest>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (manifest == null || string.IsNullOrWhiteSpace(manifest.EntryPoint))
                {
                    systemLog.Warning($"Plugin in '{dir}' has invalid manifest (missing entryPoint)");
                    continue;
                }

                var entryDll = Path.Combine(dir, manifest.EntryPoint);
                if (!File.Exists(entryDll))
                {
                    systemLog.Warning($"Plugin '{manifest.Name ?? dir}' entry-point DLL not found: {manifest.EntryPoint}");
                    continue;
                }

                var dirName = Path.GetFileName(dir);
                var hash = ComputeHash(entryDll);

                plugins.Add(new DiscoveredPlugin
                {
                    Path = dirName,
                    FullPath = entryDll,
                    Hash = hash,
                    FileName = manifest.EntryPoint,
                    DirectoryName = dirName,
                    SizeBytes = new FileInfo(entryDll).Length,
                    Name = manifest.Name ?? dirName,
                    Description = manifest.Description ?? "",
                    Version = manifest.Version ?? "",
                });

                systemLog.Info($"Discovered plugin: {manifest.Name ?? dirName} v{manifest.Version ?? "?"} ({manifest.EntryPoint})");
            }
            catch (Exception ex)
            {
                systemLog.Error($"Failed to read manifest in '{dir}'", ex);
            }
        }

        systemLog.Info($"Discovery complete: {plugins.Count} plugin(s) found");
        return plugins;
    }

    /// <summary>
    /// Loads only user-approved plugins whose SHA-256 hash matches what was approved.
    /// Called during app startup, before builder.Build().
    /// </summary>
    public static List<string> LoadEnabledProviders(IServiceCollection services, IReadOnlyList<EnabledPlugin> enabledPlugins)
    {
        var systemLog = new PluginFileLogger("_system");
        var warnings = new List<string>();

        systemLog.Info($"Loading {enabledPlugins.Count} enabled plugin(s)");

        foreach (var plugin in enabledPlugins)
        {
            var pluginLog = new PluginFileLogger(plugin.DisplayName ?? plugin.Path);
            pluginLog.Info($"--- Plugin load started ---");

            // Resolve the entry-point DLL from the plugin subdirectory
            var pluginDir = Path.Combine(PluginsDir, plugin.Path);
            string fullPath;

            if (Directory.Exists(pluginDir))
            {
                // Manifest-based: read entry point from plugin.json
                var manifestPath = Path.Combine(pluginDir, "plugin.json");
                if (File.Exists(manifestPath))
                {
                    try
                    {
                        var json = File.ReadAllText(manifestPath);
                        var manifest = JsonSerializer.Deserialize<PluginManifest>(json,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        fullPath = Path.Combine(pluginDir, manifest?.EntryPoint ?? "");
                        pluginLog.Info($"Manifest loaded: entryPoint={manifest?.EntryPoint}");
                    }
                    catch (Exception ex)
                    {
                        var msg = $"Plugin '{plugin.DisplayName}' has invalid manifest";
                        warnings.Add(msg);
                        pluginLog.Error(msg, ex);
                        continue;
                    }
                }
                else
                {
                    var msg = $"Plugin '{plugin.DisplayName}' missing plugin.json manifest";
                    warnings.Add(msg);
                    pluginLog.Warning(msg);
                    continue;
                }
            }
            else
            {
                // Legacy: Path might be a direct DLL path (backward compat)
                fullPath = Path.Combine(PluginsDir, plugin.Path);
                pluginLog.Info($"Using legacy DLL path: {fullPath}");
            }

            if (!File.Exists(fullPath))
            {
                var msg = $"Plugin '{plugin.DisplayName}' not found: {plugin.Path}";
                warnings.Add(msg);
                pluginLog.Error(msg);
                continue;
            }

            // Defense-in-depth: ensure resolved path is within the plugins directory
            var normalizedPath = Path.GetFullPath(fullPath);
            var pluginsRoot = Path.GetFullPath(PluginsDir) + Path.DirectorySeparatorChar;
            if (!normalizedPath.StartsWith(pluginsRoot, StringComparison.OrdinalIgnoreCase))
            {
                var msg = $"Plugin '{plugin.DisplayName}' path escapes plugins directory — blocked";
                warnings.Add(msg);
                pluginLog.Error(msg);
                continue;
            }

            var currentHash = ComputeHash(fullPath);
            pluginLog.Info($"Hash check: stored={plugin.Hash?[..12]}... current={currentHash[..12]}...");

            if (!string.Equals(currentHash, plugin.Hash, StringComparison.OrdinalIgnoreCase))
            {
                var msg = $"Plugin '{plugin.DisplayName}' hash changed — needs re-approval";
                warnings.Add(msg);
                pluginLog.Warning(msg);
                continue;
            }

            pluginLog.Info("Hash verified OK — loading assembly");

            try
            {
                var loadContext = new PluginLoadContext(fullPath);
                var assembly = loadContext.LoadFromAssemblyPath(fullPath);
                var assemblyDir = Path.GetDirectoryName(fullPath) ?? PluginsDir;

                pluginLog.Info($"Assembly loaded: {assembly.FullName}");

                // Register the plugin's logger in DI so providers can resolve it
                services.AddSingleton<IPluginLogger>(pluginLog);

                var factoryCount = 0;
                foreach (var type in assembly.GetExportedTypes()
                    .Where(t => typeof(ISessionProviderFactory).IsAssignableFrom(t) && !t.IsAbstract))
                {
                    pluginLog.Info($"Found factory: {type.FullName}");

                    if (Activator.CreateInstance(type) is ISessionProviderFactory factory)
                    {
                        factory.ConfigureServices(services, assemblyDir, pluginLog);
                        factoryCount++;
                        pluginLog.Info($"ConfigureServices completed for {type.Name}");
                    }
                }

                if (factoryCount == 0)
                    pluginLog.Warning("No ISessionProviderFactory implementations found in assembly");
                else
                    pluginLog.Info($"Plugin loaded successfully ({factoryCount} factory/factories)");
            }
            catch (Exception ex)
            {
                var msg = $"Plugin '{plugin.DisplayName}' failed to load: {ex.Message}";
                warnings.Add(msg);
                pluginLog.Error(msg, ex);
            }
        }

        systemLog.Info($"Plugin loading complete: {warnings.Count} warning(s)");
        foreach (var w in warnings)
            systemLog.Warning(w);

        return warnings;
    }

    internal static string ComputeHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hashBytes = SHA256.HashData(stream);
        return Convert.ToHexStringLower(hashBytes);
    }

    /// <summary>
    /// Custom AssemblyLoadContext that shares host assemblies but isolates plugin-specific deps.
    /// </summary>
    private class PluginLoadContext : AssemblyLoadContext
    {
        private readonly string _pluginDir;

        // Assemblies shared with the host to avoid type identity conflicts
        private static readonly HashSet<string> SharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
        {
            "PolyPilot.Provider.Abstractions",
            "Microsoft.Extensions.DependencyInjection.Abstractions",
            "Microsoft.Extensions.DependencyInjection",
            "Microsoft.Extensions.Logging",
            "Microsoft.Extensions.Logging.Abstractions",
            "Microsoft.Extensions.Options",
            "Microsoft.Extensions.Hosting.Abstractions",
            "Microsoft.Extensions.AI",
            "Microsoft.Extensions.AI.Abstractions",
            "GitHub.Copilot.SDK",
        };

        public PluginLoadContext(string pluginPath) : base(isCollectible: false)
        {
            _pluginDir = Path.GetDirectoryName(pluginPath) ?? "";
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Share host assemblies to prevent type identity conflicts
            if (assemblyName.Name != null && SharedAssemblies.Contains(assemblyName.Name))
                return null; // Fall back to default context

            // Try to load from the plugin's directory
            if (assemblyName.Name != null)
            {
                var candidatePath = Path.Combine(_pluginDir, assemblyName.Name + ".dll");
                if (File.Exists(candidatePath))
                    return LoadFromAssemblyPath(candidatePath);
            }

            return null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            // Resolve native libraries from plugin's runtimes directory
            var arch = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
            var candidatePaths = new[]
            {
                Path.Combine(_pluginDir, "runtimes", arch, "native", $"lib{unmanagedDllName}.dylib"),
                Path.Combine(_pluginDir, "runtimes", arch, "native", $"{unmanagedDllName}.dylib"),
                Path.Combine(_pluginDir, "runtimes", arch, "native", $"lib{unmanagedDllName}.so"),
                Path.Combine(_pluginDir, "runtimes", arch, "native", $"{unmanagedDllName}.so"),
                Path.Combine(_pluginDir, $"lib{unmanagedDllName}.dylib"),
                Path.Combine(_pluginDir, $"{unmanagedDllName}.dylib"),
            };

            foreach (var path in candidatePaths)
            {
                if (File.Exists(path))
                    return LoadUnmanagedDllFromPath(path);
            }

            return IntPtr.Zero;
        }
    }
}

/// <summary>
/// The plugin.json manifest file that lives in each plugin subdirectory.
/// </summary>
public class PluginManifest
{
    /// <summary>The DLL filename containing the ISessionProviderFactory (e.g., "Papilot.PolyPilot.dll").</summary>
    public string EntryPoint { get; set; } = "";
    /// <summary>Human-readable plugin name shown in Settings.</summary>
    public string? Name { get; set; }
    /// <summary>Short description shown in Settings.</summary>
    public string? Description { get; set; }
    /// <summary>Plugin version (e.g., "0.1.0").</summary>
    public string? Version { get; set; }
}

/// <summary>
/// Metadata about a discovered plugin. No code is loaded at this stage.
/// </summary>
public class DiscoveredPlugin
{
    public string Path { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string Hash { get; init; } = "";
    public string FileName { get; init; } = "";
    public string DirectoryName { get; init; } = "";
    public long SizeBytes { get; init; }
    /// <summary>Display name from plugin.json manifest.</summary>
    public string Name { get; init; } = "";
    /// <summary>Description from plugin.json manifest.</summary>
    public string Description { get; init; } = "";
    /// <summary>Version from plugin.json manifest.</summary>
    public string Version { get; init; } = "";
}
