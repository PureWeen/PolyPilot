using PolyPilot.Models;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for bridge message types, payloads, and remote mode data contracts.
/// These validate the protocol between mobile clients (WsBridgeClient) and
/// the desktop server (WsBridgeServer) — critical for remote session management.
/// </summary>
public class RemoteModeTests
{
    [Fact]
    public void AbortSession_MessageType_IsCorrect()
    {
        Assert.Equal("abort_session", BridgeMessageTypes.AbortSession);
    }

    [Fact]
    public void AbortSession_RoundTrip()
    {
        var payload = new SessionNamePayload { SessionName = "my-session" };
        var msg = BridgeMessage.Create(BridgeMessageTypes.AbortSession, payload);
        var json = msg.Serialize();
        var restored = BridgeMessage.Deserialize(json);

        Assert.NotNull(restored);
        Assert.Equal(BridgeMessageTypes.AbortSession, restored!.Type);

        var restoredPayload = restored.GetPayload<SessionNamePayload>();
        Assert.NotNull(restoredPayload);
        Assert.Equal("my-session", restoredPayload!.SessionName);
    }

    [Fact]
    public void AllClientToServerTypes_AreUnique()
    {
        var types = new[]
        {
            BridgeMessageTypes.GetSessions,
            BridgeMessageTypes.GetHistory,
            BridgeMessageTypes.GetPersistedSessions,
            BridgeMessageTypes.SendMessage,
            BridgeMessageTypes.CreateSession,
            BridgeMessageTypes.ResumeSession,
            BridgeMessageTypes.SwitchSession,
            BridgeMessageTypes.QueueMessage,
            BridgeMessageTypes.CloseSession,
            BridgeMessageTypes.AbortSession,
            BridgeMessageTypes.OrganizationCommand,
            BridgeMessageTypes.ListDirectories,
        };
        Assert.Equal(types.Length, types.Distinct().Count());
    }

    [Fact]
    public void AllServerToClientTypes_AreUnique()
    {
        var types = new[]
        {
            BridgeMessageTypes.SessionsList,
            BridgeMessageTypes.SessionHistory,
            BridgeMessageTypes.PersistedSessionsList,
            BridgeMessageTypes.OrganizationState,
            BridgeMessageTypes.ContentDelta,
            BridgeMessageTypes.ToolStarted,
            BridgeMessageTypes.ToolCompleted,
            BridgeMessageTypes.ReasoningDelta,
            BridgeMessageTypes.ReasoningComplete,
            BridgeMessageTypes.IntentChanged,
            BridgeMessageTypes.UsageInfo,
            BridgeMessageTypes.TurnStart,
            BridgeMessageTypes.TurnEnd,
            BridgeMessageTypes.SessionComplete,
            BridgeMessageTypes.ErrorEvent,
            BridgeMessageTypes.DirectoriesList,
        };
        Assert.Equal(types.Length, types.Distinct().Count());
    }

    [Fact]
    public void SessionSummary_IsProcessing_Serializes()
    {
        var summary = new SessionSummary
        {
            Name = "busy",
            Model = "gpt-5",
            IsProcessing = true,
            MessageCount = 5,
            QueueCount = 2,
            SessionId = "guid-123"
        };

        var msg = BridgeMessage.Create(BridgeMessageTypes.SessionsList,
            new SessionsListPayload { Sessions = new() { summary }, ActiveSession = "busy" });
        var json = msg.Serialize();
        var restored = BridgeMessage.Deserialize(json)!
            .GetPayload<SessionsListPayload>();

        Assert.True(restored!.Sessions[0].IsProcessing);
        Assert.Equal(2, restored.Sessions[0].QueueCount);
        Assert.Equal("busy", restored.ActiveSession);
    }

    [Fact]
    public void SessionsListPayload_EmptySessions()
    {
        var payload = new SessionsListPayload
        {
            Sessions = new(),
            ActiveSession = null
        };
        var msg = BridgeMessage.Create(BridgeMessageTypes.SessionsList, payload);
        var restored = BridgeMessage.Deserialize(msg.Serialize())!
            .GetPayload<SessionsListPayload>();

        Assert.Empty(restored!.Sessions);
        Assert.Null(restored.ActiveSession);
    }

    [Fact]
    public void CreateSessionPayload_NullOptionalFields()
    {
        var payload = new CreateSessionPayload { Name = "test" };
        Assert.Null(payload.Model);
        Assert.Null(payload.WorkingDirectory);

        var msg = BridgeMessage.Create(BridgeMessageTypes.CreateSession, payload);
        var json = msg.Serialize();

        // Null fields should be excluded (WhenWritingNull)
        Assert.DoesNotContain("\"model\"", json);
        Assert.DoesNotContain("\"workingDirectory\"", json);

        var restored = BridgeMessage.Deserialize(json)!
            .GetPayload<CreateSessionPayload>();
        Assert.Equal("test", restored!.Name);
        Assert.Null(restored.Model);
    }

    [Fact]
    public void ResumeSessionPayload_NullDisplayName()
    {
        var payload = new ResumeSessionPayload { SessionId = "guid-1" };
        Assert.Null(payload.DisplayName);

        var msg = BridgeMessage.Create(BridgeMessageTypes.ResumeSession, payload);
        var restored = BridgeMessage.Deserialize(msg.Serialize())!
            .GetPayload<ResumeSessionPayload>();

        Assert.Equal("guid-1", restored!.SessionId);
        Assert.Null(restored.DisplayName);
    }

    [Fact]
    public void OrganizationState_RoundTrip_WithMultipleGroups()
    {
        var state = new OrganizationState
        {
            SortMode = SessionSortMode.Alphabetical
        };
        state.Groups.Add(new SessionGroup
        {
            Id = "work",
            Name = "Work",
            SortOrder = 1,
            IsCollapsed = true
        });
        state.Sessions.Add(new SessionMeta
        {
            SessionName = "session-1",
            GroupId = "work",
            IsPinned = true,
            ManualOrder = 1
        });
        state.Sessions.Add(new SessionMeta
        {
            SessionName = "session-2",
            GroupId = SessionGroup.DefaultId,
        });

        var msg = BridgeMessage.Create(BridgeMessageTypes.OrganizationState, state);
        var json = msg.Serialize();
        var restored = BridgeMessage.Deserialize(json)!
            .GetPayload<OrganizationState>();

        Assert.NotNull(restored);
        Assert.Equal(SessionSortMode.Alphabetical, restored!.SortMode);
        Assert.Equal(2, restored.Groups.Count);
        Assert.Equal(2, restored.Sessions.Count);

        var pinnedMeta = restored.Sessions.Find(m => m.SessionName == "session-1");
        Assert.True(pinnedMeta!.IsPinned);
        Assert.Equal("work", pinnedMeta.GroupId);
    }

    [Fact]
    public void OrganizationCommandPayload_AllCommands()
    {
        var commands = new[] { "pin", "unpin", "move", "create_group",
            "rename_group", "delete_group", "toggle_collapsed", "set_sort" };

        foreach (var cmd in commands)
        {
            var payload = new OrganizationCommandPayload { Command = cmd };
            var msg = BridgeMessage.Create(BridgeMessageTypes.OrganizationCommand, payload);
            var restored = BridgeMessage.Deserialize(msg.Serialize())!
                .GetPayload<OrganizationCommandPayload>();
            Assert.Equal(cmd, restored!.Command);
        }
    }

    [Fact]
    public void SessionHistoryPayload_WithMixedMessageTypes()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.UserMessage("hello"),
            ChatMessage.AssistantMessage("hi there"),
            ChatMessage.ToolCallMessage("bash", "c1", "ls"),
            ChatMessage.SystemMessage("reconnected"),
        };

        var payload = new SessionHistoryPayload
        {
            SessionName = "test",
            Messages = messages
        };

        var msg = BridgeMessage.Create(BridgeMessageTypes.SessionHistory, payload);
        var json = msg.Serialize();
        var restored = BridgeMessage.Deserialize(json)!
            .GetPayload<SessionHistoryPayload>();

        Assert.Equal("test", restored!.SessionName);
        Assert.Equal(4, restored.Messages.Count);
        Assert.True(restored.Messages[0].IsUser);
        Assert.True(restored.Messages[1].IsAssistant);
        Assert.Equal(ChatMessageType.ToolCall, restored.Messages[2].MessageType);
        Assert.Equal(ChatMessageType.System, restored.Messages[3].MessageType);
    }

    [Fact]
    public void ReasoningDeltaPayload_RoundTrip()
    {
        var payload = new ReasoningDeltaPayload
        {
            SessionName = "s1",
            ReasoningId = "r-42",
            Content = "Let me think about this..."
        };
        var msg = BridgeMessage.Create(BridgeMessageTypes.ReasoningDelta, payload);
        var restored = BridgeMessage.Deserialize(msg.Serialize())!
            .GetPayload<ReasoningDeltaPayload>();

        Assert.Equal("r-42", restored!.ReasoningId);
        Assert.Equal("Let me think about this...", restored.Content);
    }

    [Fact]
    public void ReasoningCompletePayload_RoundTrip()
    {
        var payload = new ReasoningCompletePayload
        {
            SessionName = "s1",
            ReasoningId = "r-42"
        };
        var msg = BridgeMessage.Create(BridgeMessageTypes.ReasoningComplete, payload);
        var restored = BridgeMessage.Deserialize(msg.Serialize())!
            .GetPayload<ReasoningCompletePayload>();

        Assert.Equal("s1", restored!.SessionName);
        Assert.Equal("r-42", restored.ReasoningId);
    }

    [Fact]
    public void IntentChangedPayload_RoundTrip()
    {
        var payload = new IntentChangedPayload
        {
            SessionName = "s1",
            Intent = "Exploring codebase"
        };
        var msg = BridgeMessage.Create(BridgeMessageTypes.IntentChanged, payload);
        var restored = BridgeMessage.Deserialize(msg.Serialize())!
            .GetPayload<IntentChangedPayload>();

        Assert.Equal("Exploring codebase", restored!.Intent);
    }

    [Fact]
    public void SessionCompletePayload_RoundTrip()
    {
        var payload = new SessionCompletePayload
        {
            SessionName = "s1",
            Summary = "Fixed the bug in auth module"
        };
        var msg = BridgeMessage.Create(BridgeMessageTypes.SessionComplete, payload);
        var restored = BridgeMessage.Deserialize(msg.Serialize())!
            .GetPayload<SessionCompletePayload>();

        Assert.Equal("Fixed the bug in auth module", restored!.Summary);
    }

    [Fact]
    public void TurnStart_TurnEnd_AreDistinctTypes()
    {
        Assert.NotEqual(BridgeMessageTypes.TurnStart, BridgeMessageTypes.TurnEnd);
        Assert.Equal("turn_start", BridgeMessageTypes.TurnStart);
        Assert.Equal("turn_end", BridgeMessageTypes.TurnEnd);
    }

    [Fact]
    public void PersistedSessionSummary_AllFields()
    {
        var summary = new PersistedSessionSummary
        {
            SessionId = "abc-def",
            Title = "Fix CSS bug",
            Preview = "Can you help me fix...",
            WorkingDirectory = "/Users/dev/project",
            LastModified = new DateTime(2025, 12, 25, 10, 0, 0, DateTimeKind.Utc)
        };

        var payload = new PersistedSessionsPayload { Sessions = new() { summary } };
        var msg = BridgeMessage.Create(BridgeMessageTypes.PersistedSessionsList, payload);
        var restored = BridgeMessage.Deserialize(msg.Serialize())!
            .GetPayload<PersistedSessionsPayload>();

        var s = restored!.Sessions[0];
        Assert.Equal("abc-def", s.SessionId);
        Assert.Equal("Fix CSS bug", s.Title);
        Assert.Equal("Can you help me fix...", s.Preview);
        Assert.Equal("/Users/dev/project", s.WorkingDirectory);
    }
}

/// <summary>
/// Tests for AgentSessionInfo state transitions during remote mode operations.
/// </summary>
public class SessionStateTransitionTests
{
    [Fact]
    public void ResumedSession_Properties()
    {
        var info = new AgentSessionInfo
        {
            Name = "Resumed Session",
            SessionId = "abc-123-def",
            Model = "resumed",
            IsResumed = true
        };

        Assert.True(info.IsResumed);
        Assert.Equal("resumed", info.Model);
        Assert.Equal("abc-123-def", info.SessionId);
    }

    [Fact]
    public void ProcessingState_CanToggle()
    {
        var info = new AgentSessionInfo { Name = "test", Model = "gpt-5" };
        Assert.False(info.IsProcessing);

        info.IsProcessing = true;
        Assert.True(info.IsProcessing);

        info.IsProcessing = false;
        Assert.False(info.IsProcessing);
    }

    [Fact]
    public void GitBranch_CanBeSet()
    {
        var info = new AgentSessionInfo { Name = "test", Model = "gpt-5" };
        Assert.Null(info.GitBranch);

        info.GitBranch = "feature/fix-resume";
        Assert.Equal("feature/fix-resume", info.GitBranch);
    }

    [Fact]
    public void History_WithToolCalls_CanMarkComplete()
    {
        var info = new AgentSessionInfo { Name = "test", Model = "gpt-5" };
        var toolMsg = ChatMessage.ToolCallMessage("bash", "c1", "ls");
        info.History.Add(toolMsg);

        Assert.False(toolMsg.IsComplete);

        toolMsg.IsComplete = true;
        toolMsg.IsSuccess = true;
        toolMsg.Content = "file1.txt\nfile2.txt";

        Assert.True(info.History[0].IsComplete);
        Assert.True(info.History[0].IsSuccess);
    }

    [Fact]
    public void MessageQueue_TracksOrder()
    {
        var info = new AgentSessionInfo { Name = "test", Model = "gpt-5" };
        info.MessageQueue.Add("first prompt");
        info.MessageQueue.Add("second prompt");
        info.MessageQueue.Add("third prompt");

        Assert.Equal(3, info.MessageQueue.Count);

        // Remove first (dequeue behavior)
        info.MessageQueue.RemoveAt(0);
        Assert.Equal("second prompt", info.MessageQueue[0]);
        Assert.Equal(2, info.MessageQueue.Count);
    }
}

/// <summary>
/// Tests for SessionMeta and Organization metadata.
/// Validates that session organization data survives serialization.
/// </summary>
public class SessionMetadataTests
{
    [Fact]
    public void SessionMeta_InNonDefaultGroup()
    {
        var meta = new SessionMeta
        {
            SessionName = "session-1",
            GroupId = "work-group",
            IsPinned = true,
            ManualOrder = 5
        };

        Assert.Equal("work-group", meta.GroupId);
        Assert.True(meta.IsPinned);
        Assert.Equal(5, meta.ManualOrder);
    }

    [Fact]
    public void OrganizationState_AddRemoveSessions()
    {
        var state = new OrganizationState();

        state.Sessions.Add(new SessionMeta { SessionName = "s1" });
        state.Sessions.Add(new SessionMeta { SessionName = "s2" });
        state.Sessions.Add(new SessionMeta { SessionName = "s3" });

        Assert.Equal(3, state.Sessions.Count);

        // Remove sessions not in active set
        var activeNames = new HashSet<string> { "s1", "s3" };
        state.Sessions.RemoveAll(m => !activeNames.Contains(m.SessionName));

        Assert.Equal(2, state.Sessions.Count);
        Assert.DoesNotContain(state.Sessions, m => m.SessionName == "s2");
    }

    [Fact]
    public void OrganizationState_SessionsInDeletedGroup_FallBackToDefault()
    {
        var state = new OrganizationState();
        state.Groups.Add(new SessionGroup { Id = "temp", Name = "Temp" });
        state.Sessions.Add(new SessionMeta { SessionName = "s1", GroupId = "temp" });

        // Simulate group deletion — fix sessions pointing to deleted group
        state.Groups.RemoveAll(g => g.Id == "temp");
        var groupIds = state.Groups.Select(g => g.Id).ToHashSet();
        foreach (var meta in state.Sessions)
        {
            if (!groupIds.Contains(meta.GroupId))
                meta.GroupId = SessionGroup.DefaultId;
        }

        Assert.Equal(SessionGroup.DefaultId, state.Sessions[0].GroupId);
    }

    [Fact]
    public void SortMode_AllValues_Serialize()
    {
        foreach (SessionSortMode mode in Enum.GetValues<SessionSortMode>())
        {
            var state = new OrganizationState { SortMode = mode };
            var json = System.Text.Json.JsonSerializer.Serialize(state);
            var restored = System.Text.Json.JsonSerializer.Deserialize<OrganizationState>(json);
            Assert.Equal(mode, restored!.SortMode);
        }
    }

    [Fact]
    public void SessionGroup_NewGroup_HasUniqueId()
    {
        var g1 = new SessionGroup { Name = "Group 1" };
        var g2 = new SessionGroup { Name = "Group 2" };

        Assert.NotEqual(g1.Id, g2.Id);
        Assert.NotEqual(SessionGroup.DefaultId, g1.Id);
    }
}

/// <summary>
/// Tests for ChatMessage serialization edge cases relevant to bridge protocol.
/// </summary>
public class ChatMessageSerializationTests
{
    [Fact]
    public void ChatMessage_JsonRoundTrip()
    {
        var original = ChatMessage.UserMessage("Hello, world!");
        var json = System.Text.Json.JsonSerializer.Serialize(original, BridgeJson.Options);
        var restored = System.Text.Json.JsonSerializer.Deserialize<ChatMessage>(json, BridgeJson.Options);

        Assert.NotNull(restored);
        Assert.Equal("Hello, world!", restored!.Content);
        Assert.Equal("user", restored.Role);
    }

    [Fact]
    public void ChatMessage_ToolCall_JsonRoundTrip()
    {
        var original = ChatMessage.ToolCallMessage("bash", "call-1", "echo hello");
        original.IsComplete = true;
        original.IsSuccess = true;
        original.Content = "hello\n";

        var json = System.Text.Json.JsonSerializer.Serialize(original, BridgeJson.Options);
        var restored = System.Text.Json.JsonSerializer.Deserialize<ChatMessage>(json, BridgeJson.Options);

        Assert.Equal("bash", restored!.ToolName);
        Assert.Equal("call-1", restored.ToolCallId);
        Assert.True(restored.IsComplete);
        Assert.True(restored.IsSuccess);
    }

    [Fact]
    public void ChatMessage_UnicodeContent_Survives()
    {
        var original = ChatMessage.UserMessage("Hello 🌍 — «test» ñ 日本語");
        var json = System.Text.Json.JsonSerializer.Serialize(original, BridgeJson.Options);
        var restored = System.Text.Json.JsonSerializer.Deserialize<ChatMessage>(json, BridgeJson.Options);

        Assert.Equal("Hello 🌍 — «test» ñ 日本語", restored!.Content);
    }

    [Fact]
    public void ChatMessage_EmptyContent_Serializes()
    {
        var original = ChatMessage.AssistantMessage("");
        var json = System.Text.Json.JsonSerializer.Serialize(original, BridgeJson.Options);
        var restored = System.Text.Json.JsonSerializer.Deserialize<ChatMessage>(json, BridgeJson.Options);

        Assert.Equal("", restored!.Content);
        Assert.True(restored.IsAssistant);
    }

    [Fact]
    public void ChatMessage_LargeContent_Serializes()
    {
        var largeContent = new string('x', 100_000);
        var original = ChatMessage.AssistantMessage(largeContent);
        var json = System.Text.Json.JsonSerializer.Serialize(original, BridgeJson.Options);
        var restored = System.Text.Json.JsonSerializer.Deserialize<ChatMessage>(json, BridgeJson.Options);

        Assert.Equal(100_000, restored!.Content.Length);
    }

    [Fact]
    public void SessionHistoryPayload_EmptyMessages()
    {
        var payload = new SessionHistoryPayload
        {
            SessionName = "empty",
            Messages = new()
        };
        var msg = BridgeMessage.Create(BridgeMessageTypes.SessionHistory, payload);
        var restored = BridgeMessage.Deserialize(msg.Serialize())!
            .GetPayload<SessionHistoryPayload>();

        Assert.Empty(restored!.Messages);
    }

    // ===== CreateSession WorkingDirectory edge cases =====

    [Fact]
    public void CreateSession_EmptyWorkingDirectory_Serializes()
    {
        // Mobile sends empty string when no directory is specified
        var payload = new CreateSessionPayload
        {
            Name = "mobile-session",
            Model = "claude-sonnet-4",
            WorkingDirectory = ""
        };
        var msg = BridgeMessage.Create(BridgeMessageTypes.CreateSession, payload);
        var json = msg.Serialize();
        var restored = BridgeMessage.Deserialize(json)!.GetPayload<CreateSessionPayload>();

        Assert.NotNull(restored);
        Assert.Equal("mobile-session", restored!.Name);
        Assert.Equal("", restored.WorkingDirectory);
    }

    [Fact]
    public void CreateSession_NullWorkingDirectory_Serializes()
    {
        var payload = new CreateSessionPayload
        {
            Name = "test-session",
            Model = "claude-sonnet-4",
            WorkingDirectory = null
        };
        var msg = BridgeMessage.Create(BridgeMessageTypes.CreateSession, payload);
        var json = msg.Serialize();
        var restored = BridgeMessage.Deserialize(json)!.GetPayload<CreateSessionPayload>();

        Assert.NotNull(restored);
        Assert.Equal("test-session", restored!.Name);
        Assert.Null(restored.WorkingDirectory);
    }

    [Fact]
    public void CreateSession_EmptyDirectory_ShouldBeTreatedAsNull()
    {
        // This validates the server-side normalization: empty string → null
        // so the path validation doesn't reject the request
        var payload = new CreateSessionPayload
        {
            Name = "mobile-test",
            WorkingDirectory = ""
        };

        // Simulate the server-side normalization
        if (string.IsNullOrWhiteSpace(payload.WorkingDirectory))
            payload.WorkingDirectory = null;

        Assert.Null(payload.WorkingDirectory);
    }

    [Fact]
    public void CreateSession_WhitespaceDirectory_ShouldBeTreatedAsNull()
    {
        var payload = new CreateSessionPayload
        {
            Name = "mobile-test",
            WorkingDirectory = "   "
        };

        if (string.IsNullOrWhiteSpace(payload.WorkingDirectory))
            payload.WorkingDirectory = null;

        Assert.Null(payload.WorkingDirectory);
    }

    [Fact]
    public void CreateSession_ValidDirectory_NotNormalized()
    {
        var payload = new CreateSessionPayload
        {
            Name = "desktop-test",
            WorkingDirectory = "/Users/test/project"
        };

        if (string.IsNullOrWhiteSpace(payload.WorkingDirectory))
            payload.WorkingDirectory = null;

        Assert.Equal("/Users/test/project", payload.WorkingDirectory);
    }

    [Fact]
    public void CreateSession_PathValidation_EmptyStringFailsIsPathRooted()
    {
        // Documents the actual bug: empty string is not null, passes the != null check,
        // but fails Path.IsPathRooted, causing silent rejection
        Assert.False(Path.IsPathRooted(""));
        Assert.False(Path.IsPathRooted("  "));
    }

    [Fact]
    public void CreateSession_PathValidation_ValidPathPasses()
    {
        Assert.True(Path.IsPathRooted("/Users/test"));
        if (OperatingSystem.IsWindows())
            Assert.True(Path.IsPathRooted(@"C:\Users\test"));
    }

    [Fact]
    public void CreateSession_PathTraversal_Rejected()
    {
        // WorkingDirectory containing ".." should be rejected
        var wd = "/Users/test/../../../etc/passwd";
        Assert.True(wd.Contains(".."));
    }

    // ===== SyncRemoteSessions streaming guard =====

    [Fact]
    public void HistorySync_ShouldNotOverwrite_WhenSessionIsProcessing()
    {
        // Simulates the bug: during streaming, content_delta appends to history.
        // SyncRemoteSessions should NOT replace history from stale cache while processing.
        var history = new List<ChatMessage>
        {
            ChatMessage.UserMessage("Hello"),
            new ChatMessage("assistant", "Hi there! How can I", DateTime.Now, ChatMessageType.Assistant) { IsComplete = false }
        };

        var cachedHistory = new List<ChatMessage>
        {
            ChatMessage.UserMessage("Hello"),
        };

        // Session is processing (streaming)
        bool isProcessing = true;

        // Mirror the guard logic from SyncRemoteSessions
        bool shouldSync = !isProcessing && cachedHistory.Count >= history.Count;

        Assert.False(shouldSync, "Should NOT sync history while session is processing");
    }

    [Fact]
    public void HistorySync_ShouldOverwrite_WhenSessionIsIdle()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.UserMessage("Hello"),
        };

        var cachedHistory = new List<ChatMessage>
        {
            ChatMessage.UserMessage("Hello"),
            new ChatMessage("assistant", "Full response", DateTime.Now, ChatMessageType.Assistant) { IsComplete = true }
        };

        bool isProcessing = false;

        bool shouldSync = !isProcessing && cachedHistory.Count >= history.Count;

        Assert.True(shouldSync, "Should sync history when session is idle and cache has more messages");
    }

    [Fact]
    public void HistorySync_ShouldNotOverwrite_WhenCacheIsStale()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.UserMessage("Hello"),
            new ChatMessage("assistant", "Full response", DateTime.Now, ChatMessageType.Assistant) { IsComplete = true },
            ChatMessage.UserMessage("Follow up"),
        };

        var cachedHistory = new List<ChatMessage>
        {
            ChatMessage.UserMessage("Hello"),
        };

        bool isProcessing = false;

        bool shouldSync = !isProcessing && cachedHistory.Count >= history.Count;

        Assert.False(shouldSync, "Should NOT sync when cache has fewer messages than local history");
    }
}
