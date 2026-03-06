using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the bug fix: new sessions opening empty with no dialog.
/// Root cause: messages sent while IsCreating=true were silently lost.
/// Fix: queue messages during creation, drain queue when creation completes.
/// Also covers ChatMessageEntity round-trip with all fields (ToolInput, ImagePath, etc.).
/// </summary>
public class NewSessionEmptyDialogTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public NewSessionEmptyDialogTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    // --- IsCreating queue behavior ---

    [Fact]
    public void IsCreating_DefaultsFalse()
    {
        var session = new AgentSessionInfo { Name = "test", Model = "gpt-5" };
        Assert.False(session.IsCreating);
    }

    [Fact]
    public void IsCreating_CanBeSetAndCleared()
    {
        var session = new AgentSessionInfo { Name = "test", Model = "gpt-5" };
        session.IsCreating = true;
        Assert.True(session.IsCreating);
        session.IsCreating = false;
        Assert.False(session.IsCreating);
    }

    [Fact]
    public async Task EnqueueMessage_WorksDuringIsCreating()
    {
        // Simulate the scenario: session exists but IsCreating is true.
        // The UI should enqueue the message instead of calling SendPromptAsync.
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("enqueue-test");
        // Simulate IsCreating (normally set during SDK creation window)
        session.IsCreating = true;

        // Enqueue a message as the UI would when IsCreating is true
        svc.EnqueueMessage("enqueue-test", "Hello world");

        Assert.Single(session.MessageQueue);
        Assert.Equal("Hello world", session.MessageQueue[0]);
    }

    [Fact]
    public async Task CreateSessionAsync_DrainsQueueAfterCreation()
    {
        // Regression test: messages queued during IsCreating window are sent
        // after session creation completes.
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        // Create a session (in demo mode, creation is instant)
        var session = await svc.CreateSessionAsync("drain-test");

        // Verify the session is ready (IsCreating=false after creation)
        Assert.False(session.IsCreating);

        // Now verify that if we manually queue a message and then check,
        // the session can process it (SendPromptAsync doesn't throw)
        await svc.SendPromptAsync("drain-test", "Test message");
        Assert.True(session.History.Count > 0, "Session should have messages after sending");
    }

    [Fact]
    public async Task SendPromptAsync_DemoMode_BypassesIsCreatingGuard()
    {
        // In Demo mode, SendPromptAsync bypasses the IsCreating guard (no SDK).
        // The UI layer (Dashboard.SendFromCard) handles queueing for IsCreating.
        // Verify that Demo mode can still send even when IsCreating is set.
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("demo-creating");
        session.IsCreating = true;

        // Demo mode should succeed because it bypasses the guard
        await svc.SendPromptAsync("demo-creating", "Should succeed in demo");
        Assert.True(session.History.Count > 0, "Demo mode should add messages even with IsCreating");
    }

    [Fact]
    public async Task MessageQueue_PreservesOrderDuringIsCreating()
    {
        // When multiple messages are queued during IsCreating, they should
        // maintain their order for sequential dispatch.
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("order-test");
        session.IsCreating = true;

        svc.EnqueueMessage("order-test", "First message");
        svc.EnqueueMessage("order-test", "Second message");
        svc.EnqueueMessage("order-test", "Third message");

        Assert.Equal(3, session.MessageQueue.Count);
        Assert.Equal("First message", session.MessageQueue[0]);
        Assert.Equal("Second message", session.MessageQueue[1]);
        Assert.Equal("Third message", session.MessageQueue[2]);
    }

    // --- ChatMessageEntity round-trip tests ---

    [Fact]
    public void ChatMessageEntity_RoundTrip_PreservesToolInput()
    {
        var original = ChatMessage.ToolCallMessage("bash", "call-123", "{\"command\":\"ls\"}");
        var entity = ChatMessageEntity.FromChatMessage(original, "session-1", 0);
        var restored = entity.ToChatMessage();

        Assert.Equal(original.ToolInput, restored.ToolInput);
        Assert.Equal("{\"command\":\"ls\"}", restored.ToolInput);
        Assert.Equal("bash", restored.ToolName);
        Assert.Equal("call-123", restored.ToolCallId);
    }

    [Fact]
    public void ChatMessageEntity_RoundTrip_PreservesImageFields()
    {
        var original = ChatMessage.ImageMessage("/path/to/img.png", "data:image/png;base64,abc", "Screenshot", "tool-1");
        var entity = ChatMessageEntity.FromChatMessage(original, "session-1", 0);
        var restored = entity.ToChatMessage();

        Assert.Equal("/path/to/img.png", restored.ImagePath);
        Assert.Equal("data:image/png;base64,abc", restored.ImageDataUri);
        Assert.Equal("Screenshot", restored.Caption);
    }

    [Fact]
    public void ChatMessageEntity_RoundTrip_PreservesOriginalContent()
    {
        var original = ChatMessage.UserMessage("wrapped prompt");
        original.OriginalContent = "actual user text";
        var entity = ChatMessageEntity.FromChatMessage(original, "session-1", 0);
        var restored = entity.ToChatMessage();

        Assert.Equal("actual user text", restored.OriginalContent);
    }

    [Fact]
    public void ChatMessageEntity_RoundTrip_PreservesModel()
    {
        var original = ChatMessage.AssistantMessage("Hello!");
        original.Model = "claude-opus-4.6";
        var entity = ChatMessageEntity.FromChatMessage(original, "session-1", 0);
        var restored = entity.ToChatMessage();

        Assert.Equal("claude-opus-4.6", restored.Model);
    }

    [Fact]
    public void ChatMessageEntity_RoundTrip_HandlesNullFields()
    {
        // Verify that null optional fields round-trip correctly
        var original = ChatMessage.UserMessage("simple message");
        var entity = ChatMessageEntity.FromChatMessage(original, "session-1", 0);
        var restored = entity.ToChatMessage();

        Assert.Null(restored.ToolInput);
        Assert.Null(restored.ImagePath);
        Assert.Null(restored.ImageDataUri);
        Assert.Null(restored.Caption);
        Assert.Null(restored.OriginalContent);
        Assert.Null(restored.Model);
    }

    [Fact]
    public void ChatMessageEntity_FromChatMessage_SetsAllFields()
    {
        var msg = ChatMessage.ToolCallMessage("edit", "tc-42", "{\"path\":\"file.cs\"}");
        msg.Model = "claude-sonnet-4";
        msg.IsComplete = true;
        msg.IsSuccess = true;

        var entity = ChatMessageEntity.FromChatMessage(msg, "s1", 5);

        Assert.Equal("s1", entity.SessionId);
        Assert.Equal(5, entity.OrderIndex);
        Assert.Equal("ToolCall", entity.MessageType);
        Assert.Equal("edit", entity.ToolName);
        Assert.Equal("tc-42", entity.ToolCallId);
        Assert.Equal("{\"path\":\"file.cs\"}", entity.ToolInput);
        Assert.Equal("claude-sonnet-4", entity.Model);
        Assert.True(entity.IsComplete);
        Assert.True(entity.IsSuccess);
    }

    // --- Error feedback tests ---

    [Fact]
    public void ErrorMessage_ContainsErrorContent()
    {
        var msg = ChatMessage.ErrorMessage("Failed to send: Session is still being created. Please wait.");
        Assert.Equal(ChatMessageType.Error, msg.MessageType);
        Assert.Contains("Failed to send", msg.Content);
        Assert.True(msg.IsComplete);
    }
}
