using System.Text;
using System.Text.Json;

namespace PolyPilot.Services;

/// <summary>
/// A background service that maintains a dedicated copilot CLI session to perpetually
/// analyze running PolyPilot sessions for issues. The analyzer reports findings but
/// does NOT autonomously create PRs — all actions require human review.
/// </summary>
public class SessionAnalyzerService : IAsyncDisposable, IDisposable
{
    private readonly CopilotService _copilotService;
    private readonly IServerManager _serverManager;
    private CancellationTokenSource? _cts;
    private Task? _analysisLoop;
    private string? _analyzerSessionName;
    private bool _disposed;
    private int _analysisCount;
    private long _lastAnalysisAtTicks;

    internal const string AnalyzerGroupName = "🔍 Session Analyzer";
    internal const string AnalyzerSessionName = "PolyPilot Monitor";
    internal const int DefaultAnalysisIntervalMinutes = 10;
    internal const int MinAnalysisIntervalMinutes = 1;
    internal const int DiagnosticLogTailLines = 200;
    internal const int CrashLogTailLines = 50;
    internal const int MaxLogFileSizeBytes = 10 * 1024 * 1024; // 10 MB cap for TailFile

    private static string? _polypilotDir;
    private static string PolyPilotDir => _polypilotDir ??= CopilotService.BaseDir;

    public bool IsRunning => _analysisLoop is { IsCompleted: false };

    public DateTime? LastAnalysisAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastAnalysisAtTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    public int AnalysisCount => Interlocked.CompareExchange(ref _analysisCount, 0, 0);
    public string? LastFinding { get; private set; }

    public SessionAnalyzerService(CopilotService copilotService, IServerManager serverManager)
    {
        _copilotService = copilotService;
        _serverManager = serverManager;
    }

    /// <summary>
    /// Start the perpetual analysis loop. Creates the analyzer session if needed.
    /// </summary>
    public async Task StartAsync(string repoWorkingDirectory, int intervalMinutes = DefaultAnalysisIntervalMinutes)
    {
        if (IsRunning) return;

        var clampedInterval = Math.Max(MinAnalysisIntervalMinutes, intervalMinutes);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            var session = await _copilotService.CreateSessionAsync(
                AnalyzerSessionName,
                model: "claude-sonnet-4.5",
                workingDirectory: repoWorkingDirectory,
                cancellationToken: token);

            session.IsHidden = true;
            // Only set name after successful creation
            _analyzerSessionName = AnalyzerSessionName;
        }
        catch (Exception ex)
        {
            LogAnalyzer($"Failed to create analyzer session: {ex.Message}");
            _analyzerSessionName = null;
            return;
        }

        _analysisLoop = RunAnalysisLoopAsync(clampedInterval, token);
    }

    /// <summary>
    /// Stop the analysis loop, await completion, and clean up the analyzer session.
    /// </summary>
    public async Task StopAsync()
    {
        _cts?.Cancel();

        if (_analysisLoop is not null)
        {
            try { await _analysisLoop.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { LogAnalyzer("Analysis loop did not stop within 5s timeout"); }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex) { LogAnalyzer($"Error awaiting analysis loop: {ex.Message}"); }
            _analysisLoop = null;
        }

        _cts?.Dispose();
        _cts = null;
        _analyzerSessionName = null;
    }

    /// <summary>
    /// Synchronous stop for IDisposable — prefer StopAsync/DisposeAsync.
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _analysisLoop = null;
        _analyzerSessionName = null;
    }

    /// <summary>
    /// Run a single analysis pass immediately (for testing or on-demand use).
    /// </summary>
    public async Task<string?> RunSingleAnalysisAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_analyzerSessionName)) return null;

        var diagnostics = CollectDiagnostics();
        var prompt = BuildAnalysisPrompt(diagnostics);

        try
        {
            // Use a linked token with a 10-minute timeout so autopilot can't block forever
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));

            var response = await _copilotService.SendPromptAsync(
                _analyzerSessionName,
                prompt,
                cancellationToken: timeoutCts.Token,
                agentMode: "autopilot");

            Interlocked.Exchange(ref _lastAnalysisAtTicks, DateTime.UtcNow.Ticks);
            Interlocked.Increment(ref _analysisCount);

            if (!string.IsNullOrWhiteSpace(response))
                LastFinding = response.Length > 200 ? response[..200] + "..." : response;

            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // caller-initiated cancellation — propagate
        }
        catch (Exception ex)
        {
            LogAnalyzer($"Analysis failed: {ex.Message}");
            return null;
        }
    }

    private async Task RunAnalysisLoopAsync(int intervalMinutes, CancellationToken token)
    {
        // Initial delay — let the app settle after launch
        await Task.Delay(TimeSpan.FromMinutes(2), token);

        while (!token.IsCancellationRequested)
        {
            try
            {
                await RunSingleAnalysisAsync(token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                LogAnalyzer($"Analysis loop error: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), token);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Collect current diagnostic data from all available sources.
    /// </summary>
    internal string CollectDiagnostics()
    {
        var sb = new StringBuilder();

        // 1. Recent event diagnostics
        var diagLog = Path.Combine(PolyPilotDir, "event-diagnostics.log");
        if (File.Exists(diagLog))
        {
            var lines = TailFile(diagLog, DiagnosticLogTailLines);
            if (lines.Length > 0)
            {
                sb.AppendLine("## Recent Event Diagnostics (last 200 lines)");
                sb.AppendLine("```");
                sb.AppendLine(string.Join(Environment.NewLine, lines));
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        // 2. Crash log
        var crashLog = Path.Combine(PolyPilotDir, "crash.log");
        if (File.Exists(crashLog))
        {
            var lines = TailFile(crashLog, CrashLogTailLines);
            if (lines.Length > 0)
            {
                sb.AppendLine("## Recent Crash Log (last 50 lines)");
                sb.AppendLine("```");
                sb.AppendLine(string.Join(Environment.NewLine, lines));
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        // 3. Active session states (snapshot to avoid torn reads)
        sb.AppendLine("## Active Session States");
        sb.AppendLine("```json");
        sb.AppendLine(CollectSessionStates());
        sb.AppendLine("```");
        sb.AppendLine();

        // 4. Server health
        sb.AppendLine("## Server Health");
        sb.AppendLine($"- Server running: {_serverManager.IsServerRunning}");
        sb.AppendLine($"- Server PID: {_serverManager.ServerPid}");
        sb.AppendLine($"- Server port: {_serverManager.ServerPort}");
        if (!string.IsNullOrEmpty(_serverManager.LastError))
            sb.AppendLine($"- Last error: {_serverManager.LastError}");
        sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>
    /// Collect summary state for all active sessions.
    /// Snapshots the enumeration with ToList() to avoid torn reads.
    /// </summary>
    private string CollectSessionStates()
    {
        var sessions = _copilotService.GetAllSessions().ToList();
        var summaries = new List<object>();

        foreach (var session in sessions)
        {
            if (session.Name == AnalyzerSessionName) continue;

            summaries.Add(new
            {
                name = session.Name,
                isProcessing = session.IsProcessing,
                processingPhase = session.ProcessingPhase,
                toolCallCount = session.ToolCallCount,
                processingStartedAt = session.ProcessingStartedAt,
                messageCount = session.MessageCount,
                lastUpdated = session.LastUpdatedAt,
                isResumed = session.IsResumed,
            });
        }

        return JsonSerializer.Serialize(summaries, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Build the analysis prompt with collected diagnostics.
    /// The analyzer is instructed to REPORT issues only — never to autonomously create PRs.
    /// This prevents prompt injection from untrusted log content directing autonomous actions.
    /// </summary>
    internal static string BuildAnalysisPrompt(string diagnostics)
    {
        return $"""
            You are the PolyPilot Session Analyzer — a reliability monitor that runs perpetually alongside PolyPilot.

            Your job is to analyze the diagnostic data below and identify any issues with running sessions.

            ## What to look for:
            1. **Stuck sessions** — sessions showing IsProcessing=true for too long without recent events
            2. **Watchdog kills** — [WATCHDOG] entries that indicate sessions were force-completed
            3. **Error patterns** — [ERROR], [RECONNECT], crash log entries
            4. **Premature completions** — [IDLE-FALLBACK] or [COMPLETE] entries that shouldn't have fired
            5. **Dead connections** — sessions with no event activity but still marked as processing
            6. **Phantom sessions** — (previous) or (resumed) sessions that shouldn't exist
            7. **Resource leaks** — growing file descriptor counts, memory issues

            ## How to report:
            - Classify each finding as critical/warning/info
            - Describe the issue, the evidence from the logs, and a recommended fix
            - For code bugs, describe the root cause and which file/method to fix
            - Do NOT autonomously create branches or PRs — report only so a human can review

            ## Current Diagnostic Data:

            {diagnostics}

            Analyze the data above. If everything looks healthy, say "All sessions healthy" and briefly explain why.
            If you find issues, describe each one with severity (critical/warning/info) and recommended action.
            """;
    }

    /// <summary>
    /// Read the last N lines of a file efficiently using reverse seek.
    /// Caps file read to MaxLogFileSizeBytes to avoid unbounded memory usage.
    /// </summary>
    internal static string[] TailFile(string path, int lineCount)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            // Cap how much we read from the end to avoid loading huge files
            var readLength = Math.Min(fs.Length, MaxLogFileSizeBytes);
            if (readLength < fs.Length)
                fs.Seek(fs.Length - readLength, SeekOrigin.Begin);

            using var reader = new StreamReader(fs);
            var buffer = new Queue<string>();
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                buffer.Enqueue(line);
                if (buffer.Count > lineCount)
                    buffer.Dequeue();
            }

            return buffer.ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static void LogAnalyzer(string message)
    {
        try
        {
            var logPath = Path.Combine(PolyPilotDir, "event-diagnostics.log");
            File.AppendAllText(logPath, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} [ANALYZER] {message}{Environment.NewLine}");
        }
        catch { /* best effort */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    // For test isolation
    internal static void SetBaseDirForTesting(string dir)
    {
        _polypilotDir = dir;
    }
}
