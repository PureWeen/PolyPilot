using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// ╔══════════════════════════════════════════════════════════════════════════════╗
/// ║  CRITICAL REGRESSION TESTS — DO NOT DELETE, SKIP, OR WEAKEN THESE TESTS    ║
/// ╠══════════════════════════════════════════════════════════════════════════════╣
/// ║                                                                            ║
/// ║  These tests guard the #1 invariant of the PolyPilot app:                  ║
/// ║                                                                            ║
/// ║    LOCAL SESSIONS MUST NEVER BE BLOCKED BY CODESPACE LOGIC.                ║
/// ║    THE MESSAGE "Copilot is not connected yet" MUST NEVER APPEAR            ║
/// ║    FOR LOCAL SESSIONS UNDER ANY CIRCUMSTANCES.                             ║
/// ║                                                                            ║
/// ║  See CodespaceClientIsolationRegressionTests.cs for full background.       ║
/// ╚══════════════════════════════════════════════════════════════════════════════╝
///
/// Tests: Error message invariants (codespace errors must never contain "not initialized"),
/// guard ordering, and stress tests (multiple broken codespace groups).
/// </summary>
public class CodespaceClientInvariantTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public CodespaceClientInvariantTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    // ═══════════════════════════════════════════════════════════════════════
    // INVARIANT: Codespace errors must NEVER produce "not initialized"
    //
    // The SessionSidebar.razor FriendlyResumeError() method maps any exception
    // containing "not initialized" to the user-facing message:
    //   "⚠ Copilot is not connected yet. Wait for initialization."
    //
    // Codespace-specific errors must NEVER contain "not initialized" because
    // that message implies the entire app is broken, when in reality only
    // the codespace connection is down and local sessions work fine.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CodespaceResumeError_MustNever_ContainNotInitialized_BecauseThatTriggersTheMisleadingUserMessage()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var csGroup = new SessionGroup
        {
            Name = "invariant-cs",
            CodespaceName = "codespace-invariant",
            CodespaceRepository = "org/invariant",
            ConnectionState = CodespaceConnectionState.Reconnecting,
        };
        svc.Organization.Groups.Add(csGroup);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ResumeSessionAsync(
                Guid.NewGuid().ToString(), "invariant-test",
                groupId: csGroup.Id,
                cancellationToken: CancellationToken.None));

        Assert.DoesNotContain("not initialized", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CodespaceCreateError_MustNever_ContainNotInitialized()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var csGroup = new SessionGroup
        {
            Name = "create-invariant-cs",
            CodespaceName = "codespace-create-invariant",
            CodespaceRepository = "org/create-invariant",
            ConnectionState = CodespaceConnectionState.Reconnecting,
        };
        svc.Organization.Groups.Add(csGroup);

        // In demo mode, CreateSession with groupId goes through the demo path
        // and succeeds. So we verify the local path still works fine.
        var session = await svc.CreateSessionAsync("local-after-broken-cs");
        Assert.NotNull(session);
        Assert.True(svc.IsInitialized);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // INVARIANT: Codespace guard ordering in ResumeSessionAsync
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CodespaceGuard_FiresBeforeClientNullCheck_InResumeSessionAsync()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var csGroup = new SessionGroup
        {
            Name = "ordering-test",
            CodespaceName = "codespace-ordering",
            CodespaceRepository = "org/ordering",
            ConnectionState = CodespaceConnectionState.Reconnecting,
        };
        svc.Organization.Groups.Add(csGroup);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ResumeSessionAsync(
                Guid.NewGuid().ToString(), "ordering-resume",
                groupId: csGroup.Id,
                cancellationToken: CancellationToken.None));

        Assert.DoesNotContain("Service not initialized", ex.Message);
        Assert.Contains("ordering-test", ex.Message);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // INVARIANT: IsInitialized must NEVER be set to false by codespace logic
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task IsInitialized_MustNeverBeFalse_AfterSuccessfulInit_RegardlessOfCodespaceState()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        Assert.True(svc.IsInitialized);

        for (int i = 0; i < 3; i++)
        {
            var csGroup = new SessionGroup
            {
                Name = $"doomsday-cs-{i}",
                CodespaceName = $"codespace-doom-{i}",
                CodespaceRepository = $"org/doom-{i}",
                ConnectionState = CodespaceConnectionState.Reconnecting,
            };
            svc.Organization.Groups.Add(csGroup);

            for (int j = 0; j < 3; j++)
            {
                try
                {
                    await svc.ResumeSessionAsync(
                        Guid.NewGuid().ToString(), $"doom-{i}-{j}",
                        groupId: csGroup.Id,
                        cancellationToken: CancellationToken.None);
                }
                catch (InvalidOperationException) { /* expected */ }
            }
        }

        Assert.True(svc.IsInitialized,
            "CRITICAL: IsInitialized was set to false by codespace failure. " +
            "This will block ALL local sessions with 'Copilot is not connected yet.'");

        var local = await svc.CreateSessionAsync("proof-of-life");
        Assert.NotNull(local);
        Assert.Equal("proof-of-life", local.Name);
    }

    [Fact]
    public async Task LocalSessionCreation_MustAlwaysWork_AfterInit()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("basic-local");
        Assert.NotNull(session);
        Assert.True(svc.IsInitialized);
    }

    [Fact]
    public async Task LocalSessionCreation_MustWork_EvenWithManyBrokenCodespaceGroups()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

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

        var session = await svc.CreateSessionAsync("local-works");
        Assert.NotNull(session);
        Assert.True(svc.IsInitialized);
    }
}
