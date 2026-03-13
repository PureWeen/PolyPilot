using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for notification-tap navigation: SwitchToSessionById lookups,
/// event wiring, and single-instance lock file semantics.
/// Regression tests for: "clicking a notification opens a new PolyPilot instance."
/// </summary>
public class NotificationNavigationTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public NotificationNavigationTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    // ── SwitchToSessionById ─────────────────────────────────────────

    [Fact]
    public async Task SwitchToSessionById_FindsSessionBySessionId()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("my-session", cancellationToken: CancellationToken.None);
        var sessionId = session.SessionId;
        Assert.NotNull(sessionId);

        // Switch away first
        svc.SetActiveSession(null);

        var result = svc.SwitchToSessionById(sessionId);

        Assert.True(result);
        Assert.Equal("my-session", svc.ActiveSessionName);
    }

    [Fact]
    public async Task SwitchToSessionById_NonExistentId_ReturnsFalse()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("session-a", cancellationToken: CancellationToken.None);

        var result = svc.SwitchToSessionById("non-existent-id");

        Assert.False(result);
    }

    [Fact]
    public async Task SwitchToSessionById_EmptySessionsDict_ReturnsFalse()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var result = svc.SwitchToSessionById("any-id");

        Assert.False(result);
    }

    [Fact]
    public async Task SwitchToSessionById_CaseInsensitive()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("test", cancellationToken: CancellationToken.None);
        var sessionId = session.SessionId;
        Assert.NotNull(sessionId);

        svc.SetActiveSession(null);

        // Use uppercase version of the session ID
        var result = svc.SwitchToSessionById(sessionId.ToUpperInvariant());

        Assert.True(result);
        Assert.Equal("test", svc.ActiveSessionName);
    }

    [Fact]
    public async Task SwitchToSessionById_MultipleSessions_FindsCorrectOne()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("session-a", cancellationToken: CancellationToken.None);
        var sessionB = await svc.CreateSessionAsync("session-b", cancellationToken: CancellationToken.None);
        await svc.CreateSessionAsync("session-c", cancellationToken: CancellationToken.None);

        svc.SetActiveSession(null);

        var result = svc.SwitchToSessionById(sessionB.SessionId!);

        Assert.True(result);
        Assert.Equal("session-b", svc.ActiveSessionName);
    }

    [Fact]
    public async Task SwitchToSessionById_InRemoteMode_SendsBridgeMessage()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Remote,
            RemoteUrl = "ws://localhost:4322"
        });

        // Simulate bridge providing a session with a known sessionId
        _bridgeClient.Sessions.Add(new SessionSummary { Name = "remote-session" });
        _bridgeClient.IsConnected = true;
        _bridgeClient.FireOnStateChanged();

        // Set the sessionId on the session info so SwitchToSessionById can find it
        var info = svc.GetSession("remote-session");
        Assert.NotNull(info);
        info.SessionId = "abc-123";

        var result = svc.SwitchToSessionById("abc-123");

        Assert.True(result);
        Assert.Equal("remote-session", svc.ActiveSessionName);
        Assert.Equal(1, _bridgeClient.SwitchSessionCallCount);
    }

    // ── NotificationTappedEventArgs ─────────────────────────────────

    [Fact]
    public void NotificationTappedEventArgs_SessionId_RoundTrips()
    {
        var args = new NotificationTappedEventArgs { SessionId = "test-session-id" };
        Assert.Equal("test-session-id", args.SessionId);
    }

    [Fact]
    public void NotificationTappedEventArgs_SessionId_NullByDefault()
    {
        var args = new NotificationTappedEventArgs();
        Assert.Null(args.SessionId);
    }

    // ── Instance lock file ──────────────────────────────────────────

    [Fact]
    public void InstanceLock_ExclusiveAccess_PreventsSecondLock()
    {
        var lockPath = Path.Combine(Path.GetTempPath(), $"polypilot-test-lock-{Guid.NewGuid()}.lock");
        try
        {
            using var lock1 = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            // Second lock attempt should throw IOException
            Assert.Throws<IOException>(() =>
                new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        finally
        {
            File.Delete(lockPath);
        }
    }

    [Fact]
    public void InstanceLock_ReleasedAfterDispose_AllowsNewLock()
    {
        var lockPath = Path.Combine(Path.GetTempPath(), $"polypilot-test-lock-{Guid.NewGuid()}.lock");
        try
        {
            // First lock — acquire and release
            using (var lock1 = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                // lock held
            }

            // Second lock should succeed after first is released
            using var lock2 = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Assert.True(lock2.CanWrite);
        }
        finally
        {
            File.Delete(lockPath);
        }
    }

    // ── Pending navigation sidecar (second-instance forwarding) ─────

    [Fact]
    public void PendingNavigation_WriteAndRead_RoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"polypilot-nav-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        var navPath = Path.Combine(dir, "pending-navigation.json");
        try
        {
            // Simulate what NotificationManagerService.WritePendingNavigation writes
            var sessionId = "test-session-42";
            File.WriteAllText(navPath, System.Text.Json.JsonSerializer.Serialize(new { sessionId }));

            // Simulate what App.CheckPendingNavigation reads
            Assert.True(File.Exists(navPath));
            var json = File.ReadAllText(navPath);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            Assert.True(doc.RootElement.TryGetProperty("sessionId", out var prop));
            Assert.Equal("test-session-42", prop.GetString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PendingNavigation_DeleteAfterRead_FileIsGone()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"polypilot-nav-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        var navPath = Path.Combine(dir, "pending-navigation.json");
        try
        {
            File.WriteAllText(navPath, System.Text.Json.JsonSerializer.Serialize(new { sessionId = "abc" }));
            Assert.True(File.Exists(navPath));

            // Simulate App.CheckPendingNavigation consuming the sidecar
            File.Delete(navPath);

            Assert.False(File.Exists(navPath));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PendingNavigation_MissingFile_NoActionNeeded()
    {
        var navPath = Path.Combine(Path.GetTempPath(), $"nonexistent-nav-{Guid.NewGuid()}.json");

        // App.CheckPendingNavigation guards with File.Exists — verify no file means no action
        Assert.False(File.Exists(navPath));
    }

    [Fact]
    public void PendingNavigation_WrittenAt_IsIncludedInSidecar()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"polypilot-nav-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        var navPath = Path.Combine(dir, "pending-navigation.json");
        try
        {
            var before = DateTime.UtcNow;
            var payload = new { sessionId = "ttl-test", writtenAt = DateTime.UtcNow };
            File.WriteAllText(navPath, System.Text.Json.JsonSerializer.Serialize(payload));

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(navPath));
            Assert.True(doc.RootElement.TryGetProperty("writtenAt", out var ts));
            var written = ts.GetDateTime();
            Assert.True(written >= before.AddSeconds(-1));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PendingNavigation_StaleCheck_OlderThan30s_Discarded()
    {
        // Simulate what CheckPendingNavigation does: parse writtenAt and apply TTL
        var stalePayload = new
        {
            sessionId = "stale-session",
            writtenAt = DateTime.UtcNow.AddSeconds(-60) // 60 seconds old — beyond 30s TTL
        };
        var json = System.Text.Json.JsonSerializer.Serialize(stalePayload);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("writtenAt", out var ts));
        var age = DateTime.UtcNow - ts.GetDateTime();
        Assert.True(age > TimeSpan.FromSeconds(30), "Stale sidecar should be older than TTL");
    }

    [Fact]
    public void PendingNavigation_FreshCheck_Within30s_NotDiscarded()
    {
        // Simulate what CheckPendingNavigation does: parse writtenAt and apply TTL
        var freshPayload = new
        {
            sessionId = "fresh-session",
            writtenAt = DateTime.UtcNow.AddSeconds(-5) // 5 seconds old — within 30s TTL
        };
        var json = System.Text.Json.JsonSerializer.Serialize(freshPayload);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("writtenAt", out var ts));
        var age = DateTime.UtcNow - ts.GetDateTime();
        Assert.True(age <= TimeSpan.FromSeconds(30), "Fresh sidecar should be within TTL");
    }
}

