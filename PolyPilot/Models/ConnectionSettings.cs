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

public enum ChatStyle
{
    Normal,       // Colored bubbles with borders
    Minimal       // Full-width, no bubbles (Claude.ai style)
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
    public string? LanUrl { get; set; }
    public string? LanToken { get; set; }
    public string? TunnelId { get; set; }
    public bool AutoStartTunnel { get; set; } = false;
    public string? ServerPassword { get; set; }
    public bool DirectSharingEnabled { get; set; } = false;
    public ChatLayout ChatLayout { get; set; } = ChatLayout.Default;
    public ChatStyle ChatStyle { get; set; } = ChatStyle.Normal;
    public UiTheme Theme { get; set; } = UiTheme.System;
    public bool AutoUpdateFromMain { get; set; } = false;
    public CliSourceMode CliSource { get; set; } = CliSourceMode.BuiltIn;
    public List<string> DisabledMcpServers { get; set; } = new();
    public List<string> DisabledPlugins { get; set; } = new();
    public bool EnableSessionNotifications { get; set; } = false;

    /// <summary>
    /// When non-zero, sends a "still running" reminder notification every N minutes
    /// while a session is processing. 0 = disabled.
    /// </summary>
    public int NotificationReminderIntervalMinutes { get; set; } = 0;

    /// <summary>
    /// Normalizes a remote URL by ensuring it has an http(s):// scheme.
    /// Plain IPs/hostnames get http://, devtunnels/known TLS hosts get https://.
    /// Already-schemed URLs pass through unchanged. Returns null for null/empty input.
    /// </summary>
    public static string? NormalizeRemoteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        var trimmed = url.Trim().TrimEnd('/');

        // Already has any scheme — return as-is (prevents double-scheme like http://ftp://host)
        if (trimmed.Contains("://"))
            return trimmed;

        // Heuristic: known tunnel/proxy hosts always use TLS — match exact suffixes to avoid
        // false-positives from hostnames that merely contain ".ngrok" or ".cloudflare"
        if (trimmed.EndsWith(".devtunnels.ms", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(".ngrok.io", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(".ngrok-free.app", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(".ngrok.app", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(".trycloudflare.com", StringComparison.OrdinalIgnoreCase))
            return "https://" + trimmed;

        // Everything else (bare IP, localhost, LAN hostname) → http
        return "http://" + trimmed;
    }

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

        // Ensure CliSource is a valid enum value (guards against corrupt settings)
        if (!Enum.IsDefined(settings.CliSource))
            settings.CliSource = CliSourceMode.BuiltIn;

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
