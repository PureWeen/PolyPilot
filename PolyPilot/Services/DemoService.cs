using System.Collections.Concurrent;
using PolyPilot.Models;

namespace PolyPilot.Services;

/// <summary>
/// Simulates chat responses for Demo mode — no network connection needed.
/// Fires the same events as CopilotService for UI testing.
/// </summary>
public class DemoService : IDemoService
{
    private readonly ConcurrentDictionary<string, AgentSessionInfo> _sessions = new();
    private string? _activeSessionName;
    private int _sessionCounter;

    public event Action? OnStateChanged;
    public event Action<string, string>? OnContentReceived;
    public event Action<string, string, string>? OnToolStarted;
    public event Action<string, string, string, bool>? OnToolCompleted;
    public event Action<string, string>? OnIntentChanged;
    public event Action<string>? OnTurnStart;
    public event Action<string>? OnTurnEnd;

    public IReadOnlyDictionary<string, AgentSessionInfo> Sessions => _sessions;
    public string? ActiveSessionName => _activeSessionName;

    public AgentSessionInfo CreateSession(string name, string? model = null)
    {
        var info = new AgentSessionInfo
        {
            Name = name,
            Model = model ?? "demo-model",
            CreatedAt = DateTime.Now,
            SessionId = $"demo-{Interlocked.Increment(ref _sessionCounter)}"
        };
        _sessions[name] = info;
        _activeSessionName ??= name;
        OnStateChanged?.Invoke();
        return info;
    }

    public bool TryGetSession(string name, out AgentSessionInfo? info) =>
        _sessions.TryGetValue(name, out info);

    public void SetActiveSession(string name)
    {
        if (_sessions.ContainsKey(name))
            _activeSessionName = name;
    }

    public async Task SimulateResponseAsync(string sessionName, string userPrompt, SynchronizationContext? syncContext, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(sessionName, out var session)) return;

        session.IsProcessing = true;
        session.ProcessingStartedAt = DateTime.Now;
        Post(syncContext, () => OnStateChanged?.Invoke());
        Post(syncContext, () => OnTurnStart?.Invoke(sessionName));

        // Small pause before responding
        await Task.Delay(300, ct);

        // Decide whether to simulate a tool call (~30% chance, skip for short messages)
        var shouldSimulateTool = userPrompt.Length > 20 && Random.Shared.NextDouble() < 0.3;

        if (shouldSimulateTool)
        {
            var toolName = PickRandomTool();
            var callId = $"call_{Guid.NewGuid():N}"[..12];

            Post(syncContext, () => OnIntentChanged?.Invoke(sessionName, $"Using {toolName}..."));
            Post(syncContext, () => OnToolStarted?.Invoke(sessionName, toolName, callId));

            await Task.Delay(Random.Shared.Next(500, 1500), ct);

            Post(syncContext, () => OnToolCompleted?.Invoke(sessionName, callId, "Done", true));

            await Task.Delay(200, ct);
        }

        Post(syncContext, () => OnIntentChanged?.Invoke(sessionName, "Responding..."));

        // Stream the response word by word
        var response = GenerateResponse(userPrompt);
        var words = response.Split(' ');
        bool first = true;

        foreach (var word in words)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = (first ? "" : " ") + word;
            first = false;
            Post(syncContext, () => OnContentReceived?.Invoke(sessionName, chunk));
            await Task.Delay(Random.Shared.Next(20, 80), ct);
        }

        session.IsProcessing = false;
        session.MessageCount = session.History.Count;

        Post(syncContext, () => OnTurnEnd?.Invoke(sessionName));
        Post(syncContext, () => OnStateChanged?.Invoke());
    }

    private static void Post(SynchronizationContext? ctx, Action action)
    {
        if (ctx != null) ctx.Post(_ => action(), null);
        else action();
    }

    private static string PickRandomTool() =>
        Random.Shared.Next(5) switch
        {
            0 => "bash",
            1 => "grep",
            2 => "view",
            3 => "edit",
            _ => "glob"
        };

    private static string GenerateResponse(string prompt)
    {
        var lower = prompt.ToLowerInvariant();

        if (lower.Contains("hello") || lower.Contains("hi ") || lower.StartsWith("hi"))
            return "Hey there! 👋 This is a demo response. The chat UI is working — you can test sending messages, scrolling, and all the visual elements without needing a real connection.";

        if (lower.Contains("help"))
            return "I'm running in **Demo mode**, so I'm just simulating responses. In a real session I'd have access to tools like `bash`, `grep`, `view`, and `edit` to help you with coding tasks. Switch to Remote mode and scan a QR code to connect to a real Copilot instance.";

        if (lower.Contains("code") || lower.Contains("function") || lower.Contains("implement"))
            return "Here's a simulated code snippet:\n\n```csharp\npublic class Example\n{\n    public string Greet(string name)\n    {\n        return $\"Hello, {name}!\";\n    }\n}\n```\n\nThis is just a demo — in a real session I'd analyze your actual codebase and generate relevant code.";

        if (lower.Contains("test"))
            return "Demo mode is working! ✅ You can verify:\n- Message sending and receiving\n- Streaming text appearance\n- Tool call indicators\n- Markdown rendering\n- Scroll behavior\n\nEverything runs locally with no network needed.";

        var responses = new[]
        {
            $"Got your message: \"{Truncate(prompt, 50)}\". This is a simulated response in Demo mode. The full chat pipeline is working — messages flow through the same rendering path as real responses.",
            $"Demo mode echo: I received \"{Truncate(prompt, 40)}\". In a real session, I'd process this with the Copilot SDK and use tools to help. The UI you're seeing is identical to the real experience.",
            $"Thanks for testing! Your message was {prompt.Length} characters long. Demo mode simulates streaming responses with realistic timing so you can verify the chat experience works correctly on this device."
        };

        return responses[Random.Shared.Next(responses.Length)];
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
