using PolyPilot.Models;

namespace PolyPilot.Services;

/// <summary>
/// Interface for chat message persistence.
/// </summary>
public interface IChatDatabase
{
    Task<int> AddMessageAsync(string sessionId, ChatMessage message);
    Task BulkInsertAsync(string sessionId, List<ChatMessage> messages);
    Task UpdateToolCompleteAsync(string sessionId, string toolCallId, string result, bool success);
    Task UpdateReasoningContentAsync(string sessionId, string reasoningId, string content, bool isComplete);
}
