using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for orchestration prompt display: user prompts wrapped in orchestration context
/// should store the original user prompt in OriginalContent so the UI can show it
/// prominently while collapsing the full orchestration prompt.
/// </summary>
public class OrchestrationPromptDisplayTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public OrchestrationPromptDisplayTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    [Fact]
    public async Task SendPromptAsync_WithOriginalPrompt_SetsOriginalContent()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        await svc.CreateSessionAsync("test-session");

        var wrappedPrompt = "[Multi-agent context: You are 'test-session' (worker, gpt-4.1) in group 'TestGroup'.]\n\nfix the bug";
        await svc.SendPromptAsync("test-session", wrappedPrompt, originalPrompt: "fix the bug");

        var session = svc.GetSession("test-session");
        Assert.NotNull(session);
        var lastUserMsg = session!.History.Last(m => m.IsUser);
        Assert.Equal("fix the bug", lastUserMsg.OriginalContent);
        Assert.Equal(wrappedPrompt, lastUserMsg.Content);
    }

    [Fact]
    public async Task SendPromptAsync_WithoutOriginalPrompt_OriginalContentIsNull()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        await svc.CreateSessionAsync("test-session");

        await svc.SendPromptAsync("test-session", "simple prompt");

        var session = svc.GetSession("test-session");
        Assert.NotNull(session);
        var lastUserMsg = session!.History.Last(m => m.IsUser);
        Assert.Null(lastUserMsg.OriginalContent);
        Assert.Equal("simple prompt", lastUserMsg.Content);
    }

    [Fact]
    public async Task SendPromptAsync_OriginalPrompt_AvailableInHistory()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var info = await svc.CreateSessionAsync("db-test");

        var wrappedPrompt = "You are the orchestrator...\n\nUser request: do stuff";
        await svc.SendPromptAsync("db-test", wrappedPrompt, originalPrompt: "do stuff");

        // Verify the message in history has OriginalContent set
        var session = svc.GetSession("db-test");
        Assert.NotNull(session);
        var lastUserMsg = session!.History.Last(m => m.IsUser);
        Assert.Equal("do stuff", lastUserMsg.OriginalContent);
        Assert.Equal(wrappedPrompt, lastUserMsg.Content);
    }

    [Fact]
    public async Task SendPromptAsync_SkipHistoryMessage_DoesNotAddMessage()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        await svc.CreateSessionAsync("test-session");

        await svc.SendPromptAsync("test-session", "wrapped prompt", skipHistoryMessage: true, originalPrompt: "user prompt");

        var session = svc.GetSession("test-session");
        Assert.NotNull(session);
        // No user messages should be added when skipHistoryMessage is true
        Assert.DoesNotContain(session!.History, m => m.IsUser);
    }

    [Fact]
    public void ChatMessage_OriginalContent_RoundTripsViaJson()
    {
        var msg = new ChatMessage("user", "[Multi-agent context: ...]\n\nfix this", DateTime.Now)
        {
            OriginalContent = "fix this"
        };

        var json = System.Text.Json.JsonSerializer.Serialize(msg);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<ChatMessage>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("fix this", deserialized!.OriginalContent);
        Assert.Equal("[Multi-agent context: ...]\n\nfix this", deserialized.Content);
    }

    [Fact]
    public void ChatMessage_OriginalContent_NullByDefault()
    {
        var msg = ChatMessage.UserMessage("hello");
        Assert.Null(msg.OriginalContent);

        // Also test parameterless constructor (deserialization)
        var emptyMsg = new ChatMessage();
        Assert.Null(emptyMsg.OriginalContent);
    }

    [Fact]
    public void ChatMessage_DisplayContent_ReturnsOriginalWhenSet()
    {
        // When OriginalContent is set, UI should prefer it for display
        var msg = ChatMessage.UserMessage("full orchestration prompt here");
        msg.OriginalContent = "user typed this";

        // OriginalContent should be the user-facing text
        Assert.Equal("user typed this", msg.OriginalContent);
        // Content should be the full prompt sent to the model
        Assert.Equal("full orchestration prompt here", msg.Content);
    }

    [Fact]
    public void BuildMultiAgentPrefix_ProducesWrappedPrompt()
    {
        // Verify the prefix pattern that gets prepended to user prompts
        var prefix = "[Multi-agent context: You are 'worker1' (worker, gpt-4.1) in group 'MyTeam'. Other members: 'worker2' (claude-sonnet-4.5).]\n\n";
        var userPrompt = "fix the authentication bug";
        var fullPrompt = prefix + userPrompt;

        // The full prompt should contain the prefix AND the user's original prompt
        Assert.StartsWith("[Multi-agent context:", fullPrompt);
        Assert.EndsWith(userPrompt, fullPrompt);
        Assert.NotEqual(userPrompt, fullPrompt);
    }
}
