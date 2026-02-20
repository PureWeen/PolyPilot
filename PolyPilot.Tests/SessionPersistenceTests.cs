using System.Text.Json;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the session persistence merge logic in SaveActiveSessionsToDisk.
/// The merge ensures sessions aren't lost during mode switches or app kill.
/// </summary>
public class SessionPersistenceTests
{
    private static ActiveSessionEntry Entry(string id, string name = "s") =>
        new() { SessionId = id, DisplayName = name, Model = "m", WorkingDirectory = "/w" };

    // --- MergeSessionEntries: basic behavior ---

    [Fact]
    public void Merge_NoPersistedEntries_ReturnsActiveOnly()
    {
        var active = new List<ActiveSessionEntry> { Entry("a1", "Session1") };
        var persisted = new List<ActiveSessionEntry>();
        var closed = new HashSet<string>();

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, _ => true);

        Assert.Single(result);
        Assert.Equal("a1", result[0].SessionId);
    }

    [Fact]
    public void Merge_NoActiveEntries_ReturnsPersistedIfDirExists()
    {
        var active = new List<ActiveSessionEntry>();
        var persisted = new List<ActiveSessionEntry> { Entry("p1", "Persisted1") };
        var closed = new HashSet<string>();

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, _ => true);

        Assert.Single(result);
        Assert.Equal("p1", result[0].SessionId);
    }

    [Fact]
    public void Merge_BothActiveAndPersisted_CombinesBoth()
    {
        var active = new List<ActiveSessionEntry> { Entry("a1") };
        var persisted = new List<ActiveSessionEntry> { Entry("p1") };
        var closed = new HashSet<string>();

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, _ => true);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, e => e.SessionId == "a1");
        Assert.Contains(result, e => e.SessionId == "p1");
    }

    // --- MergeSessionEntries: dedup ---

    [Fact]
    public void Merge_DuplicateIdInBoth_KeepsActiveVersion()
    {
        var active = new List<ActiveSessionEntry> { Entry("same-id", "ActiveName") };
        var persisted = new List<ActiveSessionEntry> { Entry("same-id", "PersistedName") };
        var closed = new HashSet<string>();

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, _ => true);

        Assert.Single(result);
        Assert.Equal("ActiveName", result[0].DisplayName);
    }

    [Fact]
    public void Merge_CaseInsensitiveDedup()
    {
        var active = new List<ActiveSessionEntry> { Entry("ABC-123") };
        var persisted = new List<ActiveSessionEntry> { Entry("abc-123") };
        var closed = new HashSet<string>();

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, _ => true);

        Assert.Single(result);
    }

    // --- MergeSessionEntries: closed sessions excluded ---

    [Fact]
    public void Merge_ClosedSession_NotMergedBack()
    {
        var active = new List<ActiveSessionEntry>();
        var persisted = new List<ActiveSessionEntry> { Entry("closed-1", "ClosedSession") };
        var closed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "closed-1" };

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, _ => true);

        Assert.Empty(result);
    }

    [Fact]
    public void Merge_ClosedSession_CaseInsensitive()
    {
        var active = new List<ActiveSessionEntry>();
        var persisted = new List<ActiveSessionEntry> { Entry("ABC-DEF") };
        var closed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "abc-def" };

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, _ => true);

        Assert.Empty(result);
    }

    [Fact]
    public void Merge_OnlyClosedSessionExcluded_OthersKept()
    {
        var active = new List<ActiveSessionEntry>();
        var persisted = new List<ActiveSessionEntry>
        {
            Entry("keep-me", "Keep"),
            Entry("close-me", "Close"),
            Entry("also-keep", "AlsoKeep")
        };
        var closed = new HashSet<string> { "close-me" };

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, _ => true);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, e => e.SessionId == "close-me");
    }

    // --- MergeSessionEntries: directory existence check ---

    [Fact]
    public void Merge_PersistedWithMissingDir_NotMerged()
    {
        var active = new List<ActiveSessionEntry>();
        var persisted = new List<ActiveSessionEntry> { Entry("no-dir") };
        var closed = new HashSet<string>();

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, _ => false);

        Assert.Empty(result);
    }

    [Fact]
    public void Merge_SomeDirsExist_OnlyThoseKept()
    {
        var active = new List<ActiveSessionEntry>();
        var persisted = new List<ActiveSessionEntry>
        {
            Entry("exists"),
            Entry("gone"),
            Entry("also-exists")
        };
        var closed = new HashSet<string>();
        var existingDirs = new HashSet<string> { "exists", "also-exists" };

        var result = CopilotService.MergeSessionEntries(
            active, persisted, closed, id => existingDirs.Contains(id));

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, e => e.SessionId == "gone");
    }

    // --- MergeSessionEntries: mode switch simulation ---

    [Fact]
    public void Merge_SimulatePartialRestore_PreservesUnrestoredSessions()
    {
        // Simulate: 5 sessions in file, only 2 restored to memory
        var active = new List<ActiveSessionEntry>
        {
            Entry("restored-1", "Session1"),
            Entry("restored-2", "Session2")
        };
        var persisted = new List<ActiveSessionEntry>
        {
            Entry("restored-1", "Session1"),
            Entry("restored-2", "Session2"),
            Entry("failed-3", "Session3"),
            Entry("failed-4", "Session4"),
            Entry("failed-5", "Session5")
        };
        var closed = new HashSet<string>();

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, _ => true);

        Assert.Equal(5, result.Count);
    }

    [Fact]
    public void Merge_SimulateEmptyMemoryAfterClear_PreservesAll()
    {
        // Simulate: ReconnectAsync clears _sessions, save called immediately
        var active = new List<ActiveSessionEntry>();
        var persisted = new List<ActiveSessionEntry>
        {
            Entry("s1"), Entry("s2"), Entry("s3")
        };
        var closed = new HashSet<string>();

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, _ => true);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Merge_SimulateCloseAndModeSwitch_ClosedNotRestored()
    {
        // User closes session, then switches mode — closed session stays gone
        var active = new List<ActiveSessionEntry> { Entry("remaining") };
        var persisted = new List<ActiveSessionEntry>
        {
            Entry("remaining"),
            Entry("user-closed")
        };
        var closed = new HashSet<string> { "user-closed" };

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, _ => true);

        Assert.Single(result);
        Assert.Equal("remaining", result[0].SessionId);
    }

    // --- MergeSessionEntries: edge cases ---

    [Fact]
    public void Merge_BothEmpty_ReturnsEmpty()
    {
        var result = CopilotService.MergeSessionEntries(
            new List<ActiveSessionEntry>(),
            new List<ActiveSessionEntry>(),
            new HashSet<string>(),
            _ => true);

        Assert.Empty(result);
    }

    [Fact]
    public void Merge_DuplicatesInPersisted_NoDuplicatesInResult()
    {
        var active = new List<ActiveSessionEntry>();
        var persisted = new List<ActiveSessionEntry>
        {
            Entry("dup", "First"),
            Entry("dup", "Second")
        };
        var closed = new HashSet<string>();

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, _ => true);

        Assert.Single(result);
    }

    [Fact]
    public void Merge_PreservesOriginalActiveOrder()
    {
        var active = new List<ActiveSessionEntry>
        {
            Entry("z-last", "Z"),
            Entry("a-first", "A"),
            Entry("m-middle", "M")
        };
        var persisted = new List<ActiveSessionEntry>();
        var closed = new HashSet<string>();

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, _ => true);

        Assert.Equal("z-last", result[0].SessionId);
        Assert.Equal("a-first", result[1].SessionId);
        Assert.Equal("m-middle", result[2].SessionId);
    }

    [Fact]
    public void Merge_ActiveEntriesNotSubjectToDirectoryCheck()
    {
        // Active entries are always kept, even if directory check would fail
        var active = new List<ActiveSessionEntry> { Entry("active-no-dir") };
        var persisted = new List<ActiveSessionEntry>();
        var closed = new HashSet<string>();

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, _ => false);

        Assert.Single(result);
        Assert.Equal("active-no-dir", result[0].SessionId);
    }

    // --- ActiveSessionEntry.LastPrompt ---

    [Fact]
    public void ActiveSessionEntry_LastPrompt_RoundTrips()
    {
        var entry = new ActiveSessionEntry
        {
            SessionId = "s1",
            DisplayName = "Session1",
            Model = "gpt-4.1",
            WorkingDirectory = "/w",
            LastPrompt = "fix the bug in main.cs"
        };

        var json = JsonSerializer.Serialize(entry);
        var deserialized = JsonSerializer.Deserialize<ActiveSessionEntry>(json)!;

        Assert.Equal("fix the bug in main.cs", deserialized.LastPrompt);
        Assert.Equal("s1", deserialized.SessionId);
        Assert.Equal("Session1", deserialized.DisplayName);
    }

    [Fact]
    public void ActiveSessionEntry_LastPrompt_NullByDefault()
    {
        var entry = new ActiveSessionEntry
        {
            SessionId = "s2",
            DisplayName = "Session2",
            Model = "m",
            WorkingDirectory = "/w"
        };

        Assert.Null(entry.LastPrompt);

        // Also verify null survives round-trip
        var json = JsonSerializer.Serialize(entry);
        var deserialized = JsonSerializer.Deserialize<ActiveSessionEntry>(json)!;
        Assert.Null(deserialized.LastPrompt);
    }

    [Fact]
    public void MergeSessionEntries_PreservesLastPrompt()
    {
        // Persisted entry has a LastPrompt (session was mid-turn when app died).
        // Active list is empty (app just restarted, nothing in memory yet).
        // Merge should preserve the persisted entry including its LastPrompt.
        var active = new List<ActiveSessionEntry>();
        var persisted = new List<ActiveSessionEntry>
        {
            new()
            {
                SessionId = "mid-turn",
                DisplayName = "MidTurn",
                Model = "m",
                WorkingDirectory = "/w",
                LastPrompt = "deploy to production"
            }
        };
        var closed = new HashSet<string>();

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, _ => true);

        Assert.Single(result);
        Assert.Equal("deploy to production", result[0].LastPrompt);
    }
}
