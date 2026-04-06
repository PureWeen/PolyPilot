using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the post-relaunch orphaned worker diagnostic scan.
/// Verifies that workers with unsynthesized results are detected
/// and a system message is added to the orchestrator session.
/// </summary>
[Collection("BaseDir")]
public class OrphanedWorkerScanTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly IServiceProvider _serviceProvider;

    public OrphanedWorkerScanTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, new RepoManager(), _serviceProvider, _demoService);

    /// <summary>
    /// Inject session entries into _sessions via reflection so GetSession() returns them.
    /// Returns the AgentSessionInfo for further manipulation (adding history, etc).
    /// </summary>
    private static AgentSessionInfo AddDummySession(CopilotService svc, string name)
    {
        var sessionsField = typeof(CopilotService).GetField("_sessions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var dict = sessionsField.GetValue(svc)!;
        var stateType = sessionsField.FieldType.GenericTypeArguments[1]; // SessionState

        var info = new AgentSessionInfo { Name = name, Model = "test-model" };
        var state = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(stateType);
        stateType.GetProperty("Info")!.SetValue(state, info);
        dict.GetType().GetMethod("TryAdd")!.Invoke(dict, new[] { name, state });
        return info;
    }

    [Fact]
    public async Task Scan_OrphanedWorkerWithResponse_AddsOrchestratorWarning()
    {
        var svc = CreateService();

        // Set up multi-agent group
        var group = new SessionGroup { Id = "team-1", Name = "TestTeam", IsMultiAgent = true };
        svc.Organization.Groups.Add(group);
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "orch-1", GroupId = "team-1", Role = MultiAgentRole.Orchestrator
        });
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "worker-1", GroupId = "team-1", Role = MultiAgentRole.Worker
        });

        // Add sessions with history
        var orchInfo = AddDummySession(svc, "orch-1");
        orchInfo.History.Add(ChatMessage.UserMessage("Deploy the fix"));
        orchInfo.History.Last().Timestamp = DateTime.Now.AddMinutes(-10);

        var workerInfo = AddDummySession(svc, "worker-1");
        workerInfo.History.Add(ChatMessage.AssistantMessage("I've completed the deployment."));
        workerInfo.History.Last().Timestamp = DateTime.Now.AddMinutes(-5);

        // Run scan
        await svc.ScanForOrphanedWorkersAsync();

        // Verify system message was added to orchestrator
        var systemMsgs = orchInfo.History
            .Where(m => m.MessageType == ChatMessageType.System)
            .ToList();
        Assert.Single(systemMsgs);
        Assert.Contains("worker-1", systemMsgs[0].Content);
        Assert.Contains("not synthesized", systemMsgs[0].Content);
    }

    [Fact]
    public async Task Scan_WorkerStillProcessing_NoWarning()
    {
        var svc = CreateService();

        var group = new SessionGroup { Id = "team-2", Name = "ActiveTeam", IsMultiAgent = true };
        svc.Organization.Groups.Add(group);
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "orch-2", GroupId = "team-2", Role = MultiAgentRole.Orchestrator
        });
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "worker-2", GroupId = "team-2", Role = MultiAgentRole.Worker
        });

        var orchInfo = AddDummySession(svc, "orch-2");
        orchInfo.History.Add(ChatMessage.UserMessage("Do something"));

        var workerInfo = AddDummySession(svc, "worker-2");
        workerInfo.IsProcessing = true; // Still processing

        await svc.ScanForOrphanedWorkersAsync();

        // No system message should be added
        var systemMsgs = orchInfo.History
            .Where(m => m.MessageType == ChatMessageType.System)
            .ToList();
        Assert.Empty(systemMsgs);
    }

    [Fact]
    public async Task Scan_NoMultiAgentGroups_NoOp()
    {
        var svc = CreateService();

        // Only regular groups
        svc.Organization.Groups.Add(new SessionGroup { Id = "regular", Name = "Regular", IsMultiAgent = false });
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "session-1", GroupId = "regular", Role = MultiAgentRole.None
        });

        var info = AddDummySession(svc, "session-1");
        info.History.Add(ChatMessage.AssistantMessage("Some response"));

        await svc.ScanForOrphanedWorkersAsync();

        // Nothing should crash or add messages
        Assert.Single(info.History);
    }

    [Fact]
    public async Task Scan_OrchestratorAlreadyHasNewerResponse_NoWarning()
    {
        var svc = CreateService();

        var group = new SessionGroup { Id = "team-3", Name = "SyncedTeam", IsMultiAgent = true };
        svc.Organization.Groups.Add(group);
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "orch-3", GroupId = "team-3", Role = MultiAgentRole.Orchestrator
        });
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "worker-3", GroupId = "team-3", Role = MultiAgentRole.Worker
        });

        // Orchestrator has a NEWER response than the worker (already synthesized)
        var orchInfo = AddDummySession(svc, "orch-3");
        orchInfo.History.Add(ChatMessage.AssistantMessage("Here's the synthesized result."));
        orchInfo.History.Last().Timestamp = DateTime.Now.AddMinutes(-1);

        var workerInfo = AddDummySession(svc, "worker-3");
        workerInfo.History.Add(ChatMessage.AssistantMessage("Worker done."));
        workerInfo.History.Last().Timestamp = DateTime.Now.AddMinutes(-5);

        await svc.ScanForOrphanedWorkersAsync();

        // No warning — orchestrator already has newer content
        var systemMsgs = orchInfo.History
            .Where(m => m.MessageType == ChatMessageType.System)
            .ToList();
        Assert.Empty(systemMsgs);
    }

    [Fact]
    public async Task Scan_OrchestratorHasNewerSystemMessageButOlderAssistant_StillWarns()
    {
        var svc = CreateService();

        var group = new SessionGroup { Id = "team-3b", Name = "RecoveredTeam", IsMultiAgent = true };
        svc.Organization.Groups.Add(group);
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "orch-3b", GroupId = "team-3b", Role = MultiAgentRole.Orchestrator
        });
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "worker-3b", GroupId = "team-3b", Role = MultiAgentRole.Worker
        });

        // The orchestrator's latest assistant content is older than the worker,
        // but a newer system message was added during reconnect/recovery.
        var orchInfo = AddDummySession(svc, "orch-3b");
        orchInfo.History.Add(ChatMessage.AssistantMessage("Earlier synthesis."));
        orchInfo.History.Last().Timestamp = DateTime.Now.AddMinutes(-15);
        orchInfo.History.Add(ChatMessage.SystemMessage("Session recreated after reconnect."));
        orchInfo.History.Last().Timestamp = DateTime.Now.AddMinutes(-1);

        var workerInfo = AddDummySession(svc, "worker-3b");
        workerInfo.History.Add(ChatMessage.AssistantMessage("Worker finished after the earlier synthesis."));
        workerInfo.History.Last().Timestamp = DateTime.Now.AddMinutes(-5);

        await svc.ScanForOrphanedWorkersAsync();

        var warning = Assert.Single(orchInfo.History, m =>
            m.MessageType == ChatMessageType.System &&
            m.Content.Contains("not synthesized", StringComparison.Ordinal));
        Assert.Contains("worker-3b", warning.Content);
    }

    [Fact]
    public async Task Scan_WorkerWithNoResponse_NoWarning()
    {
        var svc = CreateService();

        var group = new SessionGroup { Id = "team-4", Name = "EmptyTeam", IsMultiAgent = true };
        svc.Organization.Groups.Add(group);
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "orch-4", GroupId = "team-4", Role = MultiAgentRole.Orchestrator
        });
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "worker-4", GroupId = "team-4", Role = MultiAgentRole.Worker
        });

        var orchInfo = AddDummySession(svc, "orch-4");
        orchInfo.History.Add(ChatMessage.UserMessage("Do something"));

        // Worker exists but has no assistant response
        AddDummySession(svc, "worker-4");

        await svc.ScanForOrphanedWorkersAsync();

        var systemMsgs = orchInfo.History
            .Where(m => m.MessageType == ChatMessageType.System)
            .ToList();
        Assert.Empty(systemMsgs);
    }

    [Fact]
    public async Task Scan_PendingOrchestrationForGroup_SkipsWarning()
    {
        var svc = CreateService();

        var group = new SessionGroup { Id = "team-5", Name = "PendingTeam", IsMultiAgent = true };
        svc.Organization.Groups.Add(group);
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "orch-5", GroupId = "team-5", Role = MultiAgentRole.Orchestrator
        });
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "worker-5", GroupId = "team-5", Role = MultiAgentRole.Worker
        });

        var orchInfo = AddDummySession(svc, "orch-5");
        orchInfo.History.Add(ChatMessage.UserMessage("Do something"));
        orchInfo.History.Last().Timestamp = DateTime.Now.AddMinutes(-10);

        var workerInfo = AddDummySession(svc, "worker-5");
        workerInfo.History.Add(ChatMessage.AssistantMessage("Done."));
        workerInfo.History.Last().Timestamp = DateTime.Now.AddMinutes(-5);

        svc.SavePendingOrchestration(new PendingOrchestration
        {
            GroupId = "team-5",
            OrchestratorName = "orch-5",
            WorkerNames = ["worker-5"],
            OriginalPrompt = "Do something",
            StartedAt = DateTime.UtcNow
        });

        await svc.ScanForOrphanedWorkersAsync();

        var systemMsgs = orchInfo.History
            .Where(m => m.MessageType == ChatMessageType.System)
            .ToList();
        Assert.Empty(systemMsgs);
    }

    [Fact]
    public async Task Scan_CancelledDuringInitialDelay_IsSilentlyIgnored()
    {
        var svc = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await svc.ScanForOrphanedWorkersAsync(cts.Token);
    }
}
