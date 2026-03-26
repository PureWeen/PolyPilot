using System.Text;
using PolyPilot.Models;

namespace PolyPilot.Services;

public partial class CopilotService
{
    /// <summary>
    /// Creates a new session pre-filled with context from an existing session's conversation history.
    /// Returns (newSessionName, transcript) so the caller can set the draft in the chat input.
    /// </summary>
    public async Task<(string NewSessionName, string Transcript)> ContinueInNewSessionAsync(
        string sourceSessionName, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(sourceSessionName, out var sourceState))
            throw new InvalidOperationException($"Session '{sourceSessionName}' not found");

        var info = sourceState.Info;
        List<ChatMessage> history;
        lock (info.HistoryLock)
        {
            history = info.History.ToList();
        }

        var transcript = BuildContinuationTranscript(history, sourceSessionName, info.SessionId);
        var newName = GenerateContinuationName(sourceSessionName);

        // Inherit model, working directory, and group from source session
        var groupId = Organization.Sessions.FirstOrDefault(m => m.SessionName == sourceSessionName)?.GroupId;
        var newSession = await CreateSessionAsync(newName, info.Model, info.WorkingDirectory, ct, groupId);

        return (newName, transcript);
    }

    /// <summary>
    /// Builds a markdown transcript from conversation history, suitable for pre-filling
    /// a new session's chat input. Caps at ~6000 chars, trimming oldest turns first.
    /// </summary>
    internal static string BuildContinuationTranscript(
        List<ChatMessage> history, string sourceSessionName, string? sessionId)
    {
        const int maxChars = 6000;
        const int assistantTruncateLen = 400;

        // Build per-turn summaries
        var turns = new List<string>();
        foreach (var msg in history)
        {
            switch (msg.MessageType)
            {
                case ChatMessageType.User:
                    turns.Add($"**User:** {msg.Content}");
                    break;

                case ChatMessageType.Assistant:
                    var content = msg.Content;
                    if (content.Length > assistantTruncateLen)
                        content = content[..assistantTruncateLen] + "…";
                    turns.Add($"**Assistant:** {content}");
                    break;

                case ChatMessageType.ToolCall:
                    var status = msg.IsComplete ? (msg.IsSuccess ? "✅" : "❌") : "⏳";
                    var toolDisplay = msg.ToolName ?? "unknown";
                    turns.Add($"  🔧 {toolDisplay} {status}");
                    break;

                case ChatMessageType.Error:
                    turns.Add($"  ⚠️ Error: {Truncate(msg.Content, 150)}");
                    break;

                case ChatMessageType.System:
                    // Skip system messages — not useful for context
                    break;

                case ChatMessageType.Image:
                    turns.Add($"  🖼️ Image{(string.IsNullOrEmpty(msg.Caption) ? "" : $": {msg.Caption}")}");
                    break;

                default:
                    // Reasoning, ShellOutput, Diff, Reflection — skip for brevity
                    break;
            }
        }

        // Trim oldest turns to fit within budget
        while (turns.Count > 2 && EstimateLength(turns, sourceSessionName, sessionId) > maxChars)
        {
            turns.RemoveAt(0);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"I'm continuing work from the session \"{sourceSessionName}\". Here's the conversation context:");
        sb.AppendLine();
        sb.AppendLine("---");
        foreach (var turn in turns)
        {
            sb.AppendLine(turn);
        }
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("Please read the above context and continue where we left off. If you need more detail, the full session log is available at:");

        if (!string.IsNullOrEmpty(sessionId))
        {
            var eventsPath = Path.Combine("~/.copilot/session-state", sessionId, "events.jsonl");
            sb.AppendLine($"`{eventsPath}`");
        }
        else
        {
            sb.AppendLine("(session ID not available — check ~/.copilot/session-state/ for recent sessions)");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates a continuation session name, stripping existing " (cont'd)" suffix to prevent nesting.
    /// </summary>
    internal static string GenerateContinuationName(string sourceName)
    {
        const string suffix = " (cont'd)";
        var baseName = sourceName.EndsWith(suffix) ? sourceName[..^suffix.Length] : sourceName;
        return baseName + suffix;
    }

    private static int EstimateLength(List<string> turns, string sourceName, string? sessionId)
    {
        var turnLen = 0;
        foreach (var t in turns) turnLen += t.Length + 1;
        return 120 + turnLen + 200;
    }
}
