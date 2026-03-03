namespace PolyPilot.Models;

/// <summary>
/// Describes a single setting entry for the data-driven settings UI.
/// The Settings page renders controls dynamically from these descriptors.
/// </summary>
public class SettingDescriptor
{
    /// <summary>Unique dot-notation ID, e.g. "connection.mode" or "ui.theme"</summary>
    public required string Id { get; init; }

    /// <summary>Display label</summary>
    public required string Label { get; init; }

    /// <summary>Help text shown below the control</summary>
    public string? Description { get; init; }

    /// <summary>Top-level category for grouping (Connection, CLI, UI, Developer)</summary>
    public required string Category { get; init; }

    /// <summary>Optional sub-heading within the category</summary>
    public string? Section { get; init; }

    /// <summary>Control type to render</summary>
    public required SettingType Type { get; init; }

    /// <summary>Space-separated keywords for search filtering</summary>
    public string SearchKeywords { get; init; } = "";

    /// <summary>Sort order within category (lower = higher)</summary>
    public int Order { get; init; }

    /// <summary>For Enum/CardEnum types: the enum options as (value, label) pairs</summary>
    public SettingOption[]? Options { get; init; }

    /// <summary>For Int type: minimum value</summary>
    public int? Min { get; init; }

    /// <summary>For Int type: maximum value</summary>
    public int? Max { get; init; }

    /// <summary>Whether the control should be masked (password fields)</summary>
    public bool IsSecret { get; init; }

    /// <summary>Predicate: should this setting be visible given current state?</summary>
    public Func<SettingsContext, bool>? IsVisible { get; init; }

    /// <summary>Read the current value from settings/state</summary>
    public Func<SettingsContext, object?>? GetValue { get; init; }

    /// <summary>Write a new value to settings/state</summary>
    public Action<SettingsContext, object?>? SetValue { get; init; }

    /// <summary>Custom async action (for action buttons). Return status message or null.</summary>
    public Func<SettingsContext, Task<string?>>? OnAction { get; init; }

    /// <summary>Label for the action button</summary>
    public string? ActionLabel { get; init; }
}

public enum SettingType
{
    Bool,        // Toggle switch
    String,      // Text input
    Int,         // Number input with +/- or slider
    Enum,        // Dropdown selector
    CardEnum,    // Visual card selector (theme, layout, mode)
    Action,      // Button that triggers an operation
    Custom       // Rendered by the page itself (complex interactive sections)
}

public record SettingOption(string Value, string Label, string? PreviewClass = null);

/// <summary>
/// Context passed to setting descriptors for reading/writing values.
/// Wraps ConnectionSettings + external state that settings may depend on.
/// </summary>
public class SettingsContext
{
    public required ConnectionSettings Settings { get; init; }
    public int FontSize { get; set; } = 20;
    public bool ServerAlive { get; set; }
    public bool IsDesktop { get; set; } = PlatformHelper.IsDesktop;
    public bool IsMobile { get; set; } = PlatformHelper.IsMobile;
    public ConnectionMode InitialMode { get; set; }
    public CliSourceMode InitialCliSource { get; set; }
}
