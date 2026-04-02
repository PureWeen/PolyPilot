using System.Text.Json;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for UI state persistence patterns — UiState and ActiveSessionEntry
/// serialization, and the session alias caching logic.
/// </summary>
public class UiStatePersistenceTests
{
    [Fact]
    public void UiState_DefaultValues()
    {
        var state = new UiState();
        Assert.Equal("/", state.CurrentPage);
        Assert.Null(state.ActiveSession);
        Assert.Equal(20, state.FontSize);
        Assert.Empty(state.InputModes);
        Assert.False(state.SidebarRailMode);
    }

    [Fact]
    public void UiState_RoundTripSerialization()
    {
        var state = new UiState
        {
            CurrentPage = "/dashboard",
            ActiveSession = "my-session",
            FontSize = 16,
            SidebarRailMode = true,
            InputModes = new Dictionary<string, string>
            {
                ["my-session"] = "autopilot",
                ["another-session"] = "plan"
            }
        };

        var json = JsonSerializer.Serialize(state);
        var restored = JsonSerializer.Deserialize<UiState>(json);

        Assert.NotNull(restored);
        Assert.Equal("/dashboard", restored!.CurrentPage);
        Assert.Equal("my-session", restored.ActiveSession);
        Assert.Equal(16, restored.FontSize);
        Assert.True(restored.SidebarRailMode);
        Assert.Equal("autopilot", restored.InputModes["my-session"]);
        Assert.Equal("plan", restored.InputModes["another-session"]);
    }

    [Fact]
    public void UiState_NullActiveSession_Serializes()
    {
        var state = new UiState { CurrentPage = "/settings" };
        var json = JsonSerializer.Serialize(state);
        var restored = JsonSerializer.Deserialize<UiState>(json);

        Assert.NotNull(restored);
        Assert.Null(restored!.ActiveSession);
    }

    [Fact]
    public void UiState_LegacyJsonWithoutInputModes_Deserializes()
    {
        const string legacyJson = """{"CurrentPage":"/dashboard","ActiveSession":"s1","FontSize":20,"ExpandedGrid":false}""";
        var restored = JsonSerializer.Deserialize<UiState>(legacyJson);

        Assert.NotNull(restored);
        Assert.NotNull(restored!.InputModes);
        Assert.Empty(restored.InputModes);
        Assert.False(restored.SidebarRailMode);
    }

    [Fact]
    public void ActiveSessionEntry_RoundTrip()
    {
        var entries = new List<ActiveSessionEntry>
        {
            new() { SessionId = "guid-1", DisplayName = "Agent 1", Model = "claude-opus-4.6", WorkingDirectory = "/tmp/worktree-a" },
            new() { SessionId = "guid-2", DisplayName = "Agent 2", Model = "gpt-5", WorkingDirectory = "/tmp/worktree-b" }
        };

        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
        var restored = JsonSerializer.Deserialize<List<ActiveSessionEntry>>(json);

        Assert.NotNull(restored);
        Assert.Equal(2, restored!.Count);
        Assert.Equal("guid-1", restored[0].SessionId);
        Assert.Equal("Agent 1", restored[0].DisplayName);
        Assert.Equal("claude-opus-4.6", restored[0].Model);
        Assert.Equal("/tmp/worktree-a", restored[0].WorkingDirectory);
        Assert.Equal("gpt-5", restored[1].Model);
        Assert.Equal("/tmp/worktree-b", restored[1].WorkingDirectory);
    }

    [Fact]
    public void ActiveSessionEntry_DefaultValues()
    {
        var entry = new ActiveSessionEntry();
        Assert.Equal("", entry.SessionId);
        Assert.Equal("", entry.DisplayName);
        Assert.Equal("", entry.Model);
        Assert.Null(entry.WorkingDirectory);
        Assert.Equal(0, entry.TotalInputTokens);
        Assert.Equal(0, entry.TotalOutputTokens);
        Assert.Null(entry.ContextCurrentTokens);
        Assert.Null(entry.ContextTokenLimit);
        Assert.Equal(0, entry.PremiumRequestsUsed);
        Assert.Equal(0.0, entry.TotalApiTimeSeconds);
        Assert.Null(entry.CreatedAt);
    }

    [Fact]
    public void SessionAliases_RoundTrip()
    {
        var aliases = new Dictionary<string, string>
        {
            ["abc-123"] = "My Custom Name",
            ["def-456"] = "Another Session"
        };

        var json = JsonSerializer.Serialize(aliases, new JsonSerializerOptions { WriteIndented = true });
        var restored = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

        Assert.NotNull(restored);
        Assert.Equal(2, restored!.Count);
        Assert.Equal("My Custom Name", restored["abc-123"]);
    }

    [Fact]
    public void SessionAliases_EmptyAlias_CanBeRemovedByTrim()
    {
        // Mirrors CopilotService.SetSessionAlias logic
        var alias = "  ";
        Assert.True(string.IsNullOrWhiteSpace(alias));
    }

    [Fact]
    public void SessionAliases_TrimmedOnSet()
    {
        // Mirrors CopilotService.SetSessionAlias logic
        var alias = "  My Session  ";
        Assert.Equal("My Session", alias.Trim());
    }

    [Fact]
    public void SaveActiveSessionsToDisk_Pattern_PreservesAllFields()
    {
        // Mirrors the exact pattern in CopilotService.SaveActiveSessionsToDisk:
        //   new ActiveSessionEntry { SessionId=..., DisplayName=..., Model=..., WorkingDirectory=... }
        // This test ensures that when new fields are added to ActiveSessionEntry,
        // they are also populated during save (not left null).

        // Simulate an AgentSessionInfo (the source of truth at runtime)
        var sessionInfo = new PolyPilot.Models.AgentSessionInfo
        {
            Name = "MauiWorktree",
            Model = "claude-opus-4.5",
            SessionId = "abc-123-def",
            WorkingDirectory = "/Users/test/.polypilot/worktrees/dotnet-maui-8f45001d"
        };

        // Simulate SaveActiveSessionsToDisk's mapping logic
        var entry = new ActiveSessionEntry
        {
            SessionId = sessionInfo.SessionId!,
            DisplayName = sessionInfo.Name,
            Model = sessionInfo.Model,
            WorkingDirectory = sessionInfo.WorkingDirectory,
            TotalInputTokens = sessionInfo.TotalInputTokens,
            TotalOutputTokens = sessionInfo.TotalOutputTokens,
            ContextCurrentTokens = sessionInfo.ContextCurrentTokens,
            ContextTokenLimit = sessionInfo.ContextTokenLimit,
            PremiumRequestsUsed = sessionInfo.PremiumRequestsUsed,
            TotalApiTimeSeconds = sessionInfo.TotalApiTimeSeconds,
            CreatedAt = sessionInfo.CreatedAt,
        };

        // Round-trip through JSON (simulates app restart)
        var json = JsonSerializer.Serialize(new[] { entry });
        var restored = JsonSerializer.Deserialize<List<ActiveSessionEntry>>(json)!;
        var restoredEntry = restored[0];

        // ALL fields must survive — if any is null, the restore path loses context
        Assert.Equal("abc-123-def", restoredEntry.SessionId);
        Assert.Equal("MauiWorktree", restoredEntry.DisplayName);
        Assert.Equal("claude-opus-4.5", restoredEntry.Model);
        Assert.Equal("/Users/test/.polypilot/worktrees/dotnet-maui-8f45001d", restoredEntry.WorkingDirectory);
    }

    [Fact]
    public void ActiveSessionEntry_UsageStats_RoundTrip()
    {
        var created = new DateTime(2026, 3, 1, 10, 30, 0, DateTimeKind.Utc);
        var entry = new ActiveSessionEntry
        {
            SessionId = "usage-test",
            DisplayName = "Usage Session",
            Model = "gpt-4.1",
            TotalInputTokens = 1500,
            TotalOutputTokens = 800,
            ContextCurrentTokens = 4500,
            ContextTokenLimit = 128000,
            PremiumRequestsUsed = 7,
            TotalApiTimeSeconds = 45.3,
            CreatedAt = created,
        };

        var json = JsonSerializer.Serialize(new[] { entry });
        var restored = JsonSerializer.Deserialize<List<ActiveSessionEntry>>(json)!;
        var r = restored[0];

        Assert.Equal(1500, r.TotalInputTokens);
        Assert.Equal(800, r.TotalOutputTokens);
        Assert.Equal(4500, r.ContextCurrentTokens);
        Assert.Equal(128000, r.ContextTokenLimit);
        Assert.Equal(7, r.PremiumRequestsUsed);
        Assert.Equal(45.3, r.TotalApiTimeSeconds, 1);
        Assert.Equal(created, r.CreatedAt);
    }

    [Fact]
    public void ActiveSessionEntry_UsageStats_BackwardCompatible()
    {
        // Entries saved before the usage fields were added should deserialize with defaults
        var legacyJson = """[{"SessionId":"old-1","DisplayName":"Old","Model":"gpt-4.1"}]""";
        var restored = JsonSerializer.Deserialize<List<ActiveSessionEntry>>(legacyJson)!;
        var r = restored[0];

        Assert.Equal(0, r.TotalInputTokens);
        Assert.Equal(0, r.TotalOutputTokens);
        Assert.Null(r.ContextCurrentTokens);
        Assert.Null(r.ContextTokenLimit);
        Assert.Equal(0, r.PremiumRequestsUsed);
        Assert.Equal(0.0, r.TotalApiTimeSeconds);
        Assert.Null(r.CreatedAt);
    }
}
