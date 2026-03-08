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
        var plugins = new List<DiscoveredPlugin>();
        if (!Directory.Exists(PluginsDir))
            return plugins;

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
                    continue;

                var entryDll = Path.Combine(dir, manifest.EntryPoint);
                if (!File.Exists(entryDll))
                    continue;

                var dirName = Path.GetFileName(dir);
                var hash = ComputeHash(entryDll);

                plugins.Add(new DiscoveredPlugin
                {
                    // Path is the subdirectory name — the unit of identity
                    Path = dirName,
                    FullPath = entryDll,
                    Hash = hash,
                    FileName = manifest.EntryPoint,
                    DirectoryName = dirName,
                    SizeBytes = new FileInfo(entryDll).Length,
                    // Manifest metadata for UI display
                    Name = manifest.Name ?? dirName,
                    Description = manifest.Description ?? "",
                    Version = manifest.Version ?? "",
                });
            }
            catch
            {
                // Skip malformed manifests
            }
        }

        return plugins;
    }

    /// <summary>
    /// Loads only user-approved plugins whose SHA-256 hash matches what was approved.
    /// Called during app startup, before builder.Build().
    /// </summary>
    public static List<string> LoadEnabledProviders(IServiceCollection services, IReadOnlyList<EnabledPlugin> enabledPlugins)
    {
        var warnings = new List<string>();

        foreach (var plugin in enabledPlugins)
        {
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
                    }
                    catch
                    {
                        warnings.Add($"Plugin '{plugin.DisplayName}' has invalid manifest");
                        continue;
                    }
                }
                else
                {
                    warnings.Add($"Plugin '{plugin.DisplayName}' missing plugin.json manifest");
                    continue;
                }
            }
            else
            {
                // Legacy: Path might be a direct DLL path (backward compat)
                fullPath = Path.Combine(PluginsDir, plugin.Path);
            }

            if (!File.Exists(fullPath))
            {
                warnings.Add($"Plugin '{plugin.DisplayName}' not found: {plugin.Path}");
                continue;
            }

            var currentHash = ComputeHash(fullPath);
            if (!string.Equals(currentHash, plugin.Hash, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Plugin '{plugin.DisplayName}' hash changed — needs re-approval");
                continue;
            }

            try
            {
                var loadContext = new PluginLoadContext(fullPath);
                var assembly = loadContext.LoadFromAssemblyPath(fullPath);
                var assemblyDir = Path.GetDirectoryName(fullPath) ?? PluginsDir;

                foreach (var type in assembly.GetExportedTypes()
                    .Where(t => typeof(ISessionProviderFactory).IsAssignableFrom(t) && !t.IsAbstract))
                {
                    if (Activator.CreateInstance(type) is ISessionProviderFactory factory)
                    {
                        factory.ConfigureServices(services, assemblyDir);
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Plugin '{plugin.DisplayName}' failed to load: {ex.Message}");
            }
        }

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
