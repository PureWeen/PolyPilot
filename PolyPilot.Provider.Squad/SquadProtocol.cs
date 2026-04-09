using System.Text.Json;
using System.Text.Json.Serialization;

namespace PolyPilot.Provider.Squad;

/// <summary>
/// C# models for Squad's Remote Control (RC) wire protocol v1.0.
/// Maps to packages/squad-sdk/src/remote/protocol.ts in bradygaster/squad.
/// </summary>
public static class SquadProtocol
{
    public const string ProtocolVersion = "1.0";

    // Default JSON options for RC protocol serialization
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };
}

// ─── Server → Client Events ─────────────────────────────────

/// <summary>Base class for all RC server events.</summary>
public class RCEvent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
}

/// <summary>Session metadata sent on initial connection.</summary>
public class RCStatusEvent : RCEvent
{
    public string Version { get; set; } = "";
    public string Repo { get; set; } = "";
    public string Branch { get; set; } = "";
    public string Machine { get; set; } = "";
    public string SquadDir { get; set; } = "";
    public string ConnectedAt { get; set; } = "";
}

/// <summary>Full conversation history sent on connection.</summary>
public class RCHistoryEvent : RCEvent
{
    public List<RCMessage> Messages { get; set; } = [];
}

/// <summary>Streaming content delta from an agent.</summary>
public class RCDeltaEvent : RCEvent
{
    public string SessionId { get; set; } = "";
    public string AgentName { get; set; } = "";
    public string Content { get; set; } = "";
}

/// <summary>Complete message (streaming finished).</summary>
public class RCCompleteEvent : RCEvent
{
    public RCMessage Message { get; set; } = new();
}

/// <summary>Agent roster with live status.</summary>
public class RCAgentsEvent : RCEvent
{
    public List<RCAgent> Agents { get; set; } = [];
}

/// <summary>Tool call visibility.</summary>
public class RCToolCallEvent : RCEvent
{
    public string AgentName { get; set; } = "";
    public string Tool { get; set; } = "";
    public JsonElement? Args { get; set; }
    public string Status { get; set; } = "";
}

/// <summary>Permission request from an agent.</summary>
public class RCPermissionEvent : RCEvent
{
    public string Id { get; set; } = "";
    public string AgentName { get; set; } = "";
    public string Tool { get; set; } = "";
    public JsonElement? Args { get; set; }
    public string Description { get; set; } = "";
}

/// <summary>Token usage update.</summary>
public class RCUsageEvent : RCEvent
{
    public string Model { get; set; } = "";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public double Cost { get; set; }
}

/// <summary>Error notification.</summary>
public class RCErrorEvent : RCEvent
{
    public string Message { get; set; } = "";
    public string? AgentName { get; set; }
}

/// <summary>Pong response.</summary>
public class RCPongEvent : RCEvent
{
    public string Timestamp { get; set; } = "";
}

// ─── Client → Server Commands ────────────────────────────────

/// <summary>Natural language prompt (coordinator routes).</summary>
public class RCPromptCommand
{
    [JsonPropertyName("type")]
    public string Type { get; } = "prompt";
    public string Text { get; set; } = "";
}

/// <summary>Direct message to a specific agent.</summary>
public class RCDirectCommand
{
    [JsonPropertyName("type")]
    public string Type { get; } = "direct";
    public string AgentName { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>Slash command.</summary>
public class RCSlashCommand
{
    [JsonPropertyName("type")]
    public string Type { get; } = "command";
    public string Name { get; set; } = "";
    public string[]? Args { get; set; }
}

/// <summary>Permission response (approve/deny).</summary>
public class RCPermissionResponse
{
    [JsonPropertyName("type")]
    public string Type { get; } = "permission_response";
    public string Id { get; set; } = "";
    public bool Approved { get; set; }
}

/// <summary>Keepalive ping.</summary>
public class RCPingCommand
{
    [JsonPropertyName("type")]
    public string Type { get; } = "ping";
}

// ─── Shared Types ────────────────────────────────────────────

/// <summary>Message in the conversation history.</summary>
public class RCMessage
{
    public string Id { get; set; } = "";
    public string Role { get; set; } = "";
    public string? AgentName { get; set; }
    public string Content { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public List<RCToolCallSummary>? ToolCalls { get; set; }
}

/// <summary>Summary of a tool call within a message.</summary>
public class RCToolCallSummary
{
    public string Tool { get; set; } = "";
    public JsonElement? Args { get; set; }
    public string Status { get; set; } = "";
    public string? Result { get; set; }
}

/// <summary>Agent info for roster display.</summary>
public class RCAgent
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string Status { get; set; } = "";
    public string? CharterPath { get; set; }
}
