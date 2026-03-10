using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for thread-safe Organization.Sessions/Groups access:
/// snapshot isolation, locked mutation helpers, and concurrent safety.
/// </summary>
public class OrganizationThreadSafetyTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public OrganizationThreadSafetyTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    // --- Snapshot isolation ---

    [Fact]
    public void SnapshotSessionMetas_ReturnsIndependentCopy()
    {
        var svc = CreateService();
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "A" });
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "B" });

        var snapshot = svc.SnapshotSessionMetas();
        snapshot.Add(new SessionMeta { SessionName = "C" });
        snapshot.RemoveAt(0);

        Assert.Equal(2, svc.Organization.Sessions.Count);
        Assert.Equal("A", svc.Organization.Sessions[0].SessionName);
    }

    [Fact]
    public void SnapshotGroups_ReturnsIndependentCopy()
    {
        var svc = CreateService();
        svc.Organization.Groups.Add(new SessionGroup { Id = "g1", Name = "Group1" });

        var snapshot = svc.SnapshotGroups();
        snapshot.Add(new SessionGroup { Id = "g2", Name = "Group2" });
        snapshot.RemoveAt(0);

        // Original should still have default + g1
        Assert.Equal(2, svc.Organization.Groups.Count);
    }

    // --- Locked mutation helpers ---

    [Fact]
    public void AddSessionMeta_AddsToOrganization()
    {
        var svc = CreateService();
        svc.AddSessionMeta(new SessionMeta { SessionName = "test" });

        Assert.Single(svc.Organization.Sessions);
        Assert.Equal("test", svc.Organization.Sessions[0].SessionName);
    }

    [Fact]
    public void RemoveSessionMetasWhere_RemovesMatching()
    {
        var svc = CreateService();
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "keep" });
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "remove-1" });
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "remove-2" });

        var removed = svc.RemoveSessionMetasWhere(m => m.SessionName.StartsWith("remove"));

        Assert.Equal(2, removed);
        Assert.Single(svc.Organization.Sessions);
        Assert.Equal("keep", svc.Organization.Sessions[0].SessionName);
    }

    [Fact]
    public void RemoveSessionMeta_RemovesSpecificInstance()
    {
        var svc = CreateService();
        var meta = new SessionMeta { SessionName = "target" };
        svc.Organization.Sessions.Add(meta);
        svc.Organization.Sessions.Add(new SessionMeta { SessionName = "other" });

        var removed = svc.RemoveSessionMeta(meta);

        Assert.True(removed);
        Assert.Single(svc.Organization.Sessions);
        Assert.Equal("other", svc.Organization.Sessions[0].SessionName);
    }

    [Fact]
    public void AddGroup_AddsToOrganization()
    {
        var svc = CreateService();
        var initialCount = svc.Organization.Groups.Count;
        svc.AddGroup(new SessionGroup { Id = "new-group", Name = "New" });

        Assert.Equal(initialCount + 1, svc.Organization.Groups.Count);
    }

    [Fact]
    public void InsertGroup_InsertsAtIndex()
    {
        var svc = CreateService();
        svc.InsertGroup(0, new SessionGroup { Id = "first", Name = "First" });

        Assert.Equal("first", svc.Organization.Groups[0].Id);
    }

    [Fact]
    public void RemoveGroupsWhere_RemovesMatching()
    {
        var svc = CreateService();
        svc.Organization.Groups.Add(new SessionGroup { Id = "del", Name = "Delete" });

        var removed = svc.RemoveGroupsWhere(g => g.Id == "del");

        Assert.Equal(1, removed);
        Assert.DoesNotContain(svc.Organization.Groups, g => g.Id == "del");
    }

    // --- Concurrent read + write safety ---

    [Fact]
    public async Task ConcurrentSnapshotAndMutation_DoesNotThrow()
    {
        var svc = CreateService();
        // Pre-populate
        for (int i = 0; i < 100; i++)
            svc.Organization.Sessions.Add(new SessionMeta { SessionName = $"s{i}" });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exceptions = new List<Exception>();

        // Background reader — continuously snapshot
        var reader = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var snap = svc.SnapshotSessionMetas();
                    _ = snap.Count; // force enumeration
                }
                catch (Exception ex) { lock (exceptions) exceptions.Add(ex); }
            }
        });

        // Writer — add and remove via locked helpers
        var writer = Task.Run(() =>
        {
            for (int i = 100; i < 200 && !cts.Token.IsCancellationRequested; i++)
            {
                try
                {
                    svc.AddSessionMeta(new SessionMeta { SessionName = $"w{i}" });
                    svc.RemoveSessionMetasWhere(m => m.SessionName == $"w{i}");
                }
                catch (Exception ex) { lock (exceptions) exceptions.Add(ex); }
            }
            cts.Cancel();
        });

        await Task.WhenAll(reader, writer);
        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task ConcurrentGroupSnapshotAndMutation_DoesNotThrow()
    {
        var svc = CreateService();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exceptions = new List<Exception>();

        var reader = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var snap = svc.SnapshotGroups();
                    _ = snap.Count;
                }
                catch (Exception ex) { lock (exceptions) exceptions.Add(ex); }
            }
        });

        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 100 && !cts.Token.IsCancellationRequested; i++)
            {
                try
                {
                    var id = $"g{i}";
                    svc.AddGroup(new SessionGroup { Id = id, Name = $"Group{i}" });
                    svc.RemoveGroupsWhere(g => g.Id == id);
                }
                catch (Exception ex) { lock (exceptions) exceptions.Add(ex); }
            }
            cts.Cancel();
        });

        await Task.WhenAll(reader, writer);
        Assert.Empty(exceptions);
    }
}
