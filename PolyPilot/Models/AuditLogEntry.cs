using System.Text.Json;
using System.Text.Json.Serialization;

namespace PolyPilot.Models;

/// <summary>
/// A single structured audit log entry. Serialized as one JSON line in the audit JSONL file.
/// All sensitive data (tokens, keys, passwords) must be sanitized before creating an entry.
/// </summary>
public class AuditLogEntry
{
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("event_type")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    [JsonPropertyName("details")]
    public Dictionary<string, object?> Details { get; set; } = new();

    public string ToJsonLine()
    {
        return JsonSerializer.Serialize(this, AuditLogJsonContext.Default.AuditLogEntry);
    }
}

/// <summary>
/// Event type constants for audit log entries.
/// </summary>
public static class AuditEventTypes
{
    public const string CodespaceConnectionInitiated = "CODESPACE_CONNECTION_INITIATED";
    public const string CodespaceSshHandshakeSuccess = "CODESPACE_SSH_HANDSHAKE_SUCCESS";
    public const string CodespaceSshHandshakeFailure = "CODESPACE_SSH_HANDSHAKE_FAILURE";
    public const string CopilotHeadlessStart = "COPILOT_HEADLESS_START";
    public const string CopilotHeadlessFailure = "COPILOT_HEADLESS_FAILURE";
    public const string CopilotHeadlessIndeterminate = "COPILOT_HEADLESS_INDETERMINATE";
    public const string DevtunnelTokenAcquired = "DEVTUNNEL_TOKEN_ACQUIRED";
    public const string DevtunnelConnectionEstablished = "DEVTUNNEL_CONNECTION_ESTABLISHED";
    public const string DevtunnelConnectionFailed = "DEVTUNNEL_CONNECTION_FAILED";
    public const string SessionClosed = "SESSION_CLOSED";
    public const string SessionError = "SESSION_ERROR";
}

// Source-generated JSON context for trimmer-safe serialization.
// Must include all value types that appear in Details dictionary.
[JsonSerializable(typeof(AuditLogEntry))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class AuditLogJsonContext : JsonSerializerContext { }
