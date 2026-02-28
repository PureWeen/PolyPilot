using System.Text.Json;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the session event log popup feature — the "Log" label in the
/// ExpandedSessionView status bar that shows a popup with events.jsonl entries.
/// </summary>
public class SessionEventLogTests
{
    [Fact]
    public void ParseEventLogFile_EmptyFile_ReturnsEmptyList()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile, "");
            var result = CopilotService.ParseEventLogFile(tmpFile);
            Assert.Empty(result);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void ParseEventLogFile_NonexistentFile_ReturnsEmptyList()
    {
        var result = CopilotService.ParseEventLogFile("/nonexistent/path/events.jsonl");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseEventLogFile_SessionStart_ExtractsCwd()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile,
                """{"type":"session.start","timestamp":"2026-02-27T09:00:00Z","data":{"context":{"cwd":"/Users/test/project"}}}""" + "\n");
            var result = CopilotService.ParseEventLogFile(tmpFile);
            Assert.Single(result);
            Assert.Equal("session.start", result[0].EventType);
            Assert.Equal("/Users/test/project", result[0].Detail);
            // Timestamp is formatted as local time, so just verify it's not empty
            Assert.NotEmpty(result[0].Timestamp);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void ParseEventLogFile_UserMessage_ExtractsContent()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile,
                """{"type":"user.message","timestamp":"2026-02-27T09:01:00Z","data":{"content":"Fix the login bug"}}""" + "\n");
            var result = CopilotService.ParseEventLogFile(tmpFile);
            Assert.Single(result);
            Assert.Equal("user.message", result[0].EventType);
            Assert.Equal("Fix the login bug", result[0].Detail);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void ParseEventLogFile_ToolExecution_ExtractsToolName()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile,
                """{"type":"tool.execution_start","timestamp":"2026-02-27T09:02:00Z","data":{"toolName":"bash"}}""" + "\n" +
                """{"type":"tool.execution_complete","timestamp":"2026-02-27T09:02:05Z","data":{"toolName":"bash"}}""" + "\n");
            var result = CopilotService.ParseEventLogFile(tmpFile);
            Assert.Equal(2, result.Count);
            Assert.Equal("tool.execution_start", result[0].EventType);
            Assert.Equal("bash", result[0].Detail);
            Assert.Equal("tool.execution_complete", result[1].EventType);
            Assert.Equal("bash", result[1].Detail);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void ParseEventLogFile_AssistantMessage_TruncatesLongContent()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            var longContent = new string('A', 200);
            File.WriteAllText(tmpFile,
                $"{{\"type\":\"assistant.message\",\"timestamp\":\"2026-02-27T09:03:00Z\",\"data\":{{\"content\":\"{longContent}\"}}}}" + "\n");
            var result = CopilotService.ParseEventLogFile(tmpFile);
            Assert.Single(result);
            Assert.Equal(81, result[0].Detail.Length); // 80 chars + ellipsis
            Assert.EndsWith("…", result[0].Detail);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void ParseEventLogFile_MalformedLine_SkippedGracefully()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile,
                "not valid json\n" +
                """{"type":"user.message","timestamp":"2026-02-27T09:01:00Z","data":{"content":"Hello"}}""" + "\n");
            var result = CopilotService.ParseEventLogFile(tmpFile);
            Assert.Single(result);
            Assert.Equal("user.message", result[0].EventType);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void ParseEventLogFile_MultipleEvents_ParsedInOrder()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile,
                """{"type":"session.start","timestamp":"2026-02-27T09:00:00Z","data":{"context":{"cwd":"/tmp"}}}""" + "\n" +
                """{"type":"user.message","timestamp":"2026-02-27T09:01:00Z","data":{"content":"Hello"}}""" + "\n" +
                """{"type":"assistant.message","timestamp":"2026-02-27T09:01:05Z","data":{"content":"Hi there"}}""" + "\n");
            var result = CopilotService.ParseEventLogFile(tmpFile);
            Assert.Equal(3, result.Count);
            Assert.Equal("session.start", result[0].EventType);
            Assert.Equal("user.message", result[1].EventType);
            Assert.Equal("assistant.message", result[2].EventType);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void ParseEventLogFile_SessionError_ExtractsMessage()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile,
                """{"type":"session.error","timestamp":"2026-02-27T09:05:00Z","data":{"message":"Connection refused"}}""" + "\n");
            var result = CopilotService.ParseEventLogFile(tmpFile);
            Assert.Single(result);
            Assert.Equal("session.error", result[0].EventType);
            Assert.Equal("Connection refused", result[0].Detail);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void ParseEventLogFile_AssistantIntent_ExtractsIntent()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile,
                """{"type":"assistant.intent","timestamp":"2026-02-27T09:04:00Z","data":{"intent":"Fixing bug"}}""" + "\n");
            var result = CopilotService.ParseEventLogFile(tmpFile);
            Assert.Single(result);
            Assert.Equal("assistant.intent", result[0].EventType);
            Assert.Equal("Fixing bug", result[0].Detail);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void ParseEventLogFile_DeltaEvent_EmptyDetail()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile,
                """{"type":"assistant.message_delta","timestamp":"2026-02-27T09:02:00Z","data":{"content":"chunk"}}""" + "\n");
            var result = CopilotService.ParseEventLogFile(tmpFile);
            Assert.Single(result);
            Assert.Equal("assistant.message_delta", result[0].EventType);
            Assert.Equal("", result[0].Detail);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void ParseEventLogFile_UnknownEventType_EmptyDetail()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile,
                """{"type":"custom.event","timestamp":"2026-02-27T09:00:00Z","data":{"foo":"bar"}}""" + "\n");
            var result = CopilotService.ParseEventLogFile(tmpFile);
            Assert.Single(result);
            Assert.Equal("custom.event", result[0].EventType);
            Assert.Equal("", result[0].Detail);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void ParseEventLogFile_NoTimestamp_UsesEmptyString()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile,
                """{"type":"user.message","data":{"content":"no timestamp"}}""" + "\n");
            var result = CopilotService.ParseEventLogFile(tmpFile);
            Assert.Single(result);
            Assert.Equal("", result[0].Timestamp);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void GetEventDetail_NoDataProperty_ReturnsEmpty()
    {
        var json = """{"type":"user.message"}""";
        using var doc = JsonDocument.Parse(json);
        var detail = CopilotService.GetEventDetail("user.message", doc.RootElement);
        Assert.Equal("", detail);
    }

    [Fact]
    public void LogLabel_HasClickHandler_InRazorMarkup()
    {
        // Verify the ExpandedSessionView.razor has a clickable log label
        var razorPath = Path.Combine(FindRepoRoot(), "PolyPilot", "Components", "ExpandedSessionView.razor");
        if (!File.Exists(razorPath)) return; // Skip if source not available
        var content = File.ReadAllText(razorPath);
        Assert.Contains("@onclick=\"ShowLogPopup\"", content);
        Assert.Contains("data-trigger=\"log-", content);
        Assert.Contains("class=\"log-label\"", content);
    }

    [Fact]
    public void LogLabel_HasCursorPointerStyle()
    {
        // Verify the CSS makes log-label clickable
        var cssPath = Path.Combine(FindRepoRoot(), "PolyPilot", "Components", "ExpandedSessionView.razor.css");
        if (!File.Exists(cssPath)) return;
        var content = File.ReadAllText(cssPath);
        Assert.Contains(".log-label", content);
        Assert.Contains("cursor: pointer", content);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "PolyPilot.slnx"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        // Fallback: check relative to test project
        var candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return Directory.Exists(candidate) ? candidate : AppContext.BaseDirectory;
    }
}
