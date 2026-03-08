using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for protocol version mismatch recovery paths and settings immutability.
/// </summary>
public class ProtocolVersionMismatchTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public ProtocolVersionMismatchTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    [Fact]
    public void StubServerManager_StopServer_ClearsIsServerRunning()
    {
        _serverManager.IsServerRunning = true;
        _serverManager.StopServer();
        Assert.False(_serverManager.IsServerRunning);
    }

    [Fact]
    public async Task StubServerManager_StartServerAsync_ReturnsConfiguredResult()
    {
        _serverManager.StartServerResult = true;
        Assert.True(await _serverManager.StartServerAsync(4321));

        _serverManager.StartServerResult = false;
        Assert.False(await _serverManager.StartServerAsync(4321));
    }

    [Fact]
    public void StubServerManager_CheckServerRunning_ReflectsIsServerRunning()
    {
        _serverManager.IsServerRunning = false;
        Assert.False(_serverManager.CheckServerRunning());

        _serverManager.IsServerRunning = true;
        Assert.True(_serverManager.CheckServerRunning());
    }

    [Fact]
    public async Task ReconnectAsync_PersistentFailure_DoesNotMutateSettingsMode()
    {
        var svc = CreateService();
        var settings = new ConnectionSettings { Mode = ConnectionMode.Persistent, Host = "localhost", Port = 19999 };

        await svc.ReconnectAsync(settings);

        // settings.Mode must remain Persistent — recovery must not leak mutations
        Assert.Equal(ConnectionMode.Persistent, settings.Mode);
    }

    [Fact]
    public async Task ReconnectAsync_PersistentFailure_SetsNeedsConfiguration()
    {
        var svc = CreateService();
        var settings = new ConnectionSettings { Mode = ConnectionMode.Persistent, Host = "localhost", Port = 19999 };

        await svc.ReconnectAsync(settings);

        Assert.True(svc.NeedsConfiguration);
        Assert.False(svc.IsInitialized);
    }

    [Fact]
    public async Task ReconnectAsync_PersistentFailure_FiresOnStateChanged()
    {
        var svc = CreateService();
        var stateChangedCount = 0;
        svc.OnStateChanged += () => stateChangedCount++;

        var settings = new ConnectionSettings { Mode = ConnectionMode.Persistent, Host = "localhost", Port = 19999 };
        await svc.ReconnectAsync(settings);

        Assert.True(stateChangedCount > 0, "OnStateChanged should fire at least once on init failure");
    }

    [Fact]
    public async Task ReconnectAsync_DemoMode_DoesNotMutateSettingsMode()
    {
        var svc = CreateService();
        var settings = new ConnectionSettings { Mode = ConnectionMode.Demo };

        await svc.ReconnectAsync(settings);

        Assert.Equal(ConnectionMode.Demo, settings.Mode);
        Assert.True(svc.IsInitialized);
    }
}
