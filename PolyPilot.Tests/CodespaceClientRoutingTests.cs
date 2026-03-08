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
/// Tests: Create/Resume session routing when codespace groups are present.
/// </summary>
public class CodespaceClientRoutingTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public CodespaceClientRoutingTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    [Fact]
    public async Task CreateSession_ForDisconnectedCodespace_ThrowsDescriptiveError_NotServiceNotInitialized()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        Assert.True(svc.IsInitialized);

        var csGroup = new SessionGroup
        {
            Name = "my-codespace",
            CodespaceName = "codespace-abc",
            CodespaceRepository = "org/repo",
            ConnectionState = CodespaceConnectionState.Reconnecting,
        };
        svc.Organization.Groups.Add(csGroup);

        var localSession = await svc.CreateSessionAsync("still-works-locally");
        Assert.NotNull(localSession);
        Assert.True(svc.IsInitialized, "IsInitialized must remain true after codespace group is added");
    }

    [Fact]
    public async Task CreateSession_LocalSession_SucceedsAfterCodespaceGroupAdded()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        svc.Organization.Groups.Add(new SessionGroup
        {
            Name = "cs-reconnecting",
            CodespaceName = "codespace-1",
            CodespaceRepository = "org/repo1",
            ConnectionState = CodespaceConnectionState.Reconnecting,
        });
        svc.Organization.Groups.Add(new SessionGroup
        {
            Name = "cs-stopped",
            CodespaceName = "codespace-2",
            CodespaceRepository = "org/repo2",
            ConnectionState = CodespaceConnectionState.CodespaceStopped,
        });

        var session1 = await svc.CreateSessionAsync("local-1");
        Assert.NotNull(session1);

        var session2 = await svc.CreateSessionAsync("local-2");
        Assert.NotNull(session2);

        Assert.True(svc.IsInitialized, "IsInitialized must survive codespace group presence");
    }

    [Fact]
    public async Task ResumeSession_ForDisconnectedCodespace_ThrowsCodespaceError_NotInitializationError()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var csGroup = new SessionGroup
        {
            Name = "resume-cs",
            CodespaceName = "codespace-resume",
            CodespaceRepository = "org/resume-repo",
            ConnectionState = CodespaceConnectionState.Reconnecting,
        };
        svc.Organization.Groups.Add(csGroup);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ResumeSessionAsync(
                Guid.NewGuid().ToString(), "test-resume",
                groupId: csGroup.Id,
                cancellationToken: CancellationToken.None));

        Assert.DoesNotContain("Service not initialized", ex.Message);
        Assert.Contains("not connected", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(svc.IsInitialized, "IsInitialized must survive codespace resume failure");
    }

    [Fact]
    public async Task ResumeSession_ForDisconnectedCodespace_ErrorMentionsGroupName()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var csGroup = new SessionGroup
        {
            Name = "named-codespace",
            CodespaceName = "codespace-named",
            CodespaceRepository = "org/named",
            ConnectionState = CodespaceConnectionState.WaitingForCopilot,
        };
        svc.Organization.Groups.Add(csGroup);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ResumeSessionAsync(
                Guid.NewGuid().ToString(), "test-resume-named",
                groupId: csGroup.Id,
                cancellationToken: CancellationToken.None));

        Assert.Contains("named-codespace", ex.Message);
    }

    [Fact]
    public async Task ResumeSession_WithNullGroupId_DoesNotTriggerCodespaceGuard()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        svc.Organization.Groups.Add(new SessionGroup
        {
            Name = "cs-present-but-unused",
            CodespaceName = "codespace-unused",
            CodespaceRepository = "org/unused",
            ConnectionState = CodespaceConnectionState.Reconnecting,
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ResumeSessionAsync(
                Guid.NewGuid().ToString(), "local-resume",
                groupId: null,
                cancellationToken: CancellationToken.None));

        Assert.Contains("not initialized", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Codespace", ex.Message);
    }

    [Fact]
    public async Task ResumeSession_ForNonCodespaceGroup_DoesNotTriggerCodespaceGuard()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var regularGroup = new SessionGroup { Name = "regular-group", CodespaceName = null };
        Assert.False(regularGroup.IsCodespace);
        svc.Organization.Groups.Add(regularGroup);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ResumeSessionAsync(
                Guid.NewGuid().ToString(), "regular-resume",
                groupId: regularGroup.Id,
                cancellationToken: CancellationToken.None));

        Assert.Contains("not initialized", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Codespace", ex.Message);
    }
}
