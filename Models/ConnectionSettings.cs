using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoPilot.App.Models;

public enum ConnectionMode
{
    Embedded,   // SDK spawns copilot via stdio (default, dies with app)
    Persistent, // App spawns detached copilot server; survives app restarts
    Remote      // Connect to a remote server via URL (e.g. DevTunnel)
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

    [JsonIgnore]
    public string CliUrl => Mode == ConnectionMode.Remote && !string.IsNullOrEmpty(RemoteUrl)
        ? RemoteUrl
        : $"{Host}:{Port}";

    private static readonly string SettingsPath = Path.Combine(
        GetCopilotDir(), "autopilot-settings.json");

    private static string GetCopilotDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(home, ".copilot");
    }

    public static ConnectionSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<ConnectionSettings>(json) ?? new();
            }
        }
        catch { }
        return new();
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
