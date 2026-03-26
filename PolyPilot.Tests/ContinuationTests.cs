using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

public class ContinuationTests
{
    [Fact]
    public void GenerateContinuationName_AppendsContdSuffix()
    {
        var result = CopilotService.GenerateContinuationName("my-session");
        Assert.Equal("my-session (cont'd)", result);
    }

    [Fact]
    public void GenerateContinuationName_StripsExistingSuffix()
    {
        var result = CopilotService.GenerateContinuationName("my-session (cont'd)");
        Assert.Equal("my-session (cont'd)", result);
    }

    [Fact]
    public void GenerateContinuationName_IncrementsWhenNameExists()
    {
        var existing = new[] { "my-session (cont'd)" };
        var result = CopilotService.GenerateContinuationName("my-session", existing);
        Assert.Equal("my-session (cont'd 2)", result);
    }

    [Fact]
    public void GenerateContinuationName_IncrementsMultipleTimes()
    {
        var existing = new[] { "my-session (cont'd)", "my-session (cont'd 2)", "my-session (cont'd 3)" };
        var result = CopilotService.GenerateContinuationName("my-session", existing);
        Assert.Equal("my-session (cont'd 4)", result);
    }

    [Fact]
    public void GenerateContinuationName_FromContdSourceWithExisting()
    {
        // Continuing from an already-continued session when the base name also exists
        var existing = new[] { "my-session (cont'd)" };
        var result = CopilotService.GenerateContinuationName("my-session (cont'd)", existing);
        Assert.Equal("my-session (cont'd 2)", result);
    }

    [Fact]
    public void GenerateContinuationName_StripsNumberedSuffix()
    {
        var result = CopilotService.GenerateContinuationName("my-session (cont'd 3)");
        Assert.Equal("my-session (cont'd)", result);
    }

    [Fact]
    public void BuildContinuationTranscript_IncludesUserMessages()
    {
        var history = new List<ChatMessage>
        {
            new("user", "Hello world", DateTime.Now, ChatMessageType.User),
            new("assistant", "Hi there!", DateTime.Now, ChatMessageType.Assistant),
        };

        var result = CopilotService.BuildContinuationTranscript(history, "test-session", "abc-123");

        Assert.Contains("**User:** Hello world", result);
        Assert.Contains("**Assistant:** Hi there!", result);
        Assert.Contains("test-session", result);
    }

    [Fact]
    public void BuildContinuationTranscript_TruncatesLongAssistantMessages()
    {
        var longContent = new string('x', 500);
        var history = new List<ChatMessage>
        {
            new("assistant", longContent, DateTime.Now, ChatMessageType.Assistant),
        };

        var result = CopilotService.BuildContinuationTranscript(history, "test", "abc");

        // Should be truncated to 400 chars + ellipsis
        Assert.DoesNotContain(longContent, result);
        Assert.Contains("…", result);
    }

    [Fact]
    public void BuildContinuationTranscript_IncludesToolCalls()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.ToolCallMessage("edit", "tc-1", "editing file"),
        };
        // Mark complete + success
        history[0].IsComplete = true;
        history[0].IsSuccess = true;

        var result = CopilotService.BuildContinuationTranscript(history, "test", "abc");

        Assert.Contains("🔧 edit ✅", result);
    }

    [Fact]
    public void BuildContinuationTranscript_IncludesSessionIdPath()
    {
        var history = new List<ChatMessage>
        {
            new("user", "test", DateTime.Now, ChatMessageType.User),
        };

        var result = CopilotService.BuildContinuationTranscript(history, "test", "abc-123");

        Assert.Contains("~/.copilot/session-state/abc-123/events.jsonl", result);
    }

    [Fact]
    public void BuildContinuationTranscript_HandlesNullSessionId()
    {
        var history = new List<ChatMessage>
        {
            new("user", "test", DateTime.Now, ChatMessageType.User),
        };

        var result = CopilotService.BuildContinuationTranscript(history, "test", null);

        Assert.Contains("session ID not available", result);
    }

    [Fact]
    public void BuildContinuationTranscript_SkipsSystemMessages()
    {
        var history = new List<ChatMessage>
        {
            new("system", "System init", DateTime.Now, ChatMessageType.System),
            new("user", "Hello", DateTime.Now, ChatMessageType.User),
        };

        var result = CopilotService.BuildContinuationTranscript(history, "test", "abc");

        Assert.DoesNotContain("System init", result);
        Assert.Contains("**User:** Hello", result);
    }

    [Fact]
    public void BuildContinuationTranscript_TrimsOldTurnsWhenOverBudget()
    {
        var history = new List<ChatMessage>();
        // Add many turns to exceed 6000 char budget
        for (int i = 0; i < 50; i++)
        {
            history.Add(new("user", $"Question {i}: {new string('q', 100)}", DateTime.Now, ChatMessageType.User));
            history.Add(new("assistant", $"Answer {i}: {new string('a', 300)}", DateTime.Now, ChatMessageType.Assistant));
        }

        var result = CopilotService.BuildContinuationTranscript(history, "test", "abc");

        // Should not contain the first turn (trimmed)
        Assert.DoesNotContain("Question 0:", result);
        // Should contain the last turn (preserved)
        Assert.Contains("Question 49:", result);
        // Total length should be reasonable
        Assert.True(result.Length < 8000, $"Transcript too long: {result.Length}");
    }

    [Fact]
    public void BuildContinuationTranscript_IncludesErrorMessages()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.ErrorMessage("Something went wrong", "bash"),
        };

        var result = CopilotService.BuildContinuationTranscript(history, "test", "abc");

        Assert.Contains("⚠️ Error:", result);
        Assert.Contains("Something went wrong", result);
    }

    [Fact]
    public void BuildContinuationTranscript_IncludesImageMessages()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.ImageMessage("/path/to/img.png", null, "Screenshot of bug"),
        };

        var result = CopilotService.BuildContinuationTranscript(history, "test", "abc");

        Assert.Contains("🖼️ Image: Screenshot of bug", result);
    }
}
