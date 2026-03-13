using System.Text;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

public class ExternalSessionScannerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sessionStateDir;

    public ExternalSessionScannerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"polypilot-ext-test-{Guid.NewGuid():N}");
        _sessionStateDir = Path.Combine(_tempDir, "session-state");
        Directory.CreateDirectory(_sessionStateDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── ParseEventsFile tests ──────────────────────────────────────────────────

    [Fact]
    public void ParseEventsFile_EmptyFile_ReturnsEmptyHistoryAndNullType()
    {
        var file = WriteEventsFile("empty-session", "");
        var (history, lastType) = ExternalSessionScanner.ParseEventsFile(file);
        Assert.Empty(history);
        Assert.Null(lastType);
    }

    [Fact]
    public void ParseEventsFile_UserAndAssistantMessages_ParsedCorrectly()
    {
        var file = WriteEventsFile("session1",
            """
            {"type":"user.message","data":{"content":"Hello world"},"timestamp":"2025-01-01T10:00:00Z"}
            {"type":"assistant.message","data":{"content":"Hi there!"},"timestamp":"2025-01-01T10:01:00Z"}
            """);

        var (history, lastType) = ExternalSessionScanner.ParseEventsFile(file);

        Assert.Equal(2, history.Count);
        Assert.True(history[0].IsUser);
        Assert.Equal("Hello world", history[0].Content);
        Assert.True(history[1].IsAssistant);
        Assert.Equal("Hi there!", history[1].Content);
        Assert.Equal("assistant.message", lastType);
    }

    [Fact]
    public void ParseEventsFile_ToolEvents_AreSkippedButLastTypeTracked()
    {
        var file = WriteEventsFile("session2",
            """
            {"type":"user.message","data":{"content":"Run tests"},"timestamp":"2025-01-01T10:00:00Z"}
            {"type":"tool.execution_start","data":{"toolName":"bash"},"timestamp":"2025-01-01T10:00:01Z"}
            {"type":"tool.execution_complete","data":{"result":"ok"},"timestamp":"2025-01-01T10:00:05Z"}
            {"type":"assistant.turn_end","data":{},"timestamp":"2025-01-01T10:00:06Z"}
            """);

        var (history, lastType) = ExternalSessionScanner.ParseEventsFile(file);

        // Only user.message parsed — tool events and turn_end not added to history
        Assert.Single(history);
        Assert.True(history[0].IsUser);
        Assert.Equal("assistant.turn_end", lastType);
    }

    [Fact]
    public void ParseEventsFile_MalformedLines_SkippedGracefully()
    {
        var file = WriteEventsFile("session3",
            """
            {"type":"user.message","data":{"content":"Hello"},"timestamp":"2025-01-01T10:00:00Z"}
            NOT_JSON
            {"incomplete":
            {"type":"assistant.message","data":{"content":"Reply"},"timestamp":"2025-01-01T10:00:01Z"}
            """);

        var (history, lastType) = ExternalSessionScanner.ParseEventsFile(file);

        Assert.Equal(2, history.Count);
        Assert.Equal("assistant.message", lastType);
    }

    [Fact]
    public void ParseEventsFile_EmptyMessageContent_SkippedGracefully()
    {
        var file = WriteEventsFile("session4",
            """
            {"type":"user.message","data":{"content":""},"timestamp":"2025-01-01T10:00:00Z"}
            {"type":"assistant.message","data":{"content":"   "},"timestamp":"2025-01-01T10:00:01Z"}
            {"type":"user.message","data":{"content":"Real message"},"timestamp":"2025-01-01T10:00:02Z"}
            """);

        var (history, lastType) = ExternalSessionScanner.ParseEventsFile(file);

        // Empty/whitespace-only messages skipped
        Assert.Single(history);
        Assert.Equal("Real message", history[0].Content);
    }

    // ── Scan() filter logic tests ──────────────────────────────────────────────

    [Fact]
    public void Scan_ExcludesOwnedSessions()
    {
        var ownedId = Guid.NewGuid().ToString();
        CreateSessionDir(ownedId, cwd: "/some/path", eventsContent: SimpleUserMessage("hello"));

        var ownedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ownedId };
        var scanner = new ExternalSessionScanner(_sessionStateDir, () => ownedIds);
        scanner.Scan();

        Assert.Empty(scanner.Sessions);
    }

    [Fact]
    public void Scan_IncludesUnownedRecentSession()
    {
        var sessionId = Guid.NewGuid().ToString();
        CreateSessionDir(sessionId, cwd: "/work/myproject", eventsContent: SimpleUserMessage("hello"));

        var scanner = new ExternalSessionScanner(_sessionStateDir, () => new HashSet<string>());
        scanner.Scan();

        Assert.Single(scanner.Sessions);
        Assert.Equal(sessionId, scanner.Sessions[0].SessionId);
        Assert.Equal("myproject", scanner.Sessions[0].DisplayName);
    }

    [Fact]
    public void Scan_ExcludesNonGuidDirectories()
    {
        // Create a non-UUID named directory
        var weirdDir = Path.Combine(_sessionStateDir, "not-a-uuid");
        Directory.CreateDirectory(weirdDir);
        File.WriteAllText(Path.Combine(weirdDir, "events.jsonl"), SimpleUserMessage("hello"));
        File.WriteAllText(Path.Combine(weirdDir, "workspace.yaml"), "cwd: /some/path\nid: abc");

        var scanner = new ExternalSessionScanner(_sessionStateDir, () => new HashSet<string>());
        scanner.Scan();

        Assert.Empty(scanner.Sessions);
    }

    [Fact]
    public void Scan_ExcludesSessionsMissingEventsFile()
    {
        var sessionId = Guid.NewGuid().ToString();
        var dir = Path.Combine(_sessionStateDir, sessionId);
        Directory.CreateDirectory(dir);
        // Only workspace.yaml, no events.jsonl
        File.WriteAllText(Path.Combine(dir, "workspace.yaml"), "cwd: /some/path\nid: " + sessionId);

        var scanner = new ExternalSessionScanner(_sessionStateDir, () => new HashSet<string>());
        scanner.Scan();

        Assert.Empty(scanner.Sessions);
    }

    [Fact]
    public void Scan_MultipleSessionsSortedActiveFirst()
    {
        // Active session: recent mtime + active last event type
        var activeId = Guid.NewGuid().ToString();
        CreateSessionDir(activeId, cwd: "/active/proj",
            eventsContent: SimpleUserMessage("working on it") + "\n" + AssistantMessage("ok"));

        // Ended session
        var endedId = Guid.NewGuid().ToString();
        CreateSessionDir(endedId, cwd: "/ended/proj",
            eventsContent: SimpleUserMessage("done") + "\n" + SessionIdleEvent());

        // Make active session file look very recent (within ActiveThreshold)
        var activeEvents = Path.Combine(_sessionStateDir, activeId, "events.jsonl");
        File.SetLastWriteTimeUtc(activeEvents, DateTime.UtcNow.AddSeconds(-30));

        // Make ended session file look older (beyond ActiveThreshold but within MaxAge)
        var endedEvents = Path.Combine(_sessionStateDir, endedId, "events.jsonl");
        File.SetLastWriteTimeUtc(endedEvents, DateTime.UtcNow.AddMinutes(-5));

        var scanner = new ExternalSessionScanner(_sessionStateDir, () => new HashSet<string>());
        scanner.Scan();

        Assert.Equal(2, scanner.Sessions.Count);
        // Active session should be first
        Assert.Equal(activeId, scanner.Sessions[0].SessionId);
        Assert.True(scanner.Sessions[0].IsActive);
        Assert.Equal(endedId, scanner.Sessions[1].SessionId);
        Assert.False(scanner.Sessions[1].IsActive);
    }

    [Fact]
    public void Scan_SessionShutdown_AlwaysEndedTier()
    {
        // session.shutdown should always classify as Ended, even if the file is very recent
        var sessionId = Guid.NewGuid().ToString();
        CreateSessionDir(sessionId, cwd: "/recent/proj",
            eventsContent: SimpleUserMessage("hello") + "\n" + AssistantMessage("hi") + "\n" + SessionShutdownEvent());

        // Make the file look very recent (within ActiveThreshold) — but shutdown should override
        var eventsFile = Path.Combine(_sessionStateDir, sessionId, "events.jsonl");
        File.SetLastWriteTimeUtc(eventsFile, DateTime.UtcNow.AddSeconds(-30));

        var scanner = new ExternalSessionScanner(_sessionStateDir, () => new HashSet<string>());
        scanner.Scan();

        Assert.Single(scanner.Sessions);
        Assert.Equal(ExternalSessionTier.Ended, scanner.Sessions[0].Tier);
        Assert.False(scanner.Sessions[0].IsActive);
    }

    [Fact]
    public void Scan_SessionIdle_RecentFile_IsIdleTier()
    {
        // session.idle with recent file (within active threshold) should be Idle, not Active
        var sessionId = Guid.NewGuid().ToString();
        CreateSessionDir(sessionId, cwd: "/idle/proj",
            eventsContent: SimpleUserMessage("done") + "\n" + SessionIdleEvent());

        var eventsFile = Path.Combine(_sessionStateDir, sessionId, "events.jsonl");
        File.SetLastWriteTimeUtc(eventsFile, DateTime.UtcNow.AddSeconds(-30));

        var scanner = new ExternalSessionScanner(_sessionStateDir, () => new HashSet<string>());
        scanner.Scan();

        Assert.Single(scanner.Sessions);
        Assert.Equal(ExternalSessionTier.Idle, scanner.Sessions[0].Tier);
        Assert.False(scanner.Sessions[0].IsActive);
    }

    [Fact]
    public void Scan_SessionShutdown_OlderThanEndedMaxAge_IsFiltered()
    {
        // session.shutdown that's older than 2 hours should be hidden entirely
        var sessionId = Guid.NewGuid().ToString();
        CreateSessionDir(sessionId, cwd: "/old/proj",
            eventsContent: SimpleUserMessage("hello") + "\n" + SessionShutdownEvent());

        // Set to 3 hours ago — beyond EndedMaxAge (2h) but within MaxAge (4h)
        var eventsFile = Path.Combine(_sessionStateDir, sessionId, "events.jsonl");
        File.SetLastWriteTimeUtc(eventsFile, DateTime.UtcNow.AddHours(-3));

        var scanner = new ExternalSessionScanner(_sessionStateDir, () => new HashSet<string>());
        scanner.Scan();

        // Should be filtered out — too old to bother resuming
        Assert.Empty(scanner.Sessions);
    }

    [Fact]
    public void Scan_SessionShutdown_WithinEndedMaxAge_IsShown()
    {
        // session.shutdown within 2 hours should still show so user can resume quickly
        var sessionId = Guid.NewGuid().ToString();
        CreateSessionDir(sessionId, cwd: "/recent-closed/proj",
            eventsContent: SimpleUserMessage("hello") + "\n" + SessionShutdownEvent());

        // Set to 1 hour ago — within EndedMaxAge (2h)
        var eventsFile = Path.Combine(_sessionStateDir, sessionId, "events.jsonl");
        File.SetLastWriteTimeUtc(eventsFile, DateTime.UtcNow.AddHours(-1));

        var scanner = new ExternalSessionScanner(_sessionStateDir, () => new HashSet<string>());
        scanner.Scan();

        Assert.Single(scanner.Sessions);
        Assert.Equal(ExternalSessionTier.Ended, scanner.Sessions[0].Tier);
    }

    
    [Fact]
    public void Scan_OnChanged_FiresWhenSessionsChange()
    {
        int changedCount = 0;
        var scanner = new ExternalSessionScanner(_sessionStateDir, () => new HashSet<string>());
        scanner.OnChanged += () => changedCount++;

        // First scan with no sessions
        scanner.Scan();
        Assert.Equal(0, changedCount);

        // Add a session
        var sessionId = Guid.NewGuid().ToString();
        CreateSessionDir(sessionId, cwd: "/new/project", eventsContent: SimpleUserMessage("hi"));

        // Second scan should fire OnChanged
        scanner.Scan();
        Assert.Equal(1, changedCount);

        // Third scan — no change — should NOT fire again
        scanner.Scan();
        Assert.Equal(1, changedCount);
    }

    [Fact]
    public void Scan_CachePreventsReparsing()
    {
        var sessionId = Guid.NewGuid().ToString();
        var eventsPath = CreateSessionDir(sessionId, cwd: "/proj", eventsContent: SimpleUserMessage("first"));

        var scanner = new ExternalSessionScanner(_sessionStateDir, () => new HashSet<string>());
        scanner.Scan();
        Assert.Single(scanner.Sessions);
        var firstHistory = scanner.Sessions[0].History;

        // Modify the events file content WITHOUT changing the mtime
        var oldMtime = File.GetLastWriteTimeUtc(eventsPath);
        File.WriteAllText(eventsPath, SimpleUserMessage("first") + "\n" + AssistantMessage("cached"));
        File.SetLastWriteTimeUtc(eventsPath, oldMtime); // reset mtime

        scanner.Scan();
        // Cache should have returned original history (1 message, not 2)
        Assert.Single(scanner.Sessions[0].History);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    [Fact]
    public void Scan_NeedsAttention_TrueForActiveSessionAskingQuestion()
    {
        var sessionId = Guid.NewGuid().ToString();
        // Active session (recent file) where last assistant message asks a question
        var eventsFile = CreateSessionDir(sessionId, cwd: "/active/proj",
            eventsContent:
                SimpleUserMessage("do the thing") + "\n" +
                AssistantMessage("Should I use tabs or spaces?"));

        File.SetLastWriteTimeUtc(eventsFile, DateTime.UtcNow.AddSeconds(-20));

        var scanner = new ExternalSessionScanner(_sessionStateDir, () => new HashSet<string>());
        scanner.Scan();

        Assert.Single(scanner.Sessions);
        Assert.True(scanner.Sessions[0].IsActive);
        // Should flag NeedsAttention even though session is active
        Assert.True(scanner.Sessions[0].NeedsAttention);
    }

    [Fact]
    public void Scan_NeedsAttention_FalseWhenUserReplied()
    {
        var sessionId = Guid.NewGuid().ToString();
        var eventsFile = CreateSessionDir(sessionId, cwd: "/proj",
            eventsContent:
                SimpleUserMessage("do it") + "\n" +
                AssistantMessage("Which option would you prefer?") + "\n" +
                SimpleUserMessage("option A"));

        File.SetLastWriteTimeUtc(eventsFile, DateTime.UtcNow.AddSeconds(-20));

        var scanner = new ExternalSessionScanner(_sessionStateDir, () => new HashSet<string>());
        scanner.Scan();

        Assert.Single(scanner.Sessions);
        // User replied, so no attention needed
        Assert.False(scanner.Sessions[0].NeedsAttention);
    }

    [Fact]
    public void Scan_GitWorktree_BranchDetected()
    {
        var sessionId = Guid.NewGuid().ToString();
        var cwd = Path.Combine(_tempDir, "worktree-repo");
        Directory.CreateDirectory(cwd);

        // Simulate a git worktree: .git is a FILE pointing to the main worktree's git dir
        var mainGitDir = Path.Combine(_tempDir, "main-repo", ".git", "worktrees", "worktree-repo");
        Directory.CreateDirectory(mainGitDir);
        File.WriteAllText(Path.Combine(mainGitDir, "HEAD"), "ref: refs/heads/feature/my-branch\n");

        // Write relative gitdir pointer in the worktree
        File.WriteAllText(Path.Combine(cwd, ".git"),
            $"gitdir: {mainGitDir}");

        CreateSessionDir(sessionId, cwd: cwd, eventsContent: SimpleUserMessage("hello"));

        var scanner = new ExternalSessionScanner(_sessionStateDir, () => new HashSet<string>());
        scanner.Scan();

        Assert.Single(scanner.Sessions);
        Assert.Equal("feature/my-branch", scanner.Sessions[0].GitBranch);
    }

    private string CreateSessionDir(string sessionId, string cwd, string eventsContent)
    {
        var dir = Path.Combine(_sessionStateDir, sessionId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "workspace.yaml"), $"id: {sessionId}\ncwd: {cwd}");
        var eventsFile = Path.Combine(dir, "events.jsonl");
        File.WriteAllText(eventsFile, eventsContent, Encoding.UTF8);
        return eventsFile;
    }

    private string WriteEventsFile(string sessionName, string content)
    {
        var dir = Path.Combine(_tempDir, sessionName);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "events.jsonl");
        File.WriteAllText(file, content, Encoding.UTF8);
        return file;
    }

    private static string SimpleUserMessage(string content) =>
        $$"""{"type":"user.message","data":{"content":"{{content}}"},"timestamp":"2025-01-01T10:00:00Z"}""";

    private static string AssistantMessage(string content) =>
        $$"""{"type":"assistant.message","data":{"content":"{{content}}"},"timestamp":"2025-01-01T10:00:01Z"}""";

    private static string SessionIdleEvent() =>
        """{"type":"session.idle","data":{},"timestamp":"2025-01-01T10:00:02Z"}""";

    private static string SessionShutdownEvent() =>
        """{"type":"session.shutdown","data":{},"timestamp":"2025-01-01T10:00:03Z"}""";

    // ── Timer / Start tests ─────────────────────────────────────────────────

    [Fact]
    public async Task Start_TimerRearms_SessionsDiscoveredAfterStart()
    {
        // Verify that sessions created AFTER Start() are picked up by the re-arm loop.
        var scanner = new ExternalSessionScanner(_sessionStateDir, () => new HashSet<string>());
        scanner.Start();
        try
        {
            // Wait for the initial scan (fires at TimeSpan.Zero)
            await Task.Delay(200);
            Assert.Empty(scanner.Sessions);

            // Now create a session — should be found on the next poll
            var sessionId = Guid.NewGuid().ToString();
            CreateSessionDir(sessionId, cwd: "/new/project", eventsContent: SimpleUserMessage("hello"));

            // Wait long enough for at least one re-armed poll (PollInterval = 15s, but
            // we just need to confirm the timer fires again — wait up to 20s)
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(500);
                if (scanner.Sessions.Count > 0) break;
            }

            Assert.Single(scanner.Sessions);
            Assert.Equal(sessionId, scanner.Sessions[0].SessionId);
        }
        finally
        {
            scanner.Dispose();
        }
    }

    [Fact]
    public void Scan_CwdExclusion_ExcludesMatchingPrefix()
    {
        var baseDir = Path.Combine(_tempDir, ".polypilot");
        var sessionId = Guid.NewGuid().ToString();
        var cwd = Path.Combine(baseDir, "worktrees", "some-project");
        Directory.CreateDirectory(cwd);
        CreateSessionDir(sessionId, cwd: cwd, eventsContent: SimpleUserMessage("hello"));

        var scanner = new ExternalSessionScanner(
            _sessionStateDir,
            () => new HashSet<string>(),
            c => !string.IsNullOrEmpty(c) && c.Replace('/', '\\').StartsWith(baseDir.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));
        scanner.Scan();

        Assert.Empty(scanner.Sessions);
    }

    [Fact]
    public void Scan_CwdExclusion_WorktreeSessionsAlsoExcluded()
    {
        // Worktree sessions under .polypilot/worktrees/ are PolyPilot's own multi-agent
        // worker sessions — they must be excluded just like any other .polypilot/ path.
        var baseDir = Path.Combine(_tempDir, ".polypilot");
        var worktreeCwd = Path.Combine(baseDir, "worktrees", "MyRepo-abc123");
        Directory.CreateDirectory(worktreeCwd);
        var sessionId = Guid.NewGuid().ToString();
        CreateSessionDir(sessionId, cwd: worktreeCwd, eventsContent: SimpleUserMessage("hello from worktree"));

        var scanner = new ExternalSessionScanner(
            _sessionStateDir,
            () => new HashSet<string>(),
            c => !string.IsNullOrEmpty(c) && c.Replace('/', '\\').StartsWith(baseDir.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));
        scanner.Scan();

        Assert.Empty(scanner.Sessions);
    }

    [Fact]
    public void Scan_CwdExclusion_AllPolypilotSubdirsExcluded()
    {
        // All sessions with CWDs under .polypilot/ should be excluded — they're internal
        var baseDir = Path.Combine(_tempDir, ".polypilot");
        var internalCwd = Path.Combine(baseDir, "internal", "some-dir");
        Directory.CreateDirectory(internalCwd);
        var sessionId = Guid.NewGuid().ToString();
        CreateSessionDir(sessionId, cwd: internalCwd, eventsContent: SimpleUserMessage("internal session"));

        var scanner = new ExternalSessionScanner(
            _sessionStateDir,
            () => new HashSet<string>(),
            c => !string.IsNullOrEmpty(c) && c.Replace('/', '\\').StartsWith(baseDir.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));
        scanner.Scan();

        Assert.Empty(scanner.Sessions);
    }

    [Fact]
    public void Scan_CwdExclusion_DoesNotExcludeNonMatchingCwd()
    {
        var baseDir = Path.Combine(_tempDir, ".polypilot");
        var sessionId = Guid.NewGuid().ToString();
        var cwd = Path.Combine(_tempDir, "other-project");
        Directory.CreateDirectory(cwd);
        CreateSessionDir(sessionId, cwd: cwd, eventsContent: SimpleUserMessage("hello"));

        var scanner = new ExternalSessionScanner(
            _sessionStateDir,
            () => new HashSet<string>(),
            c => !string.IsNullOrEmpty(c) && c.Replace('/', '\\').StartsWith(baseDir.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));
        scanner.Scan();

        Assert.Single(scanner.Sessions);
    }
}
