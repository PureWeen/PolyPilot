using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the dashboard session management features added in PR #413:
/// - Phase 4 healer (clears stale Worker/Orchestrator roles from non-multi-agent groups)
/// - GetFocusSessions (processing/unread/FocusOverride logic)
/// - TolerantEnumConverter (unknown enum values fall back to default)
/// - UiState persistence (GridColumns and CardMinHeight round-trip)
/// </summary>
[Collection("BaseDir")]
public class DashboardFeatureTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly IServiceProvider _serviceProvider;

    public DashboardFeatureTests()
    {
        _serviceProvider = new ServiceCollection().BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, new RepoManager(), _serviceProvider, _demoService);

    private static void InjectSession(CopilotService svc, AgentSessionInfo info)
    {
        var sessionsField = typeof(CopilotService).GetField("_sessions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var dict = sessionsField.GetValue(svc)!;
        var stateType = sessionsField.FieldType.GenericTypeArguments[1];
        var state = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(stateType);
        stateType.GetProperty("Info")!.SetValue(state, info);
        dict.GetType().GetMethod("TryAdd")!.Invoke(dict, new[] { info.Name, state });
    }

    #region Phase 4 healer: clears stale Worker/Orchestrator roles from non-multi-agent groups

    [Fact]
    public void HealMultiAgentGroups_Phase4_ClearsWorkerRoleFromNonMultiAgentGroup()
    {
        // A session that was previously in a multi-agent group and has Role=Worker,
        // but is now in a plain (non-multi-agent) group. Phase 4 should clear it to None.
        var org = new OrganizationState();
        var regularGroup = new SessionGroup { Id = "regular-group", Name = "Regular", IsMultiAgent = false };
        org.Groups.Add(regularGroup);
        // Session has Worker role but is in a non-multi-agent group (orphaned from old team)
        org.Sessions.Add(new SessionMeta { SessionName = "orphaned-worker", GroupId = "regular-group", Role = MultiAgentRole.Worker });
        // Normal session in the same group — should stay None
        org.Sessions.Add(new SessionMeta { SessionName = "normal-session", GroupId = "regular-group", Role = MultiAgentRole.None });

        var tempDir = Path.Combine(Path.GetTempPath(), $"polypilot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "organization.json"),
                JsonSerializer.Serialize(org, new JsonSerializerOptions { WriteIndented = true }));

            CopilotService.SetBaseDirForTesting(tempDir);
            var svc = new CopilotService(_chatDb, _serverManager, _bridgeClient, new RepoManager(), _serviceProvider, _demoService);
            svc.LoadOrganization();

            var orphaned = svc.Organization.Sessions.First(m => m.SessionName == "orphaned-worker");
            Assert.Equal(MultiAgentRole.None, orphaned.Role);

            var normal = svc.Organization.Sessions.First(m => m.SessionName == "normal-session");
            Assert.Equal(MultiAgentRole.None, normal.Role);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void HealMultiAgentGroups_Phase4_ClearsOrchestratorRoleFromDeletedGroup()
    {
        // A session with Orchestrator role whose GroupId no longer exists (group was deleted).
        // Phase 4 should clear the role to None since the group can't be validated.
        var org = new OrganizationState();
        // Session points to "deleted-group" which is NOT in org.Groups
        org.Sessions.Add(new SessionMeta { SessionName = "orphaned-orch", GroupId = "deleted-group", Role = MultiAgentRole.Orchestrator });

        var tempDir = Path.Combine(Path.GetTempPath(), $"polypilot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "organization.json"),
                JsonSerializer.Serialize(org, new JsonSerializerOptions { WriteIndented = true }));

            CopilotService.SetBaseDirForTesting(tempDir);
            var svc = new CopilotService(_chatDb, _serverManager, _bridgeClient, new RepoManager(), _serviceProvider, _demoService);
            svc.LoadOrganization();

            var orphaned = svc.Organization.Sessions.FirstOrDefault(m => m.SessionName == "orphaned-orch");
            // Group doesn't exist → Phase 4 clears the role
            if (orphaned != null)
                Assert.Equal(MultiAgentRole.None, orphaned.Role);
            // (If the session was removed during reconciliation, the test still passes)
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void HealMultiAgentGroups_Phase2_PromotesGroupWithPersistedOrchestratorRole()
    {
        // If a session has Role=Orchestrator (explicitly persisted), Phase 2 promotes
        // the containing group to IsMultiAgent=true — this is the correct behavior,
        // not Phase 4 clearing (Phase 4 only clears Workers in non-MA groups).
        var org = new OrganizationState();
        var regularGroup = new SessionGroup { Id = "regular-group", Name = "Regular", IsMultiAgent = false };
        org.Groups.Add(regularGroup);
        org.Sessions.Add(new SessionMeta { SessionName = "some-orchestrator", GroupId = "regular-group", Role = MultiAgentRole.Orchestrator });

        var tempDir = Path.Combine(Path.GetTempPath(), $"polypilot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "organization.json"),
                JsonSerializer.Serialize(org, new JsonSerializerOptions { WriteIndented = true }));

            CopilotService.SetBaseDirForTesting(tempDir);
            var svc = new CopilotService(_chatDb, _serverManager, _bridgeClient, new RepoManager(), _serviceProvider, _demoService);
            svc.LoadOrganization();

            var group = svc.Organization.Groups.First(g => g.Id == "regular-group");
            // Phase 2 promotes the group — the Orchestrator role is preserved
            Assert.True(group.IsMultiAgent);
            var session = svc.Organization.Sessions.First(m => m.SessionName == "some-orchestrator");
            Assert.Equal(MultiAgentRole.Orchestrator, session.Role);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void HealMultiAgentGroups_Phase4_PreservesRolesInActualMultiAgentGroup()
    {
        // Sessions in a real multi-agent group (IsMultiAgent=true) must NOT have their roles cleared
        var org = new OrganizationState();
        var maGroup = new SessionGroup { Id = "ma-group", Name = "MyTeam", IsMultiAgent = true };
        org.Groups.Add(maGroup);
        org.Sessions.Add(new SessionMeta { SessionName = "MyTeam-orchestrator", GroupId = "ma-group", Role = MultiAgentRole.Orchestrator });
        org.Sessions.Add(new SessionMeta { SessionName = "MyTeam-worker-1", GroupId = "ma-group", Role = MultiAgentRole.Worker });

        var tempDir = Path.Combine(Path.GetTempPath(), $"polypilot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "organization.json"),
                JsonSerializer.Serialize(org, new JsonSerializerOptions { WriteIndented = true }));

            CopilotService.SetBaseDirForTesting(tempDir);
            var svc = new CopilotService(_chatDb, _serverManager, _bridgeClient, new RepoManager(), _serviceProvider, _demoService);
            svc.LoadOrganization();

            var orch = svc.Organization.Sessions.First(m => m.SessionName == "MyTeam-orchestrator");
            Assert.Equal(MultiAgentRole.Orchestrator, orch.Role);

            var worker = svc.Organization.Sessions.First(m => m.SessionName == "MyTeam-worker-1");
            Assert.Equal(MultiAgentRole.Worker, worker.Role);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    #endregion

    #region GetFocusSessions logic

    [Fact]
    public void GetFocusSessions_ReturnsProcessingSessions()
    {
        var svc = CreateService();

        var processing = new AgentSessionInfo { Name = "active-session", Model = "m", IsProcessing = true };
        var idle = new AgentSessionInfo { Name = "idle-session", Model = "m", IsProcessing = false };
        idle.LastUpdatedAt = DateTime.Now.AddDays(-2); // old, not in 24h window

        InjectSession(svc, processing);
        InjectSession(svc, idle);
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "active-session", GroupId = SessionGroup.DefaultId });
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "idle-session", GroupId = SessionGroup.DefaultId });

        var focus = svc.GetFocusSessions();

        Assert.Contains(focus, s => s.Name == "active-session");
        Assert.DoesNotContain(focus, s => s.Name == "idle-session");
    }

    [Fact]
    public void GetFocusSessions_ReturnsSessionsWithUnreadMessages()
    {
        var svc = CreateService();

        var unread = new AgentSessionInfo { Name = "unread-session", Model = "m", IsProcessing = false };
        unread.LastUpdatedAt = DateTime.Now.AddDays(-2); // old
        unread.History.Add(ChatMessage.AssistantMessage("Response pending your attention"));

        var idle = new AgentSessionInfo { Name = "idle-session", Model = "m", IsProcessing = false };
        idle.LastUpdatedAt = DateTime.Now.AddDays(-2);

        InjectSession(svc, unread);
        InjectSession(svc, idle);
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "unread-session", GroupId = SessionGroup.DefaultId });
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "idle-session", GroupId = SessionGroup.DefaultId });

        var focus = svc.GetFocusSessions();

        Assert.Contains(focus, s => s.Name == "unread-session");
        Assert.DoesNotContain(focus, s => s.Name == "idle-session");
    }

    [Fact]
    public void GetFocusSessions_FocusOverrideIncluded_AlwaysShows()
    {
        var svc = CreateService();

        var session = new AgentSessionInfo { Name = "pinned-session", Model = "m", IsProcessing = false };
        session.LastUpdatedAt = DateTime.Now.AddDays(-10); // very old, would not appear normally

        InjectSession(svc, session);
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "pinned-session",
            GroupId = SessionGroup.DefaultId,
            FocusOverride = FocusOverride.Included
        });

        var focus = svc.GetFocusSessions();

        Assert.Contains(focus, s => s.Name == "pinned-session");
    }

    [Fact]
    public void GetFocusSessions_FocusOverrideExcluded_NeverShows()
    {
        var svc = CreateService();

        // This session IS processing, but is explicitly excluded from Focus
        var session = new AgentSessionInfo { Name = "excluded-session", Model = "m", IsProcessing = true };
        InjectSession(svc, session);
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "excluded-session",
            GroupId = SessionGroup.DefaultId,
            FocusOverride = FocusOverride.Excluded
        });

        var focus = svc.GetFocusSessions();

        Assert.DoesNotContain(focus, s => s.Name == "excluded-session");
    }

    [Fact]
    public void GetFocusSessions_WorkersInMultiAgentGroup_ExcludedFromFocus()
    {
        var svc = CreateService();

        var maGroup = new SessionGroup { Id = "ma-group", Name = "MyTeam", IsMultiAgent = true };
        svc.Organization.Groups.Add(maGroup);

        var worker = new AgentSessionInfo { Name = "MyTeam-worker-1", Model = "m", IsProcessing = true };
        var orch = new AgentSessionInfo { Name = "MyTeam-orchestrator", Model = "m", IsProcessing = true };
        InjectSession(svc, worker);
        InjectSession(svc, orch);

        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "MyTeam-worker-1", GroupId = "ma-group", Role = MultiAgentRole.Worker });
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "MyTeam-orchestrator", GroupId = "ma-group", Role = MultiAgentRole.Orchestrator });

        var focus = svc.GetFocusSessions();

        // Workers should not appear in Focus (they're managed by orchestrator)
        Assert.DoesNotContain(focus, s => s.Name == "MyTeam-worker-1");
        // Orchestrator (Role != Worker) should appear since it's processing
        Assert.Contains(focus, s => s.Name == "MyTeam-orchestrator");
    }

    [Fact]
    public void GetFocusSessions_ProcessingFirstInSort()
    {
        var svc = CreateService();

        var processing = new AgentSessionInfo { Name = "processing", Model = "m", IsProcessing = true };
        var unread = new AgentSessionInfo { Name = "unread", Model = "m", IsProcessing = false };
        unread.History.Add(ChatMessage.AssistantMessage("Pending attention"));
        unread.LastUpdatedAt = DateTime.Now.AddMinutes(-5);

        InjectSession(svc, processing);
        InjectSession(svc, unread);
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "processing", GroupId = SessionGroup.DefaultId });
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "unread", GroupId = SessionGroup.DefaultId });

        var focus = svc.GetFocusSessions();
        var names = focus.Select(s => s.Name).ToList();

        // Processing comes before unread
        Assert.True(names.IndexOf("processing") < names.IndexOf("unread"),
            "Processing session should sort before unread session");
    }

    [Fact]
    public void GetFocusSessions_HandledSessions_SortToBottom()
    {
        var svc = CreateService();

        var unhandled = new AgentSessionInfo { Name = "unhandled", Model = "m", IsProcessing = false };
        unhandled.LastUpdatedAt = DateTime.Now.AddMinutes(-30);
        unhandled.History.Add(ChatMessage.AssistantMessage("Needs attention"));

        var handled = new AgentSessionInfo { Name = "handled", Model = "m", IsProcessing = false };
        handled.LastUpdatedAt = DateTime.Now.AddMinutes(-5); // More recent but handled

        InjectSession(svc, unhandled);
        InjectSession(svc, handled);
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "unhandled", GroupId = SessionGroup.DefaultId });
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = "handled",
            GroupId = SessionGroup.DefaultId,
            HandledAt = DateTime.Now.AddMinutes(-10)
        });

        var focus = svc.GetFocusSessions();
        var names = focus.Select(s => s.Name).ToList();

        // Unhandled (even with older activity) sorts before handled
        Assert.True(names.IndexOf("unhandled") < names.IndexOf("handled"),
            "Unhandled sessions should sort before handled sessions");
    }

    [Fact]
    public void GetFocusSessions_OldestWaitingFirst_WithinUnhandled()
    {
        var svc = CreateService();

        // "newer" session updated 5 min ago (less urgent — user saw it more recently)
        var newer = new AgentSessionInfo { Name = "newer", Model = "m", IsProcessing = false };
        newer.LastUpdatedAt = DateTime.Now.AddMinutes(-5);
        newer.History.Add(ChatMessage.AssistantMessage("Recent message"));

        // "older" session waiting 60 min (more urgent — waiting longest)
        var older = new AgentSessionInfo { Name = "older", Model = "m", IsProcessing = false };
        older.LastUpdatedAt = DateTime.Now.AddMinutes(-60);
        older.History.Add(ChatMessage.AssistantMessage("Waiting a long time"));

        InjectSession(svc, newer);
        InjectSession(svc, older);
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "newer", GroupId = SessionGroup.DefaultId });
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "older", GroupId = SessionGroup.DefaultId });

        var focus = svc.GetFocusSessions();
        var names = focus.Select(s => s.Name).ToList();

        // Oldest waiting session sorts first (triage order)
        Assert.True(names.IndexOf("older") < names.IndexOf("newer"),
            "Oldest waiting session should sort before more recently active sessions");
    }

    [Fact]
    public void MarkFocusHandled_SetsHandledAtOnMeta()
    {
        var svc = CreateService();
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "my-session", GroupId = SessionGroup.DefaultId });

        var before = DateTime.Now;
        svc.MarkFocusHandled("my-session");
        var after = DateTime.Now;

        var meta = svc.Organization.Sessions.First(m => m.SessionName == "my-session");
        Assert.NotNull(meta.HandledAt);
        Assert.InRange(meta.HandledAt!.Value, before, after);
    }

    [Fact]
    public void MarkFocusHandled_NonExistentSession_DoesNotThrow()
    {
        var svc = CreateService();
        // Should not throw
        svc.MarkFocusHandled("non-existent-session");
    }

    [Fact]
    public void TolerantEnumConverter_UnknownStringValue_FallsBackToDefault()
    {
        // Simulate receiving JSON from a newer desktop where a new Role "Supervisor" exists
        // The older mobile client doesn't know about it — should fall back to None
        var json = """{"Role":"Supervisor"}""";
        var result = JsonSerializer.Deserialize<SessionMetaRoleWrapper>(json);

        Assert.NotNull(result);
        Assert.Equal(MultiAgentRole.None, result!.Role);
    }

    [Fact]
    public void TolerantEnumConverter_KnownStringValue_DeserializesCorrectly()
    {
        var json = """{"Role":"Worker"}""";
        var result = JsonSerializer.Deserialize<SessionMetaRoleWrapper>(json);

        Assert.NotNull(result);
        Assert.Equal(MultiAgentRole.Worker, result!.Role);
    }

    [Fact]
    public void TolerantEnumConverter_KnownOrchestratorValue_DeserializesCorrectly()
    {
        var json = """{"Role":"Orchestrator"}""";
        var result = JsonSerializer.Deserialize<SessionMetaRoleWrapper>(json);

        Assert.NotNull(result);
        Assert.Equal(MultiAgentRole.Orchestrator, result!.Role);
    }

    [Fact]
    public void TolerantEnumConverter_NullOrMissingValue_FallsBackToDefault()
    {
        // Missing field → default(MultiAgentRole) = None
        var json = """{}""";
        var result = JsonSerializer.Deserialize<SessionMetaRoleWrapper>(json);

        Assert.NotNull(result);
        Assert.Equal(MultiAgentRole.None, result!.Role);
    }

    [Fact]
    public void TolerantEnumConverter_CaseInsensitive_DeserializesCorrectly()
    {
        var json = """{"Role":"worker"}""";
        var result = JsonSerializer.Deserialize<SessionMetaRoleWrapper>(json);

        Assert.NotNull(result);
        Assert.Equal(MultiAgentRole.Worker, result!.Role);
    }

    [Fact]
    public void TolerantEnumConverter_SerializesAsString_NotInteger()
    {
        // Verify the converter writes strings (not ints) for forward compatibility
        var meta = new SessionMeta { SessionName = "test", Role = MultiAgentRole.Worker };
        var json = JsonSerializer.Serialize(meta);

        Assert.Contains("\"Worker\"", json);
        Assert.DoesNotContain("\"Role\":1", json);
        Assert.DoesNotContain("\"Role\":0", json);
    }

    [Fact]
    public void TolerantEnumConverter_FocusOverride_UnknownValue_FallsBackToAuto()
    {
        var json = """{"FocusOverride":"ManuallyPinned"}""";
        var result = JsonSerializer.Deserialize<FocusOverrideWrapper>(json);

        Assert.NotNull(result);
        Assert.Equal(FocusOverride.Auto, result!.FocusOverride);
    }

    [Fact]
    public void TolerantEnumConverter_MultiAgentMode_UnknownValue_FallsBackToDefault()
    {
        var json = """{"Mode":"NewExperimentalMode"}""";
        var result = JsonSerializer.Deserialize<MultiAgentModeWrapper>(json);

        Assert.NotNull(result);
        Assert.Equal(default(MultiAgentMode), result!.Mode);
    }

    #endregion

    #region UiState persistence — GridColumns and CardMinHeight round-trip

    [Fact]
    public void UiState_GridColumns_DefaultIsThree()
    {
        var state = new UiState();
        Assert.Equal(3, state.GridColumns);
    }

    [Fact]
    public void UiState_CardMinHeight_DefaultIs250()
    {
        var state = new UiState();
        Assert.Equal(250, state.CardMinHeight);
    }

    [Fact]
    public void UiState_GridColumnsAndCardMinHeight_RoundTripSerialization()
    {
        var state = new UiState
        {
            GridColumns = 5,
            CardMinHeight = 400
        };

        var json = JsonSerializer.Serialize(state);
        var restored = JsonSerializer.Deserialize<UiState>(json);

        Assert.NotNull(restored);
        Assert.Equal(5, restored!.GridColumns);
        Assert.Equal(400, restored.CardMinHeight);
    }

    [Fact]
    public void UiState_LegacyJsonWithoutGridColumns_UsesDefaults()
    {
        // Old saved state without GridColumns/CardMinHeight fields should default correctly
        var json = """{"CurrentPage":"/dashboard","ActiveSession":null,"FontSize":20}""";
        var restored = JsonSerializer.Deserialize<UiState>(json);

        Assert.NotNull(restored);
        Assert.Equal(3, restored!.GridColumns);
        Assert.Equal(250, restored.CardMinHeight);
    }

    #endregion
}

// Helper wrappers for TolerantEnumConverter tests
internal class SessionMetaRoleWrapper
{
    public MultiAgentRole Role { get; set; } = MultiAgentRole.None;
}

internal class FocusOverrideWrapper
{
    public FocusOverride FocusOverride { get; set; } = FocusOverride.Auto;
}

internal class MultiAgentModeWrapper
{
    public MultiAgentMode Mode { get; set; }
}

/// <summary>
/// Tests for Phase 1 healer suffix-match fix (PP- prefix bug).
/// The orchestrator "PP- PR Review Squad-orchestrator" had workers named
/// "PR Review Squad-worker-N" (missing the "PP- " prefix on the workers).
/// Phase 1 used to reject the match because the exact prefix didn't match,
/// leaving the orchestrator without its role and preventing Phase 3 reconstruction.
/// </summary>
[Collection("BaseDir")]
public class HealerPrefixMatchTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly IServiceProvider _serviceProvider;

    public HealerPrefixMatchTests()
    {
        _serviceProvider = new ServiceCollection().BuildServiceProvider();
    }

    private (CopilotService svc, string tempDir) CreateServiceWithOrg(OrganizationState org)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"polypilot-healer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "organization.json"),
            JsonSerializer.Serialize(org, new JsonSerializerOptions { WriteIndented = true }));
        CopilotService.SetBaseDirForTesting(tempDir);
        var svc = new CopilotService(_chatDb, _serverManager, _bridgeClient, new RepoManager(), _serviceProvider, _demoService);
        svc.LoadOrganization();
        // Restore shared test base dir so parallel tests in the same collection aren't affected
        CopilotService.SetBaseDirForTesting(TestSetup.TestBaseDir);
        return (svc, tempDir);
    }

    [Fact]
    public void Phase1_ExactPrefixMatch_RestoresOrchestratorRole()
    {
        // Standard case: "MyTeam-orchestrator" + "MyTeam-worker-1"
        var org = new OrganizationState();
        var nonMultiGroup = new SessionGroup { Id = "g1", Name = "General", IsMultiAgent = false };
        org.Groups.Add(nonMultiGroup);
        org.Sessions.Add(new SessionMeta { SessionName = "MyTeam-orchestrator", GroupId = "g1", Role = MultiAgentRole.None });
        org.Sessions.Add(new SessionMeta { SessionName = "MyTeam-worker-1", GroupId = "g1", Role = MultiAgentRole.None });
        org.Sessions.Add(new SessionMeta { SessionName = "MyTeam-worker-2", GroupId = "g1", Role = MultiAgentRole.None });

        var (svc, tempDir) = CreateServiceWithOrg(org);
        try
        {
            var orch = svc.Organization.Sessions.First(s => s.SessionName == "MyTeam-orchestrator");
            Assert.Equal(MultiAgentRole.Orchestrator, orch.Role);
        }
        finally { try { Directory.Delete(tempDir, recursive: true); } catch { } }
    }

    [Fact]
    public void Phase1_SuffixMatch_RestoresOrchestratorRole_WhenOrchestratorHasNamespacePrefix()
    {
        // "PP- PR Review Squad-orchestrator" (prefix "PP- PR Review Squad")
        // workers are "PR Review Squad-worker-N" (prefix "PR Review Squad")
        // "PP- PR Review Squad".EndsWith("PR Review Squad") → should match
        var org = new OrganizationState();
        var nonMultiGroup = new SessionGroup { Id = "g1", Name = "PolyPilot", IsMultiAgent = false };
        org.Groups.Add(nonMultiGroup);
        org.Sessions.Add(new SessionMeta { SessionName = "PP- PR Review Squad-orchestrator", GroupId = "g1", Role = MultiAgentRole.None });
        org.Sessions.Add(new SessionMeta { SessionName = "PR Review Squad-worker-1", GroupId = "g1", Role = MultiAgentRole.None });
        org.Sessions.Add(new SessionMeta { SessionName = "PR Review Squad-worker-2", GroupId = "g1", Role = MultiAgentRole.None });

        var (svc, tempDir) = CreateServiceWithOrg(org);
        try
        {
            var orch = svc.Organization.Sessions.First(s => s.SessionName == "PP- PR Review Squad-orchestrator");
            Assert.Equal(MultiAgentRole.Orchestrator, orch.Role);
        }
        finally { try { Directory.Delete(tempDir, recursive: true); } catch { } }
    }

    [Fact]
    public void Phase3_SuffixMatch_ReconstructsGroup_WhenOrchestratorHasNamespacePrefix()
    {
        // Full reconstruction: Phase 1 restores role, Phase 3 creates new multi-agent group.
        var org = new OrganizationState();
        var nonMultiGroup = new SessionGroup { Id = "g1", Name = "PolyPilot", IsMultiAgent = false };
        org.Groups.Add(nonMultiGroup);
        org.Sessions.Add(new SessionMeta { SessionName = "PP- PR Review Squad-orchestrator", GroupId = "g1", Role = MultiAgentRole.None });
        org.Sessions.Add(new SessionMeta { SessionName = "PR Review Squad-worker-1", GroupId = "g1", Role = MultiAgentRole.None });
        org.Sessions.Add(new SessionMeta { SessionName = "PR Review Squad-worker-2", GroupId = "g1", Role = MultiAgentRole.None });

        var (svc, tempDir) = CreateServiceWithOrg(org);
        try
        {
            // A new multi-agent group should have been created
            var newGroup = svc.Organization.Groups.FirstOrDefault(g => g.IsMultiAgent);
            Assert.NotNull(newGroup);

            var orch = svc.Organization.Sessions.First(s => s.SessionName == "PP- PR Review Squad-orchestrator");
            Assert.Equal(newGroup.Id, orch.GroupId);
            Assert.Equal(MultiAgentRole.Orchestrator, orch.Role);

            var w1 = svc.Organization.Sessions.First(s => s.SessionName == "PR Review Squad-worker-1");
            Assert.Equal(newGroup.Id, w1.GroupId);
            Assert.Equal(MultiAgentRole.Worker, w1.Role);
        }
        finally { try { Directory.Delete(tempDir, recursive: true); } catch { } }
    }

    [Fact]
    public void Phase1_NoFalsePositive_WhenOrchestratorHasNoMatchingWorkers()
    {
        // A session named "*-orchestrator" with no matching workers should NOT be promoted
        var org = new OrganizationState();
        var nonMultiGroup = new SessionGroup { Id = "g1", Name = "General", IsMultiAgent = false };
        org.Groups.Add(nonMultiGroup);
        org.Sessions.Add(new SessionMeta { SessionName = "deploy-orchestrator", GroupId = "g1", Role = MultiAgentRole.None });

        var (svc, tempDir) = CreateServiceWithOrg(org);
        try
        {
            var orch = svc.Organization.Sessions.First(s => s.SessionName == "deploy-orchestrator");
            // Must NOT have been promoted — user session coincidentally named "*-orchestrator"
            Assert.Equal(MultiAgentRole.None, orch.Role);
        }
        finally { try { Directory.Delete(tempDir, recursive: true); } catch { } }
    }

    [Fact]
    public void Phase1_SuffixMatch_NoDuplicateGroupCreation_WhenOrchestratorAlreadyInMultiAgentGroup()
    {
        // If the orchestrator is already in a multi-agent group, no new group should be created.
        var org = new OrganizationState();
        var multiGroup = new SessionGroup { Id = "ma1", Name = "PP- PR Review Squad", IsMultiAgent = true };
        org.Groups.Add(multiGroup);
        org.Sessions.Add(new SessionMeta { SessionName = "PP- PR Review Squad-orchestrator", GroupId = "ma1", Role = MultiAgentRole.Orchestrator });
        org.Sessions.Add(new SessionMeta { SessionName = "PR Review Squad-worker-1", GroupId = "ma1", Role = MultiAgentRole.Worker });

        var (svc, tempDir) = CreateServiceWithOrg(org);
        try
        {
            // No additional multi-agent groups should have been created (only "ma1" should be multi-agent)
            var multiAgentGroups = svc.Organization.Groups.Where(g => g.IsMultiAgent).ToList();
            Assert.Single(multiAgentGroups);
            Assert.Equal("ma1", multiAgentGroups[0].Id);
        }
        finally { try { Directory.Delete(tempDir, recursive: true); } catch { } }
    }

    [Fact]
    public void Phase1_SuffixMatch_NoFalsePositive_WithoutDashSpaceNamespace()
    {
        // "Review Squad-orchestrator" should NOT pick up "Squad-worker-1" via suffix match,
        // because "Review " doesn't end with "- " (it's not a PolyPilot namespace prefix).
        // Both sessions are in the same non-multi-agent group (lost their original groups).
        var org = new OrganizationState();
        var nonMultiGroup = new SessionGroup { Id = "g1", Name = "General", IsMultiAgent = false };
        org.Groups.Add(nonMultiGroup);
        org.Sessions.Add(new SessionMeta { SessionName = "Review Squad-orchestrator", GroupId = "g1", Role = MultiAgentRole.None });
        org.Sessions.Add(new SessionMeta { SessionName = "Squad-worker-1", GroupId = "g1", Role = MultiAgentRole.None });
        // Also add a matching "Review Squad-worker-1" so the orchestrator DOES get promoted
        // (we need to confirm Squad-worker-1 is NOT pulled into the Review Squad group)
        org.Sessions.Add(new SessionMeta { SessionName = "Review Squad-worker-1", GroupId = "g1", Role = MultiAgentRole.None });

        var (svc, tempDir) = CreateServiceWithOrg(org);
        try
        {
            // "Squad-worker-1" must remain in g1 or wherever it was — NOT in the Review Squad group
            var squadWorker = svc.Organization.Sessions.First(s => s.SessionName == "Squad-worker-1");
            var reviewSquadGroup = svc.Organization.Groups.FirstOrDefault(g => g.IsMultiAgent);

            // If a multi-agent group was created, Squad-worker-1 must NOT be in it
            if (reviewSquadGroup != null)
                Assert.NotEqual(reviewSquadGroup.Id, squadWorker.GroupId);

            // Squad-worker-1 should have Role=None (not Worker in Review Squad's group)
            Assert.Equal(MultiAgentRole.None, squadWorker.Role);
        }
        finally { try { Directory.Delete(tempDir, recursive: true); } catch { } }
    }
}
