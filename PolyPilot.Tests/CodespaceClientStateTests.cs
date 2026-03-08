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
/// Tests: IsInitialized survival across codespace state changes, error messages,
/// and edge cases (whitespace names, nonexistent groups, end-to-end local sessions).
/// </summary>
public class CodespaceClientStateTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public CodespaceClientStateTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    // ─── IsInitialized must NEVER be clobbered by codespace state ───

    [Fact]
    public async Task IsInitialized_SurvivesMultipleCodespaceGroupStateChanges()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        Assert.True(svc.IsInitialized);

        var csGroup = new SessionGroup
        {
            Name = "cycling-cs",
            CodespaceName = "codespace-cycling",
            CodespaceRepository = "org/cycling",
        };
        svc.Organization.Groups.Add(csGroup);

        foreach (var state in new[]
        {
            CodespaceConnectionState.Unknown,
            CodespaceConnectionState.Reconnecting,
            CodespaceConnectionState.CodespaceStopped,
            CodespaceConnectionState.StartingCodespace,
            CodespaceConnectionState.WaitingForCopilot,
            CodespaceConnectionState.SetupRequired,
            CodespaceConnectionState.Connected,
            CodespaceConnectionState.Reconnecting,
        })
        {
            csGroup.ConnectionState = state;
            Assert.True(svc.IsInitialized, $"IsInitialized must survive codespace state change to {state}");
        }
    }

    [Fact]
    public async Task IsInitialized_SurvivesCodespaceResumeFailure()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        Assert.True(svc.IsInitialized);

        var csGroup = new SessionGroup
        {
            Name = "fail-cs",
            CodespaceName = "codespace-fail",
            CodespaceRepository = "org/fail",
            ConnectionState = CodespaceConnectionState.Reconnecting,
        };
        svc.Organization.Groups.Add(csGroup);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ResumeSessionAsync(
                Guid.NewGuid().ToString(), "resume-fail",
                groupId: csGroup.Id,
                cancellationToken: CancellationToken.None));

        Assert.True(svc.IsInitialized,
            "CRITICAL: IsInitialized was set to false by a codespace resume failure. " +
            "This will block ALL local sessions with 'Copilot is not connected yet.'");
    }

    [Fact]
    public async Task IsInitialized_SurvivesMultipleCodespaceResumeFailures()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        Assert.True(svc.IsInitialized);

        for (int i = 0; i < 5; i++)
        {
            var csGroup = new SessionGroup
            {
                Name = $"multi-fail-{i}",
                CodespaceName = $"codespace-multi-fail-{i}",
                CodespaceRepository = $"org/multi-fail-{i}",
                ConnectionState = CodespaceConnectionState.Reconnecting,
            };
            svc.Organization.Groups.Add(csGroup);

            try
            {
                await svc.ResumeSessionAsync(
                    Guid.NewGuid().ToString(), $"resume-multi-{i}",
                    groupId: csGroup.Id,
                    cancellationToken: CancellationToken.None);
            }
            catch (InvalidOperationException) { /* expected */ }
        }

        Assert.True(svc.IsInitialized,
            "CRITICAL: IsInitialized must survive repeated codespace failures");
    }

    // ─── Error message quality ───

    [Fact]
    public async Task CodespaceResumeError_DoesNotContain_NotInitialized()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var csGroup = new SessionGroup
        {
            Name = "error-msg-cs",
            CodespaceName = "codespace-error-msg",
            CodespaceRepository = "org/error-msg",
            ConnectionState = CodespaceConnectionState.Reconnecting,
        };
        svc.Organization.Groups.Add(csGroup);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ResumeSessionAsync(
                Guid.NewGuid().ToString(), "msg-test",
                groupId: csGroup.Id,
                cancellationToken: CancellationToken.None));

        Assert.DoesNotContain("not initialized", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Service not initialized", ex.Message);
        Assert.Contains("health check", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CodespaceResumeError_SuggestsRetry()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var csGroup = new SessionGroup
        {
            Name = "retry-cs",
            CodespaceName = "codespace-retry",
            CodespaceRepository = "org/retry",
            ConnectionState = CodespaceConnectionState.Reconnecting,
        };
        svc.Organization.Groups.Add(csGroup);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ResumeSessionAsync(
                Guid.NewGuid().ToString(), "retry-test",
                groupId: csGroup.Id,
                cancellationToken: CancellationToken.None));

        Assert.Contains("retry", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Edge cases ───

    [Fact]
    public async Task ResumeSession_CodespaceGroupWithWhitespaceCodespaceName_TreatedAsNonCodespace()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var group = new SessionGroup { Name = "ws-group", CodespaceName = "  " };
        Assert.False(group.IsCodespace);
        svc.Organization.Groups.Add(group);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ResumeSessionAsync(
                Guid.NewGuid().ToString(), "ws-resume",
                groupId: group.Id,
                cancellationToken: CancellationToken.None));

        Assert.DoesNotContain("Codespace", ex.Message);
    }

    [Fact]
    public async Task ResumeSession_NonexistentGroupId_DoesNotTriggerCodespaceGuard()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ResumeSessionAsync(
                Guid.NewGuid().ToString(), "phantom-resume",
                groupId: "nonexistent-group-id",
                cancellationToken: CancellationToken.None));

        Assert.DoesNotContain("Codespace", ex.Message);
    }

    [Fact]
    public async Task CreateAndResume_LocalSessions_UnaffectedByCodespacePresence()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("early-bird");
        Assert.NotNull(session);

        svc.Organization.Groups.Add(new SessionGroup
        {
            Name = "late-cs",
            CodespaceName = "codespace-late",
            CodespaceRepository = "org/late",
            ConnectionState = CodespaceConnectionState.Reconnecting,
        });

        var session2 = await svc.CreateSessionAsync("still-fine");
        Assert.NotNull(session2);
        Assert.True(svc.IsInitialized);

        var allSessions = svc.GetAllSessions().ToList();
        Assert.Contains(allSessions, s => s.Name == "early-bird");
        Assert.Contains(allSessions, s => s.Name == "still-fine");
    }
}
