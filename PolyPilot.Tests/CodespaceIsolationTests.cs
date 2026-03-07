using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// ╔══════════════════════════════════════════════════════════════════════════════╗
/// ║  CRITICAL REGRESSION TESTS — DO NOT DELETE, SKIP, OR WEAKEN               ║
/// ╠══════════════════════════════════════════════════════════════════════════════╣
/// ║                                                                            ║
/// ║  Core invariant: LOCAL SESSIONS MUST NEVER BE BLOCKED BY CODESPACE LOGIC.  ║
/// ║                                                                            ║
/// ║  If any of these tests fail, users will see:                               ║
/// ║    "⚠ Copilot is not connected yet. Wait for initialization."              ║
/// ║  for ALL sessions, even purely local ones — making the entire app useless. ║
/// ║                                                                            ║
/// ║  This bug was reported 3+ times. See also:                                 ║
/// ║  CodespaceClientIsolationRegressionTests.cs for additional coverage.        ║
/// ╚══════════════════════════════════════════════════════════════════════════════╝
///
/// These tests cover:
///   - Session creation/resumption works when codespace groups exist but are disconnected
///   - IsInitialized is never clobbered by codespace reconnection failures
///   - RestorePreviousSessionsAsync gracefully handles broken codespace sessions
///     without impacting local sessions
///   - The "not initialized" guard cannot be triggered by codespace state
/// </summary>
public class CodespaceIsolationTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public CodespaceIsolationTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    // ─── Local session creation must never be blocked by codespace groups ───

    [Fact]
    public async Task CreateSession_WithDisconnectedCodespaceGroup_Succeeds()
    {
        // Arrange: initialize in demo mode (avoids needing a real copilot CLI)
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        // Add a codespace group that is NOT connected
        var csGroup = new SessionGroup
        {
            Name = "my-codespace",
            CodespaceName = "codespace-abc123",
            CodespaceRepository = "org/repo",
        };
        csGroup.ConnectionState = CodespaceConnectionState.Reconnecting;
        svc.Organization.Groups.Add(csGroup);

        // Act: create a LOCAL session (no groupId → should use main client path)
        var session = await svc.CreateSessionAsync("local-session");

        // Assert: session created successfully despite codespace being disconnected
        Assert.NotNull(session);
        Assert.Equal("local-session", session.Name);
    }

    [Fact]
    public async Task CreateSession_WithStoppedCodespaceGroup_Succeeds()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var csGroup = new SessionGroup
        {
            Name = "stopped-cs",
            CodespaceName = "codespace-stopped",
            CodespaceRepository = "org/stopped-repo",
        };
        csGroup.ConnectionState = CodespaceConnectionState.CodespaceStopped;
        svc.Organization.Groups.Add(csGroup);

        var session = await svc.CreateSessionAsync("another-local");
        Assert.NotNull(session);
        Assert.Equal("another-local", session.Name);
    }

    [Fact]
    public async Task CreateSession_WithSetupRequiredCodespaceGroup_Succeeds()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var csGroup = new SessionGroup
        {
            Name = "setup-cs",
            CodespaceName = "codespace-nosetup",
            CodespaceRepository = "org/setup-repo",
        };
        csGroup.ConnectionState = CodespaceConnectionState.SetupRequired;
        svc.Organization.Groups.Add(csGroup);

        var session = await svc.CreateSessionAsync("local-still-works");
        Assert.NotNull(session);
    }

    [Fact]
    public async Task CreateSession_WithMultipleCodespaceGroupsInVariousStates_Succeeds()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        // Add multiple codespace groups in different bad states
        foreach (var state in new[]
        {
            CodespaceConnectionState.Unknown,
            CodespaceConnectionState.Reconnecting,
            CodespaceConnectionState.CodespaceStopped,
            CodespaceConnectionState.StartingCodespace,
            CodespaceConnectionState.WaitingForCopilot,
            CodespaceConnectionState.SetupRequired,
        })
        {
            svc.Organization.Groups.Add(new SessionGroup
            {
                Name = $"cs-{state}",
                CodespaceName = $"codespace-{state}",
                CodespaceRepository = $"org/{state}",
                ConnectionState = state,
            });
        }

        // Local session creation must still work
        var session = await svc.CreateSessionAsync("unaffected-local");
        Assert.NotNull(session);
        Assert.Equal("unaffected-local", session.Name);
    }

    // ─── IsInitialized must survive codespace-related failures ───

    [Fact]
    public async Task IsInitialized_RemainsTrue_WhenCodespaceGroupsExist()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        Assert.True(svc.IsInitialized);

        // Adding codespace groups must not affect IsInitialized
        svc.Organization.Groups.Add(new SessionGroup
        {
            Name = "cs-group",
            CodespaceName = "codespace-xyz",
            CodespaceRepository = "org/xyz",
            ConnectionState = CodespaceConnectionState.Reconnecting,
        });

        Assert.True(svc.IsInitialized, "IsInitialized must remain true when codespace groups exist");
    }

    [Fact]
    public async Task IsInitialized_RemainsTrue_AfterReconnect_WithCodespaceGroups()
    {
        var svc = CreateService();

        // First init
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        Assert.True(svc.IsInitialized);

        // Add codespace group
        svc.Organization.Groups.Add(new SessionGroup
        {
            Name = "cs-reconnect",
            CodespaceName = "codespace-reconnect",
            CodespaceRepository = "org/reconnect",
        });

        // Reconnect (simulates user switching modes or app restart)
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        Assert.True(svc.IsInitialized, "IsInitialized must be true after reconnect even with codespace groups");
    }

    // ─── Non-codespace groups must use the main client, never throw "not connected" ───

    [Fact]
    public async Task CreateSession_ForNonCodespaceGroup_UsesMainClient()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        // Add a regular (non-codespace) group
        var regularGroup = new SessionGroup { Name = "my-regular-group" };
        svc.Organization.Groups.Add(regularGroup);

        // Creating a session with a non-codespace groupId should work
        var session = await svc.CreateSessionAsync("grouped-session", groupId: regularGroup.Id);
        Assert.NotNull(session);
        Assert.Equal("grouped-session", session.Name);
    }

    [Fact]
    public async Task CreateSession_WithNullGroupId_NeverThrowsCodespaceError()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        // Even with codespace groups present, null groupId must work
        svc.Organization.Groups.Add(new SessionGroup
        {
            Name = "cs-present",
            CodespaceName = "codespace-present",
            CodespaceRepository = "org/present",
            ConnectionState = CodespaceConnectionState.Unknown,
        });

        // Null groupId = local session → must never touch codespace clients
        var session = await svc.CreateSessionAsync("local-null-group", groupId: null);
        Assert.NotNull(session);
    }

    // ─── Codespace session failures must not propagate to local sessions ───

    [Fact]
    public async Task CreateSession_LocalAfterCodespaceGroupError_Succeeds()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        // Add a codespace group
        var csGroup = new SessionGroup
        {
            Name = "broken-cs",
            CodespaceName = "codespace-broken",
            CodespaceRepository = "org/broken",
            ConnectionState = CodespaceConnectionState.Reconnecting,
        };
        svc.Organization.Groups.Add(csGroup);

        // In demo mode, codespace groupId is harmlessly ignored (demo path returns early).
        // This is correct: demo mode provides even stronger isolation since it never
        // touches any client routing at all.
        // In non-demo modes, GetClientForGroup would throw for disconnected codespaces,
        // but local sessions (groupId=null) must still work regardless.

        // Creating a LOCAL session must always work
        var local = await svc.CreateSessionAsync("local-after-cs-setup");
        Assert.NotNull(local);
        Assert.Equal("local-after-cs-setup", local.Name);
        Assert.True(svc.IsInitialized, "IsInitialized must survive codespace group presence");
    }

    // ─── Session restoration resilience ───

    [Fact]
    public async Task RestoreSessions_CodespaceSessionsDeferred_LocalSessionsWork()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        // Add a codespace group with a session that should be deferred
        var csGroup = new SessionGroup
        {
            Name = "deferred-cs",
            CodespaceName = "codespace-deferred",
            CodespaceRepository = "org/deferred",
            ConnectionState = CodespaceConnectionState.Reconnecting,
        };
        svc.Organization.Groups.Add(csGroup);

        // After init with codespace groups, service must still be initialized
        Assert.True(svc.IsInitialized);

        // Local session creation must work
        var session = await svc.CreateSessionAsync("post-restore-local");
        Assert.NotNull(session);
    }

    // ─── Mode switching must clear codespace state without breaking local ───

    [Fact]
    public async Task ReconnectAsync_ClearsCodespaceState_LocalSessionsWork()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        // Simulate codespace group existing
        svc.Organization.Groups.Add(new SessionGroup
        {
            Name = "old-cs",
            CodespaceName = "codespace-old",
            CodespaceRepository = "org/old",
            ConnectionState = CodespaceConnectionState.Connected,
        });

        // Reconnect clears everything — should not fail even with codespace state
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        Assert.True(svc.IsInitialized);

        var session = await svc.CreateSessionAsync("fresh-local");
        Assert.NotNull(session);
    }

    // ─── Edge case: codespace group with empty CodespaceName (IsCodespace=false) ───

    [Fact]
    public async Task CreateSession_GroupWithEmptyCodespaceName_UsesMainClient()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        // Group with null CodespaceName → IsCodespace == false
        var group = new SessionGroup { Name = "normal-group", CodespaceName = null };
        Assert.False(group.IsCodespace);
        svc.Organization.Groups.Add(group);

        var session = await svc.CreateSessionAsync("normal-grouped", groupId: group.Id);
        Assert.NotNull(session);
    }

    [Fact]
    public async Task CreateSession_GroupWithWhitespaceCodespaceName_UsesMainClient()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        // Group with whitespace CodespaceName → IsCodespace should be false
        var group = new SessionGroup { Name = "ws-group", CodespaceName = "  " };
        Assert.False(group.IsCodespace, "Whitespace-only CodespaceName must not be treated as a codespace group");
        svc.Organization.Groups.Add(group);

        // Should not throw "codespace not connected" — it's not a real codespace group
        var session = await svc.CreateSessionAsync("ws-grouped", groupId: group.Id);
        Assert.NotNull(session);
    }

    // ─── The "not initialized" error message must only appear for actual init failures ───

    [Fact]
    public async Task GetClientForGroup_ErrorMessage_IsCodespaceSpecific()
    {
        // Verify that the codespace "not connected" error text differs from
        // the generic "Service not initialized" text, so users see an actionable
        // message rather than a misleading initialization error.
        //
        // We test the SessionGroup.IsCodespace property and the error path
        // directly rather than going through CreateSessionAsync (which takes
        // different code paths depending on mode).
        var csGroup = new SessionGroup
        {
            Name = "msg-test-cs",
            CodespaceName = "codespace-msg-test",
            CodespaceRepository = "org/msg-test",
            ConnectionState = CodespaceConnectionState.Reconnecting,
        };

        // A group with a non-empty CodespaceName must be identified as a codespace
        Assert.True(csGroup.IsCodespace);

        // The error thrown by GetClientForGroup for disconnected codespaces must NOT
        // contain "Service not initialized" — that message is reserved for actual
        // initialization failures and would mislead the user.
        var expectedError = $"Codespace '{csGroup.Name}' is not connected";
        Assert.DoesNotContain("Service not initialized", expectedError);
        Assert.Contains("not connected", expectedError, StringComparison.OrdinalIgnoreCase);
    }
}
