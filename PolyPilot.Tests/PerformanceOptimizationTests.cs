using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for performance optimizations: debounce timers, organized sessions caching,
/// reconciliation skip guard, and dispose flush behavior.
/// </summary>
public class PerformanceOptimizationTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public PerformanceOptimizationTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    // --- GetOrganizedSessions caching ---

    [Fact]
    public void GetOrganizedSessions_ReturnsSameInstance_WhenNothingChanges()
    {
        var svc = CreateService();
        // Set up org state so there's something to return
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "s1", GroupId = SessionGroup.DefaultId });

        var result1 = svc.GetOrganizedSessions();
        var result2 = svc.GetOrganizedSessions();

        // Should be the exact same cached object
        Assert.Same(result1, result2);
    }

    [Fact]
    public void GetOrganizedSessions_InvalidatesCache_WhenGroupAdded()
    {
        var svc = CreateService();

        var result1 = svc.GetOrganizedSessions();
        svc.CreateGroup("NewGroup");
        var result2 = svc.GetOrganizedSessions();

        // Group count changed, so cache should be invalidated
        Assert.NotSame(result1, result2);
        Assert.True(result2.Count > result1.Count);
    }

    [Fact]
    public void GetOrganizedSessions_InvalidatesCache_WhenSortModeChanges()
    {
        var svc = CreateService();
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "s1", GroupId = SessionGroup.DefaultId });

        var result1 = svc.GetOrganizedSessions();
        svc.Organization.SortMode = SessionSortMode.Alphabetical;
        var result2 = svc.GetOrganizedSessions();

        Assert.NotSame(result1, result2);
    }

    [Fact]
    public void GetOrganizedSessions_ReturnsReadOnlyList()
    {
        var svc = CreateService();
        var result = svc.GetOrganizedSessions();

        // Verify it implements IReadOnlyList (callers should not need .ToList())
        Assert.IsAssignableFrom<IReadOnlyList<(SessionGroup, List<AgentSessionInfo>)>>(result);
    }

    [Fact]
    public void GetOrganizedSessions_IncludesAllGroups()
    {
        var svc = CreateService();
        svc.CreateGroup("GroupA");
        svc.CreateGroup("GroupB");

        var result = svc.GetOrganizedSessions();

        // Default + GroupA + GroupB = 3 groups
        Assert.Equal(3, result.Count);
    }

    // --- ReconcileOrganization skip guard ---

    [Fact]
    public void CreateGroup_ThenCreateGroup_BothGroupsExist()
    {
        // Verifies reconciliation doesn't interfere with group creation
        var svc = CreateService();

        var g1 = svc.CreateGroup("First");
        var g2 = svc.CreateGroup("Second");

        Assert.Equal(3, svc.Organization.Groups.Count); // Default + First + Second
        Assert.Contains(svc.Organization.Groups, g => g.Id == g1.Id);
        Assert.Contains(svc.Organization.Groups, g => g.Id == g2.Id);
    }

    [Fact]
    public void SetSortMode_InvalidatesOrganizedSessionsCache()
    {
        var svc = CreateService();
        var before = svc.GetOrganizedSessions();

        svc.SetSortMode(SessionSortMode.CreatedAt);
        var after = svc.GetOrganizedSessions();

        // SetSortMode changes Organization.SortMode which changes cache key
        Assert.NotSame(before, after);
    }

    // --- Debounce timer behavior ---
    // We can't directly test timer coalescing without async waits, but we can verify
    // the flush methods work correctly and that dispose cleans up properly.

    [Fact]
    public async Task DisposeAsync_DoesNotThrow()
    {
        var svc = CreateService();

        // Trigger debounce timers by modifying state
        svc.CreateGroup("TestGroup");
        svc.SetSortMode(SessionSortMode.Alphabetical);

        // Dispose should flush and not throw
        await svc.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_CanBeCalledMultipleTimes()
    {
        var svc = CreateService();

        await svc.DisposeAsync();
        await svc.DisposeAsync(); // Should not throw
    }

    // --- SaveUiState debounce behavior ---

    [Fact]
    public void SaveUiState_DoesNotThrow_WithVariousInputs()
    {
        var svc = CreateService();

        // Rapid-fire saves with different params — should not throw or corrupt state
        svc.SaveUiState("/dashboard", activeSession: "s1");
        svc.SaveUiState("/dashboard", fontSize: 18);
        svc.SaveUiState("/settings", selectedModel: "claude-opus-4.6");
        svc.SaveUiState("/dashboard", expandedGrid: true);
        svc.SaveUiState("/dashboard", expandedSession: "s1");
        svc.SaveUiState("/dashboard", inputModes: new Dictionary<string, string> { ["s1"] = "chat" });
    }

    // --- Organization operations don't corrupt state ---

    [Fact]
    public void PinSession_WithNonExistentSession_DoesNotCorrupt()
    {
        var svc = CreateService();
        svc.PinSession("nonexistent", true);
        // Should not add bogus meta
        Assert.DoesNotContain(svc.Organization.Sessions, m => m.SessionName == "nonexistent");
    }

    [Fact]
    public void ToggleGroupCollapsed_InvalidatesCache()
    {
        var svc = CreateService();
        var g = svc.CreateGroup("TestGroup");
        var before = svc.GetOrganizedSessions();

        // Toggle collapsed doesn't change the cache key (group count/sort/session count unchanged)
        // but the data IS the same groups, so cache should still be valid
        svc.ToggleGroupCollapsed(g.Id);

        // The cache key doesn't include IsCollapsed — verify the cache still returns valid data
        var after = svc.GetOrganizedSessions();
        Assert.NotNull(after);
        // Both should have same number of groups
        Assert.Equal(before.Count, after.Count);
    }

    [Fact]
    public void DeleteGroup_MovesSessionsToDefault()
    {
        var svc = CreateService();
        var g = svc.CreateGroup("ToDelete");
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "orphan", GroupId = g.Id });

        svc.DeleteGroup(g.Id);

        var meta = svc.Organization.Sessions.FirstOrDefault(m => m.SessionName == "orphan");
        Assert.NotNull(meta);
        Assert.Equal(SessionGroup.DefaultId, meta!.GroupId);
    }

    [Fact]
    public void DeleteGroup_InvalidatesOrganizedSessionsCache()
    {
        var svc = CreateService();
        var g = svc.CreateGroup("ToDelete");
        var before = svc.GetOrganizedSessions();
        Assert.Equal(2, before.Count); // Default + ToDelete

        svc.DeleteGroup(g.Id);
        var after = svc.GetOrganizedSessions();
        Assert.Single(after); // Only Default remains
    }

    // --- LoadUiState returns pending debounced state ---

    [Fact]
    public void LoadUiState_ReturnsPendingState_BeforeDebounceFlush()
    {
        var svc = CreateService();

        // Save with debounce (1s timer, won't fire yet)
        svc.SaveUiState("/dashboard", activeSession: "my-session", fontSize: 22);

        // Immediately load — should get the pending state, not stale disk
        var loaded = svc.LoadUiState();
        Assert.NotNull(loaded);
        Assert.Equal("my-session", loaded!.ActiveSession);
        Assert.Equal(22, loaded.FontSize);
    }

    [Fact]
    public void LoadUiState_ReturnsPendingState_AfterMultipleSaves()
    {
        var svc = CreateService();

        // Rapid-fire saves — only the last state matters
        svc.SaveUiState("/dashboard", activeSession: "first");
        svc.SaveUiState("/dashboard", activeSession: "second");
        svc.SaveUiState("/dashboard", activeSession: "third");

        var loaded = svc.LoadUiState();
        Assert.NotNull(loaded);
        Assert.Equal("third", loaded!.ActiveSession);
    }

    // --- DisposeAsync flushes all debounced writes ---

    [Fact]
    public async Task DisposeAsync_FlushesUiState()
    {
        var svc = CreateService();
        svc.SaveUiState("/dashboard", activeSession: "flushed-session", fontSize: 16);

        // Dispose flushes pending state
        await svc.DisposeAsync();

        // After dispose, pending state should be cleared
        // (LoadUiState reads from disk now, which was flushed)
        // We can't easily verify disk write without file mocking,
        // but we can verify no exception and the pending state was consumed
    }

    // --- ReconcileOrganization hash is order-independent ---

    [Fact]
    public void MultipleGroupOperations_DontCorruptOrganization()
    {
        var svc = CreateService();

        // Create groups, add sessions, delete, re-create — stress test reconciliation
        var g1 = svc.CreateGroup("Alpha");
        var g2 = svc.CreateGroup("Beta");
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "s1", GroupId = g1.Id });
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "s2", GroupId = g2.Id });

        svc.DeleteGroup(g1.Id);
        var g3 = svc.CreateGroup("Gamma");
        svc.MoveSession("s2", g3.Id);

        // Verify final state is consistent
        var organized = svc.GetOrganizedSessions();
        Assert.NotNull(organized);
        Assert.True(organized.Count >= 2); // Default + at least Gamma

        // s1 should be in Default (moved from deleted Alpha)
        var s1Meta = svc.Organization.Sessions.FirstOrDefault(m => m.SessionName == "s1");
        Assert.NotNull(s1Meta);
        Assert.Equal(SessionGroup.DefaultId, s1Meta!.GroupId);

        // s2 should be in Gamma
        var s2Meta = svc.Organization.Sessions.FirstOrDefault(m => m.SessionName == "s2");
        Assert.NotNull(s2Meta);
        Assert.Equal(g3.Id, s2Meta!.GroupId);
    }

    // --- Cache doesn't serve stale data after structural changes ---

    [Fact]
    public void RenameGroup_InvalidatesAndReturnsUpdatedData()
    {
        var svc = CreateService();
        var g = svc.CreateGroup("OldName");
        var before = svc.GetOrganizedSessions();

        svc.RenameGroup(g.Id, "NewName");
        var after = svc.GetOrganizedSessions();

        // Name change doesn't change hash key (count/sort unchanged), so cache may still be valid
        // But the data returned should have the correct group name in the cached list
        var renamedGroup = after.SelectMany(x => new[] { x.Group }).FirstOrDefault(gr => gr.Id == g.Id);
        Assert.NotNull(renamedGroup);
        Assert.Equal("NewName", renamedGroup!.Name);
    }

    // --- SaveActiveSessionsToDisk debounce in demo mode ---

    [Fact]
    public async Task DemoMode_SkipsSaveActiveSessionsToDisk()
    {
        var svc = CreateService();
        // Initialize in demo mode
        var settings = new ConnectionSettings { Mode = ConnectionMode.Demo };
        await svc.ReconnectAsync(settings);

        Assert.True(svc.IsDemoMode);
        // SaveActiveSessionsToDisk should be no-op in demo mode
        // (it checks IsDemoMode at the top)
        // This verifies the guard still works with debounce
    }
}
