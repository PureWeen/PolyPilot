using PolyPilot.Services;

namespace PolyPilot.Tests;

public class SessionMetricsExtractorTests
{
    private string CreateTempSession(params string[] eventLines)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"test-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "events.jsonl"), eventLines);
        return dir;
    }

    [Fact]
    public void Extract_ParsesSessionStart()
    {
        var dir = CreateTempSession(
            """{"type":"session.start","timestamp":"2026-01-01T00:00:00Z","data":{"sessionId":"abc-123","copilotVersion":"0.0.414","context":{"cwd":"/home/user","branch":"main","repository":"owner/repo"}}}""");
        try
        {
            var metrics = SessionMetricsExtractor.Extract(dir);

            Assert.Equal("abc-123", metrics.Session.Id);
            Assert.Equal("0.0.414", metrics.Session.CopilotVersion);
            Assert.Equal("/home/user", metrics.Session.Cwd);
            Assert.Equal("main", metrics.Session.Branch);
            Assert.Equal("owner/repo", metrics.Session.Repository);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Extract_CountsUserMessages()
    {
        var dir = CreateTempSession(
            """{"type":"session.start","timestamp":"2026-01-01T00:00:00Z","data":{"sessionId":"s1"}}""",
            """{"type":"user.message","timestamp":"2026-01-01T00:01:00Z","data":{"content":"hello world"}}""",
            """{"type":"user.message","timestamp":"2026-01-01T00:02:00Z","data":{"content":"another message"}}""");
        try
        {
            var metrics = SessionMetricsExtractor.Extract(dir);

            Assert.Equal(2, metrics.UserMessages.Count);
            Assert.Equal(2, metrics.Turns.UserInitiated);
            Assert.Equal("hello world", metrics.UserMessages[0].ContentPreview);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Extract_CountsTurns()
    {
        var dir = CreateTempSession(
            """{"type":"session.start","timestamp":"2026-01-01T00:00:00Z","data":{"sessionId":"s1"}}""",
            """{"type":"assistant.turn_start","timestamp":"2026-01-01T00:01:00Z","data":{}}""",
            """{"type":"assistant.turn_end","timestamp":"2026-01-01T00:01:30Z","data":{}}""",
            """{"type":"assistant.turn_start","timestamp":"2026-01-01T00:02:00Z","data":{}}""",
            """{"type":"assistant.turn_end","timestamp":"2026-01-01T00:02:30Z","data":{}}""");
        try
        {
            var metrics = SessionMetricsExtractor.Extract(dir);

            Assert.Equal(2, metrics.Turns.Total);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Extract_CountsToolCalls()
    {
        var dir = CreateTempSession(
            """{"type":"session.start","timestamp":"2026-01-01T00:00:00Z","data":{"sessionId":"s1"}}""",
            """{"type":"tool.execution_start","timestamp":"2026-01-01T00:01:00Z","data":{"toolName":"bash","toolCallId":"tc1","arguments":{"command":"echo hi"}}}""",
            """{"type":"tool.execution_complete","timestamp":"2026-01-01T00:01:05Z","data":{"toolCallId":"tc1","success":true}}""",
            """{"type":"tool.execution_start","timestamp":"2026-01-01T00:02:00Z","data":{"toolName":"edit","toolCallId":"tc2","arguments":{}}}""",
            """{"type":"tool.execution_complete","timestamp":"2026-01-01T00:02:01Z","data":{"toolCallId":"tc2","success":true}}""");
        try
        {
            var metrics = SessionMetricsExtractor.Extract(dir);

            Assert.Equal(2, metrics.ToolCalls.Total);
            Assert.Equal(1, metrics.ToolCalls.ByName["bash"]);
            Assert.Equal(1, metrics.ToolCalls.ByName["edit"]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Extract_DetectsSubagents()
    {
        var dir = CreateTempSession(
            """{"type":"session.start","timestamp":"2026-01-01T00:00:00Z","data":{"sessionId":"s1"}}""",
            """{"type":"subagent.started","timestamp":"2026-01-01T00:01:00Z","data":{"toolCallId":"sa1","agentName":"explore","agentDisplayName":"Explorer"}}""",
            """{"type":"subagent.completed","timestamp":"2026-01-01T00:01:10Z","data":{"toolCallId":"sa1"}}""");
        try
        {
            var metrics = SessionMetricsExtractor.Extract(dir);

            Assert.Single(metrics.Subagents);
            Assert.Equal("explore", metrics.Subagents[0].Name);
            Assert.Equal("Explorer", metrics.Subagents[0].DisplayName);
            Assert.Equal(10_000, metrics.Subagents[0].DurationMs);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Extract_DetectsCompactions()
    {
        var dir = CreateTempSession(
            """{"type":"session.start","timestamp":"2026-01-01T00:00:00Z","data":{"sessionId":"s1"}}""",
            """{"type":"session.compaction_start","timestamp":"2026-01-01T00:10:00Z","data":{}}""",
            """{"type":"session.compaction_complete","timestamp":"2026-01-01T00:10:05Z","data":{"preCompactionTokens":128000,"success":true}}""");
        try
        {
            var metrics = SessionMetricsExtractor.Extract(dir);

            Assert.Single(metrics.Compactions);
            Assert.Equal(128000, metrics.Compactions[0].PreTokens);
            Assert.True(metrics.Compactions[0].Success);
            Assert.Equal(1, metrics.Summary.TotalCompactions);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Extract_DetectsErrors()
    {
        var dir = CreateTempSession(
            """{"type":"session.start","timestamp":"2026-01-01T00:00:00Z","data":{"sessionId":"s1"}}""",
            """{"type":"session.error","timestamp":"2026-01-01T00:05:00Z","data":{"errorType":"timeout","message":"408 Request Timeout"}}""");
        try
        {
            var metrics = SessionMetricsExtractor.Extract(dir);

            Assert.Single(metrics.Errors);
            Assert.Equal("timeout", metrics.Errors[0].Type);
            Assert.Equal(1, metrics.Summary.TotalErrors);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Extract_DetectsBuildDevLoop()
    {
        var dir = CreateTempSession(
            """{"type":"session.start","timestamp":"2026-01-01T00:00:00Z","data":{"sessionId":"s1"}}""",
            """{"type":"tool.execution_start","timestamp":"2026-01-01T00:01:00Z","data":{"toolName":"bash","toolCallId":"tc1","arguments":{"command":"dotnet build"}}}""",
            """{"type":"tool.execution_complete","timestamp":"2026-01-01T00:01:30Z","data":{"toolCallId":"tc1","success":true,"result":{"content":"Build succeeded. 0 Error(s)"}}}""",
            """{"type":"tool.execution_start","timestamp":"2026-01-01T00:02:00Z","data":{"toolName":"bash","toolCallId":"tc2","arguments":{"command":"dotnet build"}}}""",
            """{"type":"tool.execution_complete","timestamp":"2026-01-01T00:02:30Z","data":{"toolCallId":"tc2","success":false,"result":{"content":"Build FAILED. 2 Error(s)"}}}""");
        try
        {
            var metrics = SessionMetricsExtractor.Extract(dir);

            Assert.Equal(2, metrics.DevLoopSummary.TotalBuilds);
            Assert.Equal(1, metrics.DevLoopSummary.BuildFailures);
            Assert.Equal(50.0, metrics.DevLoopSummary.BuildSuccessRatePct);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Extract_DetectsTestDevLoop()
    {
        var dir = CreateTempSession(
            """{"type":"session.start","timestamp":"2026-01-01T00:00:00Z","data":{"sessionId":"s1"}}""",
            """{"type":"tool.execution_start","timestamp":"2026-01-01T00:01:00Z","data":{"toolName":"bash","toolCallId":"tc1","arguments":{"command":"dotnet test"}}}""",
            """{"type":"tool.execution_complete","timestamp":"2026-01-01T00:01:30Z","data":{"toolCallId":"tc1","success":true,"result":{"content":"Passed! - Failed: 2, Passed: 48"}}}""");
        try
        {
            var metrics = SessionMetricsExtractor.Extract(dir);

            Assert.Equal(1, metrics.DevLoopSummary.TotalTests);
            Assert.Equal(1, metrics.DevLoopSummary.TestFailures);
            Assert.Equal(48, metrics.DevLoopSummary.TestPassed);
            Assert.Equal(2, metrics.DevLoopSummary.TestFailed);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Extract_ComputesCost_WithModelPricing()
    {
        // No process log = no LLM calls = $0 cost
        var dir = CreateTempSession(
            """{"type":"session.start","timestamp":"2026-01-01T00:00:00Z","data":{"sessionId":"s1"}}""");
        try
        {
            var metrics = SessionMetricsExtractor.Extract(dir);
            Assert.Equal(0, metrics.Summary.TotalLlmCalls);
            Assert.Equal(0.0, metrics.Summary.EstimatedCostUsd);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Extract_SkipsMalformedLines()
    {
        var dir = CreateTempSession(
            """{"type":"session.start","timestamp":"2026-01-01T00:00:00Z","data":{"sessionId":"s1"}}""",
            "not json at all",
            "",
            """{"type":"user.message","timestamp":"2026-01-01T00:01:00Z","data":{"content":"valid"}}""");
        try
        {
            var metrics = SessionMetricsExtractor.Extract(dir);
            Assert.Single(metrics.UserMessages);
            Assert.Equal("valid", metrics.UserMessages[0].ContentPreview);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Extract_TruncatesLongContentPreview()
    {
        var longContent = new string('x', 500);
        var json = "{\"type\":\"user.message\",\"timestamp\":\"2026-01-01T00:01:00Z\",\"data\":{\"content\":\"" + longContent + "\"}}";
        var dir = CreateTempSession(
            """{"type":"session.start","timestamp":"2026-01-01T00:00:00Z","data":{"sessionId":"s1"}}""",
            json);
        try
        {
            var metrics = SessionMetricsExtractor.Extract(dir);
            Assert.Equal(200, metrics.UserMessages[0].ContentPreview.Length);
            Assert.Equal(500, metrics.UserMessages[0].FullLength);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Extract_ThrowsWhenNoEventsFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"test-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Throws<FileNotFoundException>(() => SessionMetricsExtractor.Extract(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Extract_ReadsWorkspaceYamlSummary()
    {
        var dir = CreateTempSession(
            """{"type":"session.start","timestamp":"2026-01-01T00:00:00Z","data":{"sessionId":"s1"}}""");
        File.WriteAllText(Path.Combine(dir, "workspace.yaml"), "summary: My session summary\nother: stuff");
        try
        {
            var metrics = SessionMetricsExtractor.Extract(dir);
            Assert.Equal("My session summary", metrics.Session.Summary);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Extract_ComputesSessionDuration()
    {
        var dir = CreateTempSession(
            """{"type":"session.start","timestamp":"2026-01-01T00:00:00Z","data":{"sessionId":"s1","startTime":"2026-01-01T00:00:00Z"}}""",
            """{"type":"assistant.turn_start","timestamp":"2026-01-01T00:01:00Z","data":{}}""",
            """{"type":"assistant.turn_end","timestamp":"2026-01-01T00:05:00Z","data":{}}""");
        try
        {
            var metrics = SessionMetricsExtractor.Extract(dir);
            Assert.Equal(300_000, metrics.Summary.SessionDurationMs); // 5 minutes
        }
        finally { Directory.Delete(dir, true); }
    }
}
