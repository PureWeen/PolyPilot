using System.Text.Json;
using PolyPilot.Models;

namespace PolyPilot.Tests;

public class BridgeMessageTests
{
    [Fact]
    public void Create_SerializesPayload()
    {
        var payload = new SessionNamePayload { SessionName = "test-session" };
        var msg = BridgeMessage.Create(BridgeMessageTypes.CloseSession, payload);

        Assert.Equal(BridgeMessageTypes.CloseSession, msg.Type);
        Assert.NotNull(msg.Payload);
    }

    [Fact]
    public void GetPayload_DeserializesCorrectly()
    {
        var original = new SendMessagePayload { SessionName = "s1", Message = "hello" };
        var msg = BridgeMessage.Create(BridgeMessageTypes.SendMessage, original);

        var deserialized = msg.GetPayload<SendMessagePayload>();

        Assert.NotNull(deserialized);
        Assert.Equal("s1", deserialized!.SessionName);
        Assert.Equal("hello", deserialized.Message);
    }

    [Fact]
    public void Serialize_Deserialize_RoundTrip()
    {
        var payload = new CreateSessionPayload
        {
            Name = "my-session",
            Model = "claude-sonnet-4.5",
            WorkingDirectory = "/tmp"
        };
        var original = BridgeMessage.Create(BridgeMessageTypes.CreateSession, payload);

        var json = original.Serialize();
        var restored = BridgeMessage.Deserialize(json);

        Assert.NotNull(restored);
        Assert.Equal(BridgeMessageTypes.CreateSession, restored!.Type);

        var restoredPayload = restored.GetPayload<CreateSessionPayload>();
        Assert.NotNull(restoredPayload);
        Assert.Equal("my-session", restoredPayload!.Name);
        Assert.Equal("claude-sonnet-4.5", restoredPayload.Model);
        Assert.Equal("/tmp", restoredPayload.WorkingDirectory);
    }

    [Fact]
    public void Deserialize_InvalidJson_ReturnsNull()
    {
        var result = BridgeMessage.Deserialize("not json");
        Assert.Null(result);
    }

    [Fact]
    public void Deserialize_EmptyObject_ReturnsMessageWithEmptyType()
    {
        var result = BridgeMessage.Deserialize("{}");
        Assert.NotNull(result);
        Assert.Equal("", result!.Type);
        Assert.Null(result.Payload);
    }

    [Fact]
    public void GetPayload_NullPayload_ReturnsDefault()
    {
        var msg = new BridgeMessage { Type = "test" };
        var result = msg.GetPayload<SessionNamePayload>();
        Assert.Null(result);
    }

    [Fact]
    public void Serialize_CamelCaseNaming()
    {
        var payload = new SessionSummary
        {
            Name = "s1",
            Model = "gpt-5",
            IsProcessing = true,
            MessageCount = 5,
            QueueCount = 2
        };
        var msg = BridgeMessage.Create(BridgeMessageTypes.SessionsList, payload);
        var json = msg.Serialize();

        // Verify camelCase property names in serialized output
        Assert.Contains("\"type\"", json);
        Assert.Contains("\"payload\"", json);
        Assert.Contains("\"name\"", json);
        Assert.Contains("\"model\"", json);
        Assert.Contains("\"isProcessing\"", json);
        Assert.Contains("\"messageCount\"", json);

        // Verify null values are excluded (JsonIgnoreCondition.WhenWritingNull)
        Assert.DoesNotContain("\"sessionId\"", json);
        Assert.DoesNotContain("\"workingDirectory\"", json);
    }

    [Fact]
    public void BridgeJson_CaseInsensitiveDeserialization()
    {
        var json = """{"Type":"test","Payload":null}""";
        var msg = JsonSerializer.Deserialize<BridgeMessage>(json, BridgeJson.Options);

        Assert.NotNull(msg);
        Assert.Equal("test", msg!.Type);
    }
}

public class BridgeMessageTypesTests
{
    [Fact]
    public void ServerToClient_TypeConstants_AreCorrect()
    {
        Assert.Equal("sessions_list", BridgeMessageTypes.SessionsList);
        Assert.Equal("session_history", BridgeMessageTypes.SessionHistory);
        Assert.Equal("content_delta", BridgeMessageTypes.ContentDelta);
        Assert.Equal("tool_started", BridgeMessageTypes.ToolStarted);
        Assert.Equal("tool_completed", BridgeMessageTypes.ToolCompleted);
        Assert.Equal("reasoning_delta", BridgeMessageTypes.ReasoningDelta);
        Assert.Equal("reasoning_complete", BridgeMessageTypes.ReasoningComplete);
        Assert.Equal("intent_changed", BridgeMessageTypes.IntentChanged);
        Assert.Equal("usage_info", BridgeMessageTypes.UsageInfo);
        Assert.Equal("turn_start", BridgeMessageTypes.TurnStart);
        Assert.Equal("turn_end", BridgeMessageTypes.TurnEnd);
        Assert.Equal("session_complete", BridgeMessageTypes.SessionComplete);
        Assert.Equal("error", BridgeMessageTypes.ErrorEvent);
        Assert.Equal("persisted_sessions", BridgeMessageTypes.PersistedSessionsList);
        Assert.Equal("attention_needed", BridgeMessageTypes.AttentionNeeded);
    }

    [Fact]
    public void ClientToServer_TypeConstants_AreCorrect()
    {
        Assert.Equal("get_sessions", BridgeMessageTypes.GetSessions);
        Assert.Equal("get_history", BridgeMessageTypes.GetHistory);
        Assert.Equal("get_persisted_sessions", BridgeMessageTypes.GetPersistedSessions);
        Assert.Equal("send_message", BridgeMessageTypes.SendMessage);
        Assert.Equal("create_session", BridgeMessageTypes.CreateSession);
        Assert.Equal("resume_session", BridgeMessageTypes.ResumeSession);
        Assert.Equal("switch_session", BridgeMessageTypes.SwitchSession);
        Assert.Equal("queue_message", BridgeMessageTypes.QueueMessage);
        Assert.Equal("close_session", BridgeMessageTypes.CloseSession);
        Assert.Equal("abort_session", BridgeMessageTypes.AbortSession);
        Assert.Equal("list_directories", BridgeMessageTypes.ListDirectories);
    }

    [Fact]
    public void DirectoriesList_TypeConstant_IsCorrect()
    {
        Assert.Equal("directories_list", BridgeMessageTypes.DirectoriesList);
    }
}

public class BridgePayloadTests
{
    [Fact]
    public void SessionsListPayload_RoundTrip()
    {
        var payload = new SessionsListPayload
        {
            ActiveSession = "main",
            Sessions = new List<SessionSummary>
            {
                new()
                {
                    Name = "main",
                    Model = "claude-opus-4.6",
                    CreatedAt = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                    MessageCount = 10,
                    IsProcessing = false,
                    SessionId = "abc-123",
                    QueueCount = 0
                },
                new()
                {
                    Name = "worker",
                    Model = "gpt-5",
                    CreatedAt = new DateTime(2025, 1, 1, 13, 0, 0, DateTimeKind.Utc),
                    MessageCount = 3,
                    IsProcessing = true,
                    QueueCount = 2
                }
            }
        };

        var msg = BridgeMessage.Create(BridgeMessageTypes.SessionsList, payload);
        var json = msg.Serialize();
        var restored = BridgeMessage.Deserialize(json);
        var restoredPayload = restored!.GetPayload<SessionsListPayload>();

        Assert.NotNull(restoredPayload);
        Assert.Equal("main", restoredPayload!.ActiveSession);
        Assert.Equal(2, restoredPayload.Sessions.Count);
        Assert.Equal("claude-opus-4.6", restoredPayload.Sessions[0].Model);
        Assert.True(restoredPayload.Sessions[1].IsProcessing);
        Assert.Equal(2, restoredPayload.Sessions[1].QueueCount);
    }

    [Fact]
    public void ContentDeltaPayload_RoundTrip()
    {
        var payload = new ContentDeltaPayload { SessionName = "s1", Content = "Hello **world**" };
        var msg = BridgeMessage.Create(BridgeMessageTypes.ContentDelta, payload);
        var json = msg.Serialize();
        var restored = BridgeMessage.Deserialize(json)!.GetPayload<ContentDeltaPayload>();

        Assert.Equal("s1", restored!.SessionName);
        Assert.Equal("Hello **world**", restored.Content);
    }

    [Fact]
    public void ToolStartedPayload_RoundTrip()
    {
        var payload = new ToolStartedPayload { SessionName = "s1", ToolName = "bash", CallId = "c-1" };
        var msg = BridgeMessage.Create(BridgeMessageTypes.ToolStarted, payload);
        var restored = BridgeMessage.Deserialize(msg.Serialize())!.GetPayload<ToolStartedPayload>();

        Assert.Equal("bash", restored!.ToolName);
        Assert.Equal("c-1", restored.CallId);
    }

    [Fact]
    public void ToolCompletedPayload_RoundTrip()
    {
        var payload = new ToolCompletedPayload
        {
            SessionName = "s1",
            CallId = "c-1",
            Result = "exit code 0",
            Success = true
        };
        var msg = BridgeMessage.Create(BridgeMessageTypes.ToolCompleted, payload);
        var restored = BridgeMessage.Deserialize(msg.Serialize())!.GetPayload<ToolCompletedPayload>();

        Assert.True(restored!.Success);
        Assert.Equal("exit code 0", restored.Result);
    }

    [Fact]
    public void UsageInfoPayload_RoundTrip()
    {
        var payload = new UsageInfoPayload
        {
            SessionName = "s1",
            Model = "claude-opus-4.6",
            CurrentTokens = 5000,
            TokenLimit = 200000,
            InputTokens = 3000,
            OutputTokens = 2000
        };
        var msg = BridgeMessage.Create(BridgeMessageTypes.UsageInfo, payload);
        var restored = BridgeMessage.Deserialize(msg.Serialize())!.GetPayload<UsageInfoPayload>();

        Assert.Equal("claude-opus-4.6", restored!.Model);
        Assert.Equal(5000, restored.CurrentTokens);
        Assert.Equal(200000, restored.TokenLimit);
    }

    [Fact]
    public void ErrorPayload_RoundTrip()
    {
        var payload = new ErrorPayload { SessionName = "s1", Error = "Connection lost" };
        var msg = BridgeMessage.Create(BridgeMessageTypes.ErrorEvent, payload);
        var restored = BridgeMessage.Deserialize(msg.Serialize())!.GetPayload<ErrorPayload>();

        Assert.Equal("Connection lost", restored!.Error);
    }

    [Fact]
    public void ResumeSessionPayload_RoundTrip()
    {
        var payload = new ResumeSessionPayload
        {
            SessionId = "abc-def-123",
            DisplayName = "My Session"
        };
        var msg = BridgeMessage.Create(BridgeMessageTypes.ResumeSession, payload);
        var restored = BridgeMessage.Deserialize(msg.Serialize())!.GetPayload<ResumeSessionPayload>();

        Assert.Equal("abc-def-123", restored!.SessionId);
        Assert.Equal("My Session", restored.DisplayName);
    }

    [Fact]
    public void QueueMessagePayload_RoundTrip()
    {
        var payload = new QueueMessagePayload { SessionName = "s1", Message = "do something" };
        var msg = BridgeMessage.Create(BridgeMessageTypes.QueueMessage, payload);
        var restored = BridgeMessage.Deserialize(msg.Serialize())!.GetPayload<QueueMessagePayload>();

        Assert.Equal("do something", restored!.Message);
    }

    [Fact]
    public void PersistedSessionsPayload_RoundTrip()
    {
        var payload = new PersistedSessionsPayload
        {
            Sessions = new List<PersistedSessionSummary>
            {
                new()
                {
                    SessionId = "guid-1",
                    Title = "First session",
                    Preview = "Hello, can you help me...",
                    WorkingDirectory = "/Users/test/project",
                    LastModified = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc)
                }
            }
        };
        var msg = BridgeMessage.Create(BridgeMessageTypes.PersistedSessionsList, payload);
        var restored = BridgeMessage.Deserialize(msg.Serialize())!.GetPayload<PersistedSessionsPayload>();

        Assert.Single(restored!.Sessions);
        Assert.Equal("First session", restored.Sessions[0].Title);
        Assert.Equal("/Users/test/project", restored.Sessions[0].WorkingDirectory);
    }

    [Fact]
    public void ListDirectoriesPayload_RoundTrip()
    {
        var payload = new ListDirectoriesPayload { Path = "/Users/test" };
        var msg = BridgeMessage.Create(BridgeMessageTypes.ListDirectories, payload);
        var json = msg.Serialize();
        var restored = BridgeMessage.Deserialize(json)!.GetPayload<ListDirectoriesPayload>();

        Assert.Equal("/Users/test", restored!.Path);
    }

    [Fact]
    public void ListDirectoriesPayload_NullPath_RoundTrip()
    {
        var payload = new ListDirectoriesPayload { Path = null };
        var msg = BridgeMessage.Create(BridgeMessageTypes.ListDirectories, payload);
        var restored = BridgeMessage.Deserialize(msg.Serialize())!.GetPayload<ListDirectoriesPayload>();

        Assert.Null(restored!.Path);
    }

    [Fact]
    public void DirectoriesListPayload_RoundTrip()
    {
        var payload = new DirectoriesListPayload
        {
            Path = "/Users/test",
            IsGitRepo = false,
            Directories = new List<DirectoryEntry>
            {
                new() { Name = "work", IsGitRepo = false },
                new() { Name = "my-project", IsGitRepo = true }
            }
        };
        var msg = BridgeMessage.Create(BridgeMessageTypes.DirectoriesList, payload);
        var restored = BridgeMessage.Deserialize(msg.Serialize())!.GetPayload<DirectoriesListPayload>();

        Assert.Equal("/Users/test", restored!.Path);
        Assert.False(restored.IsGitRepo);
        Assert.Null(restored.Error);
        Assert.Equal(2, restored.Directories.Count);
        Assert.Equal("work", restored.Directories[0].Name);
        Assert.False(restored.Directories[0].IsGitRepo);
        Assert.Equal("my-project", restored.Directories[1].Name);
        Assert.True(restored.Directories[1].IsGitRepo);
    }

    [Fact]
    public void DirectoriesListPayload_WithError_RoundTrip()
    {
        var payload = new DirectoriesListPayload
        {
            Path = "/nonexistent",
            Error = "Directory not found"
        };
        var msg = BridgeMessage.Create(BridgeMessageTypes.DirectoriesList, payload);
        var restored = BridgeMessage.Deserialize(msg.Serialize())!.GetPayload<DirectoriesListPayload>();

        Assert.Equal("/nonexistent", restored!.Path);
        Assert.Equal("Directory not found", restored.Error);
        Assert.Empty(restored.Directories);
    }

    [Fact]
    public void AttentionNeededPayload_RoundTrip()
    {
        var payload = new AttentionNeededPayload
        {
            SessionName = "test-session",
            SessionId = "abc-123",
            Reason = AttentionReason.Completed,
            Summary = "Task completed successfully"
        };
        var msg = BridgeMessage.Create(BridgeMessageTypes.AttentionNeeded, payload);
        var json = msg.Serialize();
        var restored = BridgeMessage.Deserialize(json)!.GetPayload<AttentionNeededPayload>();

        Assert.Equal("test-session", restored!.SessionName);
        Assert.Equal("abc-123", restored.SessionId);
        Assert.Equal(AttentionReason.Completed, restored.Reason);
        Assert.Equal("Task completed successfully", restored.Summary);
    }

    [Theory]
    [InlineData(AttentionReason.Completed)]
    [InlineData(AttentionReason.Error)]
    [InlineData(AttentionReason.NeedsInteraction)]
    [InlineData(AttentionReason.ReadyForMore)]
    public void AttentionNeededPayload_AllReasons_RoundTrip(AttentionReason reason)
    {
        var payload = new AttentionNeededPayload
        {
            SessionName = "s1",
            Reason = reason,
            Summary = "test"
        };
        var msg = BridgeMessage.Create(BridgeMessageTypes.AttentionNeeded, payload);
        var restored = BridgeMessage.Deserialize(msg.Serialize())!.GetPayload<AttentionNeededPayload>();

        Assert.Equal(reason, restored!.Reason);
    }
}
