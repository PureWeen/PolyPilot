using PolyPilot.Services;

namespace PolyPilot.Tests;

[Collection("BaseDir")]
public class SessionAnalyzerTests
{
    [Fact]
    public void CollectDiagnostics_IncludesServerHealth()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"analyzer-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            SessionAnalyzerService.SetBaseDirForTesting(tempDir);

            File.WriteAllText(
                Path.Combine(tempDir, "event-diagnostics.log"),
                "[SEND] 'TestSession' IsProcessing=true\n[COMPLETE] 'TestSession' done\n");

            var copilotService = CreateService();
            var serverManager = new TestServerManager { IsRunning = true, Pid = 12345, Port = 4321 };
            var analyzer = new SessionAnalyzerService(copilotService, serverManager);

            var diagnostics = analyzer.CollectDiagnostics();

            Assert.Contains("Event Diagnostics", diagnostics);
            Assert.Contains("[SEND]", diagnostics);
            Assert.Contains("[COMPLETE]", diagnostics);
            Assert.Contains("Server running: True", diagnostics);
            Assert.Contains("12345", diagnostics);
        }
        finally
        {
            SessionAnalyzerService.SetBaseDirForTesting(TestSetup.TestBaseDir);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CollectDiagnostics_IncludesCrashLog_WhenPresent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"analyzer-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            SessionAnalyzerService.SetBaseDirForTesting(tempDir);

            File.WriteAllText(
                Path.Combine(tempDir, "crash.log"),
                "=== 2026-04-18 ===\nSystem.Exception: test crash\n");

            var copilotService = CreateService();
            var serverManager = new TestServerManager();
            var analyzer = new SessionAnalyzerService(copilotService, serverManager);

            var diagnostics = analyzer.CollectDiagnostics();

            Assert.Contains("Crash Log", diagnostics);
            Assert.Contains("test crash", diagnostics);
        }
        finally
        {
            SessionAnalyzerService.SetBaseDirForTesting(TestSetup.TestBaseDir);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CollectDiagnostics_HandlesEmptyLogs()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"analyzer-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            SessionAnalyzerService.SetBaseDirForTesting(tempDir);

            var copilotService = CreateService();
            var serverManager = new TestServerManager();
            var analyzer = new SessionAnalyzerService(copilotService, serverManager);

            var diagnostics = analyzer.CollectDiagnostics();

            Assert.Contains("Active Session States", diagnostics);
            Assert.Contains("Server Health", diagnostics);
        }
        finally
        {
            SessionAnalyzerService.SetBaseDirForTesting(TestSetup.TestBaseDir);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsDiagnosticData()
    {
        var diagnostics = "## Test Data\nSome diagnostic info here";
        var prompt = SessionAnalyzerService.BuildAnalysisPrompt(diagnostics);

        Assert.Contains("PolyPilot Session Analyzer", prompt);
        Assert.Contains("Stuck sessions", prompt);
        Assert.Contains("Watchdog kills", prompt);
        Assert.Contains("Test Data", prompt);
        Assert.Contains("Some diagnostic info here", prompt);
    }

    [Fact]
    public void BuildAnalysisPrompt_ReportOnly_NoAutonomousPrCreation()
    {
        var prompt = SessionAnalyzerService.BuildAnalysisPrompt("data");

        // Must instruct report-only, NOT autonomous PR creation
        Assert.Contains("Do NOT autonomously create branches or PRs", prompt);
        Assert.Contains("report only", prompt);
        Assert.DoesNotContain("create a branch, write the fix", prompt);
    }

    [Fact]
    public void Constants_HaveReasonableDefaults()
    {
        Assert.Equal(10, SessionAnalyzerService.DefaultAnalysisIntervalMinutes);
        Assert.Equal(1, SessionAnalyzerService.MinAnalysisIntervalMinutes);
        Assert.Equal(200, SessionAnalyzerService.DiagnosticLogTailLines);
        Assert.Equal(50, SessionAnalyzerService.CrashLogTailLines);
        Assert.Equal("PolyPilot Monitor", SessionAnalyzerService.AnalyzerSessionName);
        Assert.Equal(10 * 1024 * 1024, SessionAnalyzerService.MaxLogFileSizeBytes);
    }

    [Fact]
    public void IsRunning_FalseBeforeStart()
    {
        var copilotService = CreateService();
        var serverManager = new TestServerManager();
        var analyzer = new SessionAnalyzerService(copilotService, serverManager);

        Assert.False(analyzer.IsRunning);
        Assert.Null(analyzer.LastAnalysisAt);
        Assert.Equal(0, analyzer.AnalysisCount);
    }

    [Fact]
    public void Dispose_StopsAnalyzer()
    {
        var copilotService = CreateService();
        var serverManager = new TestServerManager();
        var analyzer = new SessionAnalyzerService(copilotService, serverManager);

        analyzer.Dispose();
        analyzer.Dispose(); // double dispose is safe
        Assert.False(analyzer.IsRunning);
    }

    [Fact]
    public async Task DisposeAsync_StopsAnalyzer()
    {
        var copilotService = CreateService();
        var serverManager = new TestServerManager();
        var analyzer = new SessionAnalyzerService(copilotService, serverManager);

        await analyzer.DisposeAsync();
        await analyzer.DisposeAsync(); // double dispose is safe
        Assert.False(analyzer.IsRunning);
    }

    [Fact]
    public async Task RunSingleAnalysis_ReturnsNull_WhenNoSessionCreated()
    {
        var copilotService = CreateService();
        var serverManager = new TestServerManager();
        var analyzer = new SessionAnalyzerService(copilotService, serverManager);

        // _analyzerSessionName is null — no session was created
        var result = await analyzer.RunSingleAnalysisAsync();
        Assert.Null(result);
        Assert.Equal(0, analyzer.AnalysisCount);
    }

    [Fact]
    public void TailFile_CapsLargeFiles()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // Write a small file — TailFile should return last N lines
            var lines = Enumerable.Range(1, 500).Select(i => $"line {i}").ToArray();
            File.WriteAllLines(tempFile, lines);

            var result = SessionAnalyzerService.TailFile(tempFile, 10);
            Assert.Equal(10, result.Length);
            Assert.Equal("line 491", result[0]);
            Assert.Equal("line 500", result[9]);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void TailFile_HandlesSmallFile()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tempFile, new[] { "a", "b", "c" });
            var result = SessionAnalyzerService.TailFile(tempFile, 10);
            Assert.Equal(3, result.Length);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void TailFile_HandlesNonexistentFile()
    {
        var result = SessionAnalyzerService.TailFile("/nonexistent/path", 10);
        Assert.Empty(result);
    }

    [Fact]
    public void SessionAnalyzerIntervalMinutes_ClampsToMinimum()
    {
        var settings = new PolyPilot.Models.ConnectionSettings();

        settings.SessionAnalyzerIntervalMinutes = 0;
        Assert.Equal(1, settings.SessionAnalyzerIntervalMinutes);

        settings.SessionAnalyzerIntervalMinutes = -5;
        Assert.Equal(1, settings.SessionAnalyzerIntervalMinutes);

        settings.SessionAnalyzerIntervalMinutes = 30;
        Assert.Equal(30, settings.SessionAnalyzerIntervalMinutes);
    }

    private static string GetTempDir() => Path.GetTempPath();

    private static CopilotService CreateService()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var serviceProvider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions
            .BuildServiceProvider(services);
        return new CopilotService(
            new StubChatDatabase(),
            new StubServerManager(),
            new StubWsBridgeClient(),
            new RepoManager(),
            serviceProvider,
            new StubDemoService());
    }

    private class TestServerManager : IServerManager
    {
        public bool IsRunning { get; set; }
        public int? Pid { get; set; }
        public int Port { get; set; } = 4321;
        public string? Error { get; set; }

        bool IServerManager.IsServerRunning => IsRunning;
        int? IServerManager.ServerPid => Pid;
        int IServerManager.ServerPort => Port;
        string? IServerManager.LastError => Error;

        public event Action? OnStatusChanged;

        public bool CheckServerRunning(string host = "127.0.0.1", int? port = null) => IsRunning;
        public Task<bool> StartServerAsync(int port, string? githubToken = null) => Task.FromResult(true);
        public void StopServer() { }
        public bool DetectExistingServer() => IsRunning;
    }
}
