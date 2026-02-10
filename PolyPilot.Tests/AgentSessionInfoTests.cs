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
}
