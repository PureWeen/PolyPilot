namespace PolyPilot.Provider;

public class ProviderAction
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string? Tooltip { get; init; }
}
