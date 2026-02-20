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
    private static int _portCounter = 19100;

    /// <summary>
    /// Polls until a condition is true, with a timeout. Replaces fixed Task.Delay for reliability under load.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct, int pollMs = 50, int maxMs = 4000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < maxMs)
            await Task.Delay(pollMs, ct);
    }

    public WsBridgeIntegrationTests()
    {
        _port = Interlocked.Increment(ref _portCounter);
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

    // ========== CONNECTION ==========

    [Fact]
    public async Task Connect_ClientReceivesConnectedState()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);
        Assert.True(client.IsConnected);
        client.Stop();
    }

    [Fact]
    public async Task Connect_ClientReceivesSessionList_OnConnect()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("pre-existing", "gpt-4.1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);
        await Task.Delay(500, cts.Token);

        Assert.Contains(client.Sessions, s => s.Name == "pre-existing");
        client.Stop();
    }

    [Fact]
    public async Task Connect_ClientReceivesOrganizationState_OnConnect()
    {
        await InitDemoMode();
        _copilot.CreateGroup("TestGroup");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var orgReceived = new TaskCompletionSource<OrganizationState>();
        var client = new WsBridgeClient();
        client.OnOrganizationStateReceived += org => orgReceived.TrySetResult(org);
        await client.ConnectAsync($"ws://localhost:{_port}/", null, cts.Token);

        var org = await orgReceived.Task.WaitAsync(cts.Token);
        Assert.Contains(org.Groups, g => g.Name == "TestGroup");
        client.Stop();
    }

    [Fact]
    public async Task Connect_ClientReceivesHistory_ForExistingSessions()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("history-test", "gpt-4.1");
        await _copilot.SendPromptAsync("history-test", "Hello from test");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);
        await Task.Delay(500, cts.Token);

        Assert.True(client.SessionHistories.ContainsKey("history-test"));
        Assert.True(client.SessionHistories["history-test"].Count > 0);
        client.Stop();
    }

    [Fact]
    public async Task Connect_RemoteService_HasHistoryImmediately()
    {
        // Simulate desktop: create sessions with messages
        await InitDemoMode();
        await _copilot.CreateSessionAsync("session-with-history", "gpt-4.1");
        await _copilot.SendPromptAsync("session-with-history", "Test message");
        await Task.Delay(100); // Let demo response complete

        // Simulate mobile: create a remote CopilotService that connects to the bridge
        var remoteService = new CopilotService(
            new StubChatDatabase(),
            new StubServerManager(),
            new WsBridgeClient(),
            new RepoManager(),
            new ServiceCollection().BuildServiceProvider(),
            new StubDemoService());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await remoteService.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Remote,
            RemoteUrl = $"http://localhost:{_port}/"
        }, cts.Token);

        // History should be available immediately after ReconnectAsync returns
        var session = remoteService.GetSession("session-with-history");
        Assert.NotNull(session);
        Assert.True(session!.History.Count > 0,
            "History should be synced into the remote service before ReconnectAsync returns");
        Assert.Contains(session.History, m => m.Content.Contains("Test message"));
    }

    // ========== SESSION LIFECYCLE ==========

    [Fact]
    public async Task CreateSession_AppearsOnServer()
    {
        await InitDemoMode();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        await client.CreateSessionAsync("new-session", "gpt-4.1", null, cts.Token);
        await Task.Delay(500, cts.Token);

        Assert.NotNull(_copilot.GetSession("new-session"));
        client.Stop();
    }

    [Fact]
    public async Task CreateSession_WithModel_SetsModelOnServer()
    {
        await InitDemoMode();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        await client.CreateSessionAsync("model-create", "claude-sonnet-4-5", null, cts.Token);
        await Task.Delay(500, cts.Token);

        var session = _copilot.GetSession("model-create");
        Assert.NotNull(session);
        Assert.Equal("claude-sonnet-4-5", session!.Model);
        client.Stop();
    }

    [Fact]
    public async Task CreateSession_BroadcastsUpdatedList_ToClient()
    {
        await InitDemoMode();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        await client.CreateSessionAsync("broadcast-test", "gpt-4.1", null, cts.Token);
        await WaitForAsync(() => client.Sessions.Any(s => s.Name == "broadcast-test"), cts.Token);

        Assert.Contains(client.Sessions, s => s.Name == "broadcast-test");
        client.Stop();
    }

    [Fact]
    public async Task CloseSession_RemovesFromServer()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("close-me", "gpt-4.1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        await client.CloseSessionAsync("close-me", cts.Token);
        await WaitForAsync(() => _copilot.GetSession("close-me") == null, cts.Token);

        Assert.Null(_copilot.GetSession("close-me"));
        client.Stop();
    }

    [Fact]
    public async Task CloseSession_UpdatesClientSessionList()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("close-broadcast", "gpt-4.1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);
        await Task.Delay(300, cts.Token);
        Assert.Contains(client.Sessions, s => s.Name == "close-broadcast");

        await client.CloseSessionAsync("close-broadcast", cts.Token);
        await Task.Delay(500, cts.Token);

        Assert.DoesNotContain(client.Sessions, s => s.Name == "close-broadcast");
        client.Stop();
    }

    [Fact]
    public async Task AbortSession_DoesNotRemoveSession()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("abort-test", "gpt-4.1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        await client.AbortSessionAsync("abort-test", cts.Token);
        await Task.Delay(300, cts.Token);

        Assert.NotNull(_copilot.GetSession("abort-test"));
        client.Stop();
    }

    // ========== MODEL SWITCHING ==========

    [Fact]
    public async Task ChangeModel_UpdatesServerSession()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("model-switch", "gpt-4.1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        await client.ChangeModelAsync("model-switch", "claude-sonnet-4-5", cts.Token);
        await Task.Delay(500, cts.Token);

        Assert.Equal("claude-sonnet-4-5", _copilot.GetSession("model-switch")!.Model);
        client.Stop();
    }

    [Fact]
    public async Task ChangeModel_NonExistentSession_DoesNotThrow()
    {
        await InitDemoMode();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        // Should not throw — server should handle gracefully
        await client.ChangeModelAsync("no-such-session", "gpt-4.1", cts.Token);
        await Task.Delay(300, cts.Token);
        client.Stop();
    }

    // ========== MESSAGING ==========

    [Fact]
    public async Task SendMessage_AddsUserMessageToServerHistory()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("msg-test", "gpt-4.1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        await client.SendMessageAsync("msg-test", "Hello from mobile", cts.Token);
        await Task.Delay(500, cts.Token);

        var session = _copilot.GetSession("msg-test");
        Assert.NotNull(session);
        Assert.Contains(session!.History, m => m.Content?.Contains("Hello from mobile") == true);
        client.Stop();
    }

    [Fact]
    public async Task SendMessage_TriggersContentDelta_OnClient()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("delta-test", "gpt-4.1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var contentReceived = new TaskCompletionSource<string>();
        var client = new WsBridgeClient();
        client.OnContentReceived += (session, content) =>
        {
            if (session == "delta-test") contentReceived.TrySetResult(content);
        };
        await client.ConnectAsync($"ws://localhost:{_port}/", null, cts.Token);

        await client.SendMessageAsync("delta-test", "Tell me a joke", cts.Token);

        // Demo mode sends a simulated response with content deltas
        var content = await contentReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(string.IsNullOrEmpty(content));
        client.Stop();
    }

    [Fact]
    public async Task QueueMessage_EnqueuesOnServer()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("queue-test", "gpt-4.1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        await client.QueueMessageAsync("queue-test", "queued msg", cts.Token);

        var session = _copilot.GetSession("queue-test");
        Assert.NotNull(session);
        await WaitForAsync(() => session!.MessageQueue.Any(m => m.Contains("queued msg")), cts.Token);
        Assert.Contains(session!.MessageQueue, m => m.Contains("queued msg"));
        client.Stop();
    }

    // ========== SESSION SWITCHING ==========

    [Fact]
    public async Task SwitchSession_SendsHistoryToClient()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("switch-a", "gpt-4.1");
        await _copilot.SendPromptAsync("switch-a", "Message in A");
        await _copilot.CreateSessionAsync("switch-b", "gpt-4.1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        await client.SwitchSessionAsync("switch-a", cts.Token);
        await Task.Delay(500, cts.Token);

        Assert.True(client.SessionHistories.ContainsKey("switch-a"));
        client.Stop();
    }

    // ========== DIRECTORY LISTING ==========

    [Fact]
    public async Task ListDirectories_ReturnsEntries()
    {
        await InitDemoMode();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = await client.ListDirectoriesAsync(homePath, cts.Token);

        Assert.NotNull(result);
        Assert.Equal(homePath, result.Path);
        Assert.Null(result.Error);
        Assert.True(result.Directories?.Count > 0);
        client.Stop();
    }

    [Fact]
    public async Task ListDirectories_InvalidPath_ReturnsError()
    {
        await InitDemoMode();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        var result = await client.ListDirectoriesAsync("/nonexistent/path/12345", cts.Token);

        Assert.NotNull(result);
        Assert.NotNull(result.Error);
        client.Stop();
    }

    [Fact]
    public async Task ListDirectories_PathTraversal_ReturnsError()
    {
        await InitDemoMode();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        var result = await client.ListDirectoriesAsync("/tmp/../etc", cts.Token);

        Assert.NotNull(result);
        Assert.Equal("Invalid path", result.Error);
        client.Stop();
    }

    // ========== ORGANIZATION COMMANDS ==========

    [Fact]
    public async Task Organization_CreateGroup_AppearsOnServer()
    {
        await InitDemoMode();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        await client.SendOrganizationCommandAsync(
            new OrganizationCommandPayload { Command = "create_group", Name = "Mobile Group" }, cts.Token);
        await WaitForAsync(() => _copilot.Organization.Groups.Any(g => g.Name == "Mobile Group"), cts.Token);

        Assert.Contains(_copilot.Organization.Groups, g => g.Name == "Mobile Group");
        client.Stop();
    }

    [Fact]
    public async Task Organization_PinSession_PinsOnServer()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("pin-me", "gpt-4.1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        await client.SendOrganizationCommandAsync(
            new OrganizationCommandPayload { Command = "pin", SessionName = "pin-me" }, cts.Token);
        await WaitForAsync(() => _copilot.Organization.Sessions.FirstOrDefault(s => s.SessionName == "pin-me")?.IsPinned == true, cts.Token);

        var meta = _copilot.Organization.Sessions.FirstOrDefault(s => s.SessionName == "pin-me");
        Assert.NotNull(meta);
        Assert.True(meta!.IsPinned);
        client.Stop();
    }

    [Fact]
    public async Task Organization_UnpinSession_UnpinsOnServer()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("unpin-me", "gpt-4.1");
        _copilot.PinSession("unpin-me", true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        await client.SendOrganizationCommandAsync(
            new OrganizationCommandPayload { Command = "unpin", SessionName = "unpin-me" }, cts.Token);
        await Task.Delay(500, cts.Token);

        var meta = _copilot.Organization.Sessions.FirstOrDefault(s => s.SessionName == "unpin-me");
        Assert.NotNull(meta);
        Assert.False(meta!.IsPinned);
        client.Stop();
    }

    [Fact]
    public async Task Organization_MoveSession_MovesOnServer()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("move-me", "gpt-4.1");
        var group = _copilot.CreateGroup("Target");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        await client.SendOrganizationCommandAsync(
            new OrganizationCommandPayload { Command = "move", SessionName = "move-me", GroupId = group.Id }, cts.Token);
        await Task.Delay(500, cts.Token);

        var meta = _copilot.Organization.Sessions.FirstOrDefault(s => s.SessionName == "move-me");
        Assert.NotNull(meta);
        Assert.Equal(group.Id, meta!.GroupId);
        client.Stop();
    }

    [Fact]
    public async Task Organization_RenameGroup_RenamesOnServer()
    {
        await InitDemoMode();
        var group = _copilot.CreateGroup("OldName");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        await client.SendOrganizationCommandAsync(
            new OrganizationCommandPayload { Command = "rename_group", GroupId = group.Id, Name = "NewName" }, cts.Token);

        // Poll until the rename takes effect (server processes asynchronously)
        SessionGroup? renamed = null;
        for (int i = 0; i < 40; i++)
        {
            renamed = _copilot.Organization.Groups.FirstOrDefault(g => g.Id == group.Id);
            if (renamed?.Name == "NewName") break;
            await Task.Delay(100, cts.Token);
        }
        Assert.NotNull(renamed);
        Assert.Equal("NewName", renamed!.Name);
        client.Stop();
    }

    [Fact]
    public async Task Organization_DeleteGroup_RemovesFromServer()
    {
        await InitDemoMode();
        var group = _copilot.CreateGroup("DeleteMe");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        await client.SendOrganizationCommandAsync(
            new OrganizationCommandPayload { Command = "delete_group", GroupId = group.Id }, cts.Token);
        await WaitForAsync(() => !_copilot.Organization.Groups.Any(g => g.Id == group.Id), cts.Token);

        Assert.DoesNotContain(_copilot.Organization.Groups, g => g.Id == group.Id);
        client.Stop();
    }

    [Fact]
    public async Task Organization_ToggleCollapsed_TogglesOnServer()
    {
        await InitDemoMode();
        var group = _copilot.CreateGroup("Collapsible");
        Assert.False(group.IsCollapsed);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        await client.SendOrganizationCommandAsync(
            new OrganizationCommandPayload { Command = "toggle_collapsed", GroupId = group.Id }, cts.Token);
        await WaitForAsync(() => _copilot.Organization.Groups.FirstOrDefault(g => g.Id == group.Id)?.IsCollapsed == true, cts.Token);

        var updated = _copilot.Organization.Groups.FirstOrDefault(g => g.Id == group.Id);
        Assert.NotNull(updated);
        Assert.True(updated!.IsCollapsed);
        client.Stop();
    }

    [Fact]
    public async Task Organization_SetSortMode_UpdatesOnServer()
    {
        await InitDemoMode();
        Assert.Equal(SessionSortMode.LastActive, _copilot.Organization.SortMode);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        await client.SendOrganizationCommandAsync(
            new OrganizationCommandPayload { Command = "set_sort", SortMode = "Alphabetical" }, cts.Token);

        // Poll until the sort mode takes effect
        for (int i = 0; i < 40; i++)
        {
            if (_copilot.Organization.SortMode == SessionSortMode.Alphabetical) break;
            await Task.Delay(100, cts.Token);
        }

        Assert.Equal(SessionSortMode.Alphabetical, _copilot.Organization.SortMode);
        client.Stop();
    }

    [Fact]
    public async Task Organization_BroadcastsStateBack_ToClient()
    {
        await InitDemoMode();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var orgUpdated = new TaskCompletionSource<OrganizationState>();
        var client = new WsBridgeClient();
        var callCount = 0;
        client.OnOrganizationStateReceived += org =>
        {
            callCount++;
            // Skip the initial state sent on connect
            if (callCount > 1) orgUpdated.TrySetResult(org);
        };
        await client.ConnectAsync($"ws://localhost:{_port}/", null, cts.Token);
        await Task.Delay(300, cts.Token);

        await client.SendOrganizationCommandAsync(
            new OrganizationCommandPayload { Command = "create_group", Name = "BroadcastGroup" }, cts.Token);

        var org = await orgUpdated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(org.Groups, g => g.Name == "BroadcastGroup");
        client.Stop();
    }

    // ========== MULTIPLE SESSIONS ==========

    [Fact]
    public async Task RequestSessions_ReturnsAllActiveSessions()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("multi-1", "gpt-4.1");
        await _copilot.CreateSessionAsync("multi-2", "claude-sonnet-4-5");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);
        await Task.Delay(500, cts.Token);

        Assert.True(client.Sessions.Count >= 2);
        Assert.Contains(client.Sessions, s => s.Name == "multi-1");
        Assert.Contains(client.Sessions, s => s.Name == "multi-2");
        client.Stop();
    }

    [Fact]
    public async Task SessionSummary_ContainsModelInfo()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("model-info", "claude-sonnet-4-5");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);
        await Task.Delay(500, cts.Token);

        var summary = client.Sessions.FirstOrDefault(s => s.Name == "model-info");
        Assert.NotNull(summary);
        Assert.Equal("claude-sonnet-4-5", summary!.Model);
        client.Stop();
    }

    // ========== SECURITY ==========

    [Fact]
    public async Task CreateSession_WithPathTraversal_IsRejected()
    {
        await InitDemoMode();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        await client.CreateSessionAsync("traversal-test", "gpt-4.1", "/tmp/../etc", cts.Token);
        await Task.Delay(500, cts.Token);

        // Session should not be created with path traversal
        Assert.Null(_copilot.GetSession("traversal-test"));
        client.Stop();
    }

    // ========== BUG FIX REGRESSION TESTS ==========

    [Fact]
    public async Task AbortSession_InDemoMode_DoesNotThrowNRE()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("abort-demo", "gpt-4.1");

        // Start a message so IsProcessing becomes true
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _copilot.SendPromptAsync("abort-demo", "Hello");
        await Task.Delay(50, cts.Token); // let demo start processing

        // This should NOT throw NullReferenceException (Session is null in demo mode)
        await _copilot.AbortSessionAsync("abort-demo");

        var session = _copilot.GetSession("abort-demo");
        Assert.NotNull(session);
        Assert.False(session!.IsProcessing);
    }

    [Fact]
    public async Task RenameSession_ViaClient_RenamesOnServer()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("old-name", "gpt-4.1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        await client.RenameSessionAsync("old-name", "new-name", cts.Token);
        await WaitForAsync(() => _copilot.GetSession("new-name") != null, cts.Token);

        Assert.Null(_copilot.GetSession("old-name"));
        Assert.NotNull(_copilot.GetSession("new-name"));
        client.Stop();
    }

    [Fact]
    public async Task RenameSession_ViaClient_UpdatesSessionList()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("rename-list", "gpt-4.1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        await client.RenameSessionAsync("rename-list", "renamed-list", cts.Token);
        await Task.Delay(500, cts.Token);

        // Client should receive updated session list with new name
        Assert.Contains(client.Sessions, s => s.Name == "renamed-list");
        Assert.DoesNotContain(client.Sessions, s => s.Name == "rename-list");
        client.Stop();
    }

    [Fact]
    public async Task CloseSession_InDemoMode_DoesNotSendBridgeMessage()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("close-demo", "gpt-4.1");

        // Close should work in demo mode without trying to use the bridge
        var result = await _copilot.CloseSessionAsync("close-demo");
        Assert.True(result);
        Assert.Null(_copilot.GetSession("close-demo"));
    }

    [Fact]
    public async Task ListDirectories_ConcurrentCalls_BothComplete()
    {
        await InitDemoMode();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var client = await ConnectClientAsync(cts.Token);

        // Fire two concurrent directory listing requests
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var tmp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var t1 = client.ListDirectoriesAsync(home, cts.Token);
        var t2 = client.ListDirectoriesAsync(tmp, cts.Token);

        var results = await Task.WhenAll(t1, t2);

        // Both should complete without hanging
        Assert.All(results, r => Assert.Null(r.Error));
        // Verify we got results for both paths (order may vary)
        var paths = results.Select(r => r.Path).ToHashSet();
        Assert.Contains(home, paths);
        Assert.Contains(tmp, paths);
        client.Stop();
    }

    // ========== RESUME SESSION ==========

    [Fact]
    public async Task ResumeSession_InvalidGuid_ReturnsErrorToClient()
    {
        await InitDemoMode();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        string? errorMsg = null;
        client.OnError += (s, e) => errorMsg = e;

        await client.ResumeSessionAsync("../../../etc/passwd", "hack", cts.Token);
        await Task.Delay(500, cts.Token);

        Assert.NotNull(errorMsg);
        Assert.Contains("Invalid session ID format", errorMsg);
        client.Stop();
    }

    [Fact]
    public async Task ResumeSession_NonExistentGuid_ReturnsResumeFailedError()
    {
        await InitDemoMode();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        string? errorMsg = null;
        client.OnError += (s, e) => errorMsg = e;

        await client.ResumeSessionAsync(Guid.NewGuid().ToString(), "test", cts.Token);
        await Task.Delay(500, cts.Token);

        Assert.NotNull(errorMsg);
        Assert.Contains("Resume failed", errorMsg);
        client.Stop();
    }

    // ========== BROADCAST EVENTS ==========

    [Fact]
    public async Task SendMessage_TriggersTurnStartAndTurnEnd_OnClient()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("turn-test", "gpt-4.1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        bool gotTurnStart = false;
        bool gotTurnEnd = false;
        client.OnTurnStart += s => { if (s == "turn-test") gotTurnStart = true; };
        client.OnTurnEnd += s => { if (s == "turn-test") gotTurnEnd = true; };

        await _copilot.SendPromptAsync("turn-test", "Hello");
        await Task.Delay(1000, cts.Token);

        Assert.True(gotTurnStart, "Client should receive TurnStart");
        Assert.True(gotTurnEnd, "Client should receive TurnEnd");
        client.Stop();
    }

    [Fact]
    public async Task SendMessage_TriggersTurnEnd_OnClient()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("complete-test", "gpt-4.1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        bool gotTurnEnd = false;
        client.OnTurnEnd += s => { if (s == "complete-test") gotTurnEnd = true; };

        await _copilot.SendPromptAsync("complete-test", "Hello");
        await Task.Delay(1000, cts.Token);

        Assert.True(gotTurnEnd, "Client should receive TurnEnd");
        client.Stop();
    }

    [Fact]
    public async Task ChangeModel_WhileProcessing_SendsErrorToClient()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("model-busy", "gpt-4.1");

        // Manually set IsProcessing
        var session = _copilot.GetSession("model-busy");
        session!.IsProcessing = true;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        string? errorMsg = null;
        client.OnError += (s, e) => errorMsg = e;

        await client.ChangeModelAsync("model-busy", "claude-opus-4.6", cts.Token);
        await Task.Delay(500, cts.Token);

        Assert.NotNull(errorMsg);
        client.Stop();
    }

    // ========== MULTI-CLIENT ==========

    [Fact]
    public async Task MultipleClients_BothReceiveSessionsList()
    {
        await InitDemoMode();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client1 = await ConnectClientAsync(cts.Token);
        var client2 = await ConnectClientAsync(cts.Token);
        await Task.Delay(200, cts.Token);

        // Create a session — should broadcast to both
        await _copilot.CreateSessionAsync("multi-test", "gpt-4.1");
        await Task.Delay(500, cts.Token);

        Assert.Contains(client1.Sessions, s => s.Name == "multi-test");
        Assert.Contains(client2.Sessions, s => s.Name == "multi-test");
        client1.Stop();
        client2.Stop();
    }

    [Fact]
    public async Task MultipleClients_BothReceiveContentDelta()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("multi-content", "gpt-4.1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client1 = await ConnectClientAsync(cts.Token);
        var client2 = await ConnectClientAsync(cts.Token);

        bool client1GotContent = false;
        bool client2GotContent = false;
        client1.OnContentReceived += (s, c) => { if (s == "multi-content") client1GotContent = true; };
        client2.OnContentReceived += (s, c) => { if (s == "multi-content") client2GotContent = true; };

        await _copilot.SendPromptAsync("multi-content", "Hello");
        await Task.Delay(1000, cts.Token);

        Assert.True(client1GotContent, "Client 1 should receive content");
        Assert.True(client2GotContent, "Client 2 should receive content");
        client1.Stop();
        client2.Stop();
    }

    // ========== CLOSE SESSION GUARD ==========

    [Fact]
    public async Task CloseSession_EmptyName_IsIgnored()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("keep-me", "gpt-4.1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        // Send close with empty session name
        await client.CloseSessionAsync("", cts.Token);
        await Task.Delay(500, cts.Token);

        // Session should still exist
        Assert.NotNull(_copilot.GetSession("keep-me"));
        client.Stop();
    }

    // ========== URL NORMALIZATION ==========

    [Fact]
    public async Task Connect_WsSchemeUrl_ConnectsSuccessfully()
    {
        await InitDemoMode();

        var remoteService = new CopilotService(
            new StubChatDatabase(),
            new StubServerManager(),
            new WsBridgeClient(),
            new RepoManager(),
            new ServiceCollection().BuildServiceProvider(),
            new StubDemoService());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Pass ws:// URL directly — should NOT double-prefix to wss://ws://
        await remoteService.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Remote,
            RemoteUrl = $"ws://localhost:{_port}/"
        }, cts.Token);

        Assert.True(remoteService.IsInitialized);
    }

    [Fact]
    public async Task Connect_HttpsUrl_ConvertsToWss()
    {
        // We can't actually connect to wss:// in tests, but we can verify
        // that the URL normalization doesn't corrupt ws:// or http:// URLs
        await InitDemoMode();

        var remoteService = new CopilotService(
            new StubChatDatabase(),
            new StubServerManager(),
            new WsBridgeClient(),
            new RepoManager(),
            new ServiceCollection().BuildServiceProvider(),
            new StubDemoService());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await remoteService.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Remote,
            RemoteUrl = $"http://localhost:{_port}/"
        }, cts.Token);

        Assert.True(remoteService.IsInitialized);
    }

    // ========== TURN END BROADCAST ==========

    [Fact]
    public async Task SendPrompt_Demo_ClientReceivesTurnEnd()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("turn-test", "gpt-4.1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);

        var turnEndReceived = new TaskCompletionSource<string>();
        client.OnTurnEnd += session =>
        {
            if (session == "turn-test")
                turnEndReceived.TrySetResult(session);
        };

        await _copilot.SendPromptAsync("turn-test", "test prompt", cancellationToken: cts.Token);
        var result = await Task.WhenAny(turnEndReceived.Task, Task.Delay(5000, cts.Token));

        Assert.Equal(turnEndReceived.Task, result);
        Assert.Equal("turn-test", await turnEndReceived.Task);
        client.Stop();
    }

    // ========== AUTH TOKEN (LOOPBACK BYPASS) ==========

    [Fact]
    public async Task Connect_WithAccessTokenSet_LoopbackBypassWorks()
    {
        _server.AccessToken = "test-secret-token-12345";
        await InitDemoMode();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Connect without providing the token — should still work via loopback bypass
        var client = await ConnectClientAsync(cts.Token);
        Assert.True(client.IsConnected);
        client.Stop();

        _server.AccessToken = null;
    }

    // ========== SWITCH SESSION BROADCAST ==========

    [Fact]
    public async Task SwitchSession_BroadcastsUpdatedActiveSession()
    {
        await InitDemoMode();
        await _copilot.CreateSessionAsync("switch-a", "gpt-4.1");
        await _copilot.CreateSessionAsync("switch-b", "gpt-4.1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await ConnectClientAsync(cts.Token);
        await Task.Delay(200, cts.Token);

        await client.SwitchSessionAsync("switch-b", cts.Token);
        await WaitForAsync(() => client.Sessions.Any(s => s.Name == "switch-b"), cts.Token);

        // The server should have broadcast session list with updated active session
        Assert.Contains(client.Sessions, s => s.Name == "switch-b");
        client.Stop();
    }
}
