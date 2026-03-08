using PolyPilot.Models;

namespace PolyPilot.Tests;

public class AgentSessionInfoTests
{
    [Fact]
    public void NewSession_HasEmptyHistoryAndQueue()
    {
        var session = new AgentSessionInfo { Name = "test", Model = "gpt-5" };

        Assert.Empty(session.History);
        Assert.Empty(session.MessageQueue);
        Assert.Equal(0, session.MessageCount);
        Assert.False(session.IsProcessing);
    }

    [Fact]
    public void NewSession_HasDefaultProcessingStatusFields()
    {
        var session = new AgentSessionInfo { Name = "test", Model = "gpt-5" };

        Assert.Null(session.ProcessingStartedAt);
        Assert.Equal(0, session.ToolCallCount);
        Assert.Equal(0, session.ProcessingPhase);
    }

    [Fact]
    public void ProcessingStatusFields_CanBeSetAndCleared()
    {
        var session = new AgentSessionInfo { Name = "test", Model = "gpt-5" };

        session.ProcessingStartedAt = DateTime.UtcNow;
        session.ToolCallCount = 5;
        session.ProcessingPhase = 3;

        Assert.NotNull(session.ProcessingStartedAt);
        Assert.Equal(5, session.ToolCallCount);
        Assert.Equal(3, session.ProcessingPhase);

        // Clear (as abort/complete would)
        session.ProcessingStartedAt = null;
        session.ToolCallCount = 0;
        session.ProcessingPhase = 0;

        Assert.Null(session.ProcessingStartedAt);
        Assert.Equal(0, session.ToolCallCount);
        Assert.Equal(0, session.ProcessingPhase);
    }

    [Fact]
    public void History_CanAddMessages()
    {
        var session = new AgentSessionInfo { Name = "test", Model = "gpt-5" };
        session.History.Add(ChatMessage.UserMessage("hello"));
        session.History.Add(ChatMessage.AssistantMessage("hi"));

        Assert.Equal(2, session.History.Count);
        Assert.True(session.History[0].IsUser);
        Assert.True(session.History[1].IsAssistant);
    }

    [Fact]
    public void MessageQueue_CanEnqueueAndDequeue()
    {
        var session = new AgentSessionInfo { Name = "test", Model = "gpt-5" };
        session.MessageQueue.Add("first");
        session.MessageQueue.Add("second");

        Assert.Equal(2, session.MessageQueue.Count);
        Assert.Equal("first", session.MessageQueue[0]);

        session.MessageQueue.RemoveAt(0);
        Assert.Single(session.MessageQueue);
        Assert.Equal("second", session.MessageQueue[0]);
    }

    [Fact]
    public void Properties_CanBeSet()
    {
        var now = DateTime.Now;
        var session = new AgentSessionInfo
        {
            Name = "my-session",
            Model = "claude-opus-4.6",
            CreatedAt = now,
            WorkingDirectory = "/tmp/project",
            SessionId = "abc-123",
            IsResumed = true
        };

        Assert.Equal("my-session", session.Name);
        Assert.Equal("claude-opus-4.6", session.Model);
        Assert.Equal(now, session.CreatedAt);
        Assert.Equal("/tmp/project", session.WorkingDirectory);
        Assert.Equal("abc-123", session.SessionId);
        Assert.True(session.IsResumed);
    }

    [Fact]
    public void Name_IsMutable()
    {
        var session = new AgentSessionInfo { Name = "old", Model = "gpt-5" };
        session.Name = "new";
        Assert.Equal("new", session.Name);
    }

    [Fact]
    public void LastUpdatedAt_DefaultsToNow()
    {
        var before = DateTime.Now;
        var session = new AgentSessionInfo { Name = "test", Model = "gpt-5" };
        var after = DateTime.Now;

        Assert.InRange(session.LastUpdatedAt, before, after);
    }

    [Fact]
    public void SessionId_DefaultsToNull()
    {
        var session = new AgentSessionInfo { Name = "test", Model = "gpt-5" };
        Assert.Null(session.SessionId);
        Assert.False(session.IsResumed);
    }

    [Fact]
    public void UnreadCount_HandlesNullMessagesInHistory()
    {
        var session = new AgentSessionInfo { Name = "test", Model = "gpt-5" };
        session.History.Add(ChatMessage.UserMessage("hello"));
        session.History.Add(null!); // Simulate corrupt null entry
        session.History.Add(ChatMessage.AssistantMessage("hi"));
        session.LastReadMessageCount = 0;

        // Should not throw, and should count only non-null assistant messages
        var count = session.UnreadCount;
        Assert.Equal(1, count);
    }

    [Fact]
    public void UnreadCount_ReturnsZeroForEmptyHistory()
    {
        var session = new AgentSessionInfo { Name = "test", Model = "gpt-5" };
        Assert.Equal(0, session.UnreadCount);
    }

    [Fact]
    public void UnreadCount_CountsOnlyAssistantMessagesAfterLastRead()
    {
        var session = new AgentSessionInfo { Name = "test", Model = "gpt-5" };
        session.History.Add(ChatMessage.UserMessage("hello"));
        session.History.Add(ChatMessage.AssistantMessage("hi"));
        session.LastReadMessageCount = 2;
        session.History.Add(ChatMessage.UserMessage("more"));
        session.History.Add(ChatMessage.AssistantMessage("reply1"));
        session.History.Add(ChatMessage.AssistantMessage("reply2"));

        Assert.Equal(2, session.UnreadCount);
    }

    [Fact]
    public void UnreadCount_HandlesLastReadBeyondHistory()
    {
        var session = new AgentSessionInfo { Name = "test", Model = "gpt-5" };
        session.History.Add(ChatMessage.AssistantMessage("hi"));
        session.LastReadMessageCount = 100; // Beyond history length

        Assert.Equal(0, session.UnreadCount);
    }

    [Fact]
    public void IsCreating_DefaultsToFalse()
    {
        var session = new AgentSessionInfo { Name = "test", Model = "gpt-5" };
        Assert.False(session.IsCreating);
    }

    [Fact]
    public void IsCreating_CanBeSetAndCleared()
    {
        var session = new AgentSessionInfo { Name = "test", Model = "gpt-5", IsCreating = true };
        Assert.True(session.IsCreating);

        session.IsCreating = false;
        Assert.False(session.IsCreating);
    }

    [Fact]
    public void PermissionDenialCount_DefaultsToZero()
    {
        var info = new AgentSessionInfo { Name = "test", Model = "gpt-5" };
        Assert.Equal(0, info.PermissionDenialCount);
        Assert.False(info.HasPermissionIssue);
    }

    [Fact]
    public void HasPermissionIssue_TrueWhenSlidingWindowReachesThreshold()
    {
        var info = new AgentSessionInfo { Name = "test", Model = "gpt-5" };
        info.RecordToolResult(true);  // denial 1
        info.RecordToolResult(true);  // denial 2
        Assert.False(info.HasPermissionIssue);
        info.RecordToolResult(true);  // denial 3 — triggers
        Assert.True(info.HasPermissionIssue);
    }

    [Fact]
    public void HasPermissionIssue_SlidingWindowToleratesOccasionalOk()
    {
        var info = new AgentSessionInfo { Name = "test", Model = "gpt-5" };
        info.RecordToolResult(true);   // denial
        info.RecordToolResult(true);   // denial
        info.RecordToolResult(false);  // ok — would have reset old consecutive logic
        info.RecordToolResult(true);   // denial — window now has 3/4 denials
        Assert.True(info.HasPermissionIssue);  // should be true with sliding window
    }

    [Fact]
    public void HasPermissionIssue_FalseAfterClear()
    {
        var info = new AgentSessionInfo { Name = "test", Model = "gpt-5" };
        info.RecordToolResult(true);
        info.RecordToolResult(true);
        info.RecordToolResult(true);
        Assert.True(info.HasPermissionIssue);
        info.ClearPermissionDenials();
        Assert.False(info.HasPermissionIssue);
    }

    [Fact]
    public void SlidingWindow_OldEntriesDropOff()
    {
        var info = new AgentSessionInfo { Name = "test", Model = "gpt-5" };
        info.RecordToolResult(true);   // denial
        info.RecordToolResult(true);   // denial
        info.RecordToolResult(true);   // denial — 3/3
        Assert.True(info.HasPermissionIssue);
        // Push 3 OKs to push denials out of window (size 5)
        info.RecordToolResult(false);  // 3/4
        info.RecordToolResult(false);  // 3/5
        info.RecordToolResult(false);  // 2/5 (oldest denial drops)
        Assert.False(info.HasPermissionIssue);
    }
}
