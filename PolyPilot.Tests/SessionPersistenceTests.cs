using System.Text.Json;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the session persistence merge logic in SaveActiveSessionsToDisk.
/// The merge ensures sessions aren't lost during mode switches or app kill.
/// </summary>
public class SessionPersistenceTests
{
    private static ActiveSessionEntry Entry(string id, string? name = null) =>
        new() { SessionId = id, DisplayName = name ?? id, Model = "m", WorkingDirectory = "/w" };

    // --- MergeSessionEntries: basic behavior ---

    [Fact]
    public void Merge_NoPersistedEntries_ReturnsActiveOnly()
    {
        var active = new List<ActiveSessionEntry> { Entry("a1", "Session1") };
        var persisted = new List<ActiveSessionEntry>();
        var closed = new HashSet<string>();

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), _ => true);

        Assert.Single(result);
        Assert.Equal("a1", result[0].SessionId);
    }

    [Fact]
    public void Merge_NoActiveEntries_ReturnsPersistedIfDirExists()
    {
        var active = new List<ActiveSessionEntry>();
        var persisted = new List<ActiveSessionEntry> { Entry("p1", "Persisted1") };
        var closed = new HashSet<string>();

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), _ => true);

        Assert.Single(result);
        Assert.Equal("p1", result[0].SessionId);
    }

    [Fact]
    public void Merge_BothActiveAndPersisted_CombinesBoth()
    {
        var active = new List<ActiveSessionEntry> { Entry("a1") };
        var persisted = new List<ActiveSessionEntry> { Entry("p1") };
        var closed = new HashSet<string>();

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), _ => true);

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

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), _ => true);

        Assert.Single(result);
        Assert.Equal("ActiveName", result[0].DisplayName);
    }

    [Fact]
    public void Merge_CaseInsensitiveDedup()
    {
        var active = new List<ActiveSessionEntry> { Entry("ABC-123") };
        var persisted = new List<ActiveSessionEntry> { Entry("abc-123") };
        var closed = new HashSet<string>();

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), _ => true);

        Assert.Single(result);
    }

    // --- MergeSessionEntries: closed sessions excluded ---

    [Fact]
    public void Merge_ClosedSession_NotMergedBack()
    {
        var active = new List<ActiveSessionEntry>();
        var persisted = new List<ActiveSessionEntry> { Entry("closed-1", "ClosedSession") };
        var closed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "closed-1" };

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), _ => true);

        Assert.Empty(result);
    }

    [Fact]
    public void Merge_ClosedSession_CaseInsensitive()
    {
        var active = new List<ActiveSessionEntry>();
        var persisted = new List<ActiveSessionEntry> { Entry("ABC-DEF") };
        var closed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "abc-def" };

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), _ => true);

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

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), _ => true);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, e => e.SessionId == "close-me");
    }

    [Fact]
    public void Merge_ClosedByDisplayName_NotMergedBack()
    {
        var active = new List<ActiveSessionEntry>();
        var persisted = new List<ActiveSessionEntry> { Entry("id-1", "Worker-1") };
        var closedIds = new HashSet<string>();
        var closedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Worker-1" };

        var result = CopilotService.MergeSessionEntries(active, persisted, closedIds, closedNames, _ => true);

        Assert.Empty(result);
    }

    [Fact]
    public void Merge_ClosedByDisplayName_CaseInsensitive()
    {
        var active = new List<ActiveSessionEntry>();
        var persisted = new List<ActiveSessionEntry> { Entry("id-1", "Worker-1") };
        var closedIds = new HashSet<string>();
        var closedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "worker-1" };

        var result = CopilotService.MergeSessionEntries(active, persisted, closedIds, closedNames, _ => true);

        Assert.Empty(result);
    }

    [Fact]
    public void Merge_DuplicateSessionIds_BothFilteredByName()
    {
        var active = new List<ActiveSessionEntry>();
        var persisted = new List<ActiveSessionEntry>
        {
            Entry("id-1", "Worker-1"),
            Entry("id-2", "Worker-1")
        };
        var closedIds = new HashSet<string>();
        var closedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Worker-1" };

        var result = CopilotService.MergeSessionEntries(active, persisted, closedIds, closedNames, _ => true);

        Assert.Empty(result);
    }

    // --- MergeSessionEntries: directory existence check ---

    [Fact]
    public void Merge_PersistedWithMissingDir_NotMerged()
    {
        var active = new List<ActiveSessionEntry>();
        var persisted = new List<ActiveSessionEntry> { Entry("no-dir") };
        var closed = new HashSet<string>();

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), _ => false);

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
            active, persisted, closed, new HashSet<string>(), id => existingDirs.Contains(id));

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, e => e.SessionId == "gone");
    }

    // --- MergeSessionEntries: display name deduplication ---

    [Fact]
    public void Merge_DuplicateDisplayName_ActiveWins_PersistedDropped()
    {
        // Active session "MyChat" has ID "new-id" (from reconnect).
        // Persisted has old entry with stale ID "old-id" but same display name.
        // Only the active entry should survive — no ghost duplicates.
        var active = new List<ActiveSessionEntry> { Entry("new-id", "MyChat") };
        var persisted = new List<ActiveSessionEntry> { Entry("old-id", "MyChat") };
        var closed = new HashSet<string>();

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), _ => true);

        Assert.Single(result);
        Assert.Equal("new-id", result[0].SessionId);
    }

    [Fact]
    public void Merge_MultipleGhostEntriesSameDisplayName_OnlyOneKept()
    {
        // Simulates the "28 MEssagePierce entries" bug: multiple persisted entries
        // with different session IDs but the same display name. Only one should survive.
        var active = new List<ActiveSessionEntry>();
        var persisted = new List<ActiveSessionEntry>
        {
            Entry("ghost-1", "MEssagePierce"),
            Entry("ghost-2", "MEssagePierce"),
            Entry("ghost-3", "MEssagePierce"),
            Entry("real-1", "OtherSession"),
        };
        var closed = new HashSet<string>();

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), _ => true);

        Assert.Equal(2, result.Count);
        // First MEssagePierce entry wins, others are deduped
        Assert.Single(result, e => e.DisplayName == "MEssagePierce");
        Assert.Equal("ghost-1", result.First(e => e.DisplayName == "MEssagePierce").SessionId);
        Assert.Single(result, e => e.DisplayName == "OtherSession");
    }

    [Fact]
    public void Merge_ActiveAndPersistedDifferentNames_BothKept()
    {
        // Entries with different display names should both be kept.
        var active = new List<ActiveSessionEntry> { Entry("id-1", "Alpha") };
        var persisted = new List<ActiveSessionEntry> { Entry("id-2", "Beta") };
        var closed = new HashSet<string>();

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), _ => true);

        Assert.Equal(2, result.Count);
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

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), _ => true);

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

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), _ => true);

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

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), _ => true);

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

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), _ => true);

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

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), _ => true);

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

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), _ => false);

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

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), _ => true);

        Assert.Single(result);
        Assert.Equal("deploy to production", result[0].LastPrompt);
    }

    // --- DeleteGroup persistence tests ---

    [Fact]
    public void Merge_DeletedMultiAgentSessions_NotInClosedIds_Survive()
    {
        // Reproduces the bug: multi-agent sessions deleted via DeleteGroup
        // but their IDs not added to closedIds — merge re-adds them from file
        var active = new List<ActiveSessionEntry>
        {
            Entry("regular-session", "My Session"),
        };

        // These were written to disk before DeleteGroup ran
        var persisted = new List<ActiveSessionEntry>
        {
            Entry("regular-session", "My Session"),
            Entry("team-orch-id", "Team-orchestrator"),
            Entry("team-worker-id", "Team-worker-1"),
        };

        // Bug: closedIds is empty because DeleteGroup didn't add them
        var closedIds = new HashSet<string>();

        var result = CopilotService.MergeSessionEntries(active, persisted, closedIds, new HashSet<string>(), _ => true);

        // BUG: deleted sessions survive the merge (3 total instead of 1)
        Assert.Equal(3, result.Count);
        Assert.Contains(result, e => e.SessionId == "team-orch-id");
        Assert.Contains(result, e => e.SessionId == "team-worker-id");
    }

    [Fact]
    public void Merge_DeletedMultiAgentSessions_InClosedIds_Excluded()
    {
        // After fix: DeleteGroup adds session IDs to closedIds before merge
        var active = new List<ActiveSessionEntry>
        {
            Entry("regular-session", "My Session"),
        };

        var persisted = new List<ActiveSessionEntry>
        {
            Entry("regular-session", "My Session"),
            Entry("team-orch-id", "Team-orchestrator"),
            Entry("team-worker-id", "Team-worker-1"),
        };

        // Fix: closedIds contains the deleted sessions
        var closedIds = new HashSet<string> { "team-orch-id", "team-worker-id" };

        var result = CopilotService.MergeSessionEntries(active, persisted, closedIds, new HashSet<string>(), _ => true);

        // Deleted sessions are properly excluded
        Assert.Single(result);
        Assert.Equal("regular-session", result[0].SessionId);
    }

    // --- Restore fallback: structural regression guards ---
    // These verify the fallback path in RestorePreviousSessionsAsync preserves
    // history and usage stats when creating a fresh session (PR #225 regression).

    [Fact]
    public void RestoreFallback_LoadsHistoryFromOldSession()
    {
        // STRUCTURAL REGRESSION GUARD: The "Session not found" fallback in
        // RestorePreviousSessionsAsync must call LoadHistoryFromDisk(entry.SessionId)
        // before CreateSessionAsync so conversation history is recovered.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Persistence.cs"));

        var fallbackIdx = source.IndexOf("Falling back to CreateSessionAsync", StringComparison.Ordinal);
        Assert.True(fallbackIdx > 0, "Could not find fallback path in RestorePreviousSessionsAsync");

        // LoadHistoryFromDisk must appear BEFORE CreateSessionAsync in the fallback block
        var beforeFallback = source.Substring(
            Math.Max(0, fallbackIdx - 500),
            Math.Min(500, fallbackIdx));
        Assert.Contains("LoadHistoryFromDisk", beforeFallback);
    }

    [Fact]
    public void RestoreFallback_InjectsHistoryIntoRecreatedSession()
    {
        // STRUCTURAL REGRESSION GUARD: After CreateSessionAsync, the fallback must
        // inject the recovered history into the new session's Info.History.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Persistence.cs"));

        var fallbackIdx = source.IndexOf("Falling back to CreateSessionAsync", StringComparison.Ordinal);
        Assert.True(fallbackIdx > 0);

        var afterFallback = source.Substring(fallbackIdx, Math.Min(1500, source.Length - fallbackIdx));
        Assert.Contains("History.Add", afterFallback);
        Assert.Contains("MessageCount", afterFallback);
        Assert.Contains("LastReadMessageCount", afterFallback);
    }

    [Fact]
    public void RestoreFallback_RestoresUsageStats()
    {
        // STRUCTURAL REGRESSION GUARD: The fallback must call RestoreUsageStats(entry)
        // to preserve token counts, CreatedAt, and other stats from the old session.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Persistence.cs"));

        var fallbackIdx = source.IndexOf("Falling back to CreateSessionAsync", StringComparison.Ordinal);
        Assert.True(fallbackIdx > 0);

        var afterFallback = source.Substring(fallbackIdx, Math.Min(2500, source.Length - fallbackIdx));
        Assert.Contains("RestoreUsageStats", afterFallback);
    }

    [Fact]
    public void RestoreFallback_SyncsHistoryToDatabase()
    {
        // STRUCTURAL REGRESSION GUARD: The fallback must sync recovered history
        // to the chat database under the new session ID so it persists.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Persistence.cs"));

        var fallbackIdx = source.IndexOf("Falling back to CreateSessionAsync", StringComparison.Ordinal);
        Assert.True(fallbackIdx > 0);

        var afterFallback = source.Substring(fallbackIdx, Math.Min(1500, source.Length - fallbackIdx));
        Assert.Contains("BulkInsertAsync", afterFallback);
    }

    [Fact]
    public void RestoreFallback_AddsReconnectionIndicator()
    {
        // STRUCTURAL REGRESSION GUARD: The fallback must add a system message
        // indicating the session was recreated with recovered history, so the
        // user knows the session state was reconstructed.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Persistence.cs"));

        var fallbackIdx = source.IndexOf("Falling back to CreateSessionAsync", StringComparison.Ordinal);
        Assert.True(fallbackIdx > 0);

        var afterFallback = source.Substring(fallbackIdx, Math.Min(1500, source.Length - fallbackIdx));
        Assert.Contains("Session recreated", afterFallback);
        Assert.Contains("SystemMessage", afterFallback);
    }

    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root");
    }

    // --- RECONNECT handler: structural regression guards ---
    // These verify the RECONNECT path in SendPromptAsync persists the new session ID.
    // Without this, the debounced save captures a stale pre-reconnect session ID,
    // causing the next restore to find an empty directory with no events.jsonl.

    [Fact]
    public void Reconnect_CallsSaveActiveSessionsToDisk_AfterUpdatingSessionId()
    {
        // STRUCTURAL REGRESSION GUARD: After RECONNECT updates state.Info.SessionId
        // and _sessions[sessionName] = newState, SaveActiveSessionsToDisk() must be
        // called so the new session ID is persisted immediately.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs"));

        // Find the specific assignment where the new state replaces the old one
        var sessionsAssign = source.IndexOf("_sessions[sessionName] = newState", StringComparison.Ordinal);
        Assert.True(sessionsAssign > 0, "Could not find _sessions assignment in RECONNECT handler");

        // SaveActiveSessionsToDisk must appear within the next 500 chars (before StartProcessingWatchdog)
        var afterAssign = source.Substring(sessionsAssign, Math.Min(500, source.Length - sessionsAssign));
        Assert.Contains("SaveActiveSessionsToDisk()", afterAssign);
    }

    // --- Restore: events.jsonl existence check ---

    [Fact]
    public void Restore_SkipsSessionsWithoutEventsJsonl()
    {
        // STRUCTURAL REGRESSION GUARD: The restore loop must check for events.jsonl
        // existence, not just directory existence. Empty directories (created by SDK
        // during ResumeSessionAsync) should be skipped to prevent ghost sessions.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Persistence.cs"));

        var restoreIdx = source.IndexOf("RestorePreviousSessionsAsync", StringComparison.Ordinal);
        Assert.True(restoreIdx > 0);

        var restoreBlock = source.Substring(restoreIdx, Math.Min(5000, source.Length - restoreIdx));
        // Must check events.jsonl, not just Directory.Exists
        Assert.Contains("events.jsonl", restoreBlock);
    }

    [Fact]
    public void SaveActiveSessionsToDisk_ChecksEventsJsonlNotJustDirectory()
    {
        // STRUCTURAL REGRESSION GUARD: The sessionDirExists callback in
        // WriteActiveSessionsFile/SaveActiveSessionsToDisk must check for
        // events.jsonl file existence, not just the directory.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Persistence.cs"));

        var mergeCallIdx = source.IndexOf("MergeSessionEntries(entries", StringComparison.Ordinal);
        Assert.True(mergeCallIdx > 0);

        // The callback passed to MergeSessionEntries must reference events.jsonl
        var mergeCall = source.Substring(mergeCallIdx, Math.Min(500, source.Length - mergeCallIdx));
        Assert.Contains("events.jsonl", mergeCall);
    }
}
