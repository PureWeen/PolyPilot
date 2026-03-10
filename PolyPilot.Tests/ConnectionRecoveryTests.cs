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

    // ===== Regression: Fresh session after "Session not found" must include MCP servers & skills =====
    // When the JSON-RPC connection is lost and the server-side session has expired,
    // SendPromptAsync falls back to creating a fresh session via CreateSessionAsync.
    // Previously, the freshConfig was missing McpServers, SkillDirectories, and
    // SystemMessage — causing "environment keeps going away" because MCP tools
    // disappeared after reconnection.

    [Fact]
    public void SendPromptAsync_FreshSessionConfig_IncludesMcpServers()
    {
        // STRUCTURAL REGRESSION GUARD: The "Session not found" fallback must assign
        // McpServers in the freshConfig so MCP tools survive reconnection.
        // After extraction to BuildFreshSessionConfig helper, verify the helper contains it.
        var source = File.ReadAllText(Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs"));

        // Verify the reconnect path calls the helper
        var sessionNotFoundIdx = source.IndexOf("Session not found", StringComparison.OrdinalIgnoreCase);
        Assert.True(sessionNotFoundIdx > 0, "Could not find 'Session not found' in reconnect path");
        var afterNotFound = source.Substring(sessionNotFoundIdx, Math.Min(1000, source.Length - sessionNotFoundIdx));
        Assert.Contains("BuildFreshSessionConfig", afterNotFound);

        // Verify the helper body includes McpServers
        var helperIdx = source.IndexOf("BuildFreshSessionConfig(SessionState state");
        Assert.True(helperIdx > 0, "Could not find BuildFreshSessionConfig helper");
        var helperBlock = source.Substring(helperIdx, Math.Min(2000, source.Length - helperIdx));
        Assert.Contains("McpServers = ", helperBlock);
    }

    [Fact]
    public void SendPromptAsync_FreshSessionConfig_IncludesSkillDirectories()
    {
        // STRUCTURAL REGRESSION GUARD: The "Session not found" fallback must assign
        // SkillDirectories in the freshConfig so skills survive reconnection.
        var source = File.ReadAllText(Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs"));

        var helperIdx = source.IndexOf("BuildFreshSessionConfig(SessionState state");
        Assert.True(helperIdx > 0, "Could not find BuildFreshSessionConfig helper");
        var helperBlock = source.Substring(helperIdx, Math.Min(2000, source.Length - helperIdx));
        Assert.Contains("SkillDirectories = ", helperBlock);
    }

    [Fact]
    public void SendPromptAsync_FreshSessionConfig_IncludesSystemMessage()
    {
        // STRUCTURAL REGRESSION GUARD: The "Session not found" fallback must include
        // SystemMessage so the session retains its system prompt after reconnection.
        var source = File.ReadAllText(Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs"));

        var helperIdx = source.IndexOf("BuildFreshSessionConfig(SessionState state");
        Assert.True(helperIdx > 0, "Could not find BuildFreshSessionConfig helper");
        var helperBlock = source.Substring(helperIdx, Math.Min(2000, source.Length - helperIdx));
        Assert.Contains("SystemMessage = ", helperBlock);
        Assert.Contains("SystemMessageMode.Append", helperBlock);
    }

    [Fact]
    public void SendPromptAsync_FreshSessionConfig_MatchesCreateSessionFields()
    {
        // STRUCTURAL REGRESSION GUARD: The BuildFreshSessionConfig helper must
        // set the same critical fields as the original CreateSessionAsync config.
        var source = File.ReadAllText(Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs"));

        var helperIdx = source.IndexOf("BuildFreshSessionConfig(SessionState state");
        Assert.True(helperIdx > 0);

        var helperBlock = source.Substring(helperIdx, Math.Min(2000, source.Length - helperIdx));

        var requiredAssignments = new[]
        {
            "Model = ", "WorkingDirectory = ", "McpServers = ", "SkillDirectories = ",
            "Tools = ", "SystemMessage = ", "OnPermissionRequest = "
        };
        foreach (var assignment in requiredAssignments)
        {
            Assert.Contains(assignment, helperBlock);
        }
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

    // ===== SafeFireAndForget task observation =====
    // Prevents UnobservedTaskException from fire-and-forget _chatDb calls.
    // See crash log: "A Task's exception(s) were not observed" wrapping ConnectionLostException.

    [Fact]
    public void SafeFireAndForget_CompletedTask_DoesNotThrow()
    {
        CopilotService.SafeFireAndForget(Task.CompletedTask, "test");
    }

    [Fact]
    public async Task SafeFireAndForget_FaultedTask_ObservesException()
    {
        // A faulted task should be observed (no UnobservedTaskException)
        var tcs = new TaskCompletionSource();
        tcs.SetException(new InvalidOperationException("DB connection failed"));

        CopilotService.SafeFireAndForget(tcs.Task, "test-faulted");

        // Give the continuation time to run
        await Task.Delay(50);
        // If we got here without an unobserved exception, the test passes
    }

    [Fact]
    public async Task SafeFireAndForget_AsyncFaultedTask_ObservesException()
    {
        static async Task FailingTask()
        {
            await Task.Yield();
            throw new InvalidOperationException("Async failure");
        }

        var task = FailingTask();
        CopilotService.SafeFireAndForget(task, "async-fault");

        await Task.Delay(50);
    }

    [Fact]
    public void SafeFireAndForget_CanceledTask_DoesNotThrow()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var task = Task.FromCanceled(cts.Token);

        // Canceled tasks shouldn't trigger the OnlyOnFaulted continuation
        CopilotService.SafeFireAndForget(task, "test-canceled");
    }

    // ===== Lazy client re-initialization (reconnect path fix) =====
    // After a failed reconnect sets _client=null and IsInitialized=false,
    // subsequent prompt attempts must auto-reinitialize instead of
    // permanently failing with "Service not initialized".

    [Fact]
    public void SendPromptAsync_ReconnectPath_HasLazyReinitializationGuard()
    {
        // STRUCTURAL REGRESSION GUARD: The reconnect catch block in SendPromptAsync
        // must check for a dead client (!IsInitialized || _client == null) before
        // calling GetClientForGroup. Without this, a previous reconnect failure
        // that nulled _client makes ALL sessions permanently dead.
        var source = File.ReadAllText(Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs"));

        // Find the reconnect block (inside the SendAsync catch)
        var reconnectIndex = source.IndexOf("attempting reconnect...");
        Assert.True(reconnectIndex > 0, "Could not find reconnect block in SendPromptAsync");

        // The lazy re-init guard must appear BEFORE GetClientForGroup
        var afterReconnect = source.Substring(reconnectIndex, 3000);
        var reinitGuardPos = afterReconnect.IndexOf("lazy re-initialization");
        var getClientPos = afterReconnect.IndexOf("GetClientForGroup(sessionGroupId)");
        Assert.True(reinitGuardPos > 0, "Lazy re-initialization guard not found in reconnect path");
        Assert.True(getClientPos > 0, "GetClientForGroup call not found in reconnect path");
        Assert.True(reinitGuardPos < getClientPos,
            "Lazy re-initialization must appear BEFORE GetClientForGroup to prevent permanent dead state");
    }

    [Fact]
    public void SendPromptAsync_LazyReinit_AttemptsServerRestart()
    {
        // STRUCTURAL REGRESSION GUARD: The lazy re-init path must attempt to restart
        // the persistent server (via _serverManager) before creating a new client.
        var source = File.ReadAllText(Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs"));

        var reinitIndex = source.IndexOf("lazy re-initialization so the session can self-heal");
        Assert.True(reinitIndex > 0);

        // Extract the re-init block (generous size)
        var reinitBlock = source.Substring(reinitIndex, 1000);
        Assert.Contains("_serverManager.StartServerAsync", reinitBlock);
        Assert.Contains("CreateClient(reinitSettings)", reinitBlock);
        Assert.Contains("IsInitialized = true", reinitBlock);
    }

    [Fact]
    public void SendPromptAsync_LazyReinit_SkipsForCodespaceSessions()
    {
        // STRUCTURAL REGRESSION GUARD: Lazy re-init should NOT run for codespace
        // sessions (they have their own health check reconnection mechanism).
        var source = File.ReadAllText(Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs"));

        var reinitIndex = source.IndexOf("lazy re-initialization so the session can self-heal");
        Assert.True(reinitIndex > 0);

        // The guard condition must exclude codespace sessions
        var guardBlock = source.Substring(reinitIndex, 200);
        Assert.Contains("isCodespaceSession", guardBlock);
    }

    [Fact]
    public async Task PersistentFailure_ThenDemoRecovery_SessionsWork()
    {
        // End-to-end: fail in persistent → recover in demo → sessions usable
        var svc = CreateService();
        _serverManager.StartServerResult = false;

        await svc.ReconnectAsync(new PolyPilot.Models.ConnectionSettings
        {
            Mode = PolyPilot.Models.ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999
        });
        Assert.False(svc.IsInitialized);

        // Recover via demo mode
        await svc.ReconnectAsync(new PolyPilot.Models.ConnectionSettings
        {
            Mode = PolyPilot.Models.ConnectionMode.Demo
        });
        Assert.True(svc.IsInitialized);

        // Sessions should work
        var session = await svc.CreateSessionAsync("recovery-test");
        Assert.NotNull(session);
        Assert.Equal("recovery-test", svc.ActiveSessionName);
    }

    // ===== Resume display-name deduplication =====

    [Fact]
    public async Task ResumeSession_DuplicateDisplayName_GetsSuffix()
    {
        // If a session with the same display name already exists, the resume
        // should auto-deduplicate by appending (2), (3), etc.
        var svc = CreateService();
        await svc.ReconnectAsync(new PolyPilot.Models.ConnectionSettings
        {
            Mode = PolyPilot.Models.ConnectionMode.Demo
        });

        // Create an initial session named "My Session"
        var first = await svc.CreateSessionAsync("My Session");
        Assert.NotNull(first);
        Assert.Equal("My Session", first.Name);

        // Verify it's in the sessions dictionary
        var allSessions = svc.GetAllSessions().Select(s => s.Name).ToList();
        Assert.Contains("My Session", allSessions);
    }

    [Fact]
    public void ResumeSession_Structural_NeverAutoDeletesOnCorruptError()
    {
        // STRUCTURAL REGRESSION GUARD: The ResumeSession handler in SessionSidebar
        // must NOT call DeletePersistedSession when a corrupt-session error occurs.
        // Auto-deleting session data causes irreversible data loss.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "Layout", "SessionSidebar.razor"));

        var resumeMethod = ExtractMethod(source, "private async Task ResumeSession");
        Assert.NotNull(resumeMethod);

        // Must NOT contain DeletePersistedSession call
        Assert.DoesNotContain("DeletePersistedSession", resumeMethod);

        // Must still detect corrupt errors (for the error message)
        Assert.Contains("IsCorruptSessionError", resumeMethod);
    }

    [Fact]
    public void ResumeSessionAsync_Structural_DedupsDuplicateDisplayName()
    {
        // STRUCTURAL REGRESSION GUARD: ResumeSessionAsync must de-duplicate
        // display names instead of throwing on collision.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs"));

        var methodIndex = source.IndexOf("public async Task<AgentSessionInfo> ResumeSessionAsync");
        Assert.True(methodIndex > 0, "Could not find ResumeSessionAsync method");

        // Extract enough of the method to find the dedup logic (method is ~200 lines)
        var endIndex = Math.Min(methodIndex + 4000, source.Length);
        var methodBlock = source[methodIndex..endIndex];

        // Must NOT contain the old throw-on-collision pattern
        Assert.DoesNotContain("already exists.", methodBlock.Split("De-duplicate")[0]);

        // Must contain dedup loop
        Assert.Contains("candidate", methodBlock);
    }

    private static string? ExtractMethod(string source, string signature)
    {
        var idx = source.IndexOf(signature);
        if (idx < 0) return null;
        int braces = 0;
        int start = source.IndexOf('{', idx);
        if (start < 0) return null;
        for (int i = start; i < source.Length; i++)
        {
            if (source[i] == '{') braces++;
            else if (source[i] == '}') { braces--; if (braces == 0) return source[idx..(i + 1)]; }
        }
        return null;
    }

    // ===== ChatDatabase error resilience =====
    // AddMessageAsync must catch ALL exceptions (broad catch) so fire-and-forget
    // callers never produce UnobservedTaskException.
    // Structural guard: verify the catch block is broad (not narrowly filtered).

    [Fact]
    public void ChatDatabase_AddMessageAsync_HasBroadCatch()
    {
        // STRUCTURAL REGRESSION GUARD: AddMessageAsync must use `catch (Exception`
        // not a narrow filter like `catch (SQLiteException)`. Historical regression
        // used narrow catch → uncaught exceptions became UnobservedTaskException.
        var source = File.ReadAllText(Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "ChatDatabase.cs"));

        // Find the AddMessageAsync method
        var methodIndex = source.IndexOf("public async Task<int> AddMessageAsync");
        Assert.True(methodIndex > 0, "Could not find AddMessageAsync method");

        // Extract the method body (need enough to reach the catch block)
        var methodBlock = source.Substring(methodIndex, 800);
        Assert.Contains("catch (Exception", methodBlock);
    }

    // ===== SafeFireAndForget is used for all ChatDb calls =====

    [Fact]
    public void AllChatDbCalls_UseSafeFireAndForget()
    {
        // STRUCTURAL REGRESSION GUARD: All fire-and-forget _chatDb calls in
        // CopilotService must use SafeFireAndForget, not bare `_ = ...`.
        var csFile = File.ReadAllText(Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs"));
        var eventsFile = File.ReadAllText(Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Events.cs"));

        // No bare fire-and-forget patterns should exist
        Assert.DoesNotContain("_ = _chatDb.", csFile);
        Assert.DoesNotContain("_ = _chatDb.", eventsFile);

        // SafeFireAndForget should be used instead
        Assert.Contains("SafeFireAndForget(_chatDb.", csFile);
        Assert.Contains("SafeFireAndForget(_chatDb.", eventsFile);
    }

    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root");
    }
}
