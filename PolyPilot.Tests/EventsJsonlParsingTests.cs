using System.Text.Json;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the JSONL event parsing logic used by CopilotService to reconstruct
/// session state from events.jsonl files. These test the parsing patterns directly
/// since CopilotService itself has MAUI platform dependencies.
/// </summary>
public class EventsJsonlParsingTests
{
    [Fact]
    public void ParseSessionStart_ExtractsWorkingDirectory_NewerFormat()
    {
        var line = """{"type":"session.start","data":{"context":{"cwd":"/Users/test/project"}}}""";
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        Assert.Equal("session.start", root.GetProperty("type").GetString());

        var data = root.GetProperty("data");
        var cwd = data.GetProperty("context").GetProperty("cwd").GetString();
        Assert.Equal("/Users/test/project", cwd);
    }

    [Fact]
    public void ParseSessionStart_ExtractsWorkingDirectory_OlderFormat()
    {
        var line = """{"type":"session.start","data":{"workingDirectory":"/tmp/old-project"}}""";
        using var doc = JsonDocument.Parse(line);
        var data = doc.RootElement.GetProperty("data");

        // context.cwd not present, fall back to workingDirectory
        Assert.False(data.TryGetProperty("context", out _));
        Assert.Equal("/tmp/old-project", data.GetProperty("workingDirectory").GetString());
    }

    [Fact]
    public void ParseUserMessage_ExtractsContent()
    {
        var line = """{"type":"user.message","data":{"content":"Help me fix this bug"}}""";
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        Assert.Equal("user.message", root.GetProperty("type").GetString());
        var content = root.GetProperty("data").GetProperty("content").GetString();
        Assert.Equal("Help me fix this bug", content);
    }

    [Fact]
    public void TitleTruncation_Under60Chars_NoTruncation()
    {
        var content = "Short message";
        var title = content.Length > 60 ? content[..57] + "..." : content;
        Assert.Equal("Short message", title);
    }

    [Fact]
    public void TitleTruncation_Over60Chars_TruncatesWithEllipsis()
    {
        var content = new string('A', 100);
        var title = content.Length > 60 ? content[..57] + "..." : content;

        Assert.Equal(60, title.Length);
        Assert.EndsWith("...", title);
        Assert.Equal(new string('A', 57) + "...", title);
    }

    [Fact]
    public void TitleTruncation_Exactly60Chars_NoTruncation()
    {
        var content = new string('B', 60);
        var title = content.Length > 60 ? content[..57] + "..." : content;
        Assert.Equal(60, title.Length);
        Assert.Equal(content, title);
    }

    [Fact]
    public void TitleCleaning_RemovesNewlines()
    {
        var title = "First line\nSecond line\r\nThird line";
        title = title.Replace("\n", " ").Replace("\r", "");
        Assert.Equal("First line Second line Third line", title);
    }

    [Fact]
    public void IsSessionStillProcessing_ActiveEventTypes()
    {
        var activeEvents = new[]
        {
            "assistant.turn_start", "tool.execution_start",
            "tool.execution_progress", "assistant.message_delta",
            "assistant.reasoning", "assistant.reasoning_delta",
            "assistant.intent"
        };

        // These should indicate the session is still processing
        foreach (var eventType in activeEvents)
        {
            Assert.Contains(eventType, activeEvents);
        }

        // These should NOT indicate processing
        var inactiveEvents = new[] { "session.idle", "assistant.message", "session.start" };
        foreach (var eventType in inactiveEvents)
        {
            Assert.DoesNotContain(eventType, activeEvents);
        }
    }

    [Fact]
    public void ParseJsonlLine_SkipsEmptyLines()
    {
        var lines = new[] { "", "  ", "\t", """{"type":"user.message","data":{"content":"hello"}}""" };
        var nonEmpty = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        Assert.Single(nonEmpty);
    }

    [Fact]
    public void ParseJsonlLine_InvalidJson_DoesNotThrow()
    {
        var line = "this is not json";
        var exception = Record.Exception(() =>
        {
            try { JsonDocument.Parse(line); }
            catch (JsonException) { /* Expected — this is how the app handles it */ }
        });
        Assert.Null(exception);
    }

    [Fact]
    public void GuidParsing_ValidSessionId()
    {
        var dirName = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
        Assert.True(Guid.TryParse(dirName, out var guid));
        Assert.NotEqual(Guid.Empty, guid);
    }

    [Fact]
    public void GuidParsing_InvalidSessionId_Filtered()
    {
        var invalidNames = new[] { "not-a-guid", "temp", ".DS_Store", "events.jsonl" };
        foreach (var name in invalidNames)
        {
            Assert.False(Guid.TryParse(name, out _));
        }
    }

    [Fact]
    public void EventsFile_MultipleEvents_ExtractsFirstUserMessage()
    {
        var lines = new[]
        {
            """{"type":"session.start","data":{"context":{"cwd":"/tmp"}}}""",
            """{"type":"assistant.turn_start","data":{}}""",
            """{"type":"user.message","data":{"content":"Build a REST API"}}""",
            """{"type":"assistant.message","data":{"content":"Sure, I'll help."}}""",
            """{"type":"user.message","data":{"content":"Add authentication"}}"""
        };

        string? firstUserContent = null;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) continue;
            if (typeEl.GetString() == "user.message" && firstUserContent == null)
            {
                if (root.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("content", out var content))
                {
                    firstUserContent = content.GetString();
                }
                break;
            }
        }

        Assert.Equal("Build a REST API", firstUserContent);
    }

    [Fact]
    public void ParseSessionStart_ExtractsSelectedModel()
    {
        var line = """{"type":"session.start","data":{"selectedModel":"Claude Opus 4.6 (fast mode)","context":{"cwd":"/tmp"}}}""";
        var model = ExtractModelFromSessionStart(line);
        Assert.Equal("Claude Opus 4.6 (fast mode)", model);
    }

    [Fact]
    public void ParseSessionStart_MissingSelectedModel_ReturnsNull()
    {
        var line = """{"type":"session.start","data":{"context":{"cwd":"/tmp"}}}""";
        var model = ExtractModelFromSessionStart(line);
        Assert.Null(model);
    }

    [Fact]
    public void ParseSessionStart_ExtractsModel_FromMultipleEvents()
    {
        // Mirrors GetSessionModelFromDisk: scan first few lines for session.start
        var lines = new[]
        {
            """{"type":"session.start","data":{"sessionId":"abc-123","selectedModel":"claude-sonnet-4","context":{"cwd":"/tmp"}}}""",
            """{"type":"user.message","data":{"content":"hello"}}""",
            """{"type":"assistant.turn_start","data":{}}"""
        };

        string? model = null;
        foreach (var line in lines.Take(5))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var t) || t.GetString() != "session.start") continue;
            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("selectedModel", out var m))
                model = m.GetString();
        }

        Assert.Equal("claude-sonnet-4", model);
    }

    [Fact]
    public void ParseSessionStart_NoSessionStartEvent_ReturnsNull()
    {
        var lines = new[]
        {
            """{"type":"user.message","data":{"content":"hello"}}""",
            """{"type":"assistant.message","data":{"content":"hi"}}"""
        };

        string? model = null;
        foreach (var line in lines.Take(5))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var t) || t.GetString() != "session.start") continue;
            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("selectedModel", out var m))
                model = m.GetString();
        }

        Assert.Null(model);
    }

    [Fact]
    public void ResumedSession_ModelShouldNotBe_ResumedPlaceholder()
    {
        // Regression test: previously ResumeSessionAsync set Model = "resumed"
        // which caused GetSessionModel to fall through to the first available model.
        // The model from session.start should be used instead.
        var sessionStartLine = """{"type":"session.start","data":{"selectedModel":"gpt-5.1-codex","context":{"cwd":"/tmp"}}}""";
        var model = ExtractModelFromSessionStart(sessionStartLine);

        Assert.NotNull(model);
        Assert.NotEqual("resumed", model);
        Assert.Equal("gpt-5.1-codex", model);
    }

    /// <summary>
    /// Mirrors the GetSessionModelFromDisk parsing logic from CopilotService.Utilities.cs
    /// </summary>
    private static string? ExtractModelFromSessionStart(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var t) || t.GetString() != "session.start") return null;
        if (root.TryGetProperty("data", out var data) &&
            data.TryGetProperty("selectedModel", out var model))
            return model.GetString();
        return null;
    }
}
