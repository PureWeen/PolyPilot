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
    public ChatLayout ChatLayout { get; set; } = ChatLayout.Default;
    public UiTheme Theme { get; set; } = UiTheme.PolyPilotDark;
    public bool AutoUpdateFromMain { get; set; } = false;

    [JsonIgnore]
    public string CliUrl => Mode == ConnectionMode.Remote && !string.IsNullOrEmpty(RemoteUrl)
        ? RemoteUrl
        : $"{Host}:{Port}";

    private static string? _settingsPath;
    private static string SettingsPath => _settingsPath ??= Path.Combine(
        GetPolyPilotDir(), "settings.json");

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
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }
}
