using PolyPilot.Services;

namespace PolyPilot.Tests;

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

            // Write a test diagnostic log
            File.WriteAllText(
                Path.Combine(tempDir, "event-diagnostics.log"),
                "[SEND] 'TestSession' IsProcessing=true\n[COMPLETE] 'TestSession' done\n");

            var copilotService = TestHelpers.CreateTestCopilotService();
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

            var copilotService = TestHelpers.CreateTestCopilotService();
            var serverManager = new TestServerManager();
            var analyzer = new SessionAnalyzerService(copilotService, serverManager);

            var diagnostics = analyzer.CollectDiagnostics();

            Assert.Contains("Crash Log", diagnostics);
            Assert.Contains("test crash", diagnostics);
        }
        finally
        {
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

            var copilotService = TestHelpers.CreateTestCopilotService();
            var serverManager = new TestServerManager();
            var analyzer = new SessionAnalyzerService(copilotService, serverManager);

            var diagnostics = analyzer.CollectDiagnostics();

            // Should still have session states and server health sections
            Assert.Contains("Active Session States", diagnostics);
            Assert.Contains("Server Health", diagnostics);
        }
        finally
        {
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
    public void BuildAnalysisPrompt_InstructsAutoPrCreation()
    {
        var prompt = SessionAnalyzerService.BuildAnalysisPrompt("data");

        Assert.Contains("create a branch", prompt);
        Assert.Contains("open a PR", prompt);
    }

    [Fact]
    public void Constants_HaveReasonableDefaults()
    {
        Assert.Equal(10, SessionAnalyzerService.DefaultAnalysisIntervalMinutes);
        Assert.Equal(200, SessionAnalyzerService.DiagnosticLogTailLines);
        Assert.Equal(50, SessionAnalyzerService.CrashLogTailLines);
        Assert.Equal("PolyPilot Monitor", SessionAnalyzerService.AnalyzerSessionName);
    }

    [Fact]
    public void IsRunning_FalseBeforeStart()
    {
        var copilotService = TestHelpers.CreateTestCopilotService();
        var serverManager = new TestServerManager();
        var analyzer = new SessionAnalyzerService(copilotService, serverManager);

        Assert.False(analyzer.IsRunning);
        Assert.Null(analyzer.LastAnalysisAt);
        Assert.Equal(0, analyzer.AnalysisCount);
    }

    [Fact]
    public void Dispose_StopsAnalyzer()
    {
        var copilotService = TestHelpers.CreateTestCopilotService();
        var serverManager = new TestServerManager();
        var analyzer = new SessionAnalyzerService(copilotService, serverManager);

        analyzer.Dispose();

        // Should not throw on double dispose
        analyzer.Dispose();
        Assert.False(analyzer.IsRunning);
    }

    /// <summary>
    /// Test stub for IServerManager used by analyzer tests.
    /// </summary>
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

/// <summary>
/// Shared test helpers for creating CopilotService instances for unit tests.
/// </summary>
internal static class TestHelpers
{
    internal static CopilotService CreateTestCopilotService()
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
}
