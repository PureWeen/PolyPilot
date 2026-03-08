namespace PolyPilot.Provider;

public class ProviderMember
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Role { get; init; }
    public string Icon { get; init; } = "👤";
    public bool IsActive { get; init; }
    public string? StatusText { get; init; }
}
