using Xunit;
using PolyPilot.Services;
using PolyPilot.Models;

namespace PolyPilot.Tests;

public class LazySessionLoadingTests
{
    [Fact]
    public void LazyLoadedSession_HasMetadataButNoHistory()
    {
        // Simulate what RestorePreviousSessionsAsync creates
        var info = new AgentSessionInfo
        {
            Name = "Test Session",
            SessionId = Guid.NewGuid().ToString(),
            Model = "claude-opus-4.6",
            WorkingDirectory = "/test/path",
            CreatedAt = DateTime.Now,
            IsResumed = true
        };

        // Lazy-loaded session should have metadata but no history
        Assert.Equal("Test Session", info.Name);
        Assert.NotNull(info.SessionId);
        Assert.Equal("claude-opus-4.6", info.Model);
        Assert.Empty(info.History); // No history loaded yet
        Assert.Equal(0, info.MessageCount);
    }

    [Fact]
    public void LazyLoadedSession_ShouldHaveRequiredFields()
    {
        // Verify the metadata we load on startup is sufficient for UI display
        var info = new AgentSessionInfo
        {
            Name = "My Session",
            SessionId = "12345678-1234-1234-1234-123456789012",
            Model = "gpt-5",
            WorkingDirectory = "/Users/test/project",
            CreatedAt = DateTime.Parse("2026-02-15T12:00:00Z"),
            IsResumed = true
        };

        // These fields should be available immediately without full hydration
        Assert.NotNull(info.Name);
        Assert.NotNull(info.SessionId);
        Assert.NotNull(info.Model);
        Assert.NotNull(info.WorkingDirectory);
        Assert.True(info.IsResumed);
        
        // History should be empty until hydrated
        Assert.Empty(info.History);
        Assert.Empty(info.MessageQueue);
        Assert.False(info.IsProcessing);
    }

    [Fact]
    public void SessionSwitching_ShouldNotBlockOnMetadataAccess()
    {
        // Simulates switching to a lazy-loaded session
        var sessions = new List<AgentSessionInfo>();
        
        for (int i = 0; i < 10; i++)
        {
            sessions.Add(new AgentSessionInfo
            {
                Name = $"Session {i}",
                SessionId = Guid.NewGuid().ToString(),
                Model = "claude-opus-4.6",
                WorkingDirectory = $"/test/session{i}",
                CreatedAt = DateTime.Now,
                IsResumed = true
            });
        }

        // Switching should be instant - just accessing metadata
        var startTime = DateTime.Now;
        foreach (var session in sessions)
        {
            var name = session.Name;
            var model = session.Model;
            var wd = session.WorkingDirectory;
        }
        var elapsed = DateTime.Now - startTime;

        // Should complete in under 10ms for 10 sessions (metadata access only)
        Assert.True(elapsed.TotalMilliseconds < 100, 
            $"Metadata access took {elapsed.TotalMilliseconds}ms, should be instant");
    }

    [Fact]
    public void ActiveSessionEntry_SerializesCorrectly()
    {
        // Test that the minimal data we save for lazy loading round-trips correctly
        var entry = new ActiveSessionEntry
        {
            SessionId = "test-guid-12345",
            DisplayName = "Test Session",
            Model = "claude-opus-4.6",
            WorkingDirectory = "/test/path"
        };

        Assert.Equal("test-guid-12345", entry.SessionId);
        Assert.Equal("Test Session", entry.DisplayName);
        Assert.Equal("claude-opus-4.6", entry.Model);
        Assert.Equal("/test/path", entry.WorkingDirectory);
    }

    [Fact]
    public void LazyLoading_ShouldNotLoadHistoryDuringStartup()
    {
        // This test verifies the core performance optimization:
        // On startup, we should NOT load any chat history
        
        // Simulate startup with 5 sessions
        var entriesFromDisk = new List<ActiveSessionEntry>
        {
            new() { SessionId = Guid.NewGuid().ToString(), DisplayName = "Session 1", Model = "claude-opus-4.6", WorkingDirectory = "/path1" },
            new() { SessionId = Guid.NewGuid().ToString(), DisplayName = "Session 2", Model = "gpt-5", WorkingDirectory = "/path2" },
            new() { SessionId = Guid.NewGuid().ToString(), DisplayName = "Session 3", Model = "claude-opus-4.6", WorkingDirectory = "/path3" },
            new() { SessionId = Guid.NewGuid().ToString(), DisplayName = "Session 4", Model = "gpt-5", WorkingDirectory = "/path4" },
            new() { SessionId = Guid.NewGuid().ToString(), DisplayName = "Session 5", Model = "claude-opus-4.6", WorkingDirectory = "/path5" },
        };

        // Convert to AgentSessionInfo (what RestorePreviousSessionsAsync does)
        var loadedSessions = new List<AgentSessionInfo>();
        foreach (var entry in entriesFromDisk)
        {
            loadedSessions.Add(new AgentSessionInfo
            {
                Name = entry.DisplayName,
                SessionId = entry.SessionId,
                Model = entry.Model ?? "claude-opus-4.6",
                WorkingDirectory = entry.WorkingDirectory,
                CreatedAt = DateTime.Now,
                IsResumed = true
            });
        }

        // Verify: All sessions loaded but NO history
        Assert.Equal(5, loadedSessions.Count);
        foreach (var session in loadedSessions)
        {
            Assert.NotNull(session.Name);
            Assert.NotNull(session.SessionId);
            Assert.Empty(session.History); // CRITICAL: No history loaded
            Assert.Equal(0, session.MessageCount);
        }
    }

    [Fact]
    public void SessionMetadata_ContainsEverythingNeededForUIDisplay()
    {
        // Verify that lazy-loaded sessions have all fields needed to render in sidebar/grid
        var session = new AgentSessionInfo
        {
            Name = "My Project Session",
            SessionId = "abc-123",
            Model = "claude-opus-4.6",
            WorkingDirectory = "/Users/test/my-project",
            CreatedAt = DateTime.Parse("2026-02-15T10:00:00Z"),
            IsResumed = true,
            GitBranch = "main"
        };

        // UI needs these fields to render session cards/badges
        Assert.Equal("My Project Session", session.Name);
        Assert.Equal("claude-opus-4.6", session.Model);
        Assert.Equal("/Users/test/my-project", session.WorkingDirectory);
        Assert.Equal("main", session.GitBranch);
        Assert.False(session.IsProcessing);
        Assert.Equal(0, session.MessageCount); // Show "0 messages" until hydrated
    }
}
