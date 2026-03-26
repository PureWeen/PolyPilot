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
}
