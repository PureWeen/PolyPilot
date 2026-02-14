using System.Text.Json;
using System.Text.Json.Serialization;

namespace PolyPilot.Models;

/// <summary>
/// JSON messages for the remote viewer WebSocket protocol.
/// Server pushes state/events to clients; clients send commands back.
/// </summary>

// --- Base envelope ---

public class BridgeMessage
{
    public const int LatestProtocolVersion = 2;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; set; }

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = 1;

    [JsonPropertyName("fiestaId")]
    public string? FiestaId { get; set; }

    public static BridgeMessage Create<T>(string type, T payload, string? fiestaId = null)
    {
        var json = JsonSerializer.SerializeToElement(payload, BridgeJson.Options);
        return new BridgeMessage { Type = type, Payload = json, FiestaId = fiestaId, ProtocolVersion = LatestProtocolVersion };
    }

    public T? GetPayload<T>() =>
        Payload.HasValue ? JsonSerializer.Deserialize<T>(Payload.Value, BridgeJson.Options) : default;

    public string Serialize() => JsonSerializer.Serialize(this, BridgeJson.Options);

    public static BridgeMessage? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<BridgeMessage>(json, BridgeJson.Options); }
        catch { return null; }
    }
}

public static class BridgeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };
}

// --- Message type constants ---

public static class BridgeMessageTypes
{
    // Server → Client
    public const string SessionsList = "sessions_list";
    public const string SessionHistory = "session_history";
    public const string PersistedSessionsList = "persisted_sessions";
    public const string OrganizationState = "organization_state";
    public const string ContentDelta = "content_delta";
    public const string ToolStarted = "tool_started";
    public const string ToolCompleted = "tool_completed";
    public const string ReasoningDelta = "reasoning_delta";
    public const string ReasoningComplete = "reasoning_complete";
    public const string IntentChanged = "intent_changed";
    public const string UsageInfo = "usage_info";
    public const string TurnStart = "turn_start";
    public const string TurnEnd = "turn_end";
    public const string SessionComplete = "session_complete";
    public const string ErrorEvent = "error";
    public const string FiestaJoinStatus = "fiesta_join_status";
    public const string FiestaDispatchResult = "fiesta_dispatch_result";
    public const string FiestaSessionCommandResult = "fiesta_session_command_result";

    // Client → Server
    public const string GetSessions = "get_sessions";
    public const string GetHistory = "get_history";
    public const string GetPersistedSessions = "get_persisted_sessions";
    public const string SendMessage = "send_message";
    public const string CreateSession = "create_session";
    public const string ResumeSession = "resume_session";
    public const string SwitchSession = "switch_session";
    public const string QueueMessage = "queue_message";
    public const string CloseSession = "close_session";
    public const string AbortSession = "abort_session";
    public const string OrganizationCommand = "organization_command";
    public const string ListDirectories = "list_directories";
    public const string FiestaJoinRequest = "fiesta_join_request";
    public const string FiestaDispatchPrompt = "fiesta_dispatch_prompt";
    public const string FiestaSessionCommand = "fiesta_session_command";

    // Server → Client (response)
    public const string DirectoriesList = "directories_list";
}

// --- Server → Client payloads ---

public class SessionsListPayload
{
    public List<SessionSummary> Sessions { get; set; } = new();
    public string? ActiveSession { get; set; }
    public string? GitHubAvatarUrl { get; set; }
    public string? GitHubLogin { get; set; }
}

public class SessionSummary
{
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public int MessageCount { get; set; }
    public bool IsProcessing { get; set; }
    public string? SessionId { get; set; }
    public string? WorkingDirectory { get; set; }
    public int QueueCount { get; set; }
}

public class SessionHistoryPayload
{
    public string SessionName { get; set; } = "";
    public List<ChatMessage> Messages { get; set; } = new();
}

public class ContentDeltaPayload
{
    public string SessionName { get; set; } = "";
    public string Content { get; set; } = "";
}

public class ToolStartedPayload
{
    public string SessionName { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string CallId { get; set; } = "";
}

public class ToolCompletedPayload
{
    public string SessionName { get; set; } = "";
    public string CallId { get; set; } = "";
    public string Result { get; set; } = "";
    public bool Success { get; set; }
}

public class ReasoningDeltaPayload
{
    public string SessionName { get; set; } = "";
    public string ReasoningId { get; set; } = "";
    public string Content { get; set; } = "";
}

public class ReasoningCompletePayload
{
    public string SessionName { get; set; } = "";
    public string ReasoningId { get; set; } = "";
}

public class SessionNamePayload
{
    public string SessionName { get; set; } = "";
}

public class IntentChangedPayload
{
    public string SessionName { get; set; } = "";
    public string Intent { get; set; } = "";
}

public class UsageInfoPayload
{
    public string SessionName { get; set; } = "";
    public string? Model { get; set; }
    public int? CurrentTokens { get; set; }
    public int? TokenLimit { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
}

public class SessionCompletePayload
{
    public string SessionName { get; set; } = "";
    public string Summary { get; set; } = "";
}

public class ErrorPayload
{
    public string SessionName { get; set; } = "";
    public string Error { get; set; } = "";
}

// --- Client → Server payloads ---

public class GetHistoryPayload
{
    public string SessionName { get; set; } = "";
}

public class SendMessagePayload
{
    public string SessionName { get; set; } = "";
    public string Message { get; set; } = "";
}

public class CreateSessionPayload
{
    public string Name { get; set; } = "";
    public string? Model { get; set; }
    public string? WorkingDirectory { get; set; }
}

public class SwitchSessionPayload
{
    public string SessionName { get; set; } = "";
}

public class QueueMessagePayload
{
    public string SessionName { get; set; } = "";
    public string Message { get; set; } = "";
}

public class PersistedSessionsPayload
{
    public List<PersistedSessionSummary> Sessions { get; set; } = new();
}

public class PersistedSessionSummary
{
    public string SessionId { get; set; } = "";
    public string? Title { get; set; }
    public string? Preview { get; set; }
    public string? WorkingDirectory { get; set; }
    public DateTime LastModified { get; set; }
}

public class ResumeSessionPayload
{
    public string SessionId { get; set; } = "";
    public string? DisplayName { get; set; }
}

// --- Organization bridge payloads ---

public class OrganizationCommandPayload
{
    public string Command { get; set; } = "";  // "pin", "unpin", "move", "create_group", "rename_group", "delete_group", "toggle_collapsed", "set_sort"
    public string? SessionName { get; set; }
    public string? GroupId { get; set; }
    public string? Name { get; set; }
    public string? SortMode { get; set; }
}

// --- Directory browsing payloads ---

public class ListDirectoriesPayload
{
    public string? Path { get; set; }
}

public class DirectoriesListPayload
{
    public string Path { get; set; } = "";
    public List<DirectoryEntry> Directories { get; set; } = new();
    public bool IsGitRepo { get; set; }
    public string? Error { get; set; }
}

public class DirectoryEntry
{
    public string Name { get; set; } = "";
    public bool IsGitRepo { get; set; }
}

// --- Fiesta payloads ---

public enum FiestaJoinState
{
    Pending,
    Approved,
    Rejected
}

public class FiestaJoinRequestPayload
{
    public string RequestId { get; set; } = "";
    public string FiestaId { get; set; } = "";
    public string OrganizerInstanceId { get; set; } = "";
    public string OrganizerMachineName { get; set; } = "";
    public string OrganizerTrustToken { get; set; } = "";
    public string JoinCode { get; set; } = "";
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
}

public class FiestaJoinStatusPayload
{
    public string RequestId { get; set; } = "";
    public string FiestaId { get; set; } = "";
    public FiestaJoinState Status { get; set; } = FiestaJoinState.Pending;
    public string WorkerInstanceId { get; set; } = "";
    public string WorkerMachineName { get; set; } = "";
    public string? Reason { get; set; }
}

public class FiestaDispatchPromptPayload
{
    public string RequestId { get; set; } = "";
    public string FiestaId { get; set; } = "";
    public string SessionName { get; set; } = "";
    public string Message { get; set; } = "";
    public bool CreateSessionIfMissing { get; set; } = true;
    public string? Model { get; set; }
    public string? WorkingDirectory { get; set; }
}

public class FiestaDispatchResultPayload
{
    public string RequestId { get; set; } = "";
    public string FiestaId { get; set; } = "";
    public string WorkerInstanceId { get; set; } = "";
    public string WorkerMachineName { get; set; } = "";
    public string SessionName { get; set; } = "";
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Summary { get; set; }
}

public class FiestaSessionCommandPayload
{
    public string RequestId { get; set; } = "";
    public string FiestaId { get; set; } = "";
    public string Command { get; set; } = ""; // create | close | switch
    public string SessionName { get; set; } = "";
    public string? Model { get; set; }
    public string? WorkingDirectory { get; set; }
}

public class FiestaSessionCommandResultPayload
{
    public string RequestId { get; set; } = "";
    public string FiestaId { get; set; } = "";
    public string WorkerInstanceId { get; set; } = "";
    public string WorkerMachineName { get; set; } = "";
    public string Command { get; set; } = "";
    public string SessionName { get; set; } = "";
    public bool Success { get; set; }
    public string? Error { get; set; }
}
