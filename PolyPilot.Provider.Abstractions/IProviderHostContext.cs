using GitHub.Copilot.SDK;

namespace PolyPilot.Provider;

/// <summary>
/// Host environment context shared with providers.
/// Registered in DI by the host app (PolyPilot).
/// Providers resolve this to create CopilotClients configured identically to the host.
/// </summary>
public interface IProviderHostContext
{
    /// <summary>
    /// Creates a CopilotClientOptions pre-configured to match the host's
    /// current connection settings (embedded vs persistent, CLI path, port, etc.).
    /// The provider can further customize the returned options (e.g., set Cwd)
    /// before passing them to <c>new CopilotClient(options)</c>.
    /// </summary>
    CopilotClientOptions CreateCopilotClientOptions(string? workingDirectory = null);

    /// <summary>How the host connects to the Copilot backend.</summary>
    ProviderConnectionMode ConnectionMode { get; }

    /// <summary>Whether the host uses its built-in CLI binary or the system-installed one.</summary>
    ProviderCliSource CliSource { get; }

    /// <summary>
    /// Additional host settings as key-value pairs.
    /// Providers can read well-known keys without taking a hard dependency on host internals.
    /// </summary>
    IReadOnlyDictionary<string, string> Settings { get; }

    /// <summary>Get a setting value, or null if not set.</summary>
    string? GetSetting(string key) =>
        Settings.TryGetValue(key, out var v) ? v : null;
}

/// <summary>Mirrors PolyPilot's ConnectionMode without requiring a reference to PolyPilot.</summary>
public enum ProviderConnectionMode
{
    Embedded,
    Persistent,
    Remote,
    Demo
}

/// <summary>Mirrors PolyPilot's CliSourceMode.</summary>
public enum ProviderCliSource
{
    BuiltIn,
    System
}
