using System.Text;
using System.Text.Json;

namespace PolyPilot.Services;

/// <summary>
/// A background service that maintains a dedicated copilot CLI session to perpetually
/// analyze running PolyPilot sessions for issues. When problems are detected, the
/// analyzer session (running in autopilot) can create PRs with fixes.
/// </summary>
public class SessionAnalyzerService : IDisposable
{
    private readonly CopilotService _copilotService;
    private readonly IServerManager _serverManager;
    private CancellationTokenSource? _cts;
    private Task? _analysisLoop;
    private string? _analyzerSessionName;
    private bool _disposed;

    // The analyzer session lives in a dedicated hidden group
    internal const string AnalyzerGroupName = "🔍 Session Analyzer";
    internal const string AnalyzerSessionName = "PolyPilot Monitor";
    internal const int DefaultAnalysisIntervalMinutes = 10;
    internal const int DiagnosticLogTailLines = 200;
    internal const int CrashLogTailLines = 50;

    private static string? _polypilotDir;
    private static string PolyPilotDir => _polypilotDir ??= CopilotService.BaseDir;

    public bool IsRunning => _analysisLoop is { IsCompleted: false };
    public DateTime? LastAnalysisAt { get; private set; }
    public int AnalysisCount { get; private set; }
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

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // Create the analyzer session
        _analyzerSessionName = AnalyzerSessionName;
        try
        {
            var session = await _copilotService.CreateSessionAsync(
                _analyzerSessionName,
                model: "claude-sonnet-4-5",
                workingDirectory: repoWorkingDirectory,
                cancellationToken: token);

            session.IsHidden = true;
        }
        catch (Exception ex)
        {
            LogAnalyzer($"Failed to create analyzer session: {ex.Message}");
            return;
        }

        _analysisLoop = RunAnalysisLoopAsync(intervalMinutes, token);
    }

    /// <summary>
    /// Stop the analysis loop and clean up.
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// Run a single analysis pass immediately (for testing or on-demand use).
    /// </summary>
    public async Task<string?> RunSingleAnalysisAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_analyzerSessionName)) return null;

        var diagnostics = CollectDiagnostics();
        if (string.IsNullOrEmpty(diagnostics)) return null;

        var prompt = BuildAnalysisPrompt(diagnostics);

        try
        {
            var response = await _copilotService.SendPromptAsync(
                _analyzerSessionName,
                prompt,
                cancellationToken: cancellationToken,
                agentMode: "autopilot");

            LastAnalysisAt = DateTime.UtcNow;
            AnalysisCount++;

            if (!string.IsNullOrWhiteSpace(response))
                LastFinding = response.Length > 200 ? response[..200] + "..." : response;

            return response;
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

        // 3. Active session states
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
    /// </summary>
    private string CollectSessionStates()
    {
        var sessions = _copilotService.GetAllSessions();
        var summaries = new List<object>();

        foreach (var session in sessions)
        {
            if (session.Name == AnalyzerSessionName) continue; // skip self

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

            ## What to do when you find issues:
            - If the issue is a PolyPilot code bug, create a branch, write the fix, run tests, and open a PR
            - If the issue is a stuck session that needs user intervention, report it clearly
            - If the issue is transient (network blip, CLI restart), note it but don't act

            ## Current Diagnostic Data:

            {diagnostics}

            Analyze the data above. If everything looks healthy, say "All sessions healthy" and briefly explain why.
            If you find issues, describe each one with severity (critical/warning/info) and recommended action.
            """;
    }

    private static string[] TailFile(string path, int lineCount)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var allLines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) != null)
                allLines.Add(line);

            return allLines.Count <= lineCount
                ? allLines.ToArray()
                : allLines.Skip(allLines.Count - lineCount).ToArray();
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
