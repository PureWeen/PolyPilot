using System.Text;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

public class ExternalSessionScannerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sessionStateDir;
    private readonly List<System.Diagnostics.Process> _childProcesses = new();

    public ExternalSessionScannerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"polypilot-ext-test-{Guid.NewGuid():N}");
        _sessionStateDir = Path.Combine(_tempDir, "session-state");
        Directory.CreateDirectory(_sessionStateDir);
    }

    public void Dispose()
    {
        foreach (var p in _childProcesses)
            try { if (!p.HasExited) p.Kill(); p.Dispose(); } catch { }
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
        CreateLockFile(ownedId);

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
        CreateLockFile(sessionId);

        var scanner = new ExternalSessionScanner(_sessionStateDir, () => new HashSet<string>());
        scanner.Scan();

        Assert.Single(scanner.Sessions);
        Assert.Equal(sessionId, scanner.Sessions[0].SessionId);
        Assert.Equal("myproject", scanner.Sessions[0].DisplayName);
        Assert.True(scanner.Sessions[0].IsActive);
    }

    [Fact]
    public void Scan_ExcludesNonGuidDirectories()
    {
        // Create a non-UUID named directory
        var weirdDir = Path.Combine(_sessionStateDir, "not-a-uuid");
        Directory.CreateDirectory(weirdDir);
        File.WriteAllText(Path.Combine(weirdDir, "events.jsonl"), SimpleUserMessage("hello"));
        File.WriteAllText(Path.Combine(weirdDir, "workspace.yaml"), "cwd: /some/path\nid: abc");
        File.WriteAllText(Path.Combine(weirdDir, $"inuse.{Environment.ProcessId}.lock"), "");

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
        File.WriteAllText(Path.Combine(dir, $"inuse.{Environment.ProcessId}.lock"), "");

        var scanner = new ExternalSessionScanner(_sessionStateDir, () => new HashSet<string>());
        scanner.Scan();

        Assert.Empty(scanner.Sessions);
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
        CreateLockFile(sessionId);

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
        CreateLockFile(sessionId);

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
        // Active session where last assistant message asks a question
        CreateSessionDir(sessionId, cwd: "/active/proj",
            eventsContent:
                SimpleUserMessage("do the thing") + "\n" +
                AssistantMessage("Should I use tabs or spaces?"));
        CreateLockFile(sessionId);

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
        CreateSessionDir(sessionId, cwd: "/proj",
            eventsContent:
                SimpleUserMessage("do it") + "\n" +
                AssistantMessage("Which option would you prefer?") + "\n" +
                SimpleUserMessage("option A"));
        CreateLockFile(sessionId);

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
        CreateLockFile(sessionId);

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

    /// <summary>
    /// Create an inuse.{PID}.lock file so the scanner's lock-file pass finds this session.
    /// Uses <c>node -e "setTimeout(()=>{},60000)"</c> which blocks for 60s and has process
    /// name "node" — one of the scanner's accepted names (copilot/node/dotnet/github).
    /// The test runner process (testhost) doesn't match these patterns.
    /// </summary>
    private void CreateLockFile(string sessionId)
    {
        var dir = Path.Combine(_sessionStateDir, sessionId);
        var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "node",
            Arguments = "-e \"setTimeout(()=>{},60000)\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Failed to start node process for lock file test");
        _childProcesses.Add(child);
        if (child.HasExited)
            throw new InvalidOperationException($"node process exited too fast (exit code {child.ExitCode}) — cannot create stable lock file for test");
        File.WriteAllText(Path.Combine(dir, $"inuse.{child.Id}.lock"), "");
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
        // Create a session before starting — should be found on the initial scan (fires at TimeSpan.Zero)
        var sessionId = Guid.NewGuid().ToString();
        CreateSessionDir(sessionId, cwd: "/new/project", eventsContent: SimpleUserMessage("hello"));
        CreateLockFile(sessionId);

        var scanner = new ExternalSessionScanner(_sessionStateDir, () => new HashSet<string>());
        scanner.Start();
        try
        {
            // Wait for the initial scan
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(100);
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
        CreateLockFile(sessionId);

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
        CreateLockFile(sessionId);

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
        CreateLockFile(sessionId);

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
        CreateLockFile(sessionId);

        var scanner = new ExternalSessionScanner(
            _sessionStateDir,
            () => new HashSet<string>(),
            c => !string.IsNullOrEmpty(c) && c.Replace('/', '\\').StartsWith(baseDir.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));
        scanner.Scan();

        Assert.Single(scanner.Sessions);
    }

    // ── FindActiveLockPid tests ─────────────────────────────────────────────────

    [Fact]
    public void FindActiveLockPid_DetectsCurrentProcess()
    {
        var sessionId = Guid.NewGuid().ToString();
        var dir = Path.Combine(_sessionStateDir, sessionId);
        Directory.CreateDirectory(dir);

        // Use a command guaranteed to run for much longer than the test:
        // `dotnet repl` / `dotnet watch` aren't available everywhere, but
        // reading stdin on a `dotnet` REPL-like loop works cross-platform.
        // Simplest portable option: run `sleep` on Unix, `timeout` on Windows.
        System.Diagnostics.Process child;
        if (OperatingSystem.IsWindows())
        {
            child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd", "/c timeout /t 60 /nobreak")
            {
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            })!;
        }
        else
        {
            child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sleep", "60")
            {
                UseShellExecute = false,
            })!;
        }

        Assert.NotNull(child);

        try
        {
            File.WriteAllText(Path.Combine(dir, $"inuse.{child.Id}.lock"), "");

            // `sleep 60` / `timeout /t 60` will not exit in the test window — no race guard needed.
            Assert.False(child.HasExited, "Long-running child process should still be alive");

            // FindActiveLockPid requires a dotnet/copilot/node/github process name.
            // `sleep`/`cmd` won't pass that filter. We need to use the current test process instead.
            // On some platforms (e.g., Windows), the test host process is named "testhost"
            // which doesn't match the safety filter — verify the behaviour accordingly.
            var testSessionId = Guid.NewGuid().ToString();
            var testDir = Path.Combine(_sessionStateDir, testSessionId);
            Directory.CreateDirectory(testDir);
            var myPid = Environment.ProcessId;
            File.WriteAllText(Path.Combine(testDir, $"inuse.{myPid}.lock"), "");

            var myName = System.Diagnostics.Process.GetCurrentProcess().ProcessName.ToLowerInvariant();
            var matchesFilter = myName.Contains("copilot") || myName.Contains("node") ||
                                myName.Contains("dotnet") || myName.Contains("github");

            var scanner = new ExternalSessionScanner(_sessionStateDir, () => new HashSet<string>());
            var detectedPid = scanner.FindActiveLockPid(testDir);

            if (matchesFilter)
                Assert.Equal(myPid, detectedPid);
            else
                Assert.Null(detectedPid); // Process name doesn't pass the safety filter
        }
        finally
        {
            if (!child.HasExited) child.Kill();
            child.Dispose();
        }
    }

    [Fact]
    public void FindActiveLockPid_IgnoresStaleLockWithDeadPid()
    {
        var sessionId = Guid.NewGuid().ToString();
        var dir = Path.Combine(_sessionStateDir, sessionId);
        Directory.CreateDirectory(dir);

        // Use a PID that almost certainly doesn't exist
        File.WriteAllText(Path.Combine(dir, "inuse.999999.lock"), "");

        var scanner = new ExternalSessionScanner(_sessionStateDir, () => new HashSet<string>());
        var detectedPid = scanner.FindActiveLockPid(dir);

        Assert.Null(detectedPid);
    }
}
