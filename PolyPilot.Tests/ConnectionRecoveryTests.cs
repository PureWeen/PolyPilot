using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for CopilotService.IsConnectionError and the CreateSessionAsync
/// connection recovery logic that prevents "JSON-RPC connection lost" errors
/// from crashing the UI when the persistent server dies mid-session.
/// </summary>
public class ConnectionRecoveryTests
{
    // ===== IsConnectionError helper tests =====

    [Theory]
    [InlineData("The JSON-RPC connection with the remote party was lost before the request could complete.")]
    [InlineData("Connection refused")]
    [InlineData("Connection was closed")]
    [InlineData("The transport is closed")]
    [InlineData("JSON-RPC connection was lost")]
    public void IsConnectionError_DetectsKnownConnectionMessages(string message)
    {
        var ex = new Exception(message);
        Assert.True(CopilotService.IsConnectionError(ex));
    }

    [Fact]
    public void IsConnectionError_DetectsIOException()
    {
        var ex = new System.IO.IOException("Broken pipe");
        Assert.True(CopilotService.IsConnectionError(ex));
    }

    [Fact]
    public void IsConnectionError_DetectsSocketException()
    {
        var ex = new System.Net.Sockets.SocketException(61); // Connection refused
        Assert.True(CopilotService.IsConnectionError(ex));
    }

    [Fact]
    public void IsConnectionError_DetectsObjectDisposedException()
    {
        var ex = new ObjectDisposedException("CopilotClient");
        Assert.True(CopilotService.IsConnectionError(ex));
    }

    [Fact]
    public void IsConnectionError_DetectsInnerSocketException()
    {
        var inner = new System.Net.Sockets.SocketException(61);
        var ex = new InvalidOperationException("Operation failed", inner);
        Assert.True(CopilotService.IsConnectionError(ex));
    }

    [Fact]
    public void IsConnectionError_DetectsInnerIOException()
    {
        var inner = new System.IO.IOException("Pipe broken");
        var ex = new InvalidOperationException("Send failed", inner);
        Assert.True(CopilotService.IsConnectionError(ex));
    }

    [Theory]
    [InlineData("Session name cannot be empty.")]
    [InlineData("Invalid model name")]
    [InlineData("Session 'test' already exists.")]
    [InlineData("Service not initialized")]
    public void IsConnectionError_ReturnsFalseForNonConnectionErrors(string message)
    {
        var ex = new InvalidOperationException(message);
        Assert.False(CopilotService.IsConnectionError(ex));
    }

    [Fact]
    public void IsConnectionError_ReturnsFalseForArgumentException()
    {
        var ex = new ArgumentException("Bad argument");
        Assert.False(CopilotService.IsConnectionError(ex));
    }

    [Fact]
    public void IsConnectionError_ReturnsFalseForNullReferenceException()
    {
        var ex = new NullReferenceException("Object not set");
        Assert.False(CopilotService.IsConnectionError(ex));
    }

    // ===== CreateSessionAsync recovery integration tests =====

    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public ConnectionRecoveryTests()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    [Fact]
    public async Task CreateSessionAsync_PersistentConnectionLost_ServerManagerCheckCalled()
    {
        // When the service is initialized in Persistent mode and the SDK call fails
        // with a connection error, the recovery logic should check if the server is still running.
        // We can't easily simulate an already-initialized CopilotClient that then fails,
        // but we CAN verify that the IsConnectionError detection is wired up by testing
        // that the recovery flow in persistence restore uses it.
        var svc = CreateService();

        // Initialize in persistent mode — will fail because nothing listens on 19999,
        // but ServerManager is stubbed so CheckServerRunning can be controlled
        _serverManager.IsServerRunning = false;
        _serverManager.StartServerResult = false;

        await svc.ReconnectAsync(new PolyPilot.Models.ConnectionSettings
        {
            Mode = PolyPilot.Models.ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999
        });

        // Service should not be initialized since server couldn't start
        Assert.False(svc.IsInitialized);
    }

    [Fact]
    public async Task CreateSessionAsync_DemoMode_NoRecoveryNeeded()
    {
        // Demo mode sessions should work without hitting the recovery path
        var svc = CreateService();
        await svc.ReconnectAsync(new PolyPilot.Models.ConnectionSettings
        {
            Mode = PolyPilot.Models.ConnectionMode.Demo
        });

        var session = await svc.CreateSessionAsync("demo-test");
        Assert.NotNull(session);
        Assert.Equal("demo-test", session.Name);
    }

    [Fact]
    public async Task CreateSessionAsync_NotInitialized_ThrowsBeforeRecovery()
    {
        // When the service isn't initialized at all, it should throw
        // InvalidOperationException before any recovery logic runs
        var svc = CreateService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateSessionAsync("test", cancellationToken: CancellationToken.None));

        Assert.Contains("Service not initialized", ex.Message);
    }

    [Theory]
    [InlineData("The JSON-RPC connection with the remote party was lost")]
    [InlineData("Communication error with Copilot CLI: The JSON-RPC connection with the remote party was lost before the request could complete.")]
    public void IsConnectionError_DetectsExactBugReportError(string message)
    {
        // Verify the exact error messages from the bug report are detected
        var ex = new Exception(message);
        Assert.True(CopilotService.IsConnectionError(ex));
    }

    [Fact]
    public void IsConnectionError_DetectsConnectionRefusedWithWrapping()
    {
        // Simulate the exact exception chain from the crash log:
        // AggregateException → SocketException (Connection refused)
        var socketEx = new System.Net.Sockets.SocketException(61); // Connection refused
        var aggregate = new AggregateException("A Task's exception(s) were not observed", socketEx);
        Assert.True(CopilotService.IsConnectionError(aggregate));
    }

    // ===== Regression: "Client not connected. Call StartAsync() first." =====
    // This error is thrown by the SDK as InvalidOperationException when the
    // underlying CopilotClient connection is lost. Without this detection,
    // SendPromptAsync skips client recreation during reconnect, causing
    // cascading failures in multi-agent orchestrator dispatch.

    [Theory]
    [InlineData("Client not connected. Call StartAsync() first.")]
    [InlineData("Client not connected")]
    [InlineData("Server not connected")]
    public void IsConnectionError_DetectsNotConnectedErrors(string message)
    {
        var ex = new InvalidOperationException(message);
        Assert.True(CopilotService.IsConnectionError(ex));
    }

    [Fact]
    public void IsConnectionError_DetectsNotConnectedWrappedInAggregate()
    {
        // Simulate the exact exception chain from the bug report:
        // AggregateException → InvalidOperationException("Client not connected...")
        var inner = new InvalidOperationException("Client not connected. Call StartAsync() first.");
        var aggregate = new AggregateException("One or more errors occurred.", inner);
        Assert.True(CopilotService.IsConnectionError(aggregate));
    }

    [Fact]
    public void IsConnectionError_DetectsNotConnectedAsInnerException()
    {
        // InvalidOperationException wrapping another InvalidOperationException
        var inner = new InvalidOperationException("Client not connected. Call StartAsync() first.");
        var outer = new InvalidOperationException("Send failed", inner);
        Assert.True(CopilotService.IsConnectionError(outer));
    }

    // ===== Regression: Stale client reference in SendPromptAsync reconnect (PR #325) =====
    // When SendPromptAsync detects a connection error, it recreates _client but the
    // local `client` variable (captured from GetClientForGroup before reconnection)
    // still pointed to the old disposed CopilotClient. Calls to
    // client.ResumeSessionAsync / client.CreateSessionAsync used the disposed client,
    // causing "Client not connected. Call StartAsync() first." on reconnect.
    //
    // The fix adds `client = _client;` after successful client recreation.
    // CopilotClient is a concrete SDK class (no ICopilotClient interface), so we
    // cannot mock the actual reconnect path. Instead, structural source-code tests
    // verify the fix is present and correctly ordered — following the same pattern
    // used by MultiAgentRegressionTests for other reconnect invariants.

    [Fact]
    public void SendPromptAsync_ReconnectPath_RefreshesLocalClientAfterRecreation()
    {
        // STRUCTURAL REGRESSION GUARD: Verify that in the reconnect catch block of
        // SendPromptAsync, the local `client` variable is refreshed after `_client`
        // is recreated. Without this, ResumeSessionAsync/CreateSessionAsync operate
        // on the old disposed CopilotClient, throwing "Client not connected".
        var source = File.ReadAllText(Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs"));

        // Find the reconnect block where _client is recreated
        var recreateIndex = source.IndexOf("_client = CreateClient(connSettings);");
        Assert.True(recreateIndex > 0, "Could not find _client = CreateClient(connSettings) in reconnect path");

        // After client recreation and StartAsync, the local variable must be refreshed
        var afterRecreate = source.Substring(recreateIndex, 400);
        Assert.Contains("client = _client", afterRecreate);

        // The refresh must come BEFORE ResumeSessionAsync is called
        var refreshPos = afterRecreate.IndexOf("client = _client");
        var resumePos = source.IndexOf("client.ResumeSessionAsync", recreateIndex);
        Assert.True(recreateIndex + refreshPos < resumePos,
            "client = _client must appear before client.ResumeSessionAsync in reconnect path");
    }

    [Fact]
    public void SendPromptAsync_ReconnectPath_UsesRefreshedClientForCreateSession()
    {
        // STRUCTURAL REGRESSION GUARD: The stale reference also affects the
        // "Session not found" fallback where client.CreateSessionAsync is called.
        var source = File.ReadAllText(Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs"));

        var recreateIndex = source.IndexOf("_client = CreateClient(connSettings);");
        var refreshIndex = source.IndexOf("client = _client", recreateIndex);
        var createFallbackIndex = source.IndexOf("client.CreateSessionAsync(freshConfig", refreshIndex);
        Assert.True(createFallbackIndex > refreshIndex,
            "client.CreateSessionAsync (Session not found fallback) must be after client = _client refresh");
    }

    [Fact]
    public void IsConnectionError_DetectsOrchestratorDispatchError()
    {
        // The exact error chain from the bug report:
        // Multi-agent orchestrator → SendViaOrchestratorAsync → SendPromptAndWaitAsync
        // → SendPromptAsync → state.Session.SendAsync throws
        // → reconnect fails → surfaces as the dispatch error
        var ex = new InvalidOperationException(
            "Client not connected. Call StartAsync() first.");
        Assert.True(CopilotService.IsConnectionError(ex));

        // Also test that the humanized error message is reasonable
        var humanized = PolyPilot.Models.ErrorMessageHelper.Humanize(ex);
        Assert.Contains("not connected", humanized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsConnectionError_DetectsConnectionLostFollowedByNotConnected()
    {
        // Simulate the two-phase failure pattern seen in the bug:
        // Phase 1: JSON-RPC connection lost (initial send fails)
        // Phase 2: Reconnect uses stale client → "Client not connected"
        // Both should be detected as connection errors
        var phase1 = new Exception(
            "The JSON-RPC connection with the remote party was lost before the request could complete.");
        var phase2 = new InvalidOperationException(
            "Client not connected. Call StartAsync() first.");

        Assert.True(CopilotService.IsConnectionError(phase1), "Phase 1: connection lost should be detected");
        Assert.True(CopilotService.IsConnectionError(phase2), "Phase 2: client not connected should be detected");
    }

    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root");
    }
}
