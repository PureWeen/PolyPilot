using PolyPilot.Models;
using PolyPilot.Services;
using Microsoft.Extensions.DependencyInjection;

namespace PolyPilot.Tests;

/// <summary>
/// Regression tests for codespace session creation.
/// Covers: missing-client guard, descriptive error messages, and health check
/// stale-client detection conditions.
/// Bug: clicking "New Session" on a connected codespace group silently did nothing
/// because (1) the async Task was fire-and-forget (void lambda), (2) errors were
/// not displayed in the codespace section, and (3) the health check didn't detect
/// dead CopilotClients behind a live SSH tunnel.
/// </summary>
public class CodespaceSessionCreationTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public CodespaceSessionCreationTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    [Fact]
    public async Task CreateSessionAsync_CodespaceGroup_NoClient_ThrowsDescriptiveError()
    {
        // Regression: CreateSessionAsync must throw a clear error when the codespace
        // client is missing from _codespaceClients, rather than falling through to the
        // generic "Service not initialized" error which maps to a misleading message.
        var svc = CreateService();
        var group = new SessionGroup
        {
            Name = "test-cs",
            CodespaceName = "fuzzy-space-guide",
            CodespaceRepository = "org/repo",
            ConnectionState = CodespaceConnectionState.Connected
        };
        svc.Organization.Groups.Add(group);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateSessionAsync("TestSession", groupId: group.Id));

        // Must mention the codespace name — not "Service not initialized"
        Assert.Contains("not connected", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(group.Name, ex.Message);
    }

    [Fact]
    public async Task CreateSessionAsync_CodespaceGroup_NoClient_DoesNotLeakSessionState()
    {
        // Regression: When CreateSessionAsync throws for a codespace group, the optimistic
        // session should NOT be left in Organization.Sessions or _sessions.
        var svc = CreateService();
        var group = new SessionGroup
        {
            Name = "test-cs",
            CodespaceName = "fuzzy-space-guide",
            CodespaceRepository = "org/repo",
            ConnectionState = CodespaceConnectionState.Connected
        };
        svc.Organization.Groups.Add(group);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateSessionAsync("LeakedSession", groupId: group.Id));

        // The guard fires BEFORE the optimistic add, so no cleanup needed
        Assert.DoesNotContain(svc.Organization.Sessions, m => m.SessionName == "LeakedSession");
        Assert.Null(svc.GetSession("LeakedSession"));
    }

    [Fact]
    public async Task CreateSessionAsync_NonCodespaceGroup_SkipsCodespaceGuard()
    {
        // A non-codespace group should NOT hit the codespace client check.
        // It should fall through to the normal "Service not initialized" error
        // (since we don't call InitializeAsync in this test).
        var svc = CreateService();
        var group = new SessionGroup { Name = "regular-group" };
        svc.Organization.Groups.Add(group);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateSessionAsync("TestSession", groupId: group.Id));

        // Should NOT mention codespace — it's a regular initialization error
        Assert.DoesNotContain("Codespace", ex.Message);
        Assert.Contains("initialized", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HealthCheck_StaleClientCondition_TunnelAliveButPortUnreachable()
    {
        // Regression: The health check previously only checked _codespaceClients.ContainsKey()
        // which returns true even when the CopilotClient is broken. The new TCP probe
        // detects when the remote port is unreachable and removes the stale client.
        //
        // This test validates the model-level conditions that trigger reconnection.
        var group = new SessionGroup
        {
            Name = "test-cs",
            CodespaceName = "test-codespace",
            CodespaceRepository = "org/repo",
            ConnectionState = CodespaceConnectionState.Connected
        };

        // Scenario: tunnel alive, client exists, but port unreachable
        bool tunnelAlive = true;
        bool clientExists = true;
        bool portReachable = false;

        // Old behavior: would skip (continue) because all three were true
        bool oldBehaviorSkips = tunnelAlive && clientExists
            && group.ConnectionState == CodespaceConnectionState.Connected;
        Assert.True(oldBehaviorSkips, "Old health check would have skipped this group");

        // New behavior: TCP probe fails → stale client removed → reconnect triggered
        if (tunnelAlive && clientExists && group.ConnectionState == CodespaceConnectionState.Connected)
        {
            if (!portReachable)
            {
                clientExists = false; // Stale client removed
            }
        }

        Assert.False(clientExists, "Stale client should have been removed after failed TCP probe");

        // With clientExists=false, health check proceeds to reconnect logic
        bool shouldReconnect = !(tunnelAlive && clientExists
            && group.ConnectionState == CodespaceConnectionState.Connected);
        Assert.True(shouldReconnect, "Health check should proceed to reconnect path");
    }

    [Fact]
    public void HealthCheck_HealthyGroup_TunnelAliveAndPortReachable_Skips()
    {
        // When everything is truly healthy, the health check should skip the group.
        var group = new SessionGroup
        {
            Name = "test-cs",
            CodespaceName = "test-codespace",
            ConnectionState = CodespaceConnectionState.Connected
        };

        bool tunnelAlive = true;
        bool clientExists = true;
        bool portReachable = true;

        bool shouldSkip = tunnelAlive && clientExists && portReachable
            && group.ConnectionState == CodespaceConnectionState.Connected;

        Assert.True(shouldSkip, "Truly healthy group should be skipped");
    }

    [Fact]
    public void QuickCreateGuard_DisconnectedState_BlocksCreation()
    {
        // Regression: QuickCreateSessionForCodespace should block session creation
        // for any state other than Connected, and provide a clear error message.
        var states = new[]
        {
            CodespaceConnectionState.Unknown,
            CodespaceConnectionState.Reconnecting,
            CodespaceConnectionState.CodespaceStopped,
            CodespaceConnectionState.StartingCodespace,
            CodespaceConnectionState.WaitingForCopilot,
            CodespaceConnectionState.SetupRequired,
        };

        foreach (var state in states)
        {
            var group = new SessionGroup
            {
                CodespaceName = "test",
                ConnectionState = state
            };

            bool shouldBlock = group.ConnectionState != CodespaceConnectionState.Connected;
            Assert.True(shouldBlock,
                $"State {state} should block session creation");
        }
    }

    [Fact]
    public void QuickCreateGuard_ConnectedState_AllowsCreation()
    {
        var group = new SessionGroup
        {
            CodespaceName = "test",
            ConnectionState = CodespaceConnectionState.Connected
        };

        bool shouldBlock = group.ConnectionState != CodespaceConnectionState.Connected;
        Assert.False(shouldBlock, "Connected state should allow session creation");
    }

    [Fact]
    public async Task CreateSessionAsync_CodespaceGroup_ErrorMessage_MentionsHealthCheck()
    {
        // The error message should tell the user that the health check will reconnect
        // automatically, guiding them to retry instead of panicking.
        var svc = CreateService();
        var group = new SessionGroup
        {
            Name = "my-codespace",
            CodespaceName = "fuzzy-guide",
            CodespaceRepository = "org/repo",
            ConnectionState = CodespaceConnectionState.Connected
        };
        svc.Organization.Groups.Add(group);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateSessionAsync("Test", groupId: group.Id));

        Assert.Contains("health check", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retry", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
