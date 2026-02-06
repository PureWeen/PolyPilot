using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoPilot.App.Models;

public enum ConnectionMode
{
    Embedded,   // SDK spawns copilot via stdio (default, dies with app)
    Persistent  // App spawns detached copilot server; survives app restarts
}

public class ConnectionSettings
{
    public ConnectionMode Mode { get; set; } = ConnectionMode.Embedded;
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 4321;
    public bool AutoStartServer { get; set; } = false;

    [JsonIgnore]
    public string CliUrl => $"{Host}:{Port}";

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".copilot", "autopilot-settings.json");

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
