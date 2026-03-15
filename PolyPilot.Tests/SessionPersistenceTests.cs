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
    public void Merge_PersistedEntriesWithSameDisplayName_AllKeptWhenNoActiveNameConflict()
    {
        // Persisted entries should only be deduped against active session names.
        // Legitimate persisted sessions that share a display name are restored and
        // collision renaming is handled later in the restore flow.
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

        Assert.Equal(4, result.Count);
        Assert.Equal(3, result.Count(e => e.DisplayName == "MEssagePierce"));
        Assert.Contains(result, e => e.SessionId == "ghost-1");
        Assert.Contains(result, e => e.SessionId == "ghost-2");
        Assert.Contains(result, e => e.SessionId == "ghost-3");
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

        var afterFallback = source.Substring(fallbackIdx, Math.Min(8000, source.Length - fallbackIdx));        Assert.Contains("History.Add", afterFallback);
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

        var afterFallback = source.Substring(fallbackIdx, Math.Min(8000, source.Length - fallbackIdx));
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

        var afterFallback = source.Substring(fallbackIdx, Math.Min(8000, source.Length - fallbackIdx));
        Assert.Contains("BulkInsertAsync", afterFallback);
    }

    [Fact]
    public void RestoreFallback_CopiesEventsJsonlToNewSessionDirectory()
    {
        // STRUCTURAL REGRESSION GUARD: The fallback must copy the old
        // events.jsonl into the recreated session directory so a later restart
        // can reload history from the new session ID.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Persistence.cs"));

        var fallbackIdx = source.IndexOf("Falling back to CreateSessionAsync", StringComparison.Ordinal);
        Assert.True(fallbackIdx > 0);

        var afterFallback = source.Substring(fallbackIdx, Math.Min(8000, source.Length - fallbackIdx));
        Assert.Contains("events.jsonl", afterFallback);
        // Must sanitize old events (skip corrupt JSON lines)
        Assert.Contains("JsonDocument.Parse", afterFallback);
        // Must handle both cases: new file doesn't exist (write) and does exist (prepend)
        Assert.Contains("WriteAllLines", afterFallback);
        Assert.Contains("Prepend old events", afterFallback);
        Assert.Contains("Copied events.jsonl", afterFallback);
    }

    [Fact]
    public void RestoreFallback_NormalizesIncompleteToolAndReasoningEntries()
    {
        // STRUCTURAL REGRESSION GUARD: The fallback must mark stale incomplete
        // tool-call and reasoning entries complete, matching ResumeSessionAsync.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Persistence.cs"));

        var fallbackIdx = source.IndexOf("Falling back to CreateSessionAsync", StringComparison.Ordinal);
        Assert.True(fallbackIdx > 0);

        var afterFallback = source.Substring(fallbackIdx, Math.Min(8000, source.Length - fallbackIdx));
        Assert.Contains("ChatMessageType.ToolCall", afterFallback);
        Assert.Contains("ChatMessageType.Reasoning", afterFallback);
        Assert.Contains("msg.IsComplete = true", afterFallback);
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

        var afterFallback = source.Substring(fallbackIdx, Math.Min(8000, source.Length - fallbackIdx));
        Assert.Contains("Session recreated", afterFallback);
        Assert.Contains("SystemMessage", afterFallback);
    }

    [Fact]
    public void RestoreFallback_MessageCount_SetAfterSystemMessage()
    {
        // STRUCTURAL REGRESSION GUARD: MessageCount and LastReadMessageCount must be
        // set AFTER the system message is added, not before. Otherwise the system
        // message ("🔄 Session recreated") isn't counted, and the unread indicator
        // doesn't trigger for it.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Persistence.cs"));

        var fallbackIdx = source.IndexOf("Falling back to CreateSessionAsync", StringComparison.Ordinal);
        Assert.True(fallbackIdx > 0);

        var afterFallback = source.Substring(fallbackIdx, Math.Min(8000, source.Length - fallbackIdx));
        var systemMsgIdx = afterFallback.IndexOf("Session recreated", StringComparison.Ordinal);
        var messageCountIdx = afterFallback.IndexOf("MessageCount = recreatedState.Info.History.Count", StringComparison.Ordinal);

        Assert.True(systemMsgIdx > 0, "System message not found in fallback path");
        Assert.True(messageCountIdx > 0, "MessageCount assignment not found in fallback path");
        Assert.True(messageCountIdx > systemMsgIdx,
            "MessageCount must be set AFTER the system message is added to History");
    }

    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root");
    }

    // --- Regression: flush after restore persists fallback-recreated session IDs ---

    [Fact]
    public void InitializeAsync_FlushesSessionsAfterRestore()
    {
        // STRUCTURAL REGRESSION GUARD: After RestorePreviousSessionsAsync returns and
        // IsRestoring = false, FlushSaveActiveSessionsToDisk must be called so that
        // session IDs changed during fallback recreation are persisted immediately.
        // Without this, active-sessions.json retains stale IDs and the next restart
        // reads the wrong events.jsonl, causing history loss.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs"));

        // Find the RestorePreviousSessionsAsync call in InitializeAsync
        var restoreCallIdx = source.IndexOf("await RestorePreviousSessionsAsync(cancellationToken);", StringComparison.Ordinal);
        Assert.True(restoreCallIdx > 0, "RestorePreviousSessionsAsync call not found");

        // FlushSaveActiveSessionsToDisk must appear within the next 500 chars (before ReconcileOrganization)
        var afterRestore = source.Substring(restoreCallIdx, Math.Min(500, source.Length - restoreCallIdx));
        Assert.Contains("FlushSaveActiveSessionsToDisk", afterRestore);
    }

    [Fact]
    public void ReconnectAsync_FlushesSessionsAfterRestore()
    {
        // STRUCTURAL REGRESSION GUARD: Same as InitializeAsync — the ReconnectAsync
        // path must also flush after restore to persist recreated session IDs.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.cs"));

        // Find all RestorePreviousSessionsAsync calls
        var indices = new List<int>();
        int idx = 0;
        while ((idx = source.IndexOf("RestorePreviousSessionsAsync", idx, StringComparison.Ordinal)) >= 0)
        {
            indices.Add(idx);
            idx += 1;
        }

        // There should be at least 2 call sites (InitializeAsync + ReconnectAsync)
        Assert.True(indices.Count >= 2, $"Expected at least 2 RestorePreviousSessionsAsync calls, found {indices.Count}");

        // Each call site must have FlushSaveActiveSessionsToDisk within 500 chars
        foreach (var callIdx in indices.Where(i => source.Substring(i, Math.Min(60, source.Length - i)).Contains("await")))
        {
            var after = source.Substring(callIdx, Math.Min(500, source.Length - callIdx));
            Assert.Contains("FlushSaveActiveSessionsToDisk", after);
        }
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

    // --- Restore/save: events.jsonl handling ---

    [Fact]
    public void Restore_DoesNotSkipSessionsBeforeFallbackCanHandleMissingEvents()
    {
        // STRUCTURAL REGRESSION GUARD: RestorePreviousSessionsAsync must not
        // short-circuit on missing events.jsonl before the existing fallback path
        // can recreate legitimate never-used sessions.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Persistence.cs"));

        var restoreIdx = source.IndexOf("RestorePreviousSessionsAsync", StringComparison.Ordinal);
        Assert.True(restoreIdx > 0);

        Assert.DoesNotContain("Skipping '{entry.DisplayName}' — no events.jsonl", source);
        Assert.Contains("Session not found", source);
    }

    [Fact]
    public void SaveActiveSessionsToDisk_AcceptsEventsOrRecentDirectories()
    {
        // STRUCTURAL REGRESSION GUARD: The sessionDirExists callback in
        // WriteActiveSessionsFile/SaveActiveSessionsToDisk must keep used sessions
        // via events.jsonl and also preserve newly created directories briefly.
        var source = File.ReadAllText(
            Path.Combine(GetRepoRoot(), "PolyPilot", "Services", "CopilotService.Persistence.cs"));

        var mergeCallIdx = source.IndexOf("MergeSessionEntries(entries", StringComparison.Ordinal);
        Assert.True(mergeCallIdx > 0);

        var mergeCall = source.Substring(mergeCallIdx, Math.Min(1000, source.Length - mergeCallIdx));
        Assert.Contains("Directory.Exists(dir)", mergeCall);
        Assert.Contains("events.jsonl", mergeCall);
        Assert.Contains("Directory.GetCreationTimeUtc", mergeCall);
        Assert.Contains("TotalMinutes < 5", mergeCall);
    }

    // --- Behavioral tests: sessionDirExists with real filesystem ---
    // These verify the actual directory/events.jsonl logic that the WriteActiveSessionsFile
    // callback implements, using real temp directories instead of source-text assertions.

    [Fact]
    public void Merge_WithEventsJsonl_SessionKept()
    {
        // A persisted session whose directory has events.jsonl should survive merge.
        var tempBase = Path.Combine(Path.GetTempPath(), $"polypilot-test-{Guid.NewGuid():N}");
        try
        {
            var sessionDir = Path.Combine(tempBase, "sess-good");
            Directory.CreateDirectory(sessionDir);
            File.WriteAllText(Path.Combine(sessionDir, "events.jsonl"), "{}");

            var active = new List<ActiveSessionEntry>();
            var persisted = new List<ActiveSessionEntry> { Entry("sess-good", "Good") };
            var closed = new HashSet<string>();

            Func<string, bool> dirExists = id =>
            {
                var dir = Path.Combine(tempBase, id);
                if (!Directory.Exists(dir)) return false;
                if (File.Exists(Path.Combine(dir, "events.jsonl"))) return true;
                try { return (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir)).TotalMinutes < 5; }
                catch { return false; }
            };

            var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), dirExists);
            Assert.Single(result);
            Assert.Equal("sess-good", result[0].SessionId);
        }
        finally { try { Directory.Delete(tempBase, true); } catch { } }
    }

    [Fact]
    public void Merge_WithEmptyDir_RecentlyCreated_SessionKept()
    {
        // A session directory without events.jsonl but created recently (< 5 min)
        // should be kept — it's a brand-new session that hasn't received events yet.
        var tempBase = Path.Combine(Path.GetTempPath(), $"polypilot-test-{Guid.NewGuid():N}");
        try
        {
            var sessionDir = Path.Combine(tempBase, "sess-new");
            Directory.CreateDirectory(sessionDir);
            // No events.jsonl — simulates a just-created session

            var active = new List<ActiveSessionEntry>();
            var persisted = new List<ActiveSessionEntry> { Entry("sess-new", "New") };
            var closed = new HashSet<string>();

            Func<string, bool> dirExists = id =>
            {
                var dir = Path.Combine(tempBase, id);
                if (!Directory.Exists(dir)) return false;
                if (File.Exists(Path.Combine(dir, "events.jsonl"))) return true;
                try { return (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir)).TotalMinutes < 5; }
                catch { return false; }
            };

            var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), dirExists);
            Assert.Single(result);
            Assert.Equal("sess-new", result[0].SessionId);
        }
        finally { try { Directory.Delete(tempBase, true); } catch { } }
    }

    [Fact]
    public void Merge_WithEmptyDir_NoEvents_GhostDropped()
    {
        // A session directory without events.jsonl that is NOT recently created
        // should be dropped — it's a ghost from a reconnected session.
        var tempBase = Path.Combine(Path.GetTempPath(), $"polypilot-test-{Guid.NewGuid():N}");
        try
        {
            var sessionDir = Path.Combine(tempBase, "sess-ghost");
            Directory.CreateDirectory(sessionDir);
            // Backdate the directory creation time to simulate a stale ghost
            Directory.SetCreationTimeUtc(sessionDir, DateTime.UtcNow.AddHours(-1));

            var active = new List<ActiveSessionEntry>();
            var persisted = new List<ActiveSessionEntry> { Entry("sess-ghost", "Ghost") };
            var closed = new HashSet<string>();

            Func<string, bool> dirExists = id =>
            {
                var dir = Path.Combine(tempBase, id);
                if (!Directory.Exists(dir)) return false;
                if (File.Exists(Path.Combine(dir, "events.jsonl"))) return true;
                try { return (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir)).TotalMinutes < 5; }
                catch { return false; }
            };

            var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), dirExists);
            Assert.Empty(result);
        }
        finally { try { Directory.Delete(tempBase, true); } catch { } }
    }

    [Fact]
    public void Merge_NoDirectory_SessionDropped()
    {
        // A persisted session with no directory at all should be dropped.
        var tempBase = Path.Combine(Path.GetTempPath(), $"polypilot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempBase);
        try
        {
            var active = new List<ActiveSessionEntry>();
            var persisted = new List<ActiveSessionEntry> { Entry("nonexistent", "Gone") };
            var closed = new HashSet<string>();

            Func<string, bool> dirExists = id =>
            {
                var dir = Path.Combine(tempBase, id);
                if (!Directory.Exists(dir)) return false;
                if (File.Exists(Path.Combine(dir, "events.jsonl"))) return true;
                try { return (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir)).TotalMinutes < 5; }
                catch { return false; }
            };

            var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), dirExists);
            Assert.Empty(result);
        }
        finally { try { Directory.Delete(tempBase, true); } catch { } }
    }

    [Fact]
    public void Merge_MixedSessions_OnlyValidOnesKept()
    {
        // End-to-end scenario: mix of valid, ghost, and missing sessions.
        // Only sessions with events.jsonl or recently-created dirs should survive.
        var tempBase = Path.Combine(Path.GetTempPath(), $"polypilot-test-{Guid.NewGuid():N}");
        try
        {
            // Session with events.jsonl — kept
            var goodDir = Path.Combine(tempBase, "good");
            Directory.CreateDirectory(goodDir);
            File.WriteAllText(Path.Combine(goodDir, "events.jsonl"), "{}");

            // New session (no events, recent dir) — kept
            var newDir = Path.Combine(tempBase, "brand-new");
            Directory.CreateDirectory(newDir);

            // Ghost session (empty dir, old) — dropped
            var ghostDir = Path.Combine(tempBase, "ghost");
            Directory.CreateDirectory(ghostDir);
            Directory.SetCreationTimeUtc(ghostDir, DateTime.UtcNow.AddHours(-2));

            // "missing" — no directory at all — dropped

            var active = new List<ActiveSessionEntry>();
            var persisted = new List<ActiveSessionEntry>
            {
                Entry("good", "Good"),
                Entry("brand-new", "BrandNew"),
                Entry("ghost", "Ghost"),
                Entry("missing", "Missing"),
            };
            var closed = new HashSet<string>();

            Func<string, bool> dirExists = id =>
            {
                var dir = Path.Combine(tempBase, id);
                if (!Directory.Exists(dir)) return false;
                if (File.Exists(Path.Combine(dir, "events.jsonl"))) return true;
                try { return (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir)).TotalMinutes < 5; }
                catch { return false; }
            };

            var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), dirExists);
            Assert.Equal(2, result.Count);
            Assert.Contains(result, e => e.SessionId == "good");
            Assert.Contains(result, e => e.SessionId == "brand-new");
            Assert.DoesNotContain(result, e => e.SessionId == "ghost");
            Assert.DoesNotContain(result, e => e.SessionId == "missing");
        }
        finally { try { Directory.Delete(tempBase, true); } catch { } }
    }

    // --- Null DisplayName guard ---

    [Fact]
    public void Merge_NullDisplayNameInActive_DoesNotThrow()
    {
        var active = new List<ActiveSessionEntry>
        {
            new() { SessionId = "a1", DisplayName = null!, Model = "m", WorkingDirectory = "/w" },
            Entry("a2", "Session2"),
        };
        var persisted = new List<ActiveSessionEntry> { Entry("p1", "Persisted1") };
        var closed = new HashSet<string>();

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), _ => true);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Merge_NullDisplayNameInPersisted_DoesNotThrow()
    {
        var active = new List<ActiveSessionEntry> { Entry("a1", "Session1") };
        var persisted = new List<ActiveSessionEntry>
        {
            new() { SessionId = "p1", DisplayName = null!, Model = "m", WorkingDirectory = "/w" },
        };
        var closed = new HashSet<string>();

        // Null display name should not crash; entry kept if dir exists
        var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), _ => true);
        Assert.Equal(2, result.Count);
    }

    // --- Sanitized events.jsonl copy ---

    [Fact]
    public void WriteActiveSessionsFile_SanitizedCopy_Concept()
    {
        // Validates that corrupt JSON lines are detectable via JsonDocument.Parse,
        // which is the same mechanism used in the sanitized copy during fallback.
        var validLine = "{\"type\":\"user.message\",\"data\":{\"content\":\"hello\"}}";
        var corruptLine = "{\"type\":\"user.message\",\"data\":{\"content\":\"broken";
        var emptyLine = "";

        var lines = new[] { validLine, corruptLine, emptyLine };
        var validLines = new List<string>();
        int skipped = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                validLines.Add(line);
            }
            catch (System.Text.Json.JsonException) { skipped++; }
        }

        Assert.Single(validLines);
        Assert.Equal(validLine, validLines[0]);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public void SanitizedCopy_WritesOnlyValidJsonLines()
    {
        var tempBase = Path.Combine(Path.GetTempPath(), $"polypilot-test-sanitize-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempBase);
            var sourceDir = Path.Combine(tempBase, "old-session");
            var destDir = Path.Combine(tempBase, "new-session");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(destDir);

            var sourcePath = Path.Combine(sourceDir, "events.jsonl");
            var destPath = Path.Combine(destDir, "events.jsonl");

            // Write a mix of valid, corrupt, and empty lines
            var lines = new[]
            {
                "{\"type\":\"session.start\",\"data\":{}}",
                "THIS IS CORRUPT",
                "{\"type\":\"user.message\",\"data\":{\"content\":\"hello\"}}",
                "",
                "{\"type\":\"assistant.message\",\"data\":{\"content\":\"world\"}",  // missing closing brace
                "{\"type\":\"tool.execution_start\",\"data\":{\"toolName\":\"grep\"}}",
            };
            File.WriteAllLines(sourcePath, lines);

            // Replicate the sanitized copy logic from CopilotService.Persistence.cs
            int validCount = 0, skippedCount = 0;
            using (var writer = new StreamWriter(destPath))
            {
                foreach (var line in File.ReadLines(sourcePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(line);
                        writer.WriteLine(line);
                        validCount++;
                    }
                    catch (System.Text.Json.JsonException) { skippedCount++; }
                }
            }

            Assert.Equal(3, validCount);   // session.start, user.message, tool.execution_start
            Assert.Equal(2, skippedCount); // "THIS IS CORRUPT" and truncated assistant.message

            var writtenLines = File.ReadAllLines(destPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            Assert.Equal(3, writtenLines.Length);
            Assert.Contains("session.start", writtenLines[0]);
            Assert.Contains("user.message", writtenLines[1]);
            Assert.Contains("tool.execution_start", writtenLines[2]);
        }
        finally { try { Directory.Delete(tempBase, true); } catch { } }
    }

    // --- Regression: fallback recreation must persist new session ID ---
    // Bug: When ResumeSessionAsync fails and CreateSessionAsync creates a new session
    // with a different ID, active-sessions.json must be updated. Without this,
    // the stale old ID is read on the next restart, causing LoadHistoryFromDisk
    // to read the wrong events.jsonl and "lose" accumulated history.

    [Fact]
    public void Merge_RecreatedSessionWithNewId_OverridesOldEntry()
    {
        // Simulate: session was recreated during restore with a new ID but same display name
        var active = new List<ActiveSessionEntry> { Entry("new-id-after-recreate", "AndroidShellHandler") };
        var persisted = new List<ActiveSessionEntry> { Entry("old-stale-id", "AndroidShellHandler") };
        var closed = new HashSet<string>();

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(), _ => true);

        // Only the active (new ID) entry should survive — persisted entry has same display name
        Assert.Single(result);
        Assert.Equal("new-id-after-recreate", result[0].SessionId);
        Assert.Equal("AndroidShellHandler", result[0].DisplayName);
    }

    [Fact]
    public void Merge_RecreatedSessionNewId_OldIdNotResurrected()
    {
        // Even if the old session directory still exists, the persisted entry with a
        // matching display name must not be re-added (it has a stale SessionId).
        var active = new List<ActiveSessionEntry> { Entry("recreated-id", "MySession") };
        var persisted = new List<ActiveSessionEntry> { Entry("original-id", "MySession") };
        var closed = new HashSet<string>();

        var result = CopilotService.MergeSessionEntries(active, persisted, closed, new HashSet<string>(),
            sessionId => true); // All dirs exist

        Assert.Single(result);
        Assert.Equal("recreated-id", result[0].SessionId);
    }

    // --- Regression: events.jsonl copy when new file already exists ---

    [Fact]
    public void EventsCopy_PrependOldEventsWhenNewExists()
    {
        // When CreateSessionAsync already creates events.jsonl for the new session,
        // the old events should be prepended so history survives future restarts.
        var tempBase = Path.Combine(Path.GetTempPath(), $"polypilot-test-prepend-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempBase);
            var oldDir = Path.Combine(tempBase, "old-session");
            var newDir = Path.Combine(tempBase, "new-session");
            Directory.CreateDirectory(oldDir);
            Directory.CreateDirectory(newDir);

            var oldEvents = Path.Combine(oldDir, "events.jsonl");
            var newEvents = Path.Combine(newDir, "events.jsonl");

            // Old session had substantial history
            var oldLines = new[]
            {
                "{\"type\":\"session.start\",\"data\":{\"ts\":1}}",
                "{\"type\":\"user.message\",\"data\":{\"content\":\"old message 1\"}}",
                "{\"type\":\"assistant.message\",\"data\":{\"content\":\"old reply 1\"}}",
            };
            File.WriteAllLines(oldEvents, oldLines);

            // New session already has a few events from SDK session creation
            var newExistingLines = new[]
            {
                "{\"type\":\"session.start\",\"data\":{\"ts\":2}}",
            };
            File.WriteAllLines(newEvents, newExistingLines);

            // Replicate the updated copy logic from CopilotService.Persistence.cs
            var sanitizedOldLines = new List<string>();
            foreach (var line in File.ReadLines(oldEvents))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(line);
                    sanitizedOldLines.Add(line);
                }
                catch (System.Text.Json.JsonException) { }
            }

            if (sanitizedOldLines.Count > 0 && File.Exists(newEvents))
            {
                var existingNewLines = File.ReadAllLines(newEvents);
                using var writer = new StreamWriter(newEvents, append: false);
                foreach (var line in sanitizedOldLines) writer.WriteLine(line);
                foreach (var line in existingNewLines) writer.WriteLine(line);
            }

            // Verify: old events are prepended before new events
            var resultLines = File.ReadAllLines(newEvents).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            Assert.Equal(4, resultLines.Length);
            Assert.Contains("\"ts\":1", resultLines[0]); // old session.start first
            Assert.Contains("old message 1", resultLines[1]);
            Assert.Contains("old reply 1", resultLines[2]);
            Assert.Contains("\"ts\":2", resultLines[3]); // new session.start last
        }
        finally { try { Directory.Delete(tempBase, true); } catch { } }
    }

    [Fact]
    public void EventsCopy_WritesOldEventsWhenNewDoesNotExist()
    {
        // When the new session directory exists but events.jsonl hasn't been created yet
        var tempBase = Path.Combine(Path.GetTempPath(), $"polypilot-test-noexist-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempBase);
            var oldDir = Path.Combine(tempBase, "old-session");
            var newDir = Path.Combine(tempBase, "new-session");
            Directory.CreateDirectory(oldDir);
            Directory.CreateDirectory(newDir);

            var oldEvents = Path.Combine(oldDir, "events.jsonl");
            var newEvents = Path.Combine(newDir, "events.jsonl");

            var oldLines = new[]
            {
                "{\"type\":\"user.message\",\"data\":{\"content\":\"preserved history\"}}",
            };
            File.WriteAllLines(oldEvents, oldLines);

            // New events.jsonl does NOT exist yet
            Assert.False(File.Exists(newEvents));

            // Replicate the copy logic
            var sanitizedOldLines = new List<string>();
            foreach (var line in File.ReadLines(oldEvents))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(line);
                    sanitizedOldLines.Add(line);
                }
                catch (System.Text.Json.JsonException) { }
            }

            if (sanitizedOldLines.Count > 0 && !File.Exists(newEvents))
            {
                File.WriteAllLines(newEvents, sanitizedOldLines);
            }

            Assert.True(File.Exists(newEvents));
            var resultLines = File.ReadAllLines(newEvents).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            Assert.Single(resultLines);
            Assert.Contains("preserved history", resultLines[0]);
        }
        finally { try { Directory.Delete(tempBase, true); } catch { } }
    }
}
