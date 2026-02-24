using System.Collections.Concurrent;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PolyPilot.Services.AI;

/// <summary>
/// Thin adapter over ChatClientAgent for local AppChat sessions.
/// Each session gets its own AgentSession for conversation history.
/// </summary>
public class DirectLocalChatService
{
    private readonly IChatClient _chatClient;
    private readonly CopilotService _copilotService;
    private readonly ILogger<DirectLocalChatService> _logger;
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new();
    private ChatClientAgent? _agent;

    private const string SystemPrompt = """
        You are PolyPilot Assistant, a helpful on-device AI that answers questions about the user's 
        active Copilot sessions, queued messages, and session organization. You have access to tools 
        that let you read app state. Keep responses concise — you are running on a local model with 
        limited context. If you don't know something or a tool doesn't return useful data, say so 
        briefly rather than guessing.
        """;

    public DirectLocalChatService(
        [FromKeyedServices("local")] IChatClient chatClient,
        CopilotService copilotService,
        ILogger<DirectLocalChatService> logger)
    {
        _chatClient = chatClient;
        _copilotService = copilotService;
        _logger = logger;
    }

    private ChatClientAgent GetOrCreateAgent()
    {
        if (_agent is not null) return _agent;

        var tools = AppChatTools.Create(_copilotService);
        _agent = new ChatClientAgent(
            _chatClient,
            instructions: SystemPrompt,
            name: "PolyPilotAssistant",
            tools: tools.Cast<AITool>().ToList());
        return _agent;
    }

    private async Task<AgentSession> GetOrCreateSessionAsync(string sessionName)
    {
        if (_sessions.TryGetValue(sessionName, out var existing))
            return existing;

        var agent = GetOrCreateAgent();
        var session = await agent.CreateSessionAsync();
        _sessions[sessionName] = session;
        return session;
    }

    /// <summary>
    /// Send a prompt to the local model and stream the response back via callbacks.
    /// </summary>
    public async Task SendPromptStreamingAsync(
        string sessionName,
        string prompt,
        Action<string> onDelta,
        Action<string> onComplete,
        Action<string>? onError = null,
        CancellationToken cancellationToken = default)
    {
        var agent = GetOrCreateAgent();
        var session = await GetOrCreateSessionAsync(sessionName);

        var fullResponse = new StringBuilder();

        try
        {
            await foreach (var update in agent.RunStreamingAsync(
                prompt,
                session,
                cancellationToken: cancellationToken))
            {
                var text = update.Text;
                if (!string.IsNullOrEmpty(text))
                {
                    fullResponse.Append(text);
                    onDelta(text);
                }
            }

            onComplete(fullResponse.ToString());
        }
        catch (OperationCanceledException)
        {
            onComplete(fullResponse.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in AppChat session '{SessionName}'", sessionName);
            onError?.Invoke(ex.Message);
        }
    }

    /// <summary>
    /// Remove the session for a closed AppChat.
    /// </summary>
    public void RemoveSession(string sessionName)
    {
        _sessions.TryRemove(sessionName, out _);
    }
}
