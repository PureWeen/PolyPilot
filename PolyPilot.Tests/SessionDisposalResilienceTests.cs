using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for session disposal resilience — verifies that DisposeAsync on
/// already-disposed or disconnected sessions doesn't crash the app.
/// Regression tests for: "Session disconnected and reconnect failed:
/// Cannot access a disposed object. Object name: 'StreamJsonRpc.JsonRpc'."
/// </summary>
public class SessionDisposalResilienceTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public SessionDisposalResilienceTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    [Fact]
    public async Task CloseSession_DemoMode_DoesNotThrow()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("disposable");
        Assert.NotNull(session);

        // In demo mode, Session is null! — CloseSessionAsync must handle gracefully
        var result = await svc.CloseSessionAsync("disposable");
        Assert.True(result);
        Assert.Empty(svc.GetAllSessions());
    }

    [Fact]
    public async Task CloseSession_DemoMode_MultipleTimes_DoesNotThrow()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("multi-close");

        // First close succeeds
        Assert.True(await svc.CloseSessionAsync("multi-close"));

        // Second close returns false (already removed) — must not throw
        Assert.False(await svc.CloseSessionAsync("multi-close"));
    }

    [Fact]
    public async Task DisposeService_WithDemoSessions_DoesNotThrow()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("session-1");
        await svc.CreateSessionAsync("session-2");
        Assert.Equal(2, svc.GetAllSessions().Count());

        // DisposeAsync iterates all sessions and calls DisposeAsync on each —
        // must not throw even if sessions have null or disposed underlying objects
        await svc.DisposeAsync();

        Assert.Equal(0, svc.SessionCount);
    }

    [Fact]
    public async Task DisposeService_AfterFailedPersistentInit_DoesNotThrow()
    {
        var svc = CreateService();

        // Persistent mode fails — no sessions, _client may be null
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999
        });
        Assert.False(svc.IsInitialized);

        // DisposeAsync must handle null client gracefully
        await svc.DisposeAsync();
    }

    [Fact]
    public async Task CloseSession_NonExistent_ReturnsFalse()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        // Closing a session that was never created should return false, not throw
        Assert.False(await svc.CloseSessionAsync("ghost-session"));
    }

    [Fact]
    public async Task CloseSession_TracksClosedSessionIds()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("tracked-close");
        Assert.NotNull(session.SessionId);

        await svc.CloseSessionAsync("tracked-close");

        // After closing, the session should be gone
        Assert.Empty(svc.GetAllSessions());
        Assert.Null(svc.ActiveSessionName);
    }

    [Fact]
    public async Task SendPrompt_DemoMode_AddsHistoryAndReturns()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("prompt-test");
        var result = await svc.SendPromptAsync("prompt-test", "Hello, world!");

        Assert.Equal("", result);
        Assert.Single(session.History);
        Assert.Equal("user", session.History[0].Role);
        Assert.Contains("Hello, world!", session.History[0].Content);
    }

    [Fact]
    public async Task SendPrompt_DemoMode_SkipHistory_DoesNotAddMessage()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("skip-hist");
        await svc.SendPromptAsync("skip-hist", "hidden message", skipHistoryMessage: true);

        Assert.Empty(session.History);
    }

    [Fact]
    public async Task SendPrompt_NonExistentSession_Throws()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SendPromptAsync("no-such-session", "test"));
    }

    [Fact]
    public async Task CloseSession_ActiveSession_SwitchesToAnother()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("first");
        await svc.CreateSessionAsync("second");
        svc.SetActiveSession("first");

        Assert.Equal("first", svc.ActiveSessionName);

        await svc.CloseSessionAsync("first");

        // Active session should switch to remaining one
        Assert.Equal("second", svc.ActiveSessionName);
    }

    [Fact]
    public async Task CloseSession_LastSession_ActiveBecomesNull()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("only-one");
        Assert.Equal("only-one", svc.ActiveSessionName);

        await svc.CloseSessionAsync("only-one");

        Assert.Null(svc.ActiveSessionName);
        Assert.Empty(svc.GetAllSessions());
    }

    [Fact]
    public async Task DisposeService_ThenSessionCount_IsZero()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("pre-dispose");
        Assert.Equal(1, svc.SessionCount);

        await svc.DisposeAsync();

        // After disposal, all sessions should be cleared
        Assert.Equal(0, svc.SessionCount);
    }
}
