using System.Text.Json;
using System.Text.Json.Serialization;

namespace PolyPilot.Models;

public enum ConnectionMode
{
    Embedded,   // SDK spawns copilot via stdio (dies with app)
    Persistent, // App spawns detached copilot server; survives app restarts
    Remote,     // Connect to a remote server via URL (e.g. DevTunnel)
    Demo        // Local mock mode for testing chat UI without a real connection
}

public enum ChatLayout
{
    Default,      // Copilot left, User right
    Reversed,     // User left, Copilot right
    BothLeft      // Both on left
}

public enum UiTheme
{
    System,          // Follow OS light/dark preference
    PolyPilotDark,   // Default dark theme
    PolyPilotLight,  // Light variant
    SolarizedDark,   // Solarized dark
    SolarizedLight   // Solarized light
}

public enum CliSourceMode
{
    BuiltIn,   // Use the CLI bundled with the app
    System     // Use the CLI installed on the system (PATH, homebrew, npm)
}

public class ConnectionSettings
{
    public ConnectionMode Mode { get; set; } = PlatformHelper.DefaultMode;
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 4321;
    public bool AutoStartServer { get; set; } = false;
    public string? RemoteUrl { get; set; }
    public string? RemoteToken { get; set; }
    public string? TunnelId { get; set; }
    public bool AutoStartTunnel { get; set; } = false;
    public string? ServerPassword { get; set; }
    public bool DirectSharingEnabled { get; set; } = false;
    public ChatLayout ChatLayout { get; set; } = ChatLayout.Default;
    public UiTheme Theme { get; set; } = UiTheme.PolyPilotDark;
    public bool AutoUpdateFromMain { get; set; } = false;
    public CliSourceMode CliSource { get; set; } = CliSourceMode.BuiltIn;
    public List<string> DisabledMcpServers { get; set; } = new();
    public List<string> DisabledPlugins { get; set; } = new();
    public string? MachineName { get; set; } = DefaultMachineName;
    public string InstanceId { get; set; } = "";
    public bool FiestaDiscoveryEnabled { get; set; } = true;
    public bool FiestaTailscaleDiscoveryEnabled { get; set; } = true;
    public bool FiestaTailscaleDiscoveryConfigured { get; set; } = false;
    public bool FiestaTailnetBroadcastEnabled { get; set; } = true;
    public bool FiestaOfferAsWorker { get; set; } = false;
    public bool FiestaAutoStartWorkerHosting { get; set; } = true;
    public string? FiestaJoinCode { get; set; }
    public bool EnableSessionNotifications { get; set; } = false;

    [JsonIgnore]
    public string CliUrl => Mode == ConnectionMode.Remote && !string.IsNullOrEmpty(RemoteUrl)
        ? RemoteUrl
        : $"{Host}:{Port}";

    private static string? _settingsPath;
    private static string SettingsPath => _settingsPath ??= Path.Combine(
        GetPolyPilotDir(), "settings.json");
    private static string? _defaultMachineName;
    public static string DefaultMachineName => _defaultMachineName ??= ResolveDefaultMachineName();

    private static string GetPolyPilotDir()
    {
#if IOS || ANDROID
        try
        {
            return Path.Combine(FileSystem.AppDataDirectory, ".polypilot");
        }
        catch
        {
            var fallback = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(fallback))
                fallback = Path.GetTempPath();
            return Path.Combine(fallback, ".polypilot");
        }
#else
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(home, ".polypilot");
#endif
    }

    public static ConnectionSettings Load()
    {
        ConnectionSettings settings;
        var shouldSave = false;
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                settings = JsonSerializer.Deserialize<ConnectionSettings>(json) ?? DefaultSettings();
            }
            else
            {
                settings = DefaultSettings();
            }
        }
        catch { settings = DefaultSettings(); }

        // Ensure loaded mode is valid for this platform
        if (!PlatformHelper.AvailableModes.Contains(settings.Mode))
            settings.Mode = PlatformHelper.DefaultMode;

        if (string.IsNullOrWhiteSpace(settings.MachineName))
        {
            settings.MachineName = DefaultMachineName;
            shouldSave = true;
        }

        if (string.IsNullOrWhiteSpace(settings.InstanceId))
        {
            settings.InstanceId = Guid.NewGuid().ToString("N");
            shouldSave = true;
        }

        if (shouldSave)
            settings.Save();

        return settings;
    }

    private static ConnectionSettings DefaultSettings()
    {
#if ANDROID
        // Android can't run Copilot locally — default to persistent mode
        // User must configure the host IP in Settings to point to their Mac
        return new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 4321
        };
#else
        return new();
#endif
    }

    public void Save()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(MachineName))
                MachineName = DefaultMachineName;
            if (string.IsNullOrWhiteSpace(InstanceId))
                InstanceId = Guid.NewGuid().ToString("N");

            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }

    private static string ResolveDefaultMachineName()
    {
        try
        {
            var host = Environment.MachineName;
            if (!string.IsNullOrWhiteSpace(host))
                return host.Trim();
        }
        catch { }

        return $"PolyPilot-{Guid.NewGuid():N}"[..16];
    }
}
