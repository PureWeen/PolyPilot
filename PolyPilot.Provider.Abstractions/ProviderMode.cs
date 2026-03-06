namespace PolyPilot.Provider;

/// <summary>
/// Describes an interaction mode that the provider supports.
/// Modes appear in the group's dropdown, replacing the built-in
/// Broadcast/Sequential/Orchestrate/Reflect options.
/// </summary>
public class ProviderMode
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string Icon { get; init; } = "🔌";
    public string? Description { get; init; }
}
