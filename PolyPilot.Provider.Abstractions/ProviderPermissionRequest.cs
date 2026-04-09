namespace PolyPilot.Provider;

/// <summary>
/// A permission request from a provider's agent, e.g., Squad's RCPermissionEvent.
/// The host UI should show this to the user for approval/denial.
/// </summary>
public class ProviderPermissionRequest
{
    /// <summary>Unique identifier for this permission request.</summary>
    public required string Id { get; init; }

    /// <summary>ID of the agent requesting permission.</summary>
    public string? AgentId { get; init; }

    /// <summary>Name of the agent requesting permission (for display).</summary>
    public string? AgentName { get; init; }

    /// <summary>The tool or action the agent wants to execute.</summary>
    public required string ToolName { get; init; }

    /// <summary>Human-readable description of what the tool will do.</summary>
    public string? Description { get; init; }

    /// <summary>The arguments/input the tool will receive (for display).</summary>
    public string? Arguments { get; init; }

    /// <summary>When the request was received.</summary>
    public DateTime RequestedAt { get; init; } = DateTime.UtcNow;
}
