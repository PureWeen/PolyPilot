using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Integration tests that stand up a real WsBridgeServer on localhost,
/// connect a real WsBridgeClient, and verify end-to-end bridge message flows.
/// This simulates the mobile → devtunnel → desktop path without needing
/// a real devtunnel or device.
/// </summary>
public class WsBridgeIntegrationTests : IDisposable
{
    private readonly WsBridgeServer _server;
    private readonly CopilotService _copilot;
    private readonly int _port;

    public WsBridgeIntegrationTests()
    {
        // Use a random high port to avoid conflicts with parallel tests
        _port = Random.Shared.Next(19000, 19999);
        _server = new WsBridgeServer();

        _copilot = new CopilotService(
            new StubChatDatabase(),
            new StubServerManager(),
            new StubWsBridgeClient(),
            new RepoManager(),
            new ServiceCollection().BuildServiceProvider(),
            new StubDemoService());

        _server.SetCopilotService(_copilot);
        _server.Start(_port, 0);
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
    }

    private async Task<WsBridgeClient> ConnectClientAsync(CancellationToken ct = default)
    {
        var client = new WsBridgeClient();
        await client.ConnectAsync($"ws://localhost:{_port}/", null, ct);
        return client;
    }

    private async Task InitDemoMode()
    {
        await _copilot.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
    }

    [Fact]
    public async Task Client_CanConnect_ToLocalServer()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);
        Assert.True(client.IsConnected);
        client.Stop();
    }

    [Fact]
    public async Task Client_CreateSession_AppearsOnServer()
    {
        await InitDemoMode();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        await client.CreateSessionAsync("integration-test", "gpt-4.1", null, cts.Token);
        // Give server time to process
        await Task.Delay(500, cts.Token);

        var session = _copilot.GetSession("integration-test");
        Assert.NotNull(session);
        client.Stop();
    }

    [Fact]
    public async Task Client_ChangeModel_UpdatesServerSession()
    {
        await InitDemoMode();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Create session on server directly
        await _copilot.CreateSessionAsync("model-test", "gpt-4.1");
        var session = _copilot.GetSession("model-test");
        Assert.NotNull(session);
        Assert.Equal("gpt-4.1", session!.Model);

        // Connect client and send ChangeModel
        var client = await ConnectClientAsync(cts.Token);
        await client.ChangeModelAsync("model-test", "claude-sonnet-4-5", cts.Token);
        await Task.Delay(500, cts.Token);

        // Verify model changed on server
        session = _copilot.GetSession("model-test");
        Assert.NotNull(session);
        Assert.Equal("claude-sonnet-4-5", session!.Model);
        client.Stop();
    }

    [Fact]
    public async Task Client_AbortSession_ReachesServer()
    {
        await InitDemoMode();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await _copilot.CreateSessionAsync("abort-test", "gpt-4.1");
        var client = await ConnectClientAsync(cts.Token);

        // AbortSession should not throw
        await client.AbortSessionAsync("abort-test", cts.Token);
        await Task.Delay(300, cts.Token);

        // Session should still exist (abort stops processing, doesn't delete)
        var session = _copilot.GetSession("abort-test");
        Assert.NotNull(session);
        client.Stop();
    }

    [Fact]
    public async Task Client_CloseSession_RemovesFromServer()
    {
        await InitDemoMode();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await _copilot.CreateSessionAsync("close-test", "gpt-4.1");
        var client = await ConnectClientAsync(cts.Token);

        await client.CloseSessionAsync("close-test", cts.Token);
        await Task.Delay(500, cts.Token);

        var session = _copilot.GetSession("close-test");
        Assert.Null(session);
        client.Stop();
    }

    [Fact]
    public async Task Client_RequestSessions_ReceivesList()
    {
        await InitDemoMode();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await _copilot.CreateSessionAsync("list-test-1", "gpt-4.1");
        await _copilot.CreateSessionAsync("list-test-2", "gpt-4.1");

        var client = await ConnectClientAsync(cts.Token);

        // Client receives session list on connect; wait for it
        await Task.Delay(500, cts.Token);

        Assert.True(client.Sessions.Count >= 2);
        Assert.Contains(client.Sessions, s => s.Name == "list-test-1");
        Assert.Contains(client.Sessions, s => s.Name == "list-test-2");
        client.Stop();
    }
}
