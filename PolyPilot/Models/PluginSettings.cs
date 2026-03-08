using System.Text.Json.Serialization;

namespace PolyPilot.Models;

/// <summary>
/// Plugin configuration stored in settings.json.
/// </summary>
public class PluginSettings
{
    public List<EnabledPlugin> Enabled { get; set; } = new();
    public List<DisabledPlugin> Disabled { get; set; } = new();
}

/// <summary>
/// A plugin that the user has explicitly approved for loading.
/// Path is relative to the plugins directory for portability.
/// </summary>
public class EnabledPlugin
{
    public string Path { get; set; } = "";
    public string Hash { get; set; } = "";
    public DateTime EnabledAt { get; set; }
    public string DisplayName { get; set; } = "";
}

/// <summary>
/// A plugin that was disabled or needs re-approval.
/// </summary>
public class DisabledPlugin
{
    public string Path { get; set; } = "";
    public string? Reason { get; set; }
    public string? LastKnownHash { get; set; }
}
