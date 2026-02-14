using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

public class NotificationMessageBuilderTests
{
    [Fact]
    public void BuildMessage_Completed_GeneratesCorrectMessage()
    {
        var payload = new AttentionNeededPayload
        {
            SessionName = "Feature Work",
            Reason = AttentionReason.Completed,
            Summary = "Added unit tests for the new API"
        };

        var (title, body) = NotificationMessageBuilder.BuildMessage(payload);

        Assert.Equal("Feature Work", title);
        Assert.StartsWith("✓", body);
        Assert.Contains("Added unit tests", body);
    }

    [Fact]
    public void BuildMessage_Error_GeneratesCorrectMessage()
    {
        var payload = new AttentionNeededPayload
        {
            SessionName = "Bug Fix",
            Reason = AttentionReason.Error,
            Summary = "Connection timeout"
        };

        var (title, body) = NotificationMessageBuilder.BuildMessage(payload);

        Assert.Equal("Bug Fix", title);
        Assert.StartsWith("⚠", body);
        Assert.Contains("Connection timeout", body);
    }

    [Fact]
    public void BuildMessage_NeedsInteraction_GeneratesCorrectMessage()
    {
        var payload = new AttentionNeededPayload
        {
            SessionName = "Deploy Script",
            Reason = AttentionReason.NeedsInteraction,
            Summary = "Waiting for approval"
        };

        var (title, body) = NotificationMessageBuilder.BuildMessage(payload);

        Assert.Equal("Deploy Script", title);
        Assert.StartsWith("🔔", body);
        Assert.Contains("Waiting for approval", body);
    }

    [Fact]
    public void BuildMessage_ReadyForMore_GeneratesCorrectMessage()
    {
        var payload = new AttentionNeededPayload
        {
            SessionName = "Code Review",
            Reason = AttentionReason.ReadyForMore,
            Summary = "Review complete"
        };

        var (title, body) = NotificationMessageBuilder.BuildMessage(payload);

        Assert.Equal("Code Review", title);
        Assert.StartsWith("📋", body);
    }

    [Fact]
    public void BuildMessage_EmptySummary_UsesFallbackMessage()
    {
        var payload = new AttentionNeededPayload
        {
            SessionName = "Test",
            Reason = AttentionReason.Completed,
            Summary = ""
        };

        var (title, body) = NotificationMessageBuilder.BuildMessage(payload);

        Assert.Equal("Test", title);
        Assert.Contains("Response complete", body);
    }

    [Fact]
    public void BuildMessage_LongSummary_IsTruncated()
    {
        var longSummary = new string('x', 200);
        var payload = new AttentionNeededPayload
        {
            SessionName = "Test",
            Reason = AttentionReason.Completed,
            Summary = longSummary
        };

        var (_, body) = NotificationMessageBuilder.BuildMessage(payload);

        Assert.True(body.Length < 100); // Should be truncated
        Assert.EndsWith("…", body);
    }

    [Fact]
    public void BuildMessage_NewlinesInSummary_AreReplaced()
    {
        var payload = new AttentionNeededPayload
        {
            SessionName = "Test",
            Reason = AttentionReason.Completed,
            Summary = "Line1\nLine2\r\nLine3"
        };

        var (_, body) = NotificationMessageBuilder.BuildMessage(payload);

        Assert.DoesNotContain("\n", body);
        Assert.DoesNotContain("\r", body);
    }

    [Theory]
    [InlineData(AttentionReason.Completed, "✓")]
    [InlineData(AttentionReason.Error, "⚠")]
    [InlineData(AttentionReason.NeedsInteraction, "🔔")]
    [InlineData(AttentionReason.ReadyForMore, "📋")]
    public void BuildMessage_AllReasons_HaveCorrectEmoji(AttentionReason reason, string expectedEmoji)
    {
        var payload = new AttentionNeededPayload
        {
            SessionName = "Test",
            Reason = reason,
            Summary = "Test summary"
        };

        var (_, body) = NotificationMessageBuilder.BuildMessage(payload);

        Assert.StartsWith(expectedEmoji, body);
    }
}
