using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests that bridge prompts arriving during session restore are queued
/// and replayed after restore completes, preventing message loss (#450).
/// </summary>
[Collection("SocketIsolated")]
public class BridgePromptQueueTests : IDisposable
{
    private readonly WsBridgeServer _server;
    private readonly CopilotService _copilot;
    private readonly int _port;
    private readonly string _testDir;
    private readonly List<WsBridgeClient> _clients = new();

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct, int pollMs = 50, int maxMs = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < maxMs)
            await Task.Delay(pollMs, ct);
        if (!condition())
            throw new TimeoutException($"WaitForAsync condition not met within {maxMs}ms");
    }

    public BridgePromptQueueTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"polypilot-bridgequeue-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        CopilotService.SetBaseDirForTesting(_testDir);
        RepoManager.SetBaseDirForTesting(_testDir);
        AuditLogService.SetLogDirForTesting(Path.Combine(_testDir, "audit_logs"));
        PromptLibraryService.SetUserPromptsDirForTesting(Path.Combine(_testDir, "prompts"));
        FiestaService.SetStateFilePathForTesting(Path.Combine(_testDir, "fiesta.json"));
        ConnectionSettings.SetSettingsFilePathForTesting(Path.Combine(_testDir, "settings.json"));

        _port = GetFreePort();
        _server = new WsBridgeServer();

        var services = new ServiceCollection();
        services.AddSingleton(_server);
        var serviceProvider = services.BuildServiceProvider();

        _copilot = new CopilotService(
            new StubChatDatabase(),
            new StubServerManager(),
            new StubWsBridgeClient(),
            new RepoManager(),
            serviceProvider,
            new StubDemoService());

        _server.SetCopilotService(_copilot);
        _server.Start(_port, 0);
    }

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            try { client.Stop(); } catch { }
            try { client.Dispose(); } catch { }
        }

        try { _server.Stop(); } catch { }
        try { _server.Dispose(); } catch { }
        try { _copilot.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }

        CopilotService.SetBaseDirForTesting(TestSetup.TestBaseDir);
        RepoManager.SetBaseDirForTesting(TestSetup.TestBaseDir);
        AuditLogService.SetLogDirForTesting(Path.Combine(TestSetup.TestBaseDir, "audit_logs"));
        PromptLibraryService.SetUserPromptsDirForTesting(Path.Combine(TestSetup.TestBaseDir, "prompts"));
        FiestaService.SetStateFilePathForTesting(Path.Combine(TestSetup.TestBaseDir, "fiesta.json"));
        ConnectionSettings.SetSettingsFilePathForTesting(Path.Combine(TestSetup.TestBaseDir, "settings.json"));

        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    private async Task<WsBridgeClient> ConnectClientAsync(CancellationToken ct = default)
    {
        var client = new WsBridgeClient();
        _clients.Add(client);
        await client.ConnectAsync($"ws://localhost:{_port}/", null, ct);
        return client;
    }

    private async Task InitDemoMode()
    {
        await _copilot.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
    }

    // ========== QUEUE DURING RESTORE ==========

    [Fact]
    public async Task SendMessage_DuringRestore_IsQueuedNotSent()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("queue-test", "gpt-4.1");

        // Simulate restore in progress
        _copilot.SetIsRestoringForTesting(true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        // Send a message while restoring
        await client.SendMessageAsync("queue-test", "Hello during restore", ct: cts.Token);

        // Give it a moment to propagate
        await Task.Delay(500);

        // The message should NOT have been delivered — session should have no user messages
        var session = _copilot.GetSession("queue-test");
        Assert.NotNull(session);
        var userMessages = session!.History.Where(m => m.Role == "user").ToList();
        Assert.Empty(userMessages);

        _copilot.SetIsRestoringForTesting(false);
    }

    [Fact]
    public async Task DrainPendingPrompts_ReplaysQueuedMessages()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("drain-test", "gpt-4.1");

        // Simulate restore in progress
        _copilot.SetIsRestoringForTesting(true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var client = await ConnectClientAsync(cts.Token);

        // Send a message while restoring
        await client.SendMessageAsync("drain-test", "Replayed message", ct: cts.Token);
        await Task.Delay(500);

        // Verify nothing delivered yet
        var session = _copilot.GetSession("drain-test");
        Assert.NotNull(session);
        Assert.Empty(session!.History.Where(m => m.Role == "user"));

        // End restore and drain the queue
        _copilot.SetIsRestoringForTesting(false);
        await _server.DrainPendingPromptsAsync();

        // Wait for the prompt to be processed (demo mode adds user + assistant messages)
        await WaitForAsync(() => session.History.Any(m => m.Role == "user"), cts.Token);

        Assert.Contains(session.History, m => m.Role == "user" && m.Content.Contains("Replayed message"));
    }

    [Fact]
    public async Task DrainPendingPrompts_ReplaysInOrder()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("order-test", "gpt-4.1");

        // Enqueue messages directly to test drain FIFO ordering
        // without WebSocket round-trip timing and concurrent List<T> races.
        _server.EnqueuePendingPromptForTesting("order-test", "First message");
        _server.EnqueuePendingPromptForTesting("order-test", "Second message");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Drain processes items sequentially (FIFO)
        await _server.DrainPendingPromptsAsync();

        // Wait for both demo responses to complete so List<T> mutations settle
        await WaitForAsync(() =>
        {
            try
            {
                var s = session();
                return s.History.Count(m => m.Role == "user") >= 2
                    && s.History.Count(m => m.Role == "assistant") >= 2;
            }
            catch { return false; }
        }, cts.Token);

        var userMessages = session().History.Where(m => m.Role == "user").ToList();
        Assert.Equal(2, userMessages.Count);
        Assert.Contains("First message", userMessages[0].Content);
        Assert.Contains("Second message", userMessages[1].Content);

        AgentSessionInfo session() => _copilot.GetSession("order-test")!;
    }

    [Fact]
    public async Task DrainPendingPrompts_EmptyQueue_DoesNothing()
    {
        await InitDemoMode();

        // Drain without any queued messages — should be a no-op
        await _server.DrainPendingPromptsAsync();

        // No exceptions means success
    }

    [Fact]
    public async Task SendMessage_WhenNotRestoring_ProcessedImmediately()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("immediate-test", "gpt-4.1");

        // IsRestoring is false (normal state)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        await client.SendMessageAsync("immediate-test", "Normal message", ct: cts.Token);

        // Should be processed immediately
        var session = _copilot.GetSession("immediate-test");
        Assert.NotNull(session);
        await WaitForAsync(() => session!.History.Any(m => m.Role == "user"), cts.Token);

        Assert.Contains(session!.History, m => m.Role == "user" && m.Content.Contains("Normal message"));
    }

    // ========== BUSY SESSION QUEUEING ==========

    [Fact]
    public async Task SendMessage_WhenSessionBusy_DirectSessionQueuesMessage()
    {
        // Demo mode bypasses IsProcessing checks (early return in SendPromptAsync),
        // so we test the busy-queueing logic directly: EnqueueMessage is safe and works
        // when a session is in processing state.
        await InitDemoMode();
        await _copilot.CreateSessionAsync("busy-test", "gpt-4.1");

        var session = _copilot.GetSession("busy-test")!;
        session.IsProcessing = true;

        // EnqueueMessage should work regardless of IsProcessing state
        _copilot.EnqueueMessage("busy-test", "Queued while busy", agentMode: "agent");

        Assert.Equal(1, session.MessageQueue.Count);
        var queued = session.MessageQueue.TryDequeue();
        Assert.Equal("Queued while busy", queued);
    }

    [Fact]
    public void SessionBusyException_InheritsFromInvalidOperationException()
    {
        var ex = new SessionBusyException("test-session");
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.Equal("test-session", ex.SessionName);
        Assert.Contains("test-session", ex.Message);
        Assert.Contains("already processing", ex.Message);
    }

    [Fact]
    public void SessionBusyException_CaughtByInvalidOperationExceptionHandler()
    {
        // Verify backward compatibility: existing catch(InvalidOperationException) still works
        try
        {
            throw new SessionBusyException("compat-test");
        }
        catch (InvalidOperationException ex)
        {
            Assert.IsType<SessionBusyException>(ex);
            Assert.Contains("compat-test", ex.Message);
        }
    }
}
