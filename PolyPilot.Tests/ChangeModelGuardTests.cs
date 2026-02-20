using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

public class ChangeModelGuardTests
{
    private CopilotService CreateService() => new(
        new StubChatDatabase(),
        new StubServerManager(),
        new StubWsBridgeClient(),
        new RepoManager(),
        new ServiceCollection().BuildServiceProvider(),
        new StubDemoService());

    [Fact]
    public async Task ChangeModelAsync_DemoMode_UpdatesModel()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        await svc.CreateSessionAsync("test", "gpt-4.1");

        var result = await svc.ChangeModelAsync("test", "claude-opus-4.6");

        Assert.True(result);
        Assert.Equal("claude-opus-4.6", svc.GetSession("test")!.Model);
    }

    [Fact]
    public async Task ChangeModelAsync_WhileProcessing_ReturnsFalse()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        await svc.CreateSessionAsync("busy", "gpt-4.1");

        svc.GetSession("busy")!.IsProcessing = true;

        var result = await svc.ChangeModelAsync("busy", "claude-opus-4.6");

        Assert.False(result);
        Assert.Equal("gpt-4.1", svc.GetSession("busy")!.Model);
    }

    [Fact]
    public async Task ChangeModelAsync_NonExistentSession_ReturnsFalse()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var result = await svc.ChangeModelAsync("ghost", "claude-opus-4.6");

        Assert.False(result);
    }

    [Fact]
    public async Task ChangeModelAsync_SameModel_ReturnsTrue()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        await svc.CreateSessionAsync("same", "claude-opus-4.6");

        var result = await svc.ChangeModelAsync("same", "claude-opus-4.6");

        Assert.True(result);
    }

    [Fact]
    public async Task ChangeModelAsync_EmptyModel_ReturnsFalse()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        await svc.CreateSessionAsync("empty", "gpt-4.1");

        var result = await svc.ChangeModelAsync("empty", "");

        Assert.False(result);
    }
}
