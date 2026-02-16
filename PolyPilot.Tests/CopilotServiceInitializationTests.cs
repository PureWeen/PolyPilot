using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Integration tests for CopilotService initialization and error handling.
/// Uses stub dependencies to test the actual CopilotService class.
/// Tests use ReconnectAsync(settings) to avoid shared settings.json dependency.
/// </summary>
public class CopilotServiceInitializationTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public CopilotServiceInitializationTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    [Fact]
    public void NewService_IsNotInitialized()
    {
        var svc = CreateService();
        Assert.False(svc.IsInitialized);
        Assert.Null(svc.ActiveSessionName);
    }

    [Fact]
    public void NewService_DefaultMode_IsEmbedded()
    {
        var svc = CreateService();
        Assert.Equal(ConnectionMode.Embedded, svc.CurrentMode);
    }

    [Fact]
    public async Task CreateSession_BeforeInitialize_Throws()
    {
        var svc = CreateService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateSessionAsync("test", cancellationToken: CancellationToken.None));

        Assert.Contains("Service not initialized", ex.Message);
    }

    [Fact]
    public async Task ResumeSession_BeforeInitialize_Throws()
    {
        var svc = CreateService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ResumeSessionAsync(Guid.NewGuid().ToString(), "test", cancellationToken: CancellationToken.None));

        Assert.Contains("Service not initialized", ex.Message);
    }

    [Fact]
    public async Task ReconnectAsync_DemoMode_SetsInitialized()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        Assert.True(svc.IsInitialized);
        Assert.True(svc.IsDemoMode);
        Assert.False(svc.NeedsConfiguration);
    }

    [Fact]
    public async Task ReconnectAsync_DemoMode_CreateSession_Works()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("test-session");
        Assert.NotNull(session);
        Assert.Equal("test-session", session.Name);
    }

    [Fact]
    public async Task ReconnectAsync_DemoMode_ThenReconnectAgain_Works()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        Assert.True(svc.IsInitialized);

        // Reconnect again in demo mode
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        Assert.True(svc.IsInitialized);
        Assert.True(svc.IsDemoMode);
    }

    [Fact]
    public async Task ReconnectAsync_PersistentMode_Failure_SetsNeedsConfiguration()
    {
        // Persistent mode connecting to unreachable port — deterministic failure
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999  // Nothing listening
        });

        // StartAsync throws → caught → NeedsConfiguration = true
        Assert.False(svc.IsInitialized);
        Assert.True(svc.NeedsConfiguration);
    }

    [Fact]
    public async Task ReconnectAsync_PersistentMode_Failure_ClientIsNull()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999
        });

        // After failure, CreateSession should still throw "not initialized"
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateSessionAsync("test", cancellationToken: CancellationToken.None));
        Assert.Contains("Service not initialized", ex.Message);
    }

    [Fact]
    public async Task ReconnectAsync_PersistentMode_NoServer_Failure()
    {
        // Persistent mode but nothing listening on the port
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999  // Nothing listening
        });

        // StartAsync should throw connecting to unreachable server → caught gracefully
        Assert.False(svc.IsInitialized);
        Assert.True(svc.NeedsConfiguration);
    }

    [Fact]
    public async Task ReconnectAsync_FromDemoToPersistent_ClearsOldState()
    {
        var svc = CreateService();

        // First initialize in Demo mode (succeeds)
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        Assert.True(svc.IsInitialized);
        Assert.True(svc.IsDemoMode);

        // Now reconnect to Persistent (will fail — unreachable port)
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999
        });

        // Old Demo state should always be cleared
        Assert.False(svc.IsDemoMode);
        Assert.False(svc.IsInitialized);
        Assert.True(svc.NeedsConfiguration);
    }

    [Fact]
    public async Task ReconnectAsync_Failure_OnStateChanged_Fires()
    {
        var svc = CreateService();
        var stateChangedCount = 0;
        svc.OnStateChanged += () => stateChangedCount++;

        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999
        });

        // OnStateChanged should fire at least once on failure
        Assert.True(stateChangedCount > 0, "OnStateChanged should fire on initialization failure");
    }

    [Fact]
    public async Task ReconnectAsync_DemoMode_OnStateChanged_Fires()
    {
        var svc = CreateService();
        var stateChangedCount = 0;
        svc.OnStateChanged += () => stateChangedCount++;

        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        Assert.True(stateChangedCount > 0, "OnStateChanged should fire on Demo initialization");
    }

    [Fact]
    public async Task ReconnectAsync_DemoMode_SessionsCleared()
    {
        var svc = CreateService();

        // Create a demo session
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        await svc.CreateSessionAsync("session1");
        Assert.Single(svc.GetAllSessions());

        // Reconnect clears sessions
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        Assert.Empty(svc.GetAllSessions());
    }

    [Fact]
    public async Task ReconnectAsync_PersistentMode_SetsCurrentMode()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999
        });

        // Even if StartAsync fails, CurrentMode should reflect what was attempted
        Assert.Equal(ConnectionMode.Persistent, svc.CurrentMode);
    }
}
