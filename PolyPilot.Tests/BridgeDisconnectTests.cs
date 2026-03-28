using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the bridge disconnect fix: SendAsync must throw (not silently drop)
/// when the WebSocket is disconnected, and callers must clean up IsProcessing.
/// Regression test for: user sends message from mobile during WS disconnect,
/// message silently dropped with no error feedback.
/// </summary>
public class BridgeDisconnectTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly IServiceProvider _serviceProvider;

    public BridgeDisconnectTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateRemoteService()
    {
        _bridgeClient.IsConnected = true; // Default to connected for setup
        var svc = new CopilotService(_chatDb, _serverManager, _bridgeClient, new RepoManager(), _serviceProvider, _demoService);
        // Set IsRemoteMode via reflection (normally set during InitializeAsync with remote connection)
        typeof(CopilotService).GetProperty(nameof(CopilotService.IsRemoteMode))!.SetValue(svc, true);
        return svc;
    }

    private async Task AddRemoteSession(CopilotService svc, string name)
    {
        // Create session via public API (in remote mode this adds to _sessions + Organization)
        var wasConnected = _bridgeClient.IsConnected;
        _bridgeClient.IsConnected = true;
        await svc.CreateSessionAsync(name, "test-model");
        _bridgeClient.IsConnected = wasConnected;
    }

    [Fact]
    public async Task SendPromptAsync_WhenDisconnected_ThrowsAndCleansUpProcessing()
    {
        var svc = CreateRemoteService();
        await AddRemoteSession(svc, "test-session");
        _bridgeClient.IsConnected = false;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SendPromptAsync("test-session", "hello"));

        Assert.Contains("Not connected", ex.Message);

        // IsProcessing must be cleared so the session isn't stuck
        var session = svc.GetSession("test-session");
        Assert.NotNull(session);
        Assert.False(session!.IsProcessing);
    }

    [Fact]
    public async Task SendPromptAsync_WhenConnected_Succeeds()
    {
        var svc = CreateRemoteService();
        await AddRemoteSession(svc, "test-session");
        _bridgeClient.IsConnected = true;
        _bridgeClient.ThrowOnSend = false;

        await svc.SendPromptAsync("test-session", "hello");

        // User message should be in history
        var session = svc.GetSession("test-session");
        Assert.NotNull(session);
        Assert.Single(session!.History, m => m.IsUser && m.Content == "hello");
    }

    [Fact]
    public async Task SendPromptAsync_WhenBridgeThrowsDuringSend_CleansUpProcessing()
    {
        var svc = CreateRemoteService();
        await AddRemoteSession(svc, "test-session");
        _bridgeClient.IsConnected = true;
        _bridgeClient.ThrowOnSend = true; // Connected but send fails

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SendPromptAsync("test-session", "hello"));

        // IsProcessing must be cleaned up even though pre-flight check passed
        var session = svc.GetSession("test-session");
        Assert.NotNull(session);
        Assert.False(session!.IsProcessing);
    }

    [Fact]
    public async Task SendPromptAsync_WhenDisconnected_UserMessageNotAdded()
    {
        var svc = CreateRemoteService();
        await AddRemoteSession(svc, "test-session");
        _bridgeClient.IsConnected = false;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SendPromptAsync("test-session", "hello"));

        // Pre-flight check throws before adding the message — history should be empty
        var session = svc.GetSession("test-session");
        Assert.NotNull(session);
        Assert.Empty(session!.History);
    }

    [Fact]
    public async Task SendPromptAsync_WhenBridgeThrows_UserMessageInHistory()
    {
        var svc = CreateRemoteService();
        await AddRemoteSession(svc, "test-session");
        _bridgeClient.IsConnected = true;
        _bridgeClient.ThrowOnSend = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SendPromptAsync("test-session", "hello"));

        // Pre-flight passed so the user message was optimistically added
        var session = svc.GetSession("test-session");
        Assert.NotNull(session);
        Assert.Single(session!.History, m => m.IsUser && m.Content == "hello");
    }

    [Fact]
    public void IsRemoteMode_WhenSet_ReportsCorrectly()
    {
        var svc = CreateRemoteService();
        Assert.True(svc.IsRemoteMode);
    }

    [Fact]
    public async Task ForceSync_WhenStreamingGuardActive_AppliesServerHistoryIfLarger()
    {
        var svc = CreateRemoteService();
        await AddRemoteSession(svc, "test-session");

        // Add 2 local messages (simulating incremental streaming)
        var session = svc.GetSession("test-session");
        Assert.NotNull(session);
        session!.History.Add(ChatMessage.UserMessage("msg1"));
        session.History.Add(ChatMessage.AssistantMessage("reply1"));
        session.MessageCount = 2;

        // Activate streaming guard (session is "mid-stream")
        svc.SetRemoteStreamingGuardForTesting("test-session", true);
        Assert.True(svc.IsRemoteStreamingGuardActive("test-session"));

        // Server has 4 messages (more than local 2 — missed during disconnect)
        var serverHistory = new List<ChatMessage>
        {
            ChatMessage.UserMessage("msg1"),
            ChatMessage.AssistantMessage("reply1"),
            ChatMessage.UserMessage("msg2"),
            ChatMessage.AssistantMessage("reply2")
        };
        _bridgeClient.SessionHistories["test-session"] = serverHistory;

        var result = await svc.ForceRefreshRemoteAsync("test-session");

        Assert.True(result.Success);
        Assert.Equal(4, session.History.Count);
        Assert.Equal(4, session.MessageCount);
        Assert.Equal("msg2", session.History[2].Content);
    }

    [Fact]
    public async Task ForceSync_WhenStreamingGuardActive_SkipsIfServerHasFewerMessages()
    {
        var svc = CreateRemoteService();
        await AddRemoteSession(svc, "test-session");

        // Add 3 local messages (more than server's stale snapshot)
        var session = svc.GetSession("test-session");
        Assert.NotNull(session);
        session!.History.Add(ChatMessage.UserMessage("msg1"));
        session.History.Add(ChatMessage.AssistantMessage("reply1"));
        session.History.Add(ChatMessage.UserMessage("msg2"));
        session.MessageCount = 3;

        // Activate streaming guard
        svc.SetRemoteStreamingGuardForTesting("test-session", true);

        // Server has only 2 messages (stale snapshot)
        var serverHistory = new List<ChatMessage>
        {
            ChatMessage.UserMessage("msg1"),
            ChatMessage.AssistantMessage("reply1")
        };
        _bridgeClient.SessionHistories["test-session"] = serverHistory;

        var result = await svc.ForceRefreshRemoteAsync("test-session");

        Assert.True(result.Success);
        // Should NOT have replaced — local has more messages
        Assert.Equal(3, session.History.Count);
    }

    [Fact]
    public async Task ForceSync_WhenStreamingGuardActive_SameCount_SkipsApply()
    {
        var svc = CreateRemoteService();
        await AddRemoteSession(svc, "test-session");

        // Add 3 local messages
        var session = svc.GetSession("test-session");
        Assert.NotNull(session);
        session!.History.Add(ChatMessage.UserMessage("local-msg1"));
        session.History.Add(ChatMessage.AssistantMessage("local-reply1"));
        session.History.Add(ChatMessage.UserMessage("local-msg2"));
        session.MessageCount = 3;

        // Activate streaming guard
        svc.SetRemoteStreamingGuardForTesting("test-session", true);

        // Server has same count but different content (stale snapshot)
        var serverHistory = new List<ChatMessage>
        {
            ChatMessage.UserMessage("server-msg1"),
            ChatMessage.AssistantMessage("server-reply1"),
            ChatMessage.UserMessage("server-msg2")
        };
        _bridgeClient.SessionHistories["test-session"] = serverHistory;

        var result = await svc.ForceRefreshRemoteAsync("test-session");

        Assert.True(result.Success);
        // Should NOT replace — same count during active streaming means server snapshot is stale
        Assert.Equal(3, session.History.Count);
        Assert.Equal("local-msg1", session.History[0].Content);
    }

    [Fact]
    public async Task ForceSync_WhenNotStreaming_AlwaysAppliesServerHistory()
    {
        var svc = CreateRemoteService();
        await AddRemoteSession(svc, "test-session");

        // Add 3 local messages
        var session = svc.GetSession("test-session");
        Assert.NotNull(session);
        session!.History.Add(ChatMessage.UserMessage("msg1"));
        session.History.Add(ChatMessage.AssistantMessage("reply1"));
        session.History.Add(ChatMessage.UserMessage("msg2"));
        session.MessageCount = 3;

        // No streaming guard active
        Assert.False(svc.IsRemoteStreamingGuardActive("test-session"));

        // Server has only 2 messages — but since not streaming, should still apply
        var serverHistory = new List<ChatMessage>
        {
            ChatMessage.UserMessage("msg1"),
            ChatMessage.AssistantMessage("reply1")
        };
        _bridgeClient.SessionHistories["test-session"] = serverHistory;

        var result = await svc.ForceRefreshRemoteAsync("test-session");

        Assert.True(result.Success);
        Assert.Equal(2, session.History.Count);
        Assert.Equal(2, session.MessageCount);
    }

    // ===== Stale IsProcessing guard tests =====

    [Fact]
    public async Task SyncRemoteSessions_DoesNotResetIsProcessing_AfterTurnEnd()
    {
        // Scenario: TurnEnd clears IsProcessing=false on mobile, then a stale
        // sessions_list arrives (debounced on server) with IsProcessing=true.
        // The TurnEnd guard should prevent re-setting to true.
        var svc = CreateRemoteService();
        await AddRemoteSession(svc, "orchestrator");
        var session = svc.GetSession("orchestrator")!;

        // Server starts processing — sessions_list sets IsProcessing=true
        _bridgeClient.Sessions = new() { new SessionSummary { Name = "orchestrator", IsProcessing = true } };
        svc.SyncRemoteSessions();
        Assert.True(session.IsProcessing);

        // TurnEnd arrives — clears IsProcessing and sets the guard
        session.IsProcessing = false;
        svc.SetTurnEndGuardForTesting("orchestrator", true);

        // Stale sessions_list snapshot arrives with IsProcessing=true
        _bridgeClient.Sessions = new() { new SessionSummary { Name = "orchestrator", IsProcessing = true } };
        svc.SyncRemoteSessions();

        // Guard should prevent overwrite — still false
        Assert.False(session.IsProcessing);
    }

    [Fact]
    public async Task SyncRemoteSessions_AllowsIsProcessingTrue_OnInitialSync()
    {
        // Scenario: Fresh connection — no prior TurnEnd. sessions_list with
        // IsProcessing=true should be accepted (no guard entry exists).
        var svc = CreateRemoteService();
        _bridgeClient.Sessions = new() { new SessionSummary { Name = "new-session", IsProcessing = true } };
        svc.SyncRemoteSessions();

        var session = svc.GetSession("new-session");
        Assert.NotNull(session);
        Assert.True(session!.IsProcessing);
    }

    [Fact]
    public async Task TurnStart_ClearsGuard_AllowsSessionsListToSetProcessing()
    {
        // Scenario: After TurnEnd guard is set, a new TurnStart clears it.
        // sessions_list should then be able to set IsProcessing=true.
        var svc = CreateRemoteService();
        await AddRemoteSession(svc, "session1");
        var session = svc.GetSession("session1")!;

        // Turn completes — guard is set
        session.IsProcessing = false;
        svc.SetTurnEndGuardForTesting("session1", true);

        // New turn starts — clear the guard (simulating TurnStart handler)
        svc.SetTurnEndGuardForTesting("session1", false);
        session.IsProcessing = true; // TurnStart sets this

        // sessions_list confirms processing — guard is gone, should succeed
        _bridgeClient.Sessions = new() { new SessionSummary { Name = "session1", IsProcessing = true } };
        svc.SyncRemoteSessions();
        Assert.True(session.IsProcessing);
    }

    [Fact]
    public async Task SyncRemoteSessions_AllowsSessionsListToClearProcessing()
    {
        // Scenario: Server says session is done (IsProcessing=false).
        // SyncRemoteSessions should always accept false from the server.
        var svc = CreateRemoteService();
        await AddRemoteSession(svc, "session1");
        var session = svc.GetSession("session1")!;

        // Session is processing
        session.IsProcessing = true;
        _bridgeClient.Sessions = new() { new SessionSummary { Name = "session1", IsProcessing = false } };
        svc.SyncRemoteSessions();

        Assert.False(session.IsProcessing);
    }

    [Fact]
    public async Task ForceSync_ClearsIsProcessing_EvenWithStreamingGuard()
    {
        // Scenario: Streaming guard is stuck (TurnStart received but TurnEnd lost).
        // SyncRemoteSessions skips the session. But ForceRefreshRemoteAsync should
        // always apply the server's authoritative IsProcessing state.
        var svc = CreateRemoteService();
        await AddRemoteSession(svc, "stuck-session");
        var session = svc.GetSession("stuck-session")!;

        // Session appears processing with a stuck streaming guard
        session.IsProcessing = true;
        svc.SetRemoteStreamingGuardForTesting("stuck-session", true);

        // SyncRemoteSessions should skip (streaming guard active)
        _bridgeClient.Sessions = new() { new SessionSummary { Name = "stuck-session", IsProcessing = false } };
        svc.SyncRemoteSessions();
        Assert.True(session.IsProcessing); // Guard blocks the update

        // Force sync should override the streaming guard
        _bridgeClient.SessionHistories["stuck-session"] = new List<ChatMessage>();
        var result = await svc.ForceRefreshRemoteAsync("stuck-session");

        Assert.True(result.Success);
        Assert.False(session.IsProcessing);
        Assert.False(svc.IsRemoteStreamingGuardActive("stuck-session"));
    }
}
