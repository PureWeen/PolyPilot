using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PolyPilot.Services.AI;

/// <summary>
/// Tools that the local AppChat agent can invoke to query and navigate app state.
/// Each method returns a JSON string summary for the SLM.
/// </summary>
public static class AppChatTools
{
    /// <summary>
    /// Creates the set of AIFunction tools for AppChat sessions.
    /// The CopilotService instance is captured by closure.
    /// </summary>
    public static IList<AIFunction> Create(CopilotService service)
    {
        return
        [
            AIFunctionFactory.Create(
                () => GetSessionList(service),
                nameof(GetSessionList),
                "Lists all active Copilot sessions with their name, model, message count, processing status, working directory, and git branch."),

            AIFunctionFactory.Create(
                () => GetRecentErrors(service),
                nameof(GetRecentErrors),
                "Returns the most recent error messages from all sessions, if any."),

            AIFunctionFactory.Create(
                (string sessionName) => SwitchToSession(service, sessionName),
                nameof(SwitchToSession),
                "Switches the UI to show the specified session. Use the exact session name from GetSessionList."),

            AIFunctionFactory.Create(
                (string query) => SearchSessionHistory(service, query),
                nameof(SearchSessionHistory),
                "Searches all session chat histories for messages containing the query text. Returns matching messages with session name, role, content snippet, and timestamp."),
        ];
    }

    private static string GetSessionList(CopilotService service)
    {
        var sessions = service.GetAllSessions().Select(s => new
        {
            s.Name,
            s.Model,
            s.MessageCount,
            s.IsProcessing,
            HasQueue = s.MessageQueue.Count > 0,
            s.WorkingDirectory,
            s.GitBranch
        });
        return JsonSerializer.Serialize(sessions);
    }

    private static string GetRecentErrors(CopilotService service)
    {
        var errors = service.GetAllSessions()
            .SelectMany(s => s.History
                .Where(m => m?.MessageType == Models.ChatMessageType.Error)
                .TakeLast(3)
                .Select(m => new { Session = s.Name, m.Content, m.Timestamp }))
            .OrderByDescending(e => e.Timestamp)
            .Take(10);
        return JsonSerializer.Serialize(errors);
    }

    private static string SwitchToSession(CopilotService service, string sessionName)
    {
        var session = service.GetAllSessions().FirstOrDefault(s =>
            s.Name.Equals(sessionName, StringComparison.OrdinalIgnoreCase));
        if (session == null)
            return JsonSerializer.Serialize(new { Error = $"Session '{sessionName}' not found." });

        service.SetActiveSession(session.Name);
        return JsonSerializer.Serialize(new { Success = true, SwitchedTo = session.Name });
    }

    private static string SearchSessionHistory(CopilotService service, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return JsonSerializer.Serialize(new { Error = "Query cannot be empty." });

        var results = service.GetAllSessions()
            .SelectMany(s => s.History
                .Where(m => m?.Content != null &&
                       m.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
                .TakeLast(5)
                .Select(m => new
                {
                    Session = s.Name,
                    m.Role,
                    Content = m.Content.Length > 200
                        ? m.Content[..200] + "..."
                        : m.Content,
                    m.Timestamp
                }))
            .OrderByDescending(r => r.Timestamp)
            .Take(15);
        return JsonSerializer.Serialize(results);
    }
}
